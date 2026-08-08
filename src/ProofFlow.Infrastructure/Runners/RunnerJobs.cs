using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Runners;
using ProofFlow.Domain.Runners;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Runners;

/// <summary>
/// Handing work to an agent, and taking back what it found.
///
/// Two rules hold the whole exchange up.
///
/// <b>A job is claimed, not assigned.</b> The server never pushes; it cannot, because there is
/// nothing to push to — the agent is behind a firewall by definition. So a run sits Queued until an
/// agent asks, and the claim is the moment it becomes that agent's. Two agents enrolled as the same
/// runner cannot both get it, because claiming writes a row.
///
/// <b>Everything the agent is told is signed.</b> Not for secrecy — TLS has that — but so the agent
/// can answer the only question that matters to a process running arbitrary requests inside a
/// private network: did this instruction come from the installation I enrolled with, unchanged.
/// </summary>
public sealed class RunnerJobs(
    ProofFlowDbContext db, RunnerService runners, JobPackaging packaging, IClock clock)
{
    /// <summary>
    /// How long a claimed run may stay unreported before it is offered again.
    ///
    /// An agent that was killed mid-run leaves a claim behind, and a run nobody will ever report on
    /// is a run somebody is still waiting for. Generous, because the alternative — two agents
    /// running the same scenario against the same environment — is worse than a slow recovery.
    /// </summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The next run waiting for this agent, signed, or nothing.
    ///
    /// Ordered oldest first, so a queue that builds up during an outage drains in the order people
    /// asked rather than newest-first.
    /// </summary>
    public async Task<SignedJob?> ClaimAsync(Runner runner, CancellationToken cancellation = default)
    {
        if (runners.SigningKey(runner) is not { } signingKey) return null;

        var stale = clock.UtcNow - Abandoned;

        var run = await db.Runs
            .IgnoreQueryFilters()
            .Where(candidate => candidate.RunnerId == runner.Id
                                && candidate.Status == RunStatus.Queued
                                && (candidate.ClaimedAt == null || candidate.ClaimedAt < stale))
            .OrderBy(candidate => candidate.CreatedAt)
            .FirstOrDefaultAsync(cancellation);

        if (run is null) return null;

        run.ClaimedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellation);

        // Everything execution actually needs, and nothing else — the graph, the environment, the
        // variables and secrets it references, and the data sets and baselines the graph names.
        var payload = JsonSerializer.Serialize(await packaging.PackAsync(run, cancellation), Json);

        return new SignedJob
        {
            JobId = run.Id,
            IssuedAt = clock.UtcNow.ToString("O"),
            Payload = payload,
            Signature = JobSignature.Sign(payload, signingKey),
        };
    }

    /// <summary>
    /// Records what the agent found.
    ///
    /// The run has to belong to this runner. A token is a credential for one runner, not for the
    /// workspace, and an agent that could report on somebody else's run could mark a failing test
    /// green from the other side of a firewall.
    /// </summary>
    public Task<bool> ReportAsync(
        Runner runner, JobReport report, CancellationToken cancellation = default) =>
        packaging.RecordAsync(runner, report, cancellation);
}
