using FluentAssertions;
using ProofFlow.TestEngine.Comparison;

namespace ProofFlow.Tests;

/// <summary>
/// The comparison engine, which is the product.
///
/// Everything else — the canvas, the scheduler, the reports — exists to get two documents in front
/// of this and to show what it says. The brief forbids comparing them as strings, and these are
/// the cases that distinguish a structural comparison from one: key order, numeric spelling,
/// arrays that arrive shuffled, and a field whose type quietly changed.
/// </summary>
public class SemanticDiffTests
{
    private static DiffResult Diff(string expected, string actual, params ComparisonRule[] rules) =>
        SemanticDiff.CompareText(expected, actual, new ComparisonRuleSet(rules));

    // ---- the baseline behaviour: structure, not text ------------------------------------------

    [Fact]
    public void Key_order_is_not_a_difference()
    {
        // The single most common false positive in a text diff, and the reason people stop reading
        // them: no caller can observe the order of object keys.
        var result = Diff("""{"a":1,"b":2}""", """{"b":2,"a":1}""");

        result.Matches.Should().BeTrue();
    }

    [Fact]
    public void Whitespace_is_not_a_difference() =>
        Diff("""{"a":1}""", "{\n  \"a\" : 1\n}").Matches.Should().BeTrue();

    [Fact]
    public void A_number_written_differently_is_the_same_number()
    {
        // An API that starts emitting 1.0 where it used to emit 1 has not changed anything a
        // caller can act on.
        Diff("""{"score":12}""", """{"score":12.0}""").Matches.Should().BeTrue();
        Diff("""{"score":1e2}""", """{"score":100}""").Matches.Should().BeTrue();
    }

    [Fact]
    public void A_changed_value_is_reported_with_both_sides()
    {
        var result = Diff("""{"price":120}""", """{"price":125}""");

        result.Matches.Should().BeFalse();
        var finding = result.Findings.Single();
        finding.Kind.Should().Be(DiffKind.Changed);
        finding.Path.Should().Be("$.price");
        finding.Expected.Should().Be("120");
        finding.Actual.Should().Be("125");
    }

    [Fact]
    public void A_new_field_is_added_and_a_missing_one_is_removed()
    {
        var result = Diff("""{"a":1,"gone":2}""", """{"a":1,"fresh":3}""");

        result.Count(DiffKind.Added).Should().Be(1);
        result.Count(DiffKind.Removed).Should().Be(1);
        result.Findings.Single(f => f.Kind == DiffKind.Added).Path.Should().Be("$.fresh");
        result.Findings.Single(f => f.Kind == DiffKind.Removed).Path.Should().Be("$.gone");
    }

    [Fact]
    public void A_type_change_is_its_own_category()
    {
        // Separated from Changed because it is nearly always a defect rather than data moving:
        // a number that became a string breaks every caller that parsed it.
        var result = Diff("""{"id":21289}""", """{"id":"21289"}""");

        var finding = result.Findings.Single();
        finding.Kind.Should().Be(DiffKind.TypeChanged);
        finding.Reason.Should().Contain("number").And.Contain("text");
    }

    [Fact]
    public void Null_and_missing_are_told_apart()
    {
        // A field explicitly set to null says something; a field that is gone says something else.
        Diff("""{"a":1}""", """{"a":null}""").Findings.Single().Kind.Should().Be(DiffKind.TypeChanged);
        Diff("""{"a":1}""", "{}").Findings.Single().Kind.Should().Be(DiffKind.Removed);
    }

    [Fact]
    public void Nested_changes_carry_a_usable_path()
    {
        var result = Diff(
            """{"items":[{"id":1,"name":"a"},{"id":2,"name":"b"}]}""",
            """{"items":[{"id":1,"name":"a"},{"id":2,"name":"CHANGED"}]}""");

        // The path is the same spelling a rule is written in, so it can be copied straight across.
        result.Findings.Single().Path.Should().Be("$.items[1].name");
    }

    // ---- rules --------------------------------------------------------------------------------

    [Fact]
    public void An_ignored_field_is_kept_in_the_tree_rather_than_hidden()
    {
        var result = Diff(
            """{"id":1,"updatedAt":"2026-01-01T00:00:00Z"}""",
            """{"id":1,"updatedAt":"2026-08-07T09:30:00Z"}""",
            new ComparisonRule { Path = "$.updatedAt", Kind = MatcherKind.Ignore });

        result.Matches.Should().BeTrue();

        // Present and marked, not removed. A diff that silently drops what it set aside is one
        // nobody can audit.
        result.Count(DiffKind.Ignored).Should().Be(1);
        Flatten(result.Root).Should().Contain(node => node.Path == "$.updatedAt" && node.Kind == DiffKind.Ignored);
    }

