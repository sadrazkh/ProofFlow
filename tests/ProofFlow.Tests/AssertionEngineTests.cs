using FluentAssertions;
using ProofFlow.TestEngine.Assertions;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Tests;

/// <summary>
/// The checks, and — more importantly — what they say when they fail.
///
/// The brief forbids showing a reader a stack trace, and an assertion message is where that rule
/// is either honoured or quietly broken. "Assert failed: $.price" is a line a developer can work
/// with and nobody else can. Every message here is asserted on, because a message nobody tests is
/// a message that drifts back into jargon.
/// </summary>
public class AssertionEngineTests
{
    private static HttpExchangeResult Response(
        string body = """{"id":21289,"price":120,"name":"Study A","tags":["a","b"]}""",
        int status = 200,
        int durationMs = 40,
        params KeyValueEntry[] headers) =>
        new()
        {
            ResolvedUrl = "https://api.example.com/studies/21289",
            Method = "GET",
            StatusCode = status,
            ReasonPhrase = status == 200 ? "OK" : "Not Found",
            Body = body,
            ContentType = "application/json",
            BodyBytes = body.Length,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            ResponseHeaders = headers.Length > 0
                ? headers
                : [new KeyValueEntry("Content-Type", "application/json")],
        };

    private static AssertionOutcome Run(Assertion assertion, HttpExchangeResult? response = null) =>
        AssertionEngine.Run([assertion], response ?? Response()).Single();

    [Fact]
    public void A_status_check_that_fails_names_both_numbers()
    {
        var outcome = Run(
            new Assertion { Kind = AssertionKind.StatusCode, Expected = "201" },
            Response(status: 200));

        outcome.Passed.Should().BeFalse();
        outcome.Summary.Should().Be("the status was expected to be 201 but came back as 200 (OK)");
    }

    [Fact]
    public void A_field_check_that_fails_reads_like_the_brief_asks()
    {
        // The brief's own example: "the field price was expected to be 120 but came back as 125".
        var outcome = Run(
            new Assertion { Kind = AssertionKind.JsonField, Target = "$.price", Expected = "120" },
            Response("""{"price":125}"""));

        outcome.Passed.Should().BeFalse();
        outcome.Summary.Should().Be("the field price was expected to be 120 but came back as 125");
    }

