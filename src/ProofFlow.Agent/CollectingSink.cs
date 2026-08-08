using System.Text.Json.Nodes;
using ProofFlow.Contracts.Runners;
using ProofFlow.Domain.Runs;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Running;

namespace ProofFlow.Agent;

/// <summary>
/// Where a run's record goes when there is no database to put it in.
///
/// The server's sink writes rows as the run goes and flushes them so a console can watch. This one
/// keeps the same record in memory and hands it back with the report, and the server writes it.
///
/// That is the whole of the difference between a local run and a remote one. It is a transport
/// difference, not a behavioural one: the engine called the same methods in the same order, and a
/// scenario cannot tell which side it ran on.
/// </summary>
public sealed class CollectingSink : IRunSink
{
    private readonly List<JobNodeResult> _nodes = [];
    private readonly List<JobLogLine> _log = [];
    private readonly Dictionary<object, Record> _open = [];

    private long _sequence;
    private int _order;

    /// <summary>
    /// How many log lines one run may send back.
    ///
    /// A bound rather than everything, because a scenario looping over two thousand rows produces a
    /// log nobody reads and a request body nothing should have to accept. What is dropped is
    /// counted and said, the same rule the server's recorder follows.
    /// </summary>
    public const int MaxLogLines = 5_000;

    public int Dropped { get; private set; }

    public IReadOnlyList<JobNodeResult> Nodes => _nodes;

    public IReadOnlyList<JobLogLine> Lines => _log;

    public object Begin(GraphNode node, int iteration, int attempt)
    {
        var record = new Record(node, iteration, attempt, _order++);

        var handle = new object();
        _open[handle] = record;

        return handle;
    }

    public void Finish(object record, NodeRunStatus status, string? takenPort,
                       double durationMs, JsonNode? output, string? failure)
    {
        if (!_open.Remove(record, out var open)) return;

        _nodes.Add(new JobNodeResult
        {
            NodeId = open.Node.Id,
            NodeKey = open.Node.Key,
            NodeName = open.Node.Name,
            Iteration = open.Iteration,
            Attempt = open.Attempt,
            Status = status.ToString(),
            DurationMs = durationMs,
            TakenPort = takenPort,
            OutputJson = output?.ToJsonString(),
            FailureMessage = failure,
            SortOrder = open.Order,
            Assertions = open.Assertions,
        });
    }

    public void Assertion(object record, AssertionRecord assertion)
    {
        if (!_open.TryGetValue(record, out var open)) return;

        open.Assertions.Add(new JobAssertion
        {
            Description = assertion.Description,
            Passed = assertion.Passed,
            Soft = assertion.Soft,
            Target = assertion.Target,
            Expected = assertion.Expected,
            Actual = assertion.Actual,
        });
    }

    public void Log(RunEventLevel level, string message, string? nodeId, string? nodeName,
                    JsonNode? data = null)
    {
        if (_log.Count >= MaxLogLines)
        {
            Dropped++;
            return;
        }

        _log.Add(new JobLogLine
        {
            Sequence = ++_sequence,
            Level = level.ToString(),
            Message = message,
            NodeId = nodeId,
            NodeName = nodeName,
        });
    }

    /// <summary>
    /// Artefacts are kept as log lines rather than sent whole.
    ///
    /// An artefact is a response body somebody wanted to look at later, and shipping every one of
    /// them back over somebody's uplink is not what an agent is for. The line says it existed and
    /// how large it was, which is what a reader needs to know it is missing.
    /// </summary>
    public void Artifact(string name, string content, string? nodeId) =>
        Log(RunEventLevel.Info,
            $"Kept «{name}» ({content.Length:N0} characters) on the agent.", nodeId, null);

    /// <summary>One node's turn, while it is still running.</summary>
    private sealed record Record(GraphNode Node, int Iteration, int Attempt, int Order)
    {
        public List<JobAssertion> Assertions { get; } = [];
    }
}
