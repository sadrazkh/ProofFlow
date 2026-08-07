using System.Text;

namespace ProofFlow.TestEngine.Comparison;

/// <summary>
/// A rule's target: which places in a document it applies to.
///
/// <code>
/// $.createdAt              one field
/// $.items[*].updatedAt     that field in every item
/// $.items[0].id            that field in the first item only
/// $.items                  the array itself
/// $..requestId             that field wherever it appears, at any depth
/// $.data['odd key']        a key that is not an identifier
/// </code>
///
/// Written here rather than delegated to the JsonPath library, and the reason is the direction of
/// the question. JsonPath answers "which nodes match this expression?", which needs the whole
/// document. The diff walks two documents at once and needs the opposite: "does any rule apply to
/// the position I am standing on?" — asked thousands of times, against a path that is already
/// being built as it descends. Evaluating every rule against every document and then matching node
/// identities would be both slower and a subtle source of mismatches between the two sides.
///
/// The library is still used, for assertions, where the question really is "find me the nodes".
/// </summary>
public sealed class PathPattern
{
    private readonly Segment[] _segments;

    private PathPattern(Segment[] segments, string source)
    {
        _segments = segments;
        Source = source;
    }

    public string Source { get; }

    /// <summary>Matches every position. What a rule with no path means.</summary>
    public static PathPattern Everything { get; } = new([new AnyDepth()], "$..*");

    public static bool TryParse(string? expression, out PathPattern pattern)
    {
        pattern = null!;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var text = expression.Trim();

        // A leading $ is conventional and optional: people paste both forms, and refusing one of
        // them teaches nothing.
        if (text.StartsWith('$')) text = text[1..];

        var segments = new List<Segment>();
        var index = 0;

        while (index < text.Length)
        {
            if (text[index] == '.')
            {
                if (index + 1 < text.Length && text[index + 1] == '.')
                {
                    segments.Add(new AnyDepth());
                    index += 2;
                    continue;
                }

                index++;
                continue;
            }

            if (text[index] == '[')
            {
                var close = text.IndexOf(']', index);
                if (close < 0) return false;

                var inside = text[(index + 1)..close].Trim();
                if (inside.Length == 0) return false;

                if (inside == "*")
                {
                    segments.Add(new AnyIndex());
                }
                else if (int.TryParse(inside, out var position))
                {
                    segments.Add(new AtIndex(position));
                }
                else
                {
                    segments.Add(new Named(inside.Trim('\'', '"')));
                }

                index = close + 1;
                continue;
            }

            var next = text.IndexOfAny(['.', '['], index);
            var name = next < 0 ? text[index..] : text[index..next];
            index = next < 0 ? text.Length : next;

            if (name.Length == 0) return false;
            segments.Add(name == "*" ? new AnyName() : new Named(name));
        }

        // "$" on its own is the document itself — where a subset or an array rule that applies to
        // the whole response is written. Zero segments matches only the root, which is exactly right.
        pattern = new PathPattern([.. segments], expression.Trim());
        return true;
    }

    /// <summary>True when this pattern applies to the given concrete location.</summary>
    public bool Matches(JsonLocation location) => Matches(location.Steps, 0, 0);

    private bool Matches(IReadOnlyList<Step> steps, int stepIndex, int segmentIndex)
    {
        while (true)
        {
            if (segmentIndex == _segments.Length) return stepIndex == steps.Count;

            if (_segments[segmentIndex] is AnyDepth)
            {
                // The last segment being ".." means "everything from here down".
                if (segmentIndex + 1 == _segments.Length) return true;

                // Try skipping any number of steps, then continuing. Recursion is bounded by the
                // document's depth, which is bounded by the parser that produced it.
                for (var skip = stepIndex; skip <= steps.Count; skip++)
                {
                    if (Matches(steps, skip, segmentIndex + 1)) return true;
                }

                return false;
            }

            if (stepIndex == steps.Count) return false;
            if (!_segments[segmentIndex].Accepts(steps[stepIndex])) return false;

            stepIndex++;
            segmentIndex++;
        }
    }

    public override string ToString() => Source;

    private abstract record Segment
    {
        public abstract bool Accepts(Step step);
    }

    private sealed record Named(string Name) : Segment
    {
        // Case-sensitive, because JSON keys are. Two fields differing only in case are two fields.
        public override bool Accepts(Step step) => step.Name == Name;
    }

    private sealed record AnyName : Segment
    {
        public override bool Accepts(Step step) => step.Name is not null;
    }

    private sealed record AtIndex(int Index) : Segment
    {
        public override bool Accepts(Step step) => step.Index == Index;
    }

    private sealed record AnyIndex : Segment
    {
        public override bool Accepts(Step step) => step.Index is not null;
    }

    private sealed record AnyDepth : Segment
    {
        public override bool Accepts(Step step) => true;
    }
}

/// <summary>One move down the document: into a named field, or into an array position.</summary>
public readonly record struct Step(string? Name, int? Index)
{
    public static Step Field(string name) => new(name, null);
    public static Step At(int index) => new(null, index);

    public override string ToString() =>
        Name is not null
            ? (IsIdentifier(Name) ? $".{Name}" : $"['{Name}']")
            : $"[{Index}]";

    private static bool IsIdentifier(string name) =>
        name.Length > 0
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

/// <summary>
/// Where in a document something is, as the steps taken to reach it.
///
/// Kept as steps rather than as a string so a pattern can be matched against it without parsing,
/// and rendered to the canonical <c>$.items[0].id</c> form only when a person is going to read it.
/// The two must agree exactly — a path shown in the interface is a path somebody will paste into a
/// rule — so both come from here.
/// </summary>
public sealed class JsonLocation
{
    private readonly Step[] _steps;
    private string? _rendered;

    private JsonLocation(Step[] steps) => _steps = steps;

    public static JsonLocation Root { get; } = new([]);

    public IReadOnlyList<Step> Steps => _steps;

    public int Depth => _steps.Length;

    public JsonLocation Field(string name) => new([.. _steps, Step.Field(name)]);

    public JsonLocation At(int index) => new([.. _steps, Step.At(index)]);

    /// <summary>The canonical text form: <c>$</c>, <c>$.items</c>, <c>$.items[0].id</c>.</summary>
    public override string ToString()
    {
        if (_rendered is not null) return _rendered;

        var builder = new StringBuilder("$");
        foreach (var step in _steps) builder.Append(step.ToString());

        return _rendered = builder.ToString();
    }

    /// <summary>The field or index at the end, for a row that shows a name rather than a path.</summary>
    public string Leaf => _steps.Length == 0
        ? "$"
        : _steps[^1].Name ?? $"[{_steps[^1].Index}]";
}
