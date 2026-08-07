using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProofFlow.TestEngine.Comparison;

/// <summary>
/// How a value is allowed to differ from the one that was approved.
///
/// The default — absent any rule — is that it may not differ at all. Everything here is a
/// deliberate loosening of that, attached to a path, and each one exists because some real API
/// does the thing it permits: a timestamp that moves, an identifier that is regenerated, a score
/// that rounds differently on another machine, a list that comes back in whatever order the
/// database felt like.
/// </summary>
public enum MatcherKind
{
    /// <summary>The default. Byte-for-byte the same value, after normalisation.</summary>
    Exact = 0,

    /// <summary>Present, and its value is not looked at. What a timestamp gets.</summary>
    Ignore = 1,

    /// <summary>The field must be there. Its value is not looked at.</summary>
    Exists = 2,

    /// <summary>The field must not be there. For a field that was removed on purpose.</summary>
    NotExists = 3,

    /// <summary>Same JSON type, any value. A generated id is still a string.</summary>
    TypeOnly = 4,

    IsNull = 5,
    IsNotNull = 6,

    /// <summary>Matches a regular expression. For an id with a known shape.</summary>
    Regex = 7,

    Contains = 8,
    StartsWith = 9,
    EndsWith = 10,

    /// <summary>Within ± of the approved number. For a score that rounds differently.</summary>
    NumericTolerance = 11,

    /// <summary>Between two numbers, regardless of what was approved.</summary>
    NumericRange = 12,

    /// <summary>Within ± seconds of the approved instant. For a nearly-fixed timestamp.</summary>
    DateTolerance = 13,

    /// <summary>Every approved field is present with the same value; extras are allowed.</summary>
    JsonSubset = 14,

    /// <summary>Same items in the same order. The default for an array.</summary>
    ArrayOrdered = 15,

    /// <summary>Same items, order disregarded.</summary>
    ArrayUnordered = 16,

    /// <summary>Items paired by a key field, then compared. Reports what moved rather than
    /// declaring every row different.</summary>
    ArrayMatchByKey = 17,

    /// <summary>At least, and at most, this many items.</summary>
    ArrayCount = 18,

    /// <summary>Strings compared without regard to case.</summary>
    CaseInsensitive = 19,

    /// <summary>Whitespace at both ends removed before comparing.</summary>
    Trimmed = 20,
}

/// <summary>
/// One rule: a path, a matcher, and whatever the matcher needs.
///
/// <paramref name="Note"/> carries why. A rule that says a field may change is a rule somebody
/// will question in six months, and the answer should be next to it rather than in a chat log.
/// </summary>
public sealed record ComparisonRule
{
    public required string Path { get; init; }
    public MatcherKind Kind { get; init; } = MatcherKind.Exact;

    /// <summary>Regex, Contains, StartsWith, EndsWith, and the key name for ArrayMatchByKey.</summary>
    public string? Text { get; init; }

    /// <summary>Tolerance, minimum of a range or a count.</summary>
    public double? Number { get; init; }

    /// <summary>Maximum of a range or a count.</summary>
    public double? Number2 { get; init; }

    public string? Note { get; init; }

    /// <summary>Off while somebody is trying a rule out, without deleting it.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// The rules that apply to one comparison, indexed so the walk can ask "what applies here?"
/// thousands of times without re-parsing anything.
/// </summary>
public sealed class ComparisonRuleSet
{
    private readonly (PathPattern Pattern, ComparisonRule Rule)[] _rules;

    public ComparisonRuleSet(IEnumerable<ComparisonRule> rules)
    {
        _rules = [.. rules
            .Where(rule => rule.Enabled)
            .Select(rule => (Parsed: PathPattern.TryParse(rule.Path, out var pattern), Pattern: pattern, Rule: rule))
            // A rule whose path does not parse is dropped rather than allowed to throw mid-diff.
            // Invalid.Count says how many, so the interface can say so instead of quietly ignoring.
            .Where(entry => entry.Parsed)
            .Select(entry => (entry.Pattern, entry.Rule))];

        Invalid = [.. rules.Where(rule => rule.Enabled && !PathPattern.TryParse(rule.Path, out _))];
    }

    public static ComparisonRuleSet Empty { get; } = new([]);

