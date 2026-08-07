using System.Text.Json.Nodes;
using FluentAssertions;
using ProofFlow.TestEngine.Comparison;

namespace ProofFlow.Tests;

/// <summary>
/// The suggester that decides whether anyone keeps their baselines.
///
/// Approve a response containing a request id and a timestamp, and without this every future run
/// fails on fields nobody cares about — after the third time people stop reading the results. The
/// opposite mistake is worse though: a field silently excluded is a field that stopped being
/// checked without anyone deciding to, so confidence is graded and nothing is ever applied here.
/// </summary>
public class DynamicFieldDetectorTests
{
    private static IReadOnlyList<DynamicFieldSuggestion> Suggest(string json) =>
        DynamicFieldDetector.Suggest(JsonNode.Parse(json));

    [Fact]
    public void A_guid_is_recognised_by_its_shape_whatever_the_field_is_called()
    {
        var found = Suggest("""{"someField":"3f2504e0-4f89-41d3-9a0c-0305e82c3301"}""");

        found.Should().ContainSingle();
        found[0].Reason.Should().Be(DynamicReason.Guid);
        found[0].Confidence.Should().Be(Confidence.Certain);
        // Type-only rather than ignore: an id that becomes a number is still a defect.
        found[0].Rule.Kind.Should().Be(MatcherKind.TypeOnly);
        found[0].Rule.Path.Should().Be("$.someField");
    }

    [Fact]
    public void A_jwt_is_recognised()
    {
        var found = Suggest(
            """{"accessToken":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghijklmnop"}""");

        found[0].Reason.Should().Be(DynamicReason.Token);
        found[0].Confidence.Should().Be(Confidence.Certain);
    }

    [Fact]
    public void A_signed_url_is_recognised_by_its_query()
    {
        var found = Suggest(
            """{"download":"https://s3.example.com/f.pdf?X-Amz-Signature=abc&Expires=1754"}""");

        found[0].Reason.Should().Be(DynamicReason.ExpiringUrl);
    }

    [Fact]
    public void A_timestamp_in_a_field_named_for_one_is_certain()
    {
        var found = Suggest("""{"createdAt":"2026-08-07T09:30:00Z"}""");

        found[0].Reason.Should().Be(DynamicReason.Timestamp);
        found[0].Confidence.Should().Be(Confidence.Certain);
        found[0].Rule.Kind.Should().Be(MatcherKind.Ignore);
    }

    [Fact]
    public void A_date_somewhere_else_is_only_a_possibility()
    {
        // A birth date is a date and is not dynamic. Pre-ticking this would stop a real check from
        // ever running, which is the expensive direction of the mistake.
        var found = Suggest("""{"dateOfBirth":"1985-03-14"}""");

        found.Should().ContainSingle();
        found[0].Confidence.Should().Be(Confidence.Possible);
    }

    [Fact]
    public void Names_that_say_what_they_are_are_recognised()
    {
        Suggest("""{"requestId":"abc123"}""")[0].Reason.Should().Be(DynamicReason.TraceId);
        Suggest("""{"nonce":"xyz"}""")[0].Reason.Should().Be(DynamicReason.Random);
    }

    [Fact]
    public void Ordinary_data_is_left_alone()
    {
        // The failure mode that matters most: over-suggesting turns a baseline into a shell that
        // checks nothing.
        var found = Suggest("""{"id":21289,"name":"Study A","price":120.5,"active":true}""");

        found.Should().BeEmpty();
    }

    [Fact]
    public void Suggestions_reach_into_arrays_and_carry_a_usable_path()
    {
        var found = Suggest(
            """{"items":[{"id":1,"updatedAt":"2026-08-07T09:00:00Z"},{"id":2,"updatedAt":"2026-08-07T10:00:00Z"}]}""");

        found.Should().HaveCount(2);
        // The concrete path, not a wildcard: the interface offers collapsing them, and inventing a
        // wildcard here would silently cover items nobody looked at.
        found.Select(s => s.Path).Should().BeEquivalentTo(["$.items[0].updatedAt", "$.items[1].updatedAt"]);
    }

    [Fact]
    public void Two_captures_of_the_same_call_prove_which_fields_move()
    {
        // The strongest evidence available, and what capture mode uses: not "this looks dynamic"
        // but "this actually differed between two runs of the same request".
        var first = JsonNode.Parse("""{"id":1,"total":100,"serverNode":"web-03"}""");
        var second = JsonNode.Parse("""{"id":1,"total":100,"serverNode":"web-07"}""");

        var found = DynamicFieldDetector.SuggestFromPair(first, second);

        found.Should().ContainSingle();
        found[0].Path.Should().Be("$.serverNode");
        found[0].Reason.Should().Be(DynamicReason.DiffersBetweenRuns);
        found[0].Confidence.Should().Be(Confidence.Certain);
    }

    [Fact]
    public void Two_identical_captures_suggest_nothing()
    {
        var same = """{"id":1,"total":100}""";

        DynamicFieldDetector.SuggestFromPair(JsonNode.Parse(same), JsonNode.Parse(same))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_suggestion_becomes_a_rule_that_actually_silences_the_difference()
    {
        // The loop closes: suggest, accept, and the diff that prompted it goes quiet — while a
        // real change in the same document still reports.
        const string before = """{"requestId":"3f2504e0-4f89-41d3-9a0c-0305e82c3301","price":120}""";
        const string after = """{"requestId":"9c858901-8a57-4791-81fe-4c455b099bc9","price":125}""";

        var noisy = SemanticDiff.CompareText(before, after);
        noisy.Findings.Should().HaveCount(2);

        var suggestion = DynamicFieldDetector.Suggest(JsonNode.Parse(before))
            .Single(s => s.Path == "$.requestId");

        var quieter = SemanticDiff.CompareText(before, after, new ComparisonRuleSet([suggestion.Rule]));

        quieter.Findings.Should().ContainSingle();
        quieter.Findings[0].Path.Should().Be("$.price");
    }
}
