using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Runs;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// Which tests cannot make up their minds.
///
/// A flaky test is worse than a failing one. A failing test is information; a test that fails one
/// morning and passes the next teaches a team to ignore red, and once they do that the suite has
/// stopped working — every real regression after that point is a notification somebody dismissed.
///
/// The definition here is deliberately narrow: the same scenario, the same version of it, in the
/// same environment, producing both passes and failures. Holding the version fixed is what
/// separates "this test is unreliable" from "somebody changed the test", and holding the
/// environment fixed separates it from "staging is broken and production is fine" — which is not
/// flakiness, it is the answer the matrix exists to give.
/// </summary>
public sealed class FlakyDetector(ProofFlowDbContext db, IClock clock)
{
    /// <summary>How far back to look. Beyond a fortnight, a fixed test still reads as flaky.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    /// <summary>
    /// How many runs a verdict needs.
    ///
    /// Three, because two is a coin. One pass and one failure is the most ordinary thing in the
    /// world — somebody broke it and fixed it — and calling that flaky would make the label
    /// meaningless before anybody had read it twice.
    /// </summary>
    public const int MinimumRuns = 3;

    public async Task<IReadOnlyList<FlakyScenarioDto>> ForProjectAsync(
        Guid projectId, CancellationToken cancellation = default)
    {
        var since = clock.UtcNow - Window;

        var runs = await db.Runs
            .Where(run => run.ProjectId == projectId
                          && run.CreatedAt >= since
                          && (run.Status == RunStatus.Passed || run.Status == RunStatus.Failed))
            .Select(run => new
            {
                run.ScenarioId,
                run.ScenarioVersionId,
                run.EnvironmentId,
                run.Status,
                run.CreatedAt,
            })
            .ToListAsync(cancellation);

        if (runs.Count == 0) return [];

        var names = await db.Scenarios
            .Where(scenario => scenario.ProjectId == projectId)
            .Select(scenario => new { scenario.Id, scenario.Name, scenario.QuarantinedAt })
            .ToDictionaryAsync(scenario => scenario.Id, cancellation);

        var environments = await db.Environments
            .Where(environment => environment.ProjectId == projectId)
            .ToDictionaryAsync(environment => environment.Id, environment => environment.Name, cancellation);

        var found = new List<FlakyScenarioDto>();

        // Grouped by version and environment, then reported per scenario: a test that is flaky in
        // staging and steady in production is flaky, and the report says where.
        foreach (var group in runs.GroupBy(run =>
                     (run.ScenarioId, run.ScenarioVersionId, run.EnvironmentId)))
        {
            var total = group.Count();
            if (total < MinimumRuns) continue;

            var failed = group.Count(run => run.Status == RunStatus.Failed);
            if (failed == 0 || failed == total) continue;

            var scenario = names.GetValueOrDefault(group.Key.ScenarioId);

            found.Add(new FlakyScenarioDto
            {
                ScenarioId = group.Key.ScenarioId,
                Name = scenario?.Name ?? string.Empty,
                EnvironmentId = group.Key.EnvironmentId,
                EnvironmentName = group.Key.EnvironmentId is { } id
                    ? environments.GetValueOrDefault(id)
                    : null,
                Runs = total,
                Failed = failed,
                LastSeen = group.Max(run => run.CreatedAt),
                Quarantined = scenario?.QuarantinedAt is not null,
            });
        }

        // Worst first: the point of the list is what to fix next, and the test that flips half the
        // time is doing more damage than the one that flips once in ten.
        return [.. found.OrderByDescending(entry => entry.Rate).ThenByDescending(entry => entry.Runs)];
    }

    /// <summary>
    /// Puts a scenario in or out of quarantine.
    ///
    /// Quarantine keeps the test running and stops it failing the build. Not disabling and not
    /// deleting — a flaky test that gets deleted takes its coverage with it and nobody notices for
    /// six months.
    /// </summary>
    public async Task<bool> QuarantineAsync(
        Guid projectId, Guid scenarioId, bool quarantined, string? reason, Guid? byUserId,
        CancellationToken cancellation = default)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == scenarioId && candidate.ProjectId == projectId, cancellation);

        if (scenario is null) return false;

        scenario.QuarantinedAt = quarantined ? clock.UtcNow : null;
        scenario.QuarantineReason = quarantined ? reason?.Trim() : null;
        scenario.QuarantinedByUserId = quarantined ? byUserId : null;

        await db.SaveChangesAsync(cancellation);
        return true;
    }
}
