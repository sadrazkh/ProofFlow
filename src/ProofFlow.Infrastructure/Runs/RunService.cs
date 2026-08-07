using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Runs;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Infrastructure.Scenarios;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Running;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// Starting a run, and carrying one out.
///
/// Split in two on purpose. <see cref="QueueAsync"/> happens inside the request that asked for it:
/// it checks the scenario, takes the snapshot and writes a queued row, so the browser can be sent
/// straight to a console that exists. <see cref="ExecuteAsync"/> happens on the worker, where it
/// can take twenty minutes without holding a request open.
///
/// The snapshot is the part that matters. A run records the graph it ran rather than pointing at
/// the scenario, because the first thing anybody does after a failing run is edit the scenario, and
/// a report that changes when the test changes is not a report.
/// </summary>
public sealed class RunService(
    ProofFlowDbContext db,
    ScenarioGraphService graphs,
    ScenarioGraphSnapshots snapshots,
    EnvironmentContextBuilder environments,
    BaselineService baselines,
    IHttpExecutor executor,
    IRunWatchers watchers,
    ICurrentUser me,
    IClock clock,
    ILogger<RunService> logger)
{
    /// <summary>How often the record is written while a run is going.</summary>
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Accepts a run and returns it queued.
    ///
    /// Refuses a scenario the validator will not pass, and says which problems — starting a run
    /// that cannot work and letting it fail two seconds later wastes the person's attention and
    /// puts a red run in the history that means nothing.
    /// </summary>
    public async Task<TestRun> QueueAsync(
        Guid scenarioId, Guid? environmentId, RunTrigger trigger,
        CancellationToken cancellation = default)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(candidate => candidate.Id == scenarioId, cancellation)
            ?? throw new InvalidOperationException("No such scenario in this workspace.");

        var version = await db.ScenarioVersions
            .Where(candidate => candidate.ScenarioId == scenario.Id)
            .OrderByDescending(candidate => candidate.Number)
            .FirstOrDefaultAsync(cancellation)
            ?? throw new InvalidOperationException("This scenario has never been saved.");

        // The graph is stored as rows; the run keeps it as one document. That is the snapshot:
        // rows get edited, and a report from March has to still read the way it read in March.
        var graph = await graphs.LoadAsync(version.Id, cancellation);

        // An environment, whether or not one was asked for. Almost every scenario's first step
        // reads {{environment.baseUrl}}, and a run with no environment fails on that reference
        // before it has done anything — which reads as a broken product rather than a missing
        // choice. The project's first is a guess, and a guess that runs beats a certainty that
        // does not.
        var environment = environmentId
            ?? scenario.EnvironmentId
            ?? await db.Environments
                .Where(candidate => candidate.ProjectId == scenario.ProjectId)
                .OrderBy(candidate => candidate.IsProduction)
                .ThenBy(candidate => candidate.SortOrder)
                .Select(candidate => (Guid?)candidate.Id)
                .FirstOrDefaultAsync(cancellation);

        var run = new TestRun
        {
            WorkspaceId = scenario.WorkspaceId,
            ProjectId = scenario.ProjectId,
            ScenarioId = scenario.Id,
            ScenarioVersionId = version.Id,
            EnvironmentId = environment,
            DefinitionJson = ScenarioGraphSnapshots.Write(graph),
            Status = RunStatus.Queued,
            Trigger = trigger,
            StartedByUserId = me.UserId,
        };

        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellation);

        return run;
    }

    /// <summary>
    /// Runs one queued run to its end, whatever that end is.
    ///
    /// Nothing in here throws to the caller. A run that fell over is a run that finished as
    /// Errored, with the reason on the row and in the log — because the alternative is a run stuck
    /// on "Running" for ever and a person refreshing a page that will never change.
    /// </summary>
    public async Task ExecuteAsync(Guid runId, CancellationToken cancellation = default)
    {
        var run = await db.Runs.FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellation);
        if (run is null) return;

        if (run.Status is not RunStatus.Queued)
        {
            logger.LogWarning("Run {RunId} is {Status}, not queued.", runId, run.Status);
            return;
        }

        run.Status = RunStatus.Running;
        run.StartedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellation);

        watchers.StatusChanged(run.Id, RunStatus.Running, new RunTotals(0, 0, 0, 0, 0, null));

        var recorder = new RunRecorder(run, db, clock, watchers);

        try
        {
            var graph = snapshots.Read(run.DefinitionJson);

            if (graph is null)
            {
                await FinishAsync(run, recorder, RunStatus.Errored,
                    new RunTotals(0, 0, 0, 0, 0, "The saved graph could not be read."));
                return;
            }

            var context = run.EnvironmentId is { } environmentId
                ? await environments.BuildAsync(environmentId, cancellation)
                : null;

            var scopes = context?.Scopes ?? new VariableScopes();
            var redaction = context?.Redaction ?? new RedactionScope();

            scopes.Run["startedAt"] = System.Text.Json.Nodes.JsonValue.Create(
                run.StartedAt?.ToString("O"));

            var services = new EngineRunServices(
                db, executor, baselines, clock,
                context?.Policy ?? new UrlPolicy(), redaction, run.ProjectId);

            var runner = new ScenarioRunner(new NodeExecutors(services), recorder);
            var running = runner.RunAsync(graph, new RunScopes(scopes, redaction), cancellation);

            // Flushed while it goes, from this thread and no other. Somebody who reloads the console
            // halfway through a twenty-minute run should find the run, not an empty page.
            while (!running.IsCompleted)
            {
                await Task.WhenAny(running, Task.Delay(FlushEvery, CancellationToken.None));
                await recorder.FlushAsync();
            }

            var summary = await running;
            await services.FinishAsync(CancellationToken.None);

            await FinishAsync(run, recorder, summary.Status, new RunTotals(
                summary.Steps, summary.StepsFailed, summary.AssertionsPassed,
                summary.AssertionsFailed, summary.DurationMs, summary.Outcome));
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(run, recorder, RunStatus.Cancelled,
                new RunTotals(0, 0, 0, 0, 0, "Stopped before it finished."));
        }
        catch (Exception ex)
        {
            // Errored, not Failed. "Your API is broken" and "our runner is broken" are different
            // news, and conflating them sends somebody looking in the wrong place.
            logger.LogError(ex, "Run {RunId} could not be carried out.", runId);

            await FinishAsync(run, recorder, RunStatus.Errored,
                new RunTotals(0, 0, 0, 0, 0, "ProofFlow could not carry this run out."));
        }
    }

    private async Task FinishAsync(
        TestRun run, RunRecorder recorder, RunStatus status, RunTotals totals)
    {
        run.Status = status;
        run.FinishedAt = clock.UtcNow;
        run.DurationMs = totals.DurationMs > 0
            ? totals.DurationMs
            : (run.FinishedAt - run.StartedAt)?.TotalMilliseconds ?? 0;

        run.StepsRun = totals.Steps;
        run.StepsFailed = totals.StepsFailed;
        run.AssertionsPassed = totals.AssertionsPassed;
        run.AssertionsFailed = totals.AssertionsFailed;
        run.Outcome = totals.Outcome;

        if (recorder.Dropped > 0)
        {
            run.Outcome = $"{run.Outcome} ({recorder.Dropped:N0} further log lines were not kept.)";
        }

        await recorder.FlushAsync();
        await db.SaveChangesAsync(CancellationToken.None);

        watchers.StatusChanged(run.Id, status, totals with { Outcome = run.Outcome });
    }
}

/// <summary>
/// The run's copy of the graph, written and read back.
///
/// The same shape the canvas saves and loads, deliberately: a second format would drift from the
/// first one field at a time, and the symptom would be old runs quietly losing a property.
/// </summary>
public sealed class ScenarioGraphSnapshots
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Write(GraphDto graph) => JsonSerializer.Serialize(graph, Json);

    public Graph? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<GraphDto>(json, Json);
            if (stored is null) return null;

            return new Graph(
                [.. stored.Nodes.Select(node => new GraphNode(
                    node.Id,
                    node.Key,
                    string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name,
                    node.Properties ?? new Dictionary<string, string?>(),
                    node.ParentId,
                    node.Disabled))],
                [.. stored.Edges.Select(edge => new GraphEdge(
                    edge.FromId, edge.FromPort, edge.ToId, edge.ToPort))]);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
