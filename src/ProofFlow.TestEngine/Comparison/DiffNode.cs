namespace ProofFlow.TestEngine.Comparison;

/// <summary>
/// What happened at one place in the document.
///
/// Six categories rather than "same" and "different", because the question a reader is actually
/// asking is "did the API break?" and the categories answer it at a glance: a field that appeared
/// is usually fine, a field that vanished usually is not, and a field whose *type* changed is
/// almost always a bug somebody should hear about today.
/// </summary>
public enum DiffKind
{
    Unchanged = 0,

    /// <summary>In the response, not in the baseline. Often harmless.</summary>
    Added = 1,

    /// <summary>In the baseline, not in the response. Usually is not harmless.</summary>
    Removed = 2,

    /// <summary>Same type, different value.</summary>
    Changed = 3,

    /// <summary>A number became a string, or an object became null. Rarely intentional.</summary>
    TypeChanged = 4,

    /// <summary>Same members, different order — reported separately so an unordered list does not
    /// read as if every row changed.</summary>
    OrderChanged = 5,

    /// <summary>A rule was attached here and the value broke it. Carries the rule's own reason.</summary>
    RuleViolation = 6,

    /// <summary>A rule said not to look. Kept in the tree and shown greyed: hiding it is how a
    /// reader stops believing the diff is complete.</summary>
    Ignored = 7,
}

/// <summary>One row of the difference tree.</summary>
public sealed record DiffNode
{
    public required JsonLocation Location { get; init; }
    public required DiffKind Kind { get; init; }

    /// <summary>The approved value, as JSON text. Null when the field did not exist.</summary>
    public string? Expected { get; init; }

    /// <summary>What came back. Null when the field no longer exists.</summary>
    public string? Actual { get; init; }

    /// <summary>Why, in a sentence, when the answer is not obvious from the two values.</summary>
    public string? Reason { get; init; }

    /// <summary>The rule that applied here, if one did — so a reader can find and change it.</summary>
    public string? RulePath { get; init; }
    public MatcherKind? RuleKind { get; init; }

    public IReadOnlyList<DiffNode> Children { get; init; } = [];

    public string Path => Location.ToString();

    /// <summary>True when this node or anything under it is a real difference.</summary>
    public bool HasFindings => Kind is not (DiffKind.Unchanged or DiffKind.Ignored)
                               || Children.Any(child => child.HasFindings);
}

/// <summary>
/// The whole comparison: the tree, and the counts a summary bar shows.
/// </summary>
public sealed record DiffResult
{
    public required DiffNode Root { get; init; }
    public required IReadOnlyDictionary<DiffKind, int> Counts { get; init; }

    /// <summary>Every differing node, flattened, in document order — what the n/p keys walk.</summary>
    public required IReadOnlyList<DiffNode> Findings { get; init; }

    /// <summary>Rules whose path could not be parsed, so the interface can say so.</summary>
    public IReadOnlyList<ComparisonRule> InvalidRules { get; init; } = [];

    /// <summary>
    /// True when nothing of consequence differs.
    ///
    /// Ignored nodes do not count, which is the entire point of ignoring them — but they are still
    /// in the tree, so a reader can see what was set aside rather than having to trust that
    /// something was.
    /// </summary>
    public bool Matches => Findings.Count == 0;

    public int Count(DiffKind kind) => Counts.TryGetValue(kind, out var value) ? value : 0;
}
