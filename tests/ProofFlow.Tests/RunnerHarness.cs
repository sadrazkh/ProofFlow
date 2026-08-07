using System.Text.Json.Nodes;
using ProofFlow.Domain.Runs;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Running;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.Tests;

/// <summary>
/// A graph written the way a test wants to write one, and a sink that keeps everything.
///
/// The runner takes ports for the two things it cannot supply itself — where the record goes and
/// where requests go — which is exactly what makes it testable without a database or a network.
/// </summary>
internal sealed class GraphBuilder
{
    private readonly List<GraphNode> _nodes = [];
    private readonly List<GraphEdge> _edges = [];

    public GraphBuilder Node(string id, string key, string? parent = null,
                             bool disabled = false, params (string Name, string? Value)[] properties)
    {
        _nodes.Add(new GraphNode(id, key, id,
            properties.ToDictionary(p => p.Name, p => p.Value), parent, disabled));

        return this;
    }

    public GraphBuilder Edge(string from, string to, string fromPort = "out", string toPort = "in")
    {
        _edges.Add(new GraphEdge(from, fromPort, to, toPort));
        return this;
    }

    /// <summary>A straight line of steps from a start node, which most scenarios are.</summary>
    public GraphBuilder Chain(params string[] ids)
    {
        for (var index = 0; index < ids.Length - 1; index++) Edge(ids[index], ids[index + 1]);
        return this;
    }

    public Graph Build() => new(_nodes, _edges);
}

/// <summary>A sink that remembers, so a test can ask what the run recorded.</summary>
internal sealed class RecordingSink : IRunSink
{
    private int _next;

    public List<(string Node, int Iteration, int Attempt)> Started { get; } = [];
    public List<(string Node, NodeRunStatus Status, string? Port, string? Failure)> Finished { get; } = [];
    public List<AssertionRecord> Assertions { get; } = [];
    public List<(RunEventLevel Level, string Message, string? Node)> Logs { get; } = [];

    public object Begin(GraphNode node, int iteration, int attempt)
    {
        Started.Add((node.Name, iteration, attempt));
        return new Handle(_next++, node.Name);
    }

    public void Finish(object record, NodeRunStatus status, string? takenPort,
                       double durationMs, JsonNode? output, string? failure)
    {
        Finished.Add((((Handle)record).Name, status, takenPort, failure));
    }

    public void Assertion(object record, AssertionRecord assertion) => Assertions.Add(assertion);

    public void Log(RunEventLevel level, string message, string? nodeId, string? nodeName,
                    JsonNode? data = null)
    {
        Logs.Add((level, message, nodeName));
    }

    public void Artifact(string name, string content, string? nodeId) =>
        Artifacts.Add((name, content));

    public List<(string Name, string Content)> Artifacts { get; } = [];

    /// <summary>The order the nodes ran in, which is what most of these tests are about.</summary>
    public IReadOnlyList<string> Order => [.. Finished.Select(entry => entry.Node)];

    private sealed record Handle(int Index, string Name);
}

/// <summary>
/// Stand-in services: a canned answer per URL, and a note of every request made.
///
/// Not a mock of the HTTP stack — the real one is exercised in <see cref="RunServiceTests"/> against
/// the FakeApi. This is here so a test about looping is a test about looping.
/// </summary>
internal sealed class StubServices : IRunServices
{
    private readonly Queue<HttpNodeResult> _queued = new();

    public List<HttpNodeRequest> Requests { get; } = [];
    public List<JsonNode> Rows { get; } = [];
    public Dictionary<string, BaselineAnswer> Baselines { get; } = [];

    public HttpNodeResult Default { get; set; } = Response(200, "{\"ok\":true}");

    public StubServices Then(HttpNodeResult result)
    {
        _queued.Enqueue(result);
        return this;
    }

    public static HttpNodeResult Response(int status, string body, params (string, string)[] headers) =>
        new(true, status, status == 200 ? "OK" : "Error", headers, body, "application/json", 4, null,
            "https://example.test/");

    public Task<HttpNodeResult> SendAsync(HttpNodeRequest request, CancellationToken cancellation)
    {
        Requests.Add(request);
        return Task.FromResult(_queued.Count > 0 ? _queued.Dequeue() : Default);
    }

    public Task<IReadOnlyList<JsonNode>> DataSetRowsAsync(string reference, CancellationToken cancellation) =>
        Task.FromResult<IReadOnlyList<JsonNode>>([.. Rows]);

    public Task<BaselineAnswer?> BaselineAsync(string reference, string? key, CancellationToken cancellation) =>
        Task.FromResult(Baselines.GetValueOrDefault(key ?? reference));

    public List<(string Reference, string? Key, CapturedAnswer Answer, bool Approve)> Captured { get; } = [];

    public Task CaptureBaselineAsync(string reference, string? key, CapturedAnswer answer, bool approve,
                                     CancellationToken cancellation)
    {
        Captured.Add((reference, key, answer, approve));
        return Task.CompletedTask;
    }
}

internal static class Harness
{
    public static (ScenarioRunner Runner, RecordingSink Sink, StubServices Services) Build()
    {
        var services = new StubServices();
        var sink = new RecordingSink();
        return (new ScenarioRunner(new NodeExecutors(services), sink), sink, services);
    }

    public static RunScopes Scopes(JsonObject? variables = null) =>
        new(new VariableScopes { Variables = variables ?? [] }, new RedactionScope());
}