    [Fact]
    public void A_wildcard_rule_reaches_every_item()
    {
        var result = Diff(
            """{"items":[{"id":1,"t":"a"},{"id":2,"t":"b"}]}""",
            """{"items":[{"id":1,"t":"x"},{"id":2,"t":"y"}]}""",
            new ComparisonRule { Path = "$.items[*].t", Kind = MatcherKind.Ignore });

        result.Matches.Should().BeTrue();
        result.Count(DiffKind.Ignored).Should().Be(2);
    }

    [Fact]
    public void A_recursive_rule_reaches_any_depth()
    {
        var result = Diff(
            """{"a":{"b":{"requestId":"one"}},"requestId":"two"}""",
            """{"a":{"b":{"requestId":"three"}},"requestId":"four"}""",
            new ComparisonRule { Path = "$..requestId", Kind = MatcherKind.Ignore });

        result.Matches.Should().BeTrue();
        result.Count(DiffKind.Ignored).Should().Be(2);
    }

    [Fact]
    public void Numeric_tolerance_absorbs_rounding_but_not_a_real_move()
    {
        var rule = new ComparisonRule { Path = "$.score", Kind = MatcherKind.NumericTolerance, Number = 0.01 };

        Diff("""{"score":12.50}""", """{"score":12.505}""", rule).Matches.Should().BeTrue();

        var moved = Diff("""{"score":12.50}""", """{"score":12.9}""", rule);
        moved.Matches.Should().BeFalse();
        moved.Findings.Single().Kind.Should().Be(DiffKind.RuleViolation);
        moved.Findings.Single().Reason.Should().Contain("±0.01");
    }

    [Fact]
    public void Date_tolerance_is_measured_in_seconds()
    {
        var rule = new ComparisonRule { Path = "$.at", Kind = MatcherKind.DateTolerance, Number = 60 };

        Diff("""{"at":"2026-08-07T09:00:00Z"}""", """{"at":"2026-08-07T09:00:45Z"}""", rule)
            .Matches.Should().BeTrue();

        Diff("""{"at":"2026-08-07T09:00:00Z"}""", """{"at":"2026-08-07T09:05:00Z"}""", rule)
            .Matches.Should().BeFalse();
    }

    [Fact]
    public void Type_only_accepts_a_regenerated_identifier()
    {
        var result = Diff(
            """{"requestId":"11111111-1111-1111-1111-111111111111"}""",
            """{"requestId":"22222222-2222-2222-2222-222222222222"}""",
            new ComparisonRule { Path = "$.requestId", Kind = MatcherKind.TypeOnly });

        result.Matches.Should().BeTrue();
    }

    [Fact]
    public void Type_only_still_catches_a_type_change()
    {
        var result = Diff(
            """{"requestId":"abc"}""", """{"requestId":42}""",
            new ComparisonRule { Path = "$.requestId", Kind = MatcherKind.TypeOnly });

        result.Findings.Single().Kind.Should().Be(DiffKind.RuleViolation);
    }

    [Fact]
    public void A_regex_rule_pins_the_shape_without_pinning_the_value()
    {
        var rule = new ComparisonRule
        {
            Path = "$.token", Kind = MatcherKind.Regex, Text = "^tok_[0-9a-f]{8}$",
        };

        Diff("""{"token":"tok_deadbeef"}""", """{"token":"tok_abc12345"}""", rule).Matches.Should().BeTrue();
        Diff("""{"token":"tok_deadbeef"}""", """{"token":"nope"}""", rule).Matches.Should().BeFalse();
    }

    [Fact]
    public void A_malformed_regex_fails_the_field_rather_than_the_run()
    {
        var result = Diff("""{"a":"x"}""", """{"a":"y"}""",
            new ComparisonRule { Path = "$.a", Kind = MatcherKind.Regex, Text = "([unclosed" });

        result.Findings.Single().Reason.Should().Contain("not a valid regular expression");
    }

