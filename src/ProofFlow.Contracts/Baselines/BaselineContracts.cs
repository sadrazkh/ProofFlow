namespace ProofFlow.Contracts.Baselines;

/// <summary>
/// One row of a difference, flattened.
///
/// Flat rather than nested, deliberately. The tree is the engine's shape; the viewer's shape is a
/// list, because a list is what can be virtualised — and a response with forty thousand fields
/// will otherwise build forty thousand DOM nodes and lock the tab. <paramref name="Depth"/> carries
/// the indentation the nesting used to convey, and <paramref name="Collapsed"/> lets a whole branch
/// be skipped without rebuilding anything.
/// </summary>
public sealed record DiffRowDto
{
    public required int Index { get; init; }
    public required string Path { get; init; }

    /// <summary>The field or index alone, for a row that shows a name rather than a whole path.</summary>
    public required string Leaf { get; init; }

    public required int Depth { get; init; }

    /// <summary>Added, Removed, Changed, TypeChanged, OrderChanged, RuleViolation, Ignored, Unchanged.</summary>
    public required string Kind { get; init; }

    public string? Expected { get; init; }
    public string? Actual { get; init; }
    public string? Reason { get; init; }

    /// <summary>The rule that applied, so a reader can find and change it rather than guess.</summary>
    public string? RulePath { get; init; }
    public string? RuleKind { get; init; }

    public bool HasChildren { get; init; }

    /// <summary>True when this row or something under it is a real difference.</summary>
    public bool HasFindings { get; init; }
}

public sealed record DiffResultDto
{
    public required bool Matches { get; init; }
    public required IReadOnlyList<DiffRowDto> Rows { get; init; }

    /// <summary>Counts per kind, for the summary bar. Containers are not counted.</summary>
    public required IReadOnlyDictionary<string, int> Counts { get; init; }

    /// <summary>Row indexes of the real findings, in document order — what n and p step through.</summary>
    public required IReadOnlyList<int> FindingIndexes { get; init; }

    /// <summary>Rules whose path did not parse. Said out loud rather than silently dropped.</summary>
    public IReadOnlyList<string> InvalidRules { get; init; } = [];

    /// <summary>Set when the request itself never completed; then there is nothing to compare.</summary>
    public string? FailureMessage { get; init; }

    public string? BaselineVersion { get; init; }
    public int? StatusCode { get; init; }
    public double DurationMs { get; init; }
}

/// <summary>
/// Everything one comparison produces.
///
/// The suggestions come back with the diff rather than from a second call, because they are
/// computed from the response body — and the browser never has that body. It has rows, which are
/// values with their surrounding structure removed, and the detector needs the structure.
/// </summary>
public sealed record CompareResponseDto
{
    public required DiffResultDto Diff { get; init; }
    public IReadOnlyList<SuggestionDto> Suggestions { get; init; } = [];

    /// <summary>
    /// What came back, but only when there is nothing yet to compare it against.
    ///
    /// The diff is the answer in every other case, and shipping the whole body beside it would send
    /// the same bytes twice. For an endpoint with no approved version there is no diff to be the
    /// answer — the reader has to look at the response itself before agreeing that it is correct,
    /// and this is what they look at.
    /// </summary>
    public string? Body { get; init; }

    public string? ContentType { get; init; }
}

/// <summary>A rule as the builder edits it.</summary>
public sealed record RuleDto
{
    public Guid? Id { get; init; }
    public required string Path { get; init; }
    public required string Matcher { get; init; }
    public string? Text { get; init; }
    public double? Number { get; init; }
    public double? Number2 { get; init; }
    public string? Note { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// A proposed rule, with the evidence for it.
///
/// <paramref name="Confidence"/> drives whether the row arrives pre-ticked. Nothing here is applied
/// until somebody ticks it and saves — a field silently excluded is a field that stopped being
/// checked without anyone deciding to.
/// </summary>
public sealed record SuggestionDto(
    string Path, string Reason, string Confidence, string Matcher, string? Note, string? Sample);

/// <summary>What the reviewer decided, field by field.</summary>
public sealed record AcceptChangesCommand
{
    /// <summary>Paths whose new value is accepted into the next version.</summary>
    public IReadOnlyList<string> AcceptedPaths { get; init; } = [];

    /// <summary>Rules to add at the same time — usually from the suggestion list.</summary>
    public IReadOnlyList<RuleDto> NewRules { get; init; } = [];

    public string? Description { get; init; }
}
