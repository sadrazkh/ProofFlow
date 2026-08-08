using System.Diagnostics;
using System.Text.Json;
using ProofFlow.Contracts.Runners;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Agent;

/// <summary>
/// Checks one job, and — for now — refuses to pretend it ran it.
///
/// What is finished here is the part that had to be: the signature is verified, the graph is
/// validated against the same validator the canvas uses, and the environment's own limits are read
/// off the job rather than taken from anything on this machine.
///
/// What is not finished is execution. The engine is deliberately a project of its own with no
/// database in it, so it can run here — but the object that supplies it with baselines, data sets
/// and an HTTP executor still reaches for a DbContext, and the agent must not have one. Until that
/// seam is opened, this reports <c>Errored</c> and says so.
///
/// It does not report <c>Passed</c>. A testing tool that returns a green result for work it did not
/// do is worse than one that returns nothing, and it is worse in the specific way that takes months
/// to notice.
/// </summary>
internal static class JobRunner
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<JobResult> ExecuteAsync(SignedJob job, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var work = JsonSerializer.Deserialize<JobPayload>(job.Payload, Json)
                ?? throw new InvalidOperationException("The job payload could not be read.");

            var graph = JsonSerializer.Deserialize<GraphDto>(work.Definition ?? "{}", Json);

            if (graph is null || graph.Nodes.Count == 0)
            {
                return Finished(job, "Errored", started, "That job carried no graph to run.");
            }

            // The environment's own limits travel with the job, so an agent cannot be talked into
            // being more permissive than the environment says — the policy is the server's, and the
            // agent only supplies the route.
            var policy = new UrlPolicy
            {
                AllowedHosts = Split(work.Environment?.AllowedHosts),
                AllowPrivateNetwork = work.Environment?.AllowPrivateNetwork ?? false,
                AllowInvalidCertificate = work.Environment?.AllowInvalidCertificate ?? false,
                MaxRedirects = work.Environment?.MaxRedirects ?? 5,
                MaxResponseBytes = (work.Environment?.MaxResponseKilobytes ?? 4096) * 1024L,
                Timeout = TimeSpan.FromSeconds(work.Environment?.TimeoutSeconds ?? 30),
            };

            var problems = GraphValidator.Validate(new Graph(
                [.. graph.Nodes.Select(node => new GraphNode(
                    node.Id, node.Key, node.Name, node.Properties, node.ParentId, node.Disabled))],
                [.. graph.Edges.Select(edge => new GraphEdge(
                    edge.FromId, edge.FromPort, edge.ToId, edge.ToPort))]));

            if (problems.Any(problem => problem.Severity == GraphSeverity.Error))
            {
                // Refused here as well as on the server. The graph was valid when it was published;
                // if it is not valid now, something changed it in transit and running it anyway
                // would be the opposite of what the signature is for.
                return Finished(job, "Errored", started,
                    "That graph does not validate on this agent.");
            }

            // The policy is built and the graph is sound. What is missing is the engine's services
            // object, which still needs a database — see the note on this class.
            _ = policy;

            return Finished(job, "Errored", started,
                "This agent verified the job and the graph but cannot run it yet: executing a "
                + "scenario off the database is not finished. Point this environment back at the "
                + "server, or wait for an agent that reports a real result.");
        }
        catch (OperationCanceledException)
        {
            return Finished(job, "Cancelled", started, "The agent was asked to stop.");
        }
        catch (Exception exception)
        {
            // Errored, not Failed. "Your API is broken" and "our agent is broken" are different
            // news, and conflating them sends somebody looking in the wrong place.
            return Finished(job, "Errored", started, $"The agent could not carry this out: {exception.Message}");
        }
    }

    private static JobResult Finished(SignedJob job, string status, long started, string outcome) =>
        new()
        {
            JobId = job.JobId,
            Status = status,
            Outcome = outcome,
            DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The shape the server signs. Read here, never trusted before the signature holds.</summary>
    private sealed record JobPayload
    {
        public Guid RunId { get; init; }
        public string? Definition { get; init; }
        public JobEnvironment? Environment { get; init; }
    }

    private sealed record JobEnvironment
    {
        public string? Name { get; init; }
        public string? BaseUrl { get; init; }
        public int TimeoutSeconds { get; init; }
        public int MaxRedirects { get; init; }
        public int MaxResponseKilobytes { get; init; }
        public string? AllowedHosts { get; init; }
        public bool AllowPrivateNetwork { get; init; }
        public bool AllowInvalidCertificate { get; init; }
        public string? DefaultHeadersJson { get; init; }
    }
}