    [Fact]
    public void Exists_and_not_exists_judge_presence_alone()
    {
        Diff("{}", """{"a":1}""", new ComparisonRule { Path = "$.a", Kind = MatcherKind.Exists })
            .Matches.Should().BeTrue();

        Diff("""{"a":1}""", "{}", new ComparisonRule { Path = "$.a", Kind = MatcherKind.Exists })
            .Findings.Single().Reason.Should().Contain("missing");

        Diff("""{"a":1}""", """{"a":1}""", new ComparisonRule { Path = "$.a", Kind = MatcherKind.NotExists })
            .Findings.Single().Reason.Should().Contain("should not be");
    }

    [Fact]
    public void A_later_rule_overrides_an_earlier_one()
    {
        // Read top to bottom like a stylesheet: a broad rule, then the exception to it. That is
        // the order people write them in.
        var result = Diff(
            """{"items":[{"t":"a"},{"t":"b"}]}""",
            """{"items":[{"t":"x"},{"t":"b"}]}""",
            new ComparisonRule { Path = "$..t", Kind = MatcherKind.Ignore },
            new ComparisonRule { Path = "$.items[0].t", Kind = MatcherKind.Exact });

        result.Matches.Should().BeFalse();
        result.Findings.Single().Path.Should().Be("$.items[0].t");
    }

    [Fact]
    public void A_rule_with_an_unparseable_path_is_reported_rather_than_dropped()
    {
        var result = Diff("""{"a":1}""", """{"a":1}""",
            new ComparisonRule { Path = "$.items[", Kind = MatcherKind.Ignore });

        // Silently ignoring it would mean somebody believes a field is excluded when it is not.
        result.InvalidRules.Should().ContainSingle();
    }

    [Fact]
    public void A_disabled_rule_does_not_apply()
    {
        var result = Diff("""{"a":1}""", """{"a":2}""",
            new ComparisonRule { Path = "$.a", Kind = MatcherKind.Ignore, Enabled = false });

        result.Matches.Should().BeFalse();
    }

    // ---- arrays -------------------------------------------------------------------------------

    [Fact]
    public void An_ordered_array_reports_each_position()
    {
        var result = Diff("""{"t":[1,2,3]}""", """{"t":[1,9,3]}""");

        result.Findings.Single().Path.Should().Be("$.t[1]");
    }

    [Fact]
    public void An_unordered_array_that_was_only_shuffled_reports_order_changed()
    {
        // The fake API's categories endpoint shuffles on every call, which is exactly why this
        // category exists: without it, a reshuffle reads as every row differing.
        var result = Diff("""{"t":["a","b","c"]}""", """{"t":["c","a","b"]}""",
            new ComparisonRule { Path = "$.t", Kind = MatcherKind.ArrayUnordered });

        result.Findings.Should().ContainSingle();
        result.Findings[0].Kind.Should().Be(DiffKind.OrderChanged);
    }

    [Fact]
    public void An_unordered_array_still_reports_a_genuinely_missing_item()
    {
        var result = Diff("""{"t":["a","b","c"]}""", """{"t":["c","a"]}""",
            new ComparisonRule { Path = "$.t", Kind = MatcherKind.ArrayUnordered });

        result.Count(DiffKind.Removed).Should().Be(1);
    }

    [Fact]
    public void Matching_by_key_finds_the_one_field_that_moved()
    {
        // An ordered walk over this would report every position from the first onwards. Matching
        // by id reports the single field that changed, which is the answer.
        var result = Diff(
            """{"items":[{"id":1,"name":"a"},{"id":2,"name":"b"},{"id":3,"name":"c"}]}""",
            """{"items":[{"id":3,"name":"c"},{"id":1,"name":"CHANGED"},{"id":2,"name":"b"}]}""",
            new ComparisonRule { Path = "$.items", Kind = MatcherKind.ArrayMatchByKey, Text = "id" });

        result.Findings.Should().ContainSingle();
        result.Findings[0].Kind.Should().Be(DiffKind.Changed);
        result.Findings[0].Path.Should().Contain("id=1").And.EndWith(".name");
    }

    [Fact]
    public void Matching_by_key_reports_an_item_that_appeared_and_one_that_went()
    {
        var result = Diff(
            """{"items":[{"id":1},{"id":2}]}""",
            """{"items":[{"id":2},{"id":3}]}""",
            new ComparisonRule { Path = "$.items", Kind = MatcherKind.ArrayMatchByKey, Text = "id" });

        result.Count(DiffKind.Removed).Should().Be(1);
        result.Count(DiffKind.Added).Should().Be(1);
    }

