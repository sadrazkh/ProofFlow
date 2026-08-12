using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.Infrastructure.Scheduling;

/// <summary>
/// The thing that makes a schedule happen.
///
/// It wakes, asks which schedules are due, starts a batch for each, and works out when each is next
/// due. That is the whole of it — and everything careful about it is in what it refuses to do.
///
/// It does not catch up. A schedule that fires hourly and was missed for a day fires once when the
/// process returns, not twenty-four times: a catch-up storm against somebody's production API is a
/// far worse failure than a missed window.
///
/// It advances the schedule before starting the batch. If starting throws, the schedule has still
/// moved on — because the alternative is a schedule that is permanently due, retried every tick,
/// for ever.
/// </summary>
public sealed class ScheduleWorker(
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<ScheduleWorker> logger) : BackgroundService
{
    /// <summary>
    /// How often it looks.
    ///
    /// Thirty seconds, which bounds how late a minute-granularity schedule can be at half a minute
    /// and costs one indexed query. Polling rather than timers: a timer per schedule is a thing to
    /// keep in step with the database, and the database is the truth.
    /// </summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most schedules fired in one tick.
    ///
    /// A ceiling on what a bad expression or a clock jump can cost. What is left over is picked up
    /// on the next tick, thirty seconds later.
    /// </summary>
    public const int MaxPerTick = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A moment before the first look. The application is still starting, migrations may still
        // be running, and a schedule that fires during that is a run against a half-open database.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Tick);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FireDueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop outlives any one tick. A schedule that took it down would stop every
                // other schedule in every workspace.
                logger.LogError(ex, "A scheduling tick did not finish.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) return;
        }
    }

    private async Task FireDueAsync(CancellationToken cancellation)
    {
        var now = clock.UtcNow;

        // Schedules span workspaces and this is the machinery allowed to see across them, so the
        // read ignores the tenant filter explicitly. Each schedule is then fired under its own
        // workspace, which is where every write happens.
        using var reading = scopes.CreateScope();
        var db = reading.ServiceProvider.GetRequiredService<ProofFlowDbContext>();

        var due = await db.RunSchedules
            .IgnoreQueryFilters()
            .Where(schedule => schedule.Enabled
                               && schedule.NextRunAt != null
                               && schedule.NextRunAt <= now)
            .OrderBy(schedule => schedule.NextRunAt)
            .Take(MaxPerTick)
            .Select(schedule => new { schedule.Id, schedule.WorkspaceId, schedule.Name })
            .ToListAsync(cancellation);

        foreach (var schedule in due)
        {
            await FireAsync(schedule.Id, schedule.WorkspaceId, schedule.Name, now, cancellation);
        }
    }

    private async Task FireAsync(
        Guid scheduleId, Guid workspaceId, string name, DateTimeOffset now,
        CancellationToken cancellation)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackgroundWorkspace>().ActFor(workspaceId);

        var db = scope.ServiceProvider.GetRequiredService<ProofFlowDbContext>();

        var schedule = await db.RunSchedules
            .Include(candidate => candidate.Scenarios)
            .Include(candidate => candidate.Environments)
            .FirstOrDefaultAsync(candidate => candidate.Id == scheduleId, cancellation);

        if (schedule is null) return;

        // Advanced first, and saved first. If starting the batch throws, this schedule has still
        // moved on — otherwise it stays due, is retried every thirty seconds, and turns one broken
        // scenario into a permanent load against whatever it points at.
        schedule.LastRunAt = now;
        ScheduleService.Advance(schedule, now);
        await db.SaveChangesAsync(cancellation);

        var scenarios = schedule.Scenarios.Select(link => link.ScenarioId).ToList();
        var environments = schedule.Environments.Select(link => link.EnvironmentId).ToList();

        if (scenarios.Count == 0 || environments.Count == 0)
        {
            // Everything it pointed at has been deleted. Said out loud and switched off rather than
            // left to fail silently every morning at six for ever.
            schedule.Enabled = false;
            schedule.Problem = "cron.nothingToRun";
            await db.SaveChangesAsync(cancellation);

            logger.LogWarning("Schedule {Name} has nothing left to run and was switched off.", name);
            return;
        }

        try
        {
            var matrix = scope.ServiceProvider.GetRequiredService<MatrixService>();

            var batch = await matrix.QueueAsync(
                schedule.ProjectId, scenarios, environments, name, cancellation: cancellation);

            batch.Trigger = Domain.Runs.RunTrigger.Schedule;

            foreach (var run in await db.Runs.Where(run => run.BatchId == batch.Id).ToListAsync(cancellation))
            {
                run.Trigger = Domain.Runs.RunTrigger.Schedule;
            }

            schedule.LastBatchId = batch.Id;
            await db.SaveChangesAsync(cancellation);

            logger.LogInformation(
                "Schedule {Name} started {Cells} runs; next at {Next:O}.",
                name, batch.Total, schedule.NextRunAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schedule {Name} could not start its runs.", name);
        }
    }
}