    /// <summary>Rules whose path could not be parsed. Surfaced, never silently discarded.</summary>
    public IReadOnlyList<ComparisonRule> Invalid { get; }

    public int Count => _rules.Length;

    /// <summary>
    /// The rule that applies at a location, or null.
    ///
    /// Last one wins. Rules are read top to bottom like a stylesheet, so a broad rule can be
    /// written first and a narrow exception after it — which is the order people write them in.
    /// </summary>
    public ComparisonRule? For(JsonLocation location)
    {
        ComparisonRule? found = null;

        foreach (var (pattern, rule) in _rules)
        {
            if (pattern.Matches(location)) found = rule;
        }

        return found;
    }
}

/// <summary>Applies one matcher to a pair of values.</summary>
public static class Matcher
{
    /// <summary>
    /// Compares two values under a rule.
    ///
    /// Returns null when they are acceptable, or a sentence saying why they are not — written for
    /// the person reading a failed run, not for a log.
    /// </summary>
    public static string? Check(MatcherKind kind, ComparisonRule rule, JsonNode? expected, JsonNode? actual)
    {
        switch (kind)
        {
            case MatcherKind.Ignore:
                return null;

            case MatcherKind.Exists:
                return actual is null ? "the field is missing" : null;

            case MatcherKind.NotExists:
                return actual is null ? null : "the field is present and should not be";

            case MatcherKind.IsNull:
                return IsNull(actual) ? null : $"expected null, got {Describe(actual)}";

            case MatcherKind.IsNotNull:
                return IsNull(actual) ? "the value is null" : null;

            case MatcherKind.TypeOnly:
            {
                var expectedType = TypeName(expected);
                var actualType = TypeName(actual);
                return expectedType == actualType ? null : $"expected {expectedType}, got {actualType}";
            }

            case MatcherKind.Regex:
            {
                if (rule.Text is not { Length: > 0 } expression) return "no expression was given for this rule";
                var text = AsText(actual);
                if (text is null) return $"expected text, got {Describe(actual)}";

                try
                {
                    // A timeout, because a rule is user input and a catastrophically backtracking
                    // expression would otherwise hang the run rather than fail it.
                    return System.Text.RegularExpressions.Regex.IsMatch(
                        text, expression, RegexOptions.None, TimeSpan.FromMilliseconds(250))
                        ? null
                        : $"«{Trim(text)}» does not match {expression}";
                }
                catch (RegexMatchTimeoutException)
                {
                    return $"the expression {expression} took too long to evaluate";
                }
                catch (ArgumentException)
                {
                    return $"{expression} is not a valid regular expression";
                }
            }

            case MatcherKind.Contains:
                return Text(actual, rule.Text, (a, b) => a.Contains(b, StringComparison.Ordinal), "does not contain");

            case MatcherKind.StartsWith:
                return Text(actual, rule.Text, (a, b) => a.StartsWith(b, StringComparison.Ordinal), "does not start with");

            case MatcherKind.EndsWith:
                return Text(actual, rule.Text, (a, b) => a.EndsWith(b, StringComparison.Ordinal), "does not end with");

            case MatcherKind.NumericTolerance:
            {
                if (!TryNumber(expected, out var want) || !TryNumber(actual, out var got))
                    return $"expected a number, got {Describe(actual)}";

                var tolerance = Math.Abs(rule.Number ?? 0);
                var drift = Math.Abs(want - got);

                return drift <= tolerance
                    ? null
                    : $"{Format(got)} is {Format(drift)} away from {Format(want)}, more than ±{Format(tolerance)}";
            }

            case MatcherKind.NumericRange:
            {
                if (!TryNumber(actual, out var value)) return $"expected a number, got {Describe(actual)}";

                var low = rule.Number ?? double.MinValue;
                var high = rule.Number2 ?? double.MaxValue;

                return value >= low && value <= high
                    ? null
                    : $"{Format(value)} is outside {Format(low)} to {Format(high)}";
            }

            case MatcherKind.DateTolerance:
            {
                if (!TryDate(expected, out var want) || !TryDate(actual, out var got))
                    return $"expected a date, got {Describe(actual)}";

                var tolerance = TimeSpan.FromSeconds(Math.Abs(rule.Number ?? 0));
                var drift = (got - want).Duration();

                return drift <= tolerance
                    ? null
                    : $"{got:O} is {drift.TotalSeconds:0.##}s away from {want:O}, more than ±{tolerance.TotalSeconds:0.##}s";
            }

            case MatcherKind.ArrayCount:
            {
                if (actual is not JsonArray array) return $"expected a list, got {Describe(actual)}";

                var least = (int)(rule.Number ?? 0);
                var most = rule.Number2 is { } max ? (int)max : int.MaxValue;

                if (array.Count < least) return $"{array.Count} items, fewer than {least}";
                if (array.Count > most) return $"{array.Count} items, more than {most}";
                return null;
            }

            case MatcherKind.CaseInsensitive:
            {
                var want = AsText(expected);
                var got = AsText(actual);
                if (want is null || got is null) return Equal(expected, actual) ? null : "the values differ";

                return string.Equals(want, got, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"«{Trim(got)}» is not «{Trim(want)}», even ignoring case";
            }

            case MatcherKind.Trimmed:
            {
                var want = AsText(expected)?.Trim();
                var got = AsText(actual)?.Trim();
                if (want is null || got is null) return Equal(expected, actual) ? null : "the values differ";

                return want == got ? null : $"«{Trim(got)}» is not «{Trim(want)}»";
            }

            // Structural matchers are decided by the walk, which has both sides and can report
            // which member moved. Reaching here means one was attached to a leaf.
            case MatcherKind.JsonSubset:
            case MatcherKind.ArrayOrdered:
            case MatcherKind.ArrayUnordered:
            case MatcherKind.ArrayMatchByKey:
                return null;

            default:
                return Equal(expected, actual) ? null : null;
        }
    }

    /// <summary>
    /// Whether two nodes are the same value.
    ///
    /// Numbers are compared as numbers: 1.0 and 1 are the same value written two ways, and an API
    /// that starts serialising a whole number without its decimal point has not changed anything a
    /// caller can observe. Everything else compares by its JSON text, which is exact.
    /// </summary>
    public static bool Equal(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;

        if (TryNumber(left, out var a) && TryNumber(right, out var b))
        {
            // Both are numbers: compare numerically, with a floor that absorbs the last bit of
            // double representation rather than the difference between two real values.
            return Math.Abs(a - b) <= Math.Max(Math.Abs(a), Math.Abs(b)) * 1e-12;
        }

        return left.ToJsonString() == right.ToJsonString();
    }

    public static string TypeName(JsonNode? node) => node switch
    {
        null => "nothing",
        JsonArray => "list",
        JsonObject => "object",
        JsonValue value when value.TryGetValue<string>(out _) => "text",
        JsonValue value when value.TryGetValue<bool>(out _) => "true/false",
        JsonValue value when TryNumber(value, out _) => "number",
        _ => "null",
    };

    public static bool IsNull(JsonNode? node) =>
        node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null);