    [Fact]
    public void Matching_by_key_says_so_when_the_key_is_not_unique()
    {
        var result = Diff(
            """{"items":[{"id":1}]}""",
            """{"items":[{"id":1},{"id":1}]}""",
            new ComparisonRule { Path = "$.items", Kind = MatcherKind.ArrayMatchByKey, Text = "id" });

        result.Findings.Should().Contain(f => f.Reason != null && f.Reason.Contains("not unique"));
    }

    [Fact]
    public void Matching_by_key_needs_a_key()
    {
        var result = Diff("""{"items":[]}""", """{"items":[]}""",
            new ComparisonRule { Path = "$.items", Kind = MatcherKind.ArrayMatchByKey });

        result.Findings.Single().Reason.Should().Contain("needs the name of the field");
    }

    [Fact]
    public void An_array_count_rule_ignores_the_contents()
    {
        var rule = new ComparisonRule
        {
            Path = "$.items", Kind = MatcherKind.ArrayCount, Number = 1, Number2 = 5,
        };

        Diff("""{"items":[1,2]}""", """{"items":["x","y","z"]}""", rule).Matches.Should().BeTrue();
        Diff("""{"items":[1,2]}""", """{"items":[]}""", rule).Findings.Single().Reason
            .Should().Contain("fewer than 1");
    }

    [Fact]
    public void A_subset_rule_allows_extra_fields_but_not_changed_ones()
    {
        var rule = new ComparisonRule { Path = "$", Kind = MatcherKind.JsonSubset };

        Diff("""{"a":1}""", """{"a":1,"b":2}""", rule).Matches.Should().BeTrue();
        Diff("""{"a":1}""", """{"a":2,"b":3}""", rule).Findings.Single().Path.Should().Be("$.a");
    }

    // ---- edges --------------------------------------------------------------------------------

    [Fact]
    public void A_response_that_is_not_json_is_compared_as_text_and_says_so()
    {
        var result = SemanticDiff.CompareText("""{"a":1}""", "<html>error</html>");

        result.Matches.Should().BeFalse();
        result.Root.Reason.Should().Contain("not JSON");
    }

    [Fact]
    public void Two_identical_non_json_responses_match() =>
        SemanticDiff.CompareText("plain text", "plain text").Matches.Should().BeTrue();

    [Fact]
    public void Nesting_deeper_than_the_limit_is_reported_rather_than_overflowing()
    {
        // A pathological payload must fail one comparison, not take the process down with it.
        var deep = Nest(SemanticDiff.MaxDepth + 20);

        var result = SemanticDiff.CompareText(deep, deep);

        result.Findings.Should().Contain(f => f.Reason != null && f.Reason.Contains("nesting deeper"));
    }

    [Fact]
    public void A_large_payload_compares_in_reasonable_time()
    {
        // Two thousand records. The diff viewer virtualises rendering; the engine still has to
        // produce the tree, and it is the half that runs on the server for every run.
        var expected = Records(2_000, changeAt: -1);
        var actual = Records(2_000, changeAt: 1_500);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = SemanticDiff.CompareText(expected, actual,
            new ComparisonRuleSet([new ComparisonRule
            {
                Path = "$.items", Kind = MatcherKind.ArrayMatchByKey, Text = "id",
            }]));
        started.Stop();

        result.Findings.Should().ContainSingle();
        started.ElapsedMilliseconds.Should().BeLessThan(3_000);
    }

    [Fact]
    public void Counts_do_not_include_the_containers_walked_through()
    {
        // "3 changed" has to mean three fields, not three fields plus every object above them.
        var result = Diff(
            """{"a":{"b":{"c":1}}}""",
            """{"a":{"b":{"c":2}}}""");

        result.Count(DiffKind.Changed).Should().Be(1);
        result.Findings.Should().ContainSingle();
    }

    private static IEnumerable<DiffNode> Flatten(DiffNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child)) yield return descendant;
        }
    }

    private static string Nest(int depth)
    {
        var text = "1";
        for (var i = 0; i < depth; i++) text = $$"""{"a":{{text}}}""";
        return text;
    }

    private static string Records(int count, int changeAt)
    {
        var items = Enumerable.Range(0, count).Select(i =>
            $$"""{"id":{{i}},"name":"record {{i}}","value":{{(i == changeAt ? 999 : i)}}}""");

        return $$"""{"items":[{{string.Join(",", items)}}]}""";
    }
}
