using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Projects;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// Throws away the bulk of an old run and keeps the part anybody will ask about.
///
/// The distinction is the whole design. What a run <i>decided</i> — passed or failed, how long it
/// took, which assertions held — is small, is what a trend is made of, and is kept for ever. What a
/// run <i>saw</i> is large: response bodies, log lines, artefacts. That is the part that makes a
/// testing tool's database grow without limit, and it is also the part most likely to be holding a
/// customer's personal data long after anybody needed it.
///
/// So the row survives and its payload does not, and the run says so rather than quietly appearing
/// to have produced nothing.
/// </summary>
public sealed class RetentionService(ProofFlowDbContext db, IClock clock, ILogger<RetentionService> logger)
{
    /// <summary>
    /// How many runs one pass will clear.
    ///
    /// A bound rather than "all of them", because the first pass over a database that has been
    /// running for a year without this would otherwise be a single transaction over millions of
    /// rows. It runs again in an hour.
    /// </summary>
    public const int MaxRunsPerPass = 500;

    /// <summary>Clears one project's expired payloads and says how many runs it touched.</summary>
    public async Task<int> SweepAsync(Project project, CancellationToken cancellation = default)
    {
        if (project.RetentionDays <= 0) return 0;

        var cutoff = clock.UtcNow.AddDays(-project.RetentionDays);

        // Finished runs only. A run still going has no age worth measuring, and clearing the log
        // out from under a console somebody is watching would be a strange thing to do.
        var runs = await db.Runs
            .Where(run => run.ProjectId == project.Id
                          && run.FinishedAt != null
                          && run.FinishedAt < cutoff
                          && !run.PayloadsCleared)
            .OrderBy(run => run.FinishedAt)
            .Take(MaxRunsPerPass)
            .ToListAsync(cancellation);

        if (runs.Count == 0) return 0;

        var ids = runs.Select(run => run.Id).ToList();

        // ExecuteDelete rather than loading and removing: these are the rows there are a lot of, and
        // materialising ten thousand log lines to delete them is the version of this that runs out
        // of memory on the database it was written to protect.
        await db.RunEvents.Where(entry => ids.Contains(entry.TestRunId))
            .ExecuteDeleteAsync(cancellation);

        await db.RunArtifacts.Where(artifact => ids.Contains(artifact.TestRunId))
            .ExecuteDeleteAsync(cancellation);

        await db.NodeRuns.Where(node => ids.Contains(node.TestRunId) && node.OutputJson != null)
            .ExecuteUpdateAsync(node => node.SetProperty(row => row.OutputJson, (string?)null), cancellation);

        foreach (var run in runs) run.PayloadsCleared = true;

        await db.SaveChangesAsync(cancellation);

        logger.LogInformation(
            "Cleared the payloads of {Count} run(s) in {Project}, finished before {Cutoff:u}.",
            runs.Count, project.Slug, cutoff);

        return runs.Count;
    }
}

/// <summary>
/// Runs the sweep, quietly, once an hour.
///
/// Hourly rather than nightly because "nightly" is a time zone argument and an hour of extra
/// retention is not worth having one. Across every workspace, because retention is an obligation
/// rather than a feature somebody opts into per tenant.
/// </summary>
public sealed class RetentionWorker(
    IServiceScopeFactory scopes, ILogger<RetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    /// <summary>
    /// A pause before the first pass.
    ///
    /// Starting up is the busiest minute a process has — migrations, the first requests, the run
    /// worker waking — and a sweep over every project is not what it needs on top.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(Settle, stopping);

            while (!stopping.IsCancellationRequested)
            {
                await SweepAsync(stopping);
                await Task.Delay(Every, stopping);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task SweepAsync(CancellationToken stopping)
    {
        try
        {
            using var scope = scopes.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<ProofFlowDbContext>();
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();

            // Across tenants, so it needs the scope that spans them — the same one the schedule
            // worker uses, and the only way to see more than one workspace at a time.
            var projects = await db.Projects
                .IgnoreQueryFilters()
                .Where(project => project.RetentionDays > 0)
                .ToListAsync(stopping);

            var cleared = 0;

            foreach (var project in projects)
            {
                scope.ServiceProvider.GetRequiredService<BackgroundWorkspace>()
                    .ActFor(project.WorkspaceId);

                cleared += await retention.SweepAsync(project, stopping);
            }

            if (cleared > 0) logger.LogInformation("Retention cleared {Count} run(s).", cleared);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never fatal. A sweep that could not run is a database that is larger than it should
            // be; a worker that died is one that never sweeps again.
            logger.LogError(exception, "A retention sweep did not finish.");
        }
    }
}
