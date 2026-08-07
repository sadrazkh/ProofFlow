namespace ProofFlow.TestEngine.Nodes;

/// <summary>
/// A graph as the validator sees it: no database, no canvas, just nodes and edges.
///
/// Deliberately its own shape rather than the entities. The validator runs on the server before a
/// save and in the browser while somebody drags an edge, and the second of those has no entities —
/// what it has is what the canvas holds.
/// </summary>
public sealed record GraphNode(
    string Id,
    string Key,
    string Name,
    IReadOnlyDictionary<string, string?> Properties,
    string? ParentId = null,
    bool Disabled = false);

public sealed record GraphEdge(string FromId, string FromPort, string ToId, string ToPort);

public sealed record Graph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// Why a graph cannot run.
///
/// A code and its arguments rather than a sentence. The engine knows nothing about languages —
/// nothing in this assembly does — and a validator that returned English prose would put English
/// on a Persian canvas, which is the exact failure the whole localisation effort exists to stop.
/// The web layer turns <paramref name="Code"/> into <c>graphProblem.{code}</c> and fills in
/// <paramref name="Arguments"/>.
///
/// <paramref name="NodeId"/> is what lets the canvas put the message on the node rather than in a
/// list at the bottom, which is the difference between "something is wrong" and "this box is
/// missing its address".
/// </summary>
public sealed record GraphProblem(
    GraphSeverity Severity,
    string Code,
    IReadOnlyList<string> Arguments,
    string? NodeId = null,
    string? Port = null,
    string? Property = null)
{
    public GraphProblem(GraphSeverity severity, string code, string? nodeId = null,
                        string? port = null, string? property = null)
        : this(severity, code, [], nodeId, port, property) { }
}

public enum GraphSeverity
{
    /// <summary>Worth knowing. The graph still runs.</summary>
    Warning = 1,

    /// <summary>The graph cannot run until this is fixed.</summary>
    Error = 2,
}

/// <summary>
/// Whether a drawing is a test yet.
///
/// Everything here is a mistake that is invisible on the canvas: a step with no address, an edge
/// from a failure port into the loop it was supposed to escape, a name used twice so
/// <c>{{steps.login.…}}</c> means either of two things. The canvas is happy to draw all of them.
///
/// Messages are written for the person the brief names — somebody who is not a programmer — so
/// they say what to do and not what rule was broken. "This step has no address to send to" rather
/// than "required property 'url' missing on node http.request".
/// </summary>
public static class GraphValidator
{
    public static IReadOnlyList<GraphProblem> Validate(Graph graph)
    {
        var problems = new List<GraphProblem>();

        var byId = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes) byId[node.Id] = node;

        CheckStart(graph, problems);
        CheckNames(graph, problems);
        CheckNodes(graph, problems);
        CheckEdges(graph, byId, problems);
        CheckReachability(graph, byId, problems);
        CheckCycles(graph, byId, problems);

