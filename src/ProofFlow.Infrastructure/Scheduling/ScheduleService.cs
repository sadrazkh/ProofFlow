using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Scheduling;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Scheduling;

/// <summary>
/// Making and keeping schedules.
///
/// The interesting part is <see cref="Advance"/>. Everything else is a form.
/// </summary>
public sealed class ScheduleService(ProofFlowDbContext db, IClock clock, ICurrentUser me)
{
    public async Task<RunSchedule> SaveAsync(
        Guid projectId, Guid? scheduleId, string name, string cron, string timeZoneId,
        IReadOnlyList<Guid> scenarioIds, IReadOnlyList<Guid> environmentIds, bool enabled,
        CancellationToken cancellation = default)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellation)
            ?? throw new InvalidOperationException("No such project in this workspace.");

        // Checked against the project rather than trusted from the form: a browser can be made to
        // say anything, and an id from another project would schedule somebody else's test.
        var scenarios = await db.Scenarios
            .Where(scenario => scenario.ProjectId == projectId && scenarioIds.Contains(scenario.Id))
            .Select(scenario => scenario.Id)
            .ToListAsync(cancellation);

        var environments = await db.Environments
            .Where(environment => environment.ProjectId == projectId
                                  && environmentIds.Contains(environment.Id))
            .Select(environment => environment.Id)
            .ToListAsync(cancellation);

        if (scenarios.Count == 0) throw new InvalidOperationException("No scenario was chosen.");
        if (environments.Count == 0) throw new InvalidOperationException("No environment was chosen.");

        var schedule = scheduleId is { } id
            ? await db.RunSchedules
                  .Include(candidate => candidate.Scenarios)
                  .Include(candidate => candidate.Environments)
                  .FirstOrDefaultAsync(candidate =>
                      candidate.Id == id && candidate.ProjectId == projectId, cancellation)
              ?? throw new InvalidOperationException("No such schedule.")
            : new RunSchedule
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = projectId,
                Name = name,
                Cron = cron,
                CreatedByUserId = me.UserId,
            };

        schedule.Name = name.Trim();
        schedule.Cron = cron.Trim();
        schedule.TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId.Trim();
        schedule.Enabled = enabled;

        if (scheduleId is null) db.RunSchedules.Add(schedule);
        else
        {
            db.ScheduleScenarios.RemoveRange(schedule.Scenarios);
            db.ScheduleEnvironments.RemoveRange(schedule.Environments);
        }

        foreach (var scenario in scenarios)
        {
            db.ScheduleScenarios.Add(new ScheduleScenario
            {
                WorkspaceId = project.WorkspaceId,
                RunScheduleId = schedule.Id,
                ScenarioId = scenario,
            });
        }

        foreach (var environment in environments)
        {
            db.ScheduleEnvironments.Add(new ScheduleEnvironment
            {
                WorkspaceId = project.WorkspaceId,
                RunScheduleId = schedule.Id,
                EnvironmentId = environment,
            });
        }

        Advance(schedule, clock.UtcNow);
        await db.SaveChangesAsync(cancellation);

        return schedule;
    }

    /// <summary>
    /// Works out when a schedule is next due, and records why if it never is.
    ///
    /// From <paramref name="from"/> rather than from the last run, and that is the decision worth
    /// stating: if the process was down for a day, a schedule that fires hourly should fire once
    /// when it comes back, not twenty-four times. A catch-up storm against somebody's production
    /// API is a far worse failure than a missed window, and nobody has ever wanted the twenty-four.
    /// </summary>
    public static void Advance(RunSchedule schedule, DateTimeOffset from)
    {
        var problem = CronSchedule.Problem(schedule.Cron, schedule.TimeZoneId);

        if (problem is not null)
        {
            // A schedule that cannot be read says so and stops, rather than quietly never firing.
            schedule.Problem = problem;
            schedule.NextRunAt = null;
            return;
        }

        schedule.Problem = null;
        schedule.NextRunAt = CronSchedule.Next(schedule.Cron, schedule.TimeZoneId, from);
    }

    public async Task<bool> SetEnabledAsync(
        Guid projectId, Guid scheduleId, bool enabled, CancellationToken cancellation = default)
    {
        var schedule = await db.RunSchedules
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == scheduleId && candidate.ProjectId == projectId, cancellation);

        if (schedule is null) return false;

        schedule.Enabled = enabled;

        // Recomputed on the way back on, so a schedule switched on after a month does not think it
        // is overdue by a month's worth of occurrences.
        if (enabled) Advance(schedule, clock.UtcNow);

        await db.SaveChangesAsync(cancellation);
        return true;
    }

    public async Task<bool> DeleteAsync(
        Guid projectId, Guid scheduleId, CancellationToken cancellation = default)
    {
        var schedule = await db.RunSchedules
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == scheduleId && candidate.ProjectId == projectId, cancellation);

        if (schedule is null) return false;

        db.RunSchedules.Remove(schedule);
        await db.SaveChangesAsync(cancellation);

        return true;
    }
}
