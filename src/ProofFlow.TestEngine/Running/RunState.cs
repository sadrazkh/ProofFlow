using System.Text.Json.Nodes;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.TestEngine.Running;

/// <summary>
/// What the runner knows while it walks: where the edges go, what each step published, and which
/// pass through which loop it is on.
///
/// Internal to the runner on purpose. Nodes cannot reach it — they get a <see cref="NodeContext"/>
/// with the few things they may do — which is what keeps a node from moving the runner.
///
/// Every mutation is behind one lock, because a parallel node runs its branches at the same time
/// and they all publish here. The lock does not make a scenario whose branches read each other's
/// steps deterministic — nothing could — but it does stop two branches from corrupting the
/// dictionary between them, which is a different and much worse failure.
/// </summary>
internal sealed class RunnerState
{
    private readonly Graph _graph;
    private readonly RunScopes _scopes;
    private readonly IRunSink _sink;
    private readonly Lock _gate = new();

    /// <summary>Outgoing edges by (node, port), which is what following a branch is.</summary>
    private readonly Dictionary<(string Node, string Port), string> _control;

    /// <summary>Data edges by target, so a node's inputs can be gathered without a scan.</summary>
    private readonly Dictionary<string, List<(string Port, string FromNode, string FromPort)>> _data;

    /// <summary>What each node published last time it ran, by socket.</summary>
    private readonly Dictionary<string, Dictionary<string, JsonNode?>> _published =
        new(StringComparer.Ordinal);

    private int _steps;
    private int _stepsFailed;
    private int _assertionsPassed;
    private int _assertionsFailed;

    internal RunnerState(Graph graph, RunScopes scopes, IRunSink sink)
    {
        _graph = graph;
        _scopes = scopes;
        _sink = sink;

        ById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);