    /// <summary>A value as a short readable phrase, for a message a person reads.</summary>
    public static string Describe(JsonNode? node) => node switch
    {
        null => "nothing",
        JsonArray array => $"a list of {array.Count}",
        JsonObject obj => $"an object with {obj.Count} field(s)",
        _ => Trim(node.ToJsonString()),
    };

    public static string? AsText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue) return false;
        if (jsonValue.TryGetValue<double>(out value)) return true;

        // A number that arrived as a quoted string still compares as a number when the rule asks
        // for one — APIs that send "12.50" for money are common, and refusing them would push
        // people towards regular expressions for arithmetic.
        return jsonValue.TryGetValue<string>(out var text)
               && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryDate(JsonNode? node, out DateTimeOffset value)
    {
        value = default;
        var text = AsText(node);

        return text is not null
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static string? Text(JsonNode? actual, string? needle, Func<string, string, bool> test, string verb)
    {
        if (needle is not { Length: > 0 }) return "no text was given for this rule";

        var value = AsText(actual);
        if (value is null) return $"expected text, got {Describe(actual)}";

        return test(value, needle) ? null : $"«{Trim(value)}» {verb} «{needle}»";
    }

    private static string Format(double value) =>
        value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Trim(string value) => value.Length <= 60 ? value : value[..60] + "…";
}
