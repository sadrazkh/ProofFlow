using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Contracts.Runners;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Running;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.Agent;

/// <summary>
/// Runs one verified job, with the same engine the server runs.
///
/// There is no second engine and there is not going to be one. <see cref="ScenarioRunner"/>,
/// <see cref="NodeExecutors"/>, the URL guard and the redactor are the assemblies the server loads,
/// executing the same graph against the same policy. What differs is where the data came from — a
/// package rather than a database — and where the record goes — back over HTTP rather than into
/// rows. Both of those are behind interfaces the engine already declared.
///
/// A scenario cannot tell which side it ran on, which is the only claim that makes a remote runner
/// worth having.
/// </summary>
internal static class JobRunner
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<JobReport> ExecuteAsync(SignedJob job, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();

        // Made before the sink, because the sink masks with it. The package's secrets go in as soon
        // as it is read, below; until then there is nothing to hide.
        var redaction = new RedactionScope();

        var sink = new CollectingSink(redaction);

        try
        {
            var package = JsonSerializer.Deserialize<JobPackage>(job.Payload, Json)
                ?? throw new InvalidOperationException("The job payload could not be read.");

            var graph = Read(package.Definition);

            if (graph.Nodes.Count == 0)
            {
                return Failed(job, started, sink, "That job carried no graph to run.");
            }

            // Validated here as well as on the server. The graph was sound when it was published; if
            // it is not sound now, something changed it on the way, and running it anyway would be
            // the opposite of what the signature is for.
            var problems = GraphValidator.Validate(graph);

            if (problems.Any(problem => problem.Severity == GraphSeverity.Error))
            {
                var first = problems.First(problem => problem.Severity == GraphSeverity.Error);

                return Failed(job, started, sink,
                    $"That graph does not validate on this agent ({first.Code}).");
            }

            var policy = PolicyFor(package.Environment);

            // The redactor learns the secrets before anything runs, so a value cannot reach a log
            // line or a stored output on its way to being hidden.
            foreach (var secret in package.Secrets.Values) redaction.Remember(secret);

            var scopes = ScopesFor(package);

            using var provider = HttpProvider();

            var services = new PackagedRunServices(
                package,
                new GuardedHttpExecutor(
                    provider.GetRequiredService<IHttpClientFactory>(),
                    NullLogger<GuardedHttpExecutor>.Instance),
                policy,
                redaction);

            var runner = new ScenarioRunner(new NodeExecutors(services), sink);

            var summary = await runner.RunAsync(graph, new RunScopes(scopes, redaction), package.StartNodeId, cancellation);

            return new JobReport
            {
                JobId = job.JobId,
                Status = summary.Status.ToString(),
                Outcome = Outcome(summary.Outcome, sink),
                Steps = summary.Steps,
                StepsFailed = summary.StepsFailed,
                AssertionsPassed = summary.AssertionsPassed,
                AssertionsFailed = summary.AssertionsFailed,
                DurationMs = summary.DurationMs,
                Nodes = sink.Nodes,
                Log = sink.Lines,
                Captures = services.Captures,
            };
        }
        catch (OperationCanceledException)
        {
            return new JobReport
            {
                JobId = job.JobId,
                Status = "Cancelled",
                Outcome = "The agent was asked to stop.",
                DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Nodes = sink.Nodes,
                Log = sink.Lines,
            };
        }
        catch (Exception exception)
        {
            // Errored, not Failed. "Your API is broken" and "our agent is broken" are different news,
            // and conflating them sends somebody looking in the wrong place.
            return Failed(job, started, sink,
                $"The agent could not carry this out: {exception.Message}");
        }
    }

    /// <summary>
    /// The environment's own limits, as the server stated them.
    ///
    /// Built from the job rather than from anything on this machine, so an agent cannot be talked
    /// into being more permissive than the environment says. The policy belongs to the installation;
    /// all the agent supplies is a route.
    /// </summary>
    private static UrlPolicy PolicyFor(JobEnvironment? environment) => new()
    {
        AllowedHosts = Split(environment?.AllowedHosts),
        DeniedHosts = Split(environment?.DeniedHosts),
        AllowPrivateNetwork = environment?.AllowPrivateNetwork ?? false,
        AllowInvalidCertificate = environment?.AllowInvalidCertificate ?? false,
        MaxRedirects = environment?.MaxRedirects ?? 5,
        MaxResponseBytes = (environment?.MaxResponseKilobytes ?? 4096) * 1024L,
        Timeout = TimeSpan.FromSeconds(environment?.TimeoutSeconds ?? 30),
    };

    private static VariableScopes ScopesFor(JobPackage package)
    {
        var scopes = new VariableScopes();

        if (package.Environment?.BaseUrl is { Length: > 0 } baseUrl)
        {
            scopes.Environment["baseUrl"] = JsonValue.Create(baseUrl);
        }

        if (package.Environment?.Name is { Length: > 0 } name)
        {
            scopes.Environment["name"] = JsonValue.Create(name);
        }

        foreach (var (key, value) in package.Variables) scopes.Variables[key] = JsonValue.Create(value);
        foreach (var (key, value) in package.Inputs) scopes.Inputs[key] = JsonValue.Create(value);
        foreach (var (key, value) in package.Secrets) scopes.Secrets[key] = JsonValue.Create(value);

        scopes.Run["startedAt"] = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"));

        return scopes;
    }

    /// <summary>
    /// A provider for the one thing the executor needs.
    ///
    /// The connect callback that closes the DNS-rebinding window is registered by
    /// <c>AddProofFlowHttpClients</c>, so the agent gets exactly the same socket-level guard the
    /// server has. Building it here rather than at start-up keeps the handler's lifetime tied to
    /// the job, which for a process that runs one thing at a time is the simpler arrangement.
    /// </summary>
    private static ServiceProvider HttpProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddProofFlowHttpClients();

        return services.BuildServiceProvider();
    }

    private static Graph Read(string? definition)
    {
        var graph = string.IsNullOrWhiteSpace(definition)
            ? null
            : JsonSerializer.Deserialize<GraphDto>(definition, Json);

        if (graph is null) return new Graph([], []);

        return new Graph(
            [.. graph.Nodes.Select(node => new GraphNode(
                node.Id, node.Key, node.Name, node.Properties, node.ParentId, node.Disabled))],
            [.. graph.Edges.Select(edge => new GraphEdge(
                edge.FromId, edge.FromPort, edge.ToId, edge.ToPort))]);
    }

    /// <summary>Says out loud when the log was cut, rather than letting a short log look complete.</summary>
    private static string? Outcome(string? outcome, CollectingSink sink) =>
        sink.Dropped == 0
            ? outcome
            : $"{outcome} ({sink.Dropped:N0} further log lines were not kept.)";

    private static JobReport Failed(
        SignedJob job, long started, CollectingSink sink, string outcome) =>
        new()
        {
            JobId = job.JobId,
            Status = "Errored",
            Outcome = outcome,
            DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Nodes = sink.Nodes,
            Log = sink.Lines,
        };

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
