using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Running;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// Where a run's record goes: rows in the database, and a message to whoever is watching.
///
/// Buffered rather than written line by line. A scenario over two thousand rows produces tens of
/// thousands of log lines, and a round trip each would make the recording slower than the test —
/// the run would be measuring ProofFlow rather than the API. Lines accumulate and go in batches;
/// the live message goes out immediately, because the console is what somebody is looking at.
///
/// Nothing touches the database except <see cref="FlushAsync"/>. That is not tidiness: a parallel
/// node runs its branches at once and they all record here, and a DbContext used from two threads
/// corrupts its change tracker in ways that surface much later as a missing run.
///
/// Nothing here decides anything either. It is the engine's <see cref="IRunSink"/> and nothing
/// else, which is what lets the same engine run under a sink that keeps everything in a list inside
/// a unit test.
///
/// It is also where secrets get masked, and that placement is the point. The engine works with real
/// values — it has to, or a scenario cannot use a token it was just given — and everything that
/// leaves the run for a row, a console or a report passes through here first.
/// </summary>
public sealed class RunRecorder(
    TestRun run,
    ProofFlowDbContext db,
    IClock clock,
    IRunWatchers watchers,
    RedactionScope redaction) : IRunSink
{
    /// <summary>
    /// How many events wait before a write.
    ///
    /// Two hundred is roughly a second of a busy run: small enough that a crash loses almost
    /// nothing, large enough that the write is not the bottleneck.
    /// </summary>
    public const int BatchSize = 200;

    /// <summary>The most lines one run may store, after which they are counted and dropped.</summary>
    public const int MaxEvents = 50_000;

    private readonly ConcurrentQueue<RunEvent> _events = new();
    private readonly ConcurrentQueue<NodeRun> _finished = new();
    private readonly ConcurrentQueue<AssertionResult> _assertions = new();
    private readonly ConcurrentQueue<RunArtifact> _artifacts = new();

    private long _sequence;
    private int _stored;
    private int _dropped;
    private int _order;

    public object Begin(GraphNode node, int iteration, int attempt)
    {
        var record = new NodeRun
        {
            WorkspaceId = run.WorkspaceId,
            TestRunId = run.Id,
            NodeId = node.Id,
            NodeKey = node.Key,
            NodeName = node.Name,
            Iteration = iteration,
            Attempt = attempt,
            Status = NodeRunStatus.Running,
            StartedAt = clock.UtcNow,
        };

        record.SortOrder = Interlocked.Increment(ref _order);

        watchers.NodeChanged(run.Id, new NodeUpdate(
            node.Id, node.Name, NodeRunStatus.Running, iteration, attempt, 0, null, null));

        return record;
    }

    public void Finish(object record, NodeRunStatus status, string? takenPort,
                       double durationMs, JsonNode? output, string? failure)
    {
        var node = (NodeRun)record;

        node.Status = status;
        node.TakenPort = takenPort;
        node.DurationMs = durationMs;
        node.FinishedAt = clock.UtcNow;
        node.OutputJson = Trim(redaction.Apply(output)?.ToJsonString());
        node.FailureMessage = Hide(failure);

        // Queued at the end rather than the start: a row written while the step was still running
        // would have to be updated, and the live picture already comes from the message below.
        _finished.Enqueue(node);

        watchers.NodeChanged(run.Id, new NodeUpdate(
            node.NodeId, node.NodeName, status, node.Iteration, node.Attempt,
            durationMs, takenPort, node.FailureMessage));
    }

    public void Assertion(object record, AssertionRecord assertion)
    {
        var node = (NodeRun)record;

        // Expected and actual are both response text as often as not: an assertion that reads a
        // token field prints the token in its «expected 200, got …» line.
        _assertions.Enqueue(new AssertionResult
        {
            WorkspaceId = run.WorkspaceId,
            NodeRunId = node.Id,
            Description = Hide(assertion.Description) ?? assertion.Description,
            Passed = assertion.Passed,
            Soft = assertion.Soft,
            Expected = Trim(Hide(assertion.Expected)),
            Actual = Trim(Hide(assertion.Actual)),
            Target = assertion.Target,
        });

        watchers.AssertionRecorded(run.Id, new AssertionUpdate(
            node.NodeId, Hide(assertion.Description) ?? assertion.Description,
            assertion.Passed, assertion.Soft, assertion.Target));
    }

    public void Log(RunEventLevel level, string message, string? nodeId, string? nodeName,
                    JsonNode? data = null)
    {
        var sequence = Interlocked.Increment(ref _sequence);

        // Past the ceiling the lines stop being stored but the run carries on. A run that fell over
        // because it logged too much would be a tool that punishes the person who turned on the
        // detail they needed.
        if (Interlocked.Increment(ref _stored) > MaxEvents)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        var entry = new RunEvent
        {
            WorkspaceId = run.WorkspaceId,
            TestRunId = run.Id,
            Sequence = sequence,
            Level = level,
            Message = Hide(message) ?? message,
            NodeId = nodeId,
            NodeName = nodeName,
            At = clock.UtcNow,
            DataJson = Trim(redaction.Apply(data)?.ToJsonString()),
        };

        _events.Enqueue(entry);

        watchers.Logged(run.Id, new LogLine(
            sequence, level, entry.Message, nodeId, nodeName, entry.At, entry.DataJson));
    }

    public void Artifact(string name, string content, string? nodeId)
    {
        var hidden = Hide(content) ?? content;

        _artifacts.Enqueue(new RunArtifact
        {
            WorkspaceId = run.WorkspaceId,
            TestRunId = run.Id,
            Name = name,
            Kind = "attachment",
            ContentType = "text/plain",
            // The size of what was kept, not of what came back: the two differ once a mask is
            // shorter than the token it replaced, and the number has to describe the stored file.
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(hidden),
            Content = Trim(hidden),
        });
    }

    /// <summary>
    /// Writes what has accumulated.
    ///
    /// Called from the one thread that owns the DbContext, periodically while the run goes and once
    /// at the end. Periodically, because somebody who reloads the console halfway through a
    /// twenty-minute run should find the run rather than an empty page.
    /// </summary>
    public async Task FlushAsync()
    {
        var wrote = false;

        while (_finished.TryDequeue(out var node)) { db.NodeRuns.Add(node); wrote = true; }
        while (_assertions.TryDequeue(out var result)) { db.AssertionResults.Add(result); wrote = true; }
        while (_events.TryDequeue(out var entry)) { db.RunEvents.Add(entry); wrote = true; }
        while (_artifacts.TryDequeue(out var artifact)) { db.RunArtifacts.Add(artifact); wrote = true; }

        if (!wrote) return;

        // CancellationToken.None on purpose: this is where a cancelled run's record gets written,
        // and passing the token that cancelled it would throw the record away.
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>How many lines were produced past the ceiling, for the console to say so.</summary>
    public int Dropped => _dropped;

    /// <summary>
    /// Cuts anything oversized down to something a row can hold.
    ///
    /// A response body of several megabytes belongs in an artifact, not in a log line's data
    /// column, and a run whose console cannot be opened because one line is eight megabytes is a
    /// run nobody can read.
    /// </summary>
    private static string? Trim(string? text) =>
        text is null || text.Length <= 64 * 1024 ? text : text[..(64 * 1024)] + "…";

    /// <summary>
    /// Masks the secrets this run has seen, keeping null as null.
    ///
    /// <see cref="RedactionScope.Apply"/> turns null into an empty string, and an empty failure
    /// message is not the same thing as no failure message — one prints as a blank line under a
    /// step that passed.
    /// </summary>
    private string? Hide(string? text) => text is null ? null : redaction.Apply(text);
}

/// <summary>
/// Whoever is watching a run right now.
///
/// A port so the engine's recording does not depend on SignalR, which matters twice: the worker can
/// run with nobody watching, and a test can assert what would have been pushed without a hub.
/// </summary>
public interface IRunWatchers
{
    void NodeChanged(Guid runId, NodeUpdate update);

    void AssertionRecorded(Guid runId, AssertionUpdate update);

    void Logged(Guid runId, LogLine line);

    void StatusChanged(Guid runId, RunStatus status, RunTotals totals);
}

public sealed record NodeUpdate(
    string NodeId, string NodeName, NodeRunStatus Status, int Iteration, int Attempt,
    double DurationMs, string? TakenPort, string? Failure);

public sealed record AssertionUpdate(
    string NodeId, string Description, bool Passed, bool Soft, string? Target);

public sealed record LogLine(
    long Sequence, RunEventLevel Level, string Message, string? NodeId, string? NodeName,
    DateTimeOffset At, string? DataJson);

public sealed record RunTotals(
    int Steps, int StepsFailed, int AssertionsPassed, int AssertionsFailed,
    double DurationMs, string? Outcome);

/// <summary>Nobody is watching. What the worker uses when no browser is connected.</summary>
public sealed class NoWatchers : IRunWatchers
{
    public void NodeChanged(Guid runId, NodeUpdate update) { }

    public void AssertionRecorded(Guid runId, AssertionUpdate update) { }

    public void Logged(Guid runId, LogLine line) { }

    public void StatusChanged(Guid runId, RunStatus status, RunTotals totals) { }
}