        return problems;
    }

    /// <summary>Exactly one start. None means nothing runs; two means the runner has to choose.</summary>
    private static void CheckStart(Graph graph, List<GraphProblem> problems)
    {
        var starts = graph.Nodes
            .Where(node => NodeCatalogue.Find(node.Key)?.IsStart == true)
            .ToArray();

        if (starts.Length == 0 && graph.Nodes.Count > 0)
        {
            problems.Add(new(GraphSeverity.Error, "noStart"));
        }

        foreach (var extra in starts.Skip(1))
        {
            problems.Add(new(GraphSeverity.Error, "twoStarts", extra.Id));
        }
    }

    /// <summary>
    /// Names are unique, because they are what references point at.
    ///
    /// Two steps called "login" make <c>{{steps.login.response}}</c> mean either of two things, and
    /// it would resolve to whichever the runner reached first — a test that passes for a reason
    /// nobody chose.
    /// </summary>
    private static void CheckNames(Graph graph, List<GraphProblem> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Name))
            {
                problems.Add(new(GraphSeverity.Error, "noName", node.Id));
                continue;
            }

            if (!seen.Add(node.Name))
            {
                problems.Add(new(GraphSeverity.Error, "duplicateName", [node.Name], node.Id));
            }
        }
    }

    private static void CheckNodes(Graph graph, List<GraphProblem> problems)
    {
        foreach (var node in graph.Nodes)
        {
            var spec = NodeCatalogue.Find(node.Key);

            if (spec is null)
            {
                problems.Add(new(GraphSeverity.Error, "unknownType", [node.Key], node.Id));
                continue;
            }

            if (node.Disabled) continue;

            foreach (var property in spec.Properties.Where(p => p.Required))
            {
                // Only when the property is actually being asked for: a body is required for a
                // POST and absent from the form for a GET, and demanding it either way is how a
                // validator teaches people to ignore it.
                if (!IsVisible(property, node)) continue;

                node.Properties.TryGetValue(property.Name, out var value);

                if (string.IsNullOrWhiteSpace(value))
                {
                    problems.Add(new(GraphSeverity.Error, "missingProperty",
                        [node.Name], node.Id, null, property.Name));
                }
            }
        }
    }

    private static bool IsVisible(PropertySpec property, GraphNode node)
    {
        if (property.VisibleWhen is not { } condition) return true;

        node.Properties.TryGetValue(condition.Property, out var current);
        return current is not null && condition.Values.Contains(current, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every edge joins two ports that exist and agree about what they carry.
    ///
    /// The type check is the one the canvas runs while an edge is being dragged, so a mismatch is
    /// refused before the drop rather than reported afterwards.
    /// </summary>
    private static void CheckEdges(
        Graph graph, Dictionary<string, GraphNode> byId, List<GraphProblem> problems)
    {
        foreach (var edge in graph.Edges)
        {
            if (!byId.TryGetValue(edge.FromId, out var from) || !byId.TryGetValue(edge.ToId, out var to))
            {
                problems.Add(new(GraphSeverity.Error, "danglingEdge"));
                continue;
            }

            var fromSpec = NodeCatalogue.Find(from.Key);
            var toSpec = NodeCatalogue.Find(to.Key);
            if (fromSpec is null || toSpec is null) continue;

            var output = fromSpec.Outputs.FirstOrDefault(p => p.Name == edge.FromPort);
            var input = toSpec.Inputs.FirstOrDefault(p => p.Name == edge.ToPort);

            if (output is null || input is null)
            {
                problems.Add(new(GraphSeverity.Error, "unknownPort", [from.Name, to.Name], to.Id));
                continue;
            }

            if (output.Kind != input.Kind)
            {
                problems.Add(new(GraphSeverity.Error, "portKindMismatch",
                    [from.Name, to.Name], to.Id, edge.ToPort));
                continue;
            }

            if (output.Kind == PortKind.Data && !NodeCatalogue.Accepts(input.Type, output.Type))
            {
                problems.Add(new(GraphSeverity.Error, "typeMismatch",
                    [from.Name, output.Type.ToString(), to.Name, input.Type.ToString()],
                    to.Id, edge.ToPort));
            }
        }

        CheckRequiredInputs(graph, byId, problems);
    }

    private static void CheckRequiredInputs(
        Graph graph, Dictionary<string, GraphNode> byId, List<GraphProblem> problems)
    {
        var connected = graph.Edges
            .Select(edge => (edge.ToId, edge.ToPort))
            .ToHashSet();

        foreach (var node in graph.Nodes.Where(n => !n.Disabled))
        {
            var spec = NodeCatalogue.Find(node.Key);
            if (spec is null) continue;

            foreach (var port in spec.Inputs.Where(p => p.Required))
            {
                if (connected.Contains((node.Id, port.Name))) continue;

                problems.Add(new(GraphSeverity.Error, "missingInput", [node.Name], node.Id, port.Name));
            }
        }

        _ = byId;
    }

    /// <summary>
    /// Steps nothing leads to.
    ///
    /// A warning rather than an error: a step drawn and not yet joined up is the normal state of a
    /// canvas halfway through being built, and refusing to save that would make the editor
    /// unusable. It still has to be said, because an assertion nothing reaches is an assertion
    /// that silently never runs.
    /// </summary>
    private static void CheckReachability(
        Graph graph, Dictionary<string, GraphNode> byId, List<GraphProblem> problems)
    {
        var start = graph.Nodes.FirstOrDefault(node => NodeCatalogue.Find(node.Key)?.IsStart == true);
        if (start is null) return;

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start.Id);
        reached.Add(start.Id);

        var outgoing = graph.Edges
            .Where(edge => edge.FromPort != "" )
            .GroupBy(edge => edge.FromId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outgoing.TryGetValue(current, out var edges)) continue;

            foreach (var edge in edges)
            {
                if (reached.Add(edge.ToId)) queue.Enqueue(edge.ToId);
            }
        }

        foreach (var node in graph.Nodes)
        {
            if (reached.Contains(node.Id)) continue;

            var spec = NodeCatalogue.Find(node.Key);

            // A node inside a container is reached through its parent, not by an edge, and a
            // comment is reached by nothing at all — neither is stranded.
            if (spec is null || spec.IsStart) continue;
            if (node.ParentId is not null) continue;
            if (spec.Inputs.Count == 0 && spec.Outputs.Count == 0) continue;

            problems.Add(new(GraphSeverity.Warning, "unreachable", [node.Name], node.Id));
        }

        _ = byId;
    }

    /// <summary>
    /// Loops drawn as edges rather than as loop nodes.
    ///
    /// The Flow group has real looping nodes with a ceiling on their iterations. An edge that goes
    /// back on itself has no such ceiling, so it is a run that never finishes — on somebody's build
    /// agent, until a timeout nobody set.
    /// </summary>
    private static void CheckCycles(
        Graph graph, Dictionary<string, GraphNode> byId, List<GraphProblem> problems)
    {
        var outgoing = graph.Edges
            .GroupBy(edge => edge.FromId)
            .ToDictionary(group => group.Key, group => group.Select(e => e.ToId).ToArray(), StringComparer.Ordinal);

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            Walk(node.Id);
        }

        void Walk(string id)
        {
            if (state.TryGetValue(id, out var mark))
            {
                // 1 = on the current path, so arriving here again is a cycle. 2 = finished.
                if (mark == 1 && reported.Add(id) && byId.TryGetValue(id, out var looping))
                {
                    problems.Add(new(GraphSeverity.Error, "cycle", [looping.Name], id));
                }

                return;
            }

            state[id] = 1;

            if (outgoing.TryGetValue(id, out var next))
            {
                foreach (var target in next) Walk(target);
            }

            state[id] = 2;
        }
    }

}
