using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Runners;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Notifications;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;

namespace ProofFlow.Infrastructure.Runners;

/// <summary>
/// Turns a queued run into the package an agent needs, and turns an agent's report back into rows.
///
/// The packing side reads the graph to decide what to include. A scenario that names two data sets
/// gets two data sets; one that names none gets none. That is worth the walk: an agent lives on
/// somebody's internal network, and the less of an installation is copied onto it, the less there is
/// to lose if that machine is the one that gets compromised.
/// </summary>
public sealed class JobPackaging(
    ProofFlowDbContext db,
    EnvironmentContextBuilder environments,
    BaselineService baselines,
    IRunWatchers watchers,
    IClock clock,
    NotificationWriter? notifications = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The node properties that name a data set, and the ones that name a baseline.</summary>
    private static readonly string[] DataSetProperties = ["dataSet"];

    private static readonly string[] BaselineProperties = ["baseline"];

    public async Task<JobPackage> PackAsync(TestRun run, CancellationToken cancellation = default)
    {
        var graph = Read(run.DefinitionJson);

        var scenarioName = await db.Scenarios
            .Where(scenario => scenario.Id == run.ScenarioId)
            .Select(scenario => scenario.Name)
            .FirstOrDefaultAsync(cancellation) ?? "scenario";

        JobEnvironment? environment = null;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        if (run.EnvironmentId is { } environmentId)
        {
            var row = await db.Environments
                .FirstOrDefaultAsync(candidate => candidate.Id == environmentId, cancellation);

            if (row is not null)
            {
                environment = new JobEnvironment
                {
                    Name = row.Name,
                    BaseUrl = row.BaseUrl,
                    TimeoutSeconds = row.TimeoutSeconds,
                    MaxRedirects = row.MaxRedirects,
                    MaxResponseKilobytes = row.MaxResponseKilobytes,
                    AllowedHosts = row.AllowedHosts,
                    AllowPrivateNetwork = row.AllowPrivateNetwork,
                    AllowInvalidCertificate = row.AllowInvalidCertificate,
                    DefaultHeadersJson = row.DefaultHeadersJson,

                    // How to sign in, not a token. The agent does it itself, for the same reason
                    // it validates the graph itself: what travels is the instruction, and the
                    // machine that carries it out is the one that will be blamed if it fails.
                    AuthenticationJson = row.AuthenticationJson,
                };

                // Resolved here because this is where the cipher's master key is. The agent gets
                // values it can use for this job and never a key it could open anything else with.
                var context = await environments.BuildAsync(row, cancellation);

                foreach (var (name, value) in context.Scopes.Variables)
                {
                    if (value is not null) variables[name] = value.ToString();
                }

                foreach (var (name, value) in context.Scopes.Secrets)
                {
                    if (value is not null) secrets[name] = value.ToString();
                }
            }
        }

        return new JobPackage
        {
            RunId = run.Id,
            ScenarioName = scenarioName,
            Definition = run.DefinitionJson ?? "{}",
            Environment = environment,
            Variables = variables,
            Secrets = secrets,
            DataSets = await DataSetsAsync(run.ProjectId, Referenced(graph, DataSetProperties), cancellation),
            Baselines = await BaselinesAsync(run.ProjectId, Referenced(graph, BaselineProperties), cancellation),
            StartNodeId = run.StartNodeId,
            Inputs = ScenarioInputs.ReadValues(run.InputsJson),
        };
    }

    /// <summary>
    /// Files an agent's report: the verdict, the steps, the log, and anything it captured.
    ///
    /// Written in one transaction and only for a run that is still open, so a duplicate report from
    /// an agent that retried after a timeout does not produce a second set of rows.
    /// </summary>
    public async Task<bool> RecordAsync(
        Domain.Runners.Runner runner, JobReport report, CancellationToken cancellation = default)
    {
        var run = await db.Runs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Id == report.JobId
                                              && candidate.RunnerId == runner.Id, cancellation);

        if (run is null) return false;
        if (run.Status is not (RunStatus.Queued or RunStatus.Running)) return true;

        var order = 0;

        foreach (var node in report.Nodes.OrderBy(node => node.SortOrder))
        {
            var row = new NodeRun
            {
                WorkspaceId = run.WorkspaceId,
                TestRunId = run.Id,
                NodeId = node.NodeId,
                NodeKey = node.NodeKey,
                NodeName = node.NodeName,
                Iteration = node.Iteration,
                Attempt = node.Attempt,
                // Failed rather than a guess when the agent reports a word this does not know. A
                // step whose state cannot be read is not a step that passed.
                Status = Enum.TryParse<NodeRunStatus>(node.Status, out var status)
                    ? status
                    : NodeRunStatus.Failed,
                StartedAt = run.ClaimedAt ?? clock.UtcNow,
                FinishedAt = clock.UtcNow,
                DurationMs = node.DurationMs,
                TakenPort = node.TakenPort,
                OutputJson = node.OutputJson,
                FailureMessage = node.FailureMessage,
                SortOrder = order++,
            };

            db.NodeRuns.Add(row);

            foreach (var assertion in node.Assertions)
            {
                db.AssertionResults.Add(new AssertionResult
                {
                    WorkspaceId = run.WorkspaceId,
                    NodeRun = row,
                    Description = assertion.Description,
                    Passed = assertion.Passed,
                    Soft = assertion.Soft,
                    Target = assertion.Target,
                    Expected = assertion.Expected,
                    Actual = assertion.Actual,
                });
            }
        }

        foreach (var line in report.Log)
        {
            db.RunEvents.Add(new RunEvent
            {
                WorkspaceId = run.WorkspaceId,
                TestRunId = run.Id,
                Sequence = line.Sequence,
                Level = Enum.TryParse<RunEventLevel>(line.Level, out var level)
                    ? level
                    : RunEventLevel.Info,
                Message = line.Message,
                NodeId = line.NodeId,
                NodeName = line.NodeName,
                At = clock.UtcNow,
            });
        }

        await CaptureAsync(run, report, cancellation);

        run.Status = Enum.TryParse<RunStatus>(report.Status, ignoreCase: true, out var verdict)
                     && verdict is RunStatus.Passed or RunStatus.Failed
                         or RunStatus.Errored or RunStatus.Cancelled
            ? verdict
            : RunStatus.Errored;

        run.StartedAt ??= run.ClaimedAt ?? clock.UtcNow;
        run.FinishedAt = clock.UtcNow;
        run.DurationMs = report.DurationMs;
        run.StepsRun = report.Steps;
        run.StepsFailed = report.StepsFailed;
        run.AssertionsPassed = report.AssertionsPassed;
        run.AssertionsFailed = report.AssertionsFailed;
        run.Outcome = report.Outcome;

        // The same rule as a local run: the failure and its notification are one save. The agent
        // reports once, at the end, and this is that moment.
        if (notifications is not null && run.Status is RunStatus.Failed or RunStatus.Errored)
        {
            notifications.RunFailed(
                run,
                await db.Scenarios.Where(s => s.Id == run.ScenarioId)
                    .Select(s => s.Name).FirstOrDefaultAsync(cancellation),
                run.EnvironmentId is { } environmentId
                    ? await db.Environments.Where(e => e.Id == environmentId)
                        .Select(e => e.Name).FirstOrDefaultAsync(cancellation)
                    : null);
        }

        await db.SaveChangesAsync(cancellation);

        // Tell whoever is watching, through the same channel a local run uses.
        //
        // Without this the console is simply wrong for every remote run. It subscribes over the
        // socket and only falls back to polling when the socket will not open, so on a healthy
        // connection nothing ever arrives: the agent is not connected to the hub, and the server
        // wrote these rows without saying so. The page sits on «Queued» while the database says
        // Passed, for ever, until somebody reloads.
        //
        // One message rather than a replay of the run. An agent reports once, at the end — there is
        // no live stream from inside somebody else's network, and pretending otherwise by dribbling
        // out the recorded steps would be a story about a run that had already finished. The status
        // change is what the reader is waiting for, and the console reads the detail when it lands.
        watchers.StatusChanged(run.Id, run.Status, new RunTotals(
            report.Steps, report.StepsFailed, report.AssertionsPassed, report.AssertionsFailed,
            report.DurationMs, report.Outcome));

        return true;
    }

    /// <summary>
    /// Files what the run captured, into the same review queue a local run would have used.
    ///
    /// Nothing is approved here that the scenario did not ask to approve — the same rule the local
    /// implementation follows, because a capture that approves itself is a test that can never fail.
    /// </summary>
    private async Task CaptureAsync(TestRun run, JobReport report, CancellationToken cancellation)
    {
        if (report.Captures.Count == 0) return;

        CaptureSession? session = null;

        foreach (var capture in report.Captures)
        {
            var baseline = await FindBaselineAsync(run.ProjectId, capture.Baseline, cancellation);
            if (baseline is null) continue;

            if (string.IsNullOrWhiteSpace(capture.Key))
            {
                var version = await baselines.CaptureAsync(
                    baseline, capture.Body, capture.ContentType, capture.StatusCode, null, cancellation);

                if (capture.Approve) await baselines.ApproveAsync(version, cancellation);
                continue;
            }

            session ??= Session(run, baseline);

            db.CaptureSamples.Add(new CaptureSample
            {
                WorkspaceId = run.WorkspaceId,
                CaptureSessionId = session.Id,
                Key = capture.Key,
                Ordinal = session.Completed,
                Status = SampleStatus.Captured,
                ResolvedUrl = capture.Url,
                StatusCode = capture.StatusCode,
                ContentType = capture.ContentType,
                Body = capture.Body,
                DurationMs = capture.DurationMs,
            });

            session.Completed++;
            session.TotalRows = session.Completed;
        }

        if (session is not null)
        {
            session.Status = CaptureSessionStatus.Completed;
            session.FinishedAt = clock.UtcNow;
        }
    }

    private CaptureSession Session(TestRun run, Baseline baseline)
    {
        var session = new CaptureSession
        {
            WorkspaceId = run.WorkspaceId,
            ProjectId = run.ProjectId,
            BaselineId = baseline.Id,
            Mode = CaptureMode.Capture,
            Status = CaptureSessionStatus.Running,
            StartedAt = clock.UtcNow,
        };

        db.CaptureSessions.Add(session);

        return session;
    }

    private Task<Baseline?> FindBaselineAsync(
        Guid projectId, string reference, CancellationToken cancellation)
    {
        var query = db.Baselines.Where(baseline => baseline.ProjectId == projectId);

        query = Guid.TryParse(reference, out var id)
            ? query.Where(baseline => baseline.Id == id)
            : query.Where(baseline => baseline.Name == reference);

        return query.FirstOrDefaultAsync(cancellation);
    }

    // ---- what the graph names ------------------------------------------------------------------

    /// <summary>
    /// The values a graph puts in the given properties.
    ///
    /// Read off the snapshot rather than tracked separately, so a scenario that starts referring to
    /// a new data set tomorrow packs it without anybody remembering to update a list.
    /// </summary>
    private static IReadOnlyList<string> Referenced(GraphDto graph, string[] properties) =>
    [
        .. graph.Nodes
            .SelectMany(node => properties
                .Select(property => node.Properties.GetValueOrDefault(property)))
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal),
    ];

    private async Task<IReadOnlyList<JobDataSet>> DataSetsAsync(
        Guid projectId, IReadOnlyList<string> references, CancellationToken cancellation)
    {
        if (references.Count == 0) return [];

        var packed = new List<JobDataSet>(references.Count);

        foreach (var reference in references)
        {
            var query = db.DataSets.Where(set => set.ProjectId == projectId);

            query = Guid.TryParse(reference, out var id)
                ? query.Where(set => set.Id == id)
                : query.Where(set => set.Name == reference);

            var set = await query.FirstOrDefaultAsync(cancellation);
            if (set is null) continue;

            var version = await db.DataSetVersions
                .Where(candidate => candidate.DataSetId == set.Id)
                .OrderByDescending(candidate => candidate.Number)
                .FirstOrDefaultAsync(cancellation);

            var rows = version is null
                ? []
                : await db.DataSetRows
                    .Where(row => row.DataSetVersionId == version.Id && row.Enabled)
                    .OrderBy(row => row.Ordinal)
                    .Select(row => row.ValuesJson)
                    .ToListAsync(cancellation);

            packed.Add(new JobDataSet { Name = set.Name, Id = set.Id, Rows = rows });
        }

        return packed;
    }

    private async Task<IReadOnlyList<JobBaseline>> BaselinesAsync(
        Guid projectId, IReadOnlyList<string> references, CancellationToken cancellation)
    {
        if (references.Count == 0) return [];

        var packed = new List<JobBaseline>(references.Count);

        foreach (var reference in references)
        {
            var baseline = await FindBaselineAsync(projectId, reference, cancellation);
            if (baseline is null) continue;

            var version = await baselines.ApprovedVersionAsync(baseline.Id, cancellation);
            var rules = await baselines.LoadRulesAsync(baseline.Id, cancellation);

            // No approval filter, because approving is what writes the row — the same reason the
            // server's own lookup does not filter either. A condition here would read as though
            // unapproved samples existed and were being excluded, which would be a lie about how
            // the table works.
            var samples = await db.BaselineSamples
                .Where(sample => sample.BaselineId == baseline.Id)
                .ToDictionaryAsync(sample => sample.Key, sample => sample.Body, cancellation);

            packed.Add(new JobBaseline
            {
                Name = baseline.Name,
                Id = baseline.Id,
                ApprovedBody = version?.Body,
                RulesJson = JsonSerializer.Serialize(rules, Json),
                Samples = samples,
            });
        }

        return packed;
    }

    private static GraphDto Read(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return new GraphDto { Nodes = [], Edges = [] };

        try
        {
            return JsonSerializer.Deserialize<GraphDto>(definition, Json)
                   ?? new GraphDto { Nodes = [], Edges = [] };
        }
        catch (JsonException)
        {
            return new GraphDto { Nodes = [], Edges = [] };
        }
    }
}
