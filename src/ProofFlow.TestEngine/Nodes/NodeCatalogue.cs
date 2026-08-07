using System.Collections.Frozen;

namespace ProofFlow.TestEngine.Nodes;

/// <summary>
/// Every node the product knows about, indexed by key.
///
/// Built once. The five group files are the source; this is the only place that assembles them, so
/// adding a group means adding it here and nowhere else — and the palette, the inspector, the
/// validator and the runner all read from this rather than each keeping a list.
/// </summary>
public static class NodeCatalogue
{
    /*
      Both built in a static constructor, in order.

      As field initialisers the index ran before the list it indexes was assigned, and the failure
      was a NullReferenceException inside a type initialiser — which surfaces as every single test
      in the file failing for a reason none of them names.
    */
    private static readonly FrozenDictionary<string, NodeSpec> ByKey;

    public static IReadOnlyList<NodeSpec> All { get; }

    static NodeCatalogue()
    {
        All =
        [
            .. CoreNodes.All,
            .. DataNodes.All,
            .. TestingNodes.All,
            .. FlowNodes.All,
            .. AuthNodes.All,
        ];

        ByKey = Build();
    }

    public static NodeSpec? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var spec) ? spec : null;

    /// <summary>
    /// The spec for a key, or a refusal.
    ///
    /// Loud rather than silent: a graph holding a key nothing defines cannot be drawn, cannot be
    /// validated and cannot be run, and the version that produced it is the thing worth naming.
    /// </summary>
    public static NodeSpec Require(string key) =>
        Find(key) ?? throw new KeyNotFoundException($"No node type «{key}» in this version of ProofFlow.");

    public static IEnumerable<NodeSpec> InGroup(NodeGroup group) =>
        All.Where(spec => spec.Group == group);

    public static NodeSpec Start => All.First(spec => spec.IsStart);

    /// <summary>
    /// Whether a value of <paramref name="from"/> may be plugged into a socket wanting
    /// <paramref name="to"/>.
    ///
    /// Deliberately narrow. <c>Any</c> is compatible both ways and nothing else widens — a Number
    /// does not quietly become Text, because a test that compares "200" with 200 and passes is
    /// worse than one that refuses to be built.
    ///
    /// <c>Secret</c> is the exception in the other direction: it satisfies <c>Any</c> but nothing
    /// satisfies it, so a credential can be passed along and a plain string cannot be mistaken for
    /// one.
    /// </summary>
    public static bool Accepts(DataType to, DataType from)
    {
        if (to == from) return true;
        if (to == DataType.None || from == DataType.None) return false;
        if (to == DataType.Secret) return false;
        return to == DataType.Any || from == DataType.Any;
    }

    private static FrozenDictionary<string, NodeSpec> Build()
    {
        var byKey = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);

        foreach (var spec in All)
        {
            if (!byKey.TryAdd(spec.Key, spec))
            {
                // A duplicate key means one node silently shadows another, and which one wins
                // depends on file order. Better to refuse to start.
                throw new InvalidOperationException($"Two node types share the key «{spec.Key}».");
            }
        }

        return byKey.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
