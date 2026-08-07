using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProofFlow.TestEngine.Comparison;

/// <summary>
/// Compares what came back against what was approved, as structure rather than as text.
///
/// The brief forbids string comparison and it is right to: two JSON documents that differ only in
/// key order or in whitespace are the same document, and a diff that reports them as different
/// trains people to stop reading diffs. So this walks both trees together and reports the places
/// they actually disagree, in terms of the field rather than the line.
///
/// Depth is bounded. A document that nests deeper than <see cref="MaxDepth"/> stops being walked
/// and says so, because the alternative on a self-referential or pathological payload is a stack
/// overflow, which takes the whole process with it rather than failing one comparison.
/// </summary>
public sealed class SemanticDiff(ComparisonRuleSet? rules = null)
{
    public const int MaxDepth = 64;

    private readonly ComparisonRuleSet _rules = rules ?? ComparisonRuleSet.Empty;

    public static DiffResult Compare(JsonNode? expected, JsonNode? actual, ComparisonRuleSet? rules = null) =>
        new SemanticDiff(rules).Run(expected, actual);

    /// <summary>Parses both sides and compares. Text that is not JSON is compared as text.</summary>
    public static DiffResult CompareText(string? expected, string? actual, ComparisonRuleSet? rules = null)
    {
        var expectedNode = TryParse(expected);
        var actualNode = TryParse(actual);

        if (expectedNode is null || actualNode is null)
        {
            // One side is not JSON. Falling back to a whole-value comparison is honest: it says
            // they differ without pretending to know which field.
            var same = string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.Ordinal);

            var node = new DiffNode
            {
                Location = JsonLocation.Root,
                Kind = same ? DiffKind.Unchanged : DiffKind.Changed,
                Expected = expected,
                Actual = actual,
                Reason = same ? null : "the response is not JSON, so it was compared as text",
            };

            return Build(node, rules?.Invalid ?? []);
        }