        _control = new Dictionary<(string, string), string>();
        _data = new Dictionary<string, List<(string, string, string)>>(StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            var spec = ById.TryGetValue(edge.FromId, out var from) ? NodeCatalogue.Find(from.Key) : null;
            var port = spec?.Outputs.FirstOrDefault(p => p.Name == edge.FromPort);

            if (port?.Kind == PortKind.Data)
            {
                if (!_data.TryGetValue(edge.ToId, out var list))
                {
                    list = [];
                    _data[edge.ToId] = list;
                }

                list.Add((edge.ToPort, edge.FromId, edge.FromPort));
                continue;
            }

            // First edge wins. Two edges from one port is a graph the validator refuses, and
            // silently taking the second would make which one depend on insertion order.
            _control.TryAdd((edge.FromId, edge.FromPort), edge.ToId);
        }
    }

    public IReadOnlyDictionary<string, GraphNode> ById { get; }

    public int Steps => _steps;
    public int StepsFailed => _stepsFailed;
    public int AssertionsPassed => _assertionsPassed;
    public int AssertionsFailed => _assertionsFailed;

    public void CountStep() => Interlocked.Increment(ref _steps);

    public void CountFailedStep()
    {
        if (_inverting > 0) return;

        lock (_gate)
        {
            if (_aside is { } aside)
            {
                aside.StepsFailed++;
                return;
            }
        }

        Interlocked.Increment(ref _stepsFailed);
    }

    public void CountAssertion(bool passed)
    {
        // Inside an "expect failure" block the checks are still recorded and still shown — they
        // simply do not decide the run, because the failure is the thing being proved.
        if (_inverting > 0) return;

        lock (_gate)
        {
            if (_aside is { } aside)
            {
                if (passed) aside.Passed++;
                else aside.Failed++;
                return;
            }
        }

        if (passed) Interlocked.Increment(ref _assertionsPassed);
        else Interlocked.Increment(ref _assertionsFailed);
    }

    private int _inverting;
    private Tally? _aside;

    /// <summary>While this is held, what fails inside does not count against the run.</summary>
    public IDisposable Inverting()
    {
        Interlocked.Increment(ref _inverting);
        return new Restore(() => Interlocked.Decrement(ref _inverting));
    }

    /// <summary>
    /// While this is held, checks are counted into <paramref name="tally"/> rather than the run.
    ///
    /// For a retry, where an attempt that a later one supersedes should stay visible without
    /// deciding anything. Nothing is lost: the sink already has every assertion record.
    /// </summary>
    public IDisposable Aside(Tally tally)
    {
        Tally? previous;
        lock (_gate)
        {
            previous = _aside;
            _aside = tally;
        }

        return new Restore(() =>
        {
            lock (_gate) _aside = previous;
        });
    }

    /// <summary>Adds a held-aside tally to whatever is counting now.</summary>
    public void Absorb(Tally tally)
    {
        lock (_gate)
        {
            if (_aside is { } outer)
            {
                outer.Passed += tally.Passed;
                outer.Failed += tally.Failed;
                outer.StepsFailed += tally.StepsFailed;
                return;
            }
        }

        Interlocked.Add(ref _assertionsPassed, tally.Passed);
        Interlocked.Add(ref _assertionsFailed, tally.Failed);
        Interlocked.Add(ref _stepsFailed, tally.StepsFailed);
    }

    /// <summary>The rate limit in force, when a step is inside one.</summary>
    public Pacer? Pace { get; set; }

    /// <summary>Set by a terminal node. Stops the walk without unwinding through exceptions.</summary>
    public bool Stopped { get; set; }

    public string? Outcome { get; set; }

    /// <summary>
    /// A <c>flow.break</c> was reached: the walk unwinds to the nearest loop, which stops.
    ///
    /// A flag rather than an exception because breaking out of a loop is ordinary control flow, and
    /// paying for a stack unwind on every pass of a two-thousand-row loop is not.
    /// </summary>
    public bool Breaking { get; set; }

    /// <summary>A <c>flow.continue</c> was reached: this pass ends, the next one starts.</summary>
    public bool Continuing { get; set; }

    /// <summary>
    /// Where a parallel branch stops.
    ///
    /// Set while the branches of a <c>core.parallel</c> run; the walk halts when it reaches the join
    /// so that the steps after the join run once, not once per branch.
    /// </summary>
    public string? JoinAt { get; set; }

    public int Iteration { get; private set; }

    public int Attempt { get; private set; } = 1;

    /// <summary>The current row's key inside a data-set loop. What a captured sample is filed under.</summary>
    public string? LoopKey { get; private set; }

    /// <summary>Cleanup blocks passed on the way through, run in reverse at the end.</summary>
    public List<string> Cleanups { get; } = [];

    public string? Next(string nodeId, string port)
    {
        // No lock: the edge maps are built in the constructor and never written again.
        return _control.GetValueOrDefault((nodeId, port));
    }

    /// <summary>
    /// The first node inside a container.
    ///
    /// Contents are found by their parent rather than by an edge, which is what stops an edge from
    /// jumping into the middle of a loop body.
    /// </summary>
    public string? FirstInside(string containerId)
    {
        var children = _graph.Nodes.Where(node => node.ParentId == containerId).ToArray();
        if (children.Length == 0) return null;

        // The one nothing else inside points at. A body whose steps form a chain has exactly one.
        var targets = _graph.Edges
            .Where(edge => children.Any(child => child.Id == edge.ToId))
            .Select(edge => edge.ToId)
            .ToHashSet(StringComparer.Ordinal);

        return (children.FirstOrDefault(child => !targets.Contains(child.Id)) ?? children[0]).Id;
    }

    /// <summary>Every node inside a container, however deep. Used to find a parallel branch's join.</summary>
    public IEnumerable<GraphNode> Inside(string containerId) =>
        _graph.Nodes.Where(node => node.ParentId == containerId);

    public IEnumerable<GraphNode> Nodes => _graph.Nodes;

    /// <summary>Records what a node produced, and makes it visible to <c>{{steps.name.…}}</c>.</summary>
    public void Publish(GraphNode node, IReadOnlyDictionary<string, JsonNode?> published)
    {
        lock (_gate)
        {
            if (!_published.TryGetValue(node.Id, out var sockets))
            {
                sockets = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                _published[node.Id] = sockets;
            }

            var byName = new JsonObject();

            foreach (var (port, value) in published)
            {
                sockets[port] = value;
                if (value is not null) byName[port] = value.DeepClone();
            }

            // Published under the step's name, which is what references point at. A step called
            // "login" makes {{steps.login.response.token}} work from anywhere after it.
            _scopes.Scopes.Steps[node.Name] = byName;
        }
    }

    /// <summary>What is plugged into a node's sockets right now.</summary>
    public IReadOnlyDictionary<string, JsonNode?> InputsFor(string nodeId)
    {
        var inputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        if (!_data.TryGetValue(nodeId, out var edges)) return inputs;

        lock (_gate)
        {
            foreach (var (port, fromNode, fromPort) in edges)
            {
                if (_published.TryGetValue(fromNode, out var sockets)
                    && sockets.TryGetValue(fromPort, out var value))
                {
                    inputs[port] = value?.DeepClone();
                }
            }
        }

        return inputs;
    }

    /// <summary>What one node published, for the record of its turn.</summary>
    public JsonNode? Snapshot(string nodeId)
    {
        lock (_gate)
        {
            return _published.TryGetValue(nodeId, out var sockets) && sockets.Count > 0
                ? new JsonObject(sockets.Select(pair =>
                    new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone())))
                : null;
        }
    }

    public VariableResolver Resolver()
    {
        lock (_gate) return new VariableResolver(_scopes.Scopes, _scopes.Redaction);
    }

    /// <summary>
    /// Adds a value to what gets redacted out of logs and stored bodies.
    ///
    /// Every auth node calls this with the token it minted. A token that a scenario fetched at run
    /// time was never typed into a secret box, so nothing else would know to hide it — and the run
    /// log is the most-forwarded artefact this product makes.
    /// </summary>
    public void Remember(string? secret)
    {
        lock (_gate) _scopes.Redaction.Remember(secret);
    }

    /// <summary>What has been remembered, so the sink can hide it in what it stores.</summary>
    public string Redact(string? text)
    {
        lock (_gate) return _scopes.Redaction.Apply(text);
    }

    /// <summary>Sets a run variable, which is what <c>core.setVariable</c> does.</summary>
    public void SetVariable(string name, JsonNode? value)
    {
        lock (_gate) _scopes.Scopes.Variables[name] = value?.DeepClone();
    }

    /// <summary>
    /// Enters one pass of a loop, publishing its index and item under <c>{{loop.…}}</c>.
    ///
    /// Restored on the way out rather than left set, so a step after the loop does not read the
    /// last iteration's values as though they were current.
    /// </summary>
    public IDisposable Iterate(int index, JsonObject values, string? key = null, JsonNode? row = null)
    {
        var previousIteration = Iteration;
        var previousKey = LoopKey;

        JsonNode? previousLoop;
        JsonNode? previousRow;

        lock (_gate)
        {
            previousLoop = _scopes.Scopes.Run["loop"]?.DeepClone();
            previousRow = _scopes.Scopes.Dataset["current"]?.DeepClone();

            _scopes.Scopes.Run["loop"] = values;
            if (row is not null) _scopes.Scopes.Dataset["current"] = row.DeepClone();
        }

        Iteration = index;
        LoopKey = key ?? previousKey;

        return new Restore(() =>
        {
            Iteration = previousIteration;
            LoopKey = previousKey;

            lock (_gate)
            {
                _scopes.Scopes.Run["loop"] = previousLoop;
                _scopes.Scopes.Dataset["current"] = previousRow;
            }
        });
    }

    public IDisposable Attempting(int attempt)
    {
        var previous = Attempt;
        Attempt = attempt;
        return new Restore(() => Attempt = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public IRunSink Sink => _sink;
}

/// <summary>What one attempt amounted to, before anyone decides whether it counts.</summary>
internal sealed class Tally
{
    public int Passed;
    public int Failed;
    public int StepsFailed;
}