    [Fact]
    public void An_expected_value_may_be_written_without_quotes()
    {
        // Somebody typing Electronics means the string; somebody typing 120 means the number.
        // Making them learn which needs quotes is a distinction this reader should not carry.
        Run(new Assertion { Kind = AssertionKind.JsonField, Target = "$.name", Expected = "Study A" })
            .Passed.Should().BeTrue();

        Run(new Assertion { Kind = AssertionKind.JsonField, Target = "$.price", Expected = "120" })
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void A_missing_field_says_so_rather_than_comparing_nothing()
    {
        var outcome = Run(new Assertion
        {
            Kind = AssertionKind.JsonField, Target = "$.nope", Expected = "1",
        });

        outcome.Passed.Should().BeFalse();
        outcome.Summary.Should().Contain("nothing at $.nope");
    }

    [Fact]
    public void A_path_matching_several_places_is_refused_rather_than_guessed()
    {
        // Checking the first of five silently reports a pass nobody asked for.
        var outcome = Run(
            new Assertion { Kind = AssertionKind.JsonField, Target = "$.items[*].id", Expected = "1" },
            Response("""{"items":[{"id":1},{"id":2}]}"""));

        outcome.Passed.Should().BeFalse();
        outcome.Summary.Should().Contain("matched 2 places");
    }

    [Fact]
    public void A_field_check_can_use_any_matcher()
    {
        Run(new Assertion
        {
            Kind = AssertionKind.JsonField, Target = "$.price",
            Matcher = MatcherKind.NumericRange, Number = 100, Number2 = 150,
        }).Passed.Should().BeTrue();

        var outside = Run(new Assertion
        {
            Kind = AssertionKind.JsonField, Target = "$.price",
            Matcher = MatcherKind.NumericRange, Number = 200, Number2 = 300,
        });

        outside.Passed.Should().BeFalse();
        outside.Summary.Should().Contain("outside 200 to 300");
    }

    [Fact]
    public void A_header_check_names_the_header_when_it_is_absent()
    {
        var outcome = Run(new Assertion
        {
            Kind = AssertionKind.Header, Target = "X-Request-Id", Matcher = MatcherKind.Exists,
        });

        outcome.Passed.Should().BeFalse();
        outcome.Summary.Should().Contain("no «X-Request-Id» header");
    }

    [Fact]
    public void Header_names_are_matched_without_regard_to_case()
    {
        // HTTP/2 lowercases every header name, so a case-sensitive check breaks the day the API
        // under test is upgraded.
        Run(new Assertion
            {
                Kind = AssertionKind.Header, Target = "content-type",
                Matcher = MatcherKind.Contains, Expected = "json",
            },
            Response(headers: new KeyValueEntry("Content-Type", "application/json")))
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void A_response_time_check_reports_both_numbers()
    {
        var outcome = Run(
            new Assertion { Kind = AssertionKind.ResponseTime, Number = 100 },
            Response(durationMs: 250));

        outcome.Passed.Should().BeFalse();
        outcome.Summary.Should().Be("the response took 250ms, over the 100ms limit");
    }

    [Fact]
    public void A_schema_check_passes_a_matching_response()
    {
        var outcome = Run(new Assertion
        {
            Kind = AssertionKind.Schema,
            Expected = """
                {"type":"object","required":["id","price"],
                 "properties":{"id":{"type":"integer"},"price":{"type":"number"}}}
                """,
        });

        outcome.Passed.Should().BeTrue();
    }

    [Fact]
    public void A_schema_check_says_where_the_response_broke_it()
    {
        var outcome = Run(
            new Assertion
            {
                Kind = AssertionKind.Schema,
                Expected = """{"type":"object","required":["id"],"properties":{"id":{"type":"integer"}}}""",
            },
            Response("""{"id":"not-a-number"}"""));

        outcome.Passed.Should().BeFalse();
        outcome.Summary.Should().Contain("does not match the schema");
    }

    [Fact]
    public void A_broken_schema_blames_the_schema_rather_than_the_response()
    {
        var outcome = Run(new Assertion { Kind = AssertionKind.Schema, Expected = "{not json" });

        outcome.Passed.Should().BeFalse();
        outcome.Summary.Should().Contain("the schema itself is not valid");
    }

    [Fact]
    public void When_the_request_never_completed_every_check_says_so()
    {
        // One cause, reported once. Listing five assertion failures underneath a connection
        // refusal buries the only line that matters.
        var failed = new HttpExchangeResult
        {
            ResolvedUrl = "https://api.example.com/x",
            Method = "GET",
            Failure = new HttpFailure(HttpFailureKind.Timeout, "No response within 30 seconds."),
        };

        var outcomes = AssertionEngine.Run(
        [
            new Assertion { Kind = AssertionKind.StatusCode, Expected = "200" },
            new Assertion { Kind = AssertionKind.JsonField, Target = "$.id", Expected = "1" },
        ], failed);

        outcomes.Should().OnlyContain(o => !o.Passed);
        outcomes.Should().OnlyContain(o => o.Summary.Contains("never completed"));
        outcomes[0].Detail.Should().Contain("No response within 30 seconds");
    }

    [Fact]
    public void A_disabled_assertion_does_not_run() =>
        AssertionEngine.Run(
            [new Assertion { Kind = AssertionKind.StatusCode, Expected = "999", Enabled = false }],
            Response()).Should().BeEmpty();

    [Fact]
    public void A_non_json_response_is_explained_rather_than_parsed()
    {
        var outcome = Run(
            new Assertion { Kind = AssertionKind.JsonField, Target = "$.id", Expected = "1" },
            Response("<html>gateway timeout</html>") with { ContentType = "text/html" });

        outcome.Summary.Should().Contain("not JSON");
    }
}