        return Compare(expectedNode, actualNode, rules);
    }

    public DiffResult Run(JsonNode? expected, JsonNode? actual) =>
        Build(Walk(JsonLocation.Root, expected, actual, present: true, depth: 0), _rules.Invalid);

    private DiffNode Walk(JsonLocation location, JsonNode? expected, JsonNode? actual, bool present, int depth)
    {
        var rule = _rules.For(location);

        // Ignore short-circuits everything below it. A rule on an object means the whole subtree is
        // set aside, which is what somebody who wrote "$.meta => Ignore" meant.
        if (rule?.Kind == MatcherKind.Ignore)
        {
            return Node(location, DiffKind.Ignored, expected, actual, rule,
                reason: rule.Note ?? "set aside by a rule");
        }

        if (depth >= MaxDepth)
        {
            return Node(location, DiffKind.Changed, expected, actual, rule,
                reason: $"nesting deeper than {MaxDepth} levels was not compared");
        }

        // Presence, before anything about value.
        if (!present)
        {
            if (expected is null && actual is null) return Node(location, DiffKind.Unchanged, null, null, rule);

            if (expected is null)
            {
                var complaint = rule is null ? null : Matcher.Check(rule.Kind, rule, null, actual);
                return rule?.Kind is MatcherKind.Exists or MatcherKind.NotExists or MatcherKind.Ignore
                    ? Node(location, complaint is null ? DiffKind.Unchanged : DiffKind.RuleViolation,
                        null, actual, rule, complaint)
                    : Node(location, DiffKind.Added, null, actual, rule);
            }

            var missing = rule is null ? null : Matcher.Check(rule.Kind, rule, expected, null);
            return rule?.Kind is MatcherKind.Exists or MatcherKind.NotExists or MatcherKind.Ignore
                ? Node(location, missing is null ? DiffKind.Unchanged : DiffKind.RuleViolation,
                    expected, null, rule, missing)
                : Node(location, DiffKind.Removed, expected, null, rule);
        }

        // A matcher that judges the value on its own terms, whatever the shapes are.
        if (rule is not null && IsValueMatcher(rule.Kind))
        {
            var complaint = Matcher.Check(rule.Kind, rule, expected, actual);
            return Node(location, complaint is null ? DiffKind.Unchanged : DiffKind.RuleViolation,
                expected, actual, rule, complaint);
        }

        var expectedType = Matcher.TypeName(expected);
        var actualType = Matcher.TypeName(actual);

        if (expectedType != actualType)
        {
            return Node(location, DiffKind.TypeChanged, expected, actual, rule,
                $"was {expectedType}, is now {actualType}");
        }

        return (expected, actual) switch
        {
            (JsonObject left, JsonObject right) => WalkObject(location, left, right, rule, depth),
            (JsonArray left, JsonArray right) => WalkArray(location, left, right, rule, depth),
            _ => Matcher.Equal(expected, actual)
                ? Node(location, DiffKind.Unchanged, expected, actual, rule)
                : Node(location, DiffKind.Changed, expected, actual, rule),
        };
    }

    private DiffNode WalkObject(
        JsonLocation location, JsonObject expected, JsonObject actual, ComparisonRule? rule, int depth)
    {
        var subset = rule?.Kind == MatcherKind.JsonSubset;
        var children = new List<DiffNode>();

        foreach (var (key, value) in expected)
        {
            var present = actual.TryGetPropertyValue(key, out var counterpart);
            children.Add(Walk(location.Field(key), value, counterpart, present, depth + 1));
        }

        foreach (var (key, value) in actual)
        {
            if (expected.ContainsKey(key)) continue;

            var child = Walk(location.Field(key), null, value, present: false, depth + 1);

            // Under a subset rule, extra fields are what the rule permits — so they are recorded
            // as present rather than as findings.
            children.Add(subset && child.Kind == DiffKind.Added
                ? child with { Kind = DiffKind.Unchanged, Reason = "extra fields are allowed here" }
                : child);
        }

        return Node(location, DiffKind.Unchanged, null, null, rule, children: children);
    }

    private DiffNode WalkArray(
        JsonLocation location, JsonArray expected, JsonArray actual, ComparisonRule? rule, int depth)
    {
        var strategy = rule?.Kind ?? MatcherKind.ArrayOrdered;

        if (rule?.Kind == MatcherKind.ArrayCount)
        {
            var complaint = Matcher.Check(MatcherKind.ArrayCount, rule, expected, actual);
            return Node(location, complaint is null ? DiffKind.Unchanged : DiffKind.RuleViolation,
                expected, actual, rule, complaint);
        }

        return strategy switch
        {
            // Pairs by whole-value equality rather than descending, so it needs no depth budget.
            MatcherKind.ArrayUnordered => WalkUnordered(location, expected, actual, rule),
            MatcherKind.ArrayMatchByKey => WalkByKey(location, expected, actual, rule!, depth),
            _ => WalkOrdered(location, expected, actual, rule, depth),
        };
    }

    private DiffNode WalkOrdered(
        JsonLocation location, JsonArray expected, JsonArray actual, ComparisonRule? rule, int depth)
    {
        var children = new List<DiffNode>();

        for (var index = 0; index < Math.Max(expected.Count, actual.Count); index++)
        {
            var left = index < expected.Count ? expected[index] : null;
            var right = index < actual.Count ? actual[index] : null;
            var present = index < expected.Count && index < actual.Count;

            children.Add(Walk(location.At(index), left, right, present, depth + 1));
        }

        return Node(location, DiffKind.Unchanged, null, null, rule, children: children);
    }

    /// <summary>
    /// Same members, any order.
    ///
    /// Members are paired by value, so a list that came back shuffled reports as order-changed
    /// rather than as every position differing. Anything left unpaired on either side is a real
    /// addition or removal.
    /// </summary>
    private DiffNode WalkUnordered(
        JsonLocation location, JsonArray expected, JsonArray actual, ComparisonRule? rule)
    {
        var remaining = Enumerable.Range(0, actual.Count).ToList();
        var children = new List<DiffNode>();
        var moved = false;

        for (var index = 0; index < expected.Count; index++)
        {
            var wanted = expected[index];
            var found = remaining.FirstOrDefault(
                candidate => Same(wanted, actual[candidate]), -1);

            if (found < 0)
            {
                children.Add(Node(location.At(index), DiffKind.Removed, wanted, null, rule));
                continue;
            }

            if (found != index) moved = true;
            remaining.Remove(found);
        }

        foreach (var leftover in remaining)
        {
            children.Add(Node(location.At(leftover), DiffKind.Added, null, actual[leftover], rule));
        }

        if (children.Count == 0 && moved)
        {
            return Node(location, DiffKind.OrderChanged, null, null, rule,
                "the same items came back in a different order");
        }

        return Node(location, DiffKind.Unchanged, null, null, rule, children: children);
    }

    /// <summary>
    /// Items paired by a key field, then compared member by member.
    ///
    /// The most useful array strategy in practice: a list of records that arrives in a different
    /// order, with one field changed inside one record, should report that one field — not
    /// "everything after position two is different", which is what an ordered walk produces and
    /// what makes people give up on array diffs entirely.
    /// </summary>
    private DiffNode WalkByKey(
        JsonLocation location, JsonArray expected, JsonArray actual, ComparisonRule rule, int depth)
    {
        var key = rule.Text;
        if (string.IsNullOrWhiteSpace(key))
        {
            return Node(location, DiffKind.RuleViolation, expected, actual, rule,
                "this rule needs the name of the field to match items by");
        }

        var actualByKey = new Dictionary<string, (int Index, JsonNode? Node)>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        for (var index = 0; index < actual.Count; index++)
        {
            var id = KeyOf(actual[index], key);
            if (id is null) continue;
            if (!actualByKey.TryAdd(id, (index, actual[index]))) duplicates.Add(id);
        }

        var children = new List<DiffNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < expected.Count; index++)
        {
            var id = KeyOf(expected[index], key);

            if (id is null)
            {
                children.Add(Node(location.At(index), DiffKind.RuleViolation, expected[index], null, rule,
                    $"this item has no «{key}» to match on"));
                continue;
            }

            seen.Add(id);

            if (!actualByKey.TryGetValue(id, out var match))
            {
                children.Add(Node(location.At(index), DiffKind.Removed, expected[index], null, rule));
                continue;
            }

            // Located by the key it was found under, not by position — the position is exactly
            // what this strategy is refusing to care about.
            children.Add(Walk(location.Field($"{key}={id}"), expected[index], match.Node, present: true, depth + 1));
        }

        foreach (var (id, entry) in actualByKey)
        {
            if (seen.Contains(id)) continue;
            children.Add(Node(location.Field($"{key}={id}"), DiffKind.Added, null, entry.Node, rule));
        }

        if (duplicates.Count > 0)
        {
            children.Add(Node(location, DiffKind.RuleViolation, null, null, rule,
                $"«{key}» is not unique: {string.Join(", ", duplicates.Take(5))}"));
        }

        return Node(location, DiffKind.Unchanged, null, null, rule, children: children);
    }

    private static string? KeyOf(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.ToJsonString()
            : null;

    /// <summary>Structural equality for pairing, ignoring key order inside objects.</summary>
    private static bool Same(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is JsonObject a && right is JsonObject b)
        {
            if (a.Count != b.Count) return false;
            return a.All(pair => b.TryGetPropertyValue(pair.Key, out var other) && Same(pair.Value, other));
        }

        if (left is JsonArray x && right is JsonArray y)
        {
            return x.Count == y.Count && x.Select((item, i) => Same(item, y[i])).All(same => same);
        }

        return Matcher.Equal(left, right);
    }

    private static bool IsValueMatcher(MatcherKind kind) => kind is
        MatcherKind.Exists or MatcherKind.NotExists or MatcherKind.TypeOnly or
        MatcherKind.IsNull or MatcherKind.IsNotNull or MatcherKind.Regex or
        MatcherKind.Contains or MatcherKind.StartsWith or MatcherKind.EndsWith or
        MatcherKind.NumericTolerance or MatcherKind.NumericRange or MatcherKind.DateTolerance or
        MatcherKind.CaseInsensitive or MatcherKind.Trimmed;

    private static DiffNode Node(
        JsonLocation location, DiffKind kind, JsonNode? expected, JsonNode? actual,
        ComparisonRule? rule, string? reason = null, IReadOnlyList<DiffNode>? children = null) =>
        new()
        {
            Location = location,
            Kind = kind,
            Expected = expected?.ToJsonString(),
            Actual = actual?.ToJsonString(),
            Reason = reason,
            RulePath = rule?.Path,
            RuleKind = rule?.Kind,
            Children = children ?? [],
        };

    private static DiffResult Build(DiffNode root, IReadOnlyList<ComparisonRule> invalid)
    {
        var counts = new Dictionary<DiffKind, int>();
        var findings = new List<DiffNode>();

        Collect(root, counts, findings);

        return new DiffResult
        {
            Root = root,
            Counts = counts,
            Findings = findings,
            InvalidRules = invalid,
        };
    }

    private static void Collect(DiffNode node, Dictionary<DiffKind, int> counts, List<DiffNode> findings)
    {
        // A container is only counted when it is itself a finding. Counting its Unchanged wrapper
        // would make "3 changed" mean "3 changed fields plus every object above them".
        var isContainer = node.Children.Count > 0;

        if (!isContainer || node.Kind is not DiffKind.Unchanged)
        {
            counts[node.Kind] = counts.GetValueOrDefault(node.Kind) + 1;

            if (node.Kind is not (DiffKind.Unchanged or DiffKind.Ignored)) findings.Add(node);
        }

        foreach (var child in node.Children) Collect(child, counts, findings);
    }

    /// <summary>
    /// Parsing is allowed to go deeper than the walk will.
    ///
    /// System.Text.Json stops at 64 levels by default — the same number the walk stops at — so a
    /// payload past the limit failed to parse at all and quietly fell through to a text
    /// comparison, where two identical monsters compared equal and the depth was never mentioned.
    /// Giving the parser more room means the walk's own limit is the one that fires, and it says
    /// what happened.
    /// </summary>
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = MaxDepth * 4 };

    private static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return JsonNode.Parse(text, documentOptions: ParseOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
