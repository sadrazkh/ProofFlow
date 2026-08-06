using FluentAssertions;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Redaction;

namespace ProofFlow.Tests;

/// <summary>
/// Secrets must not survive into a log, a report, a diff or an export.
///
/// The failure this guards against is quiet and permanent: a run report containing a working
/// bearer token gets attached to a ticket, forwarded, and indexed.
/// </summary>
public class RedactionTests
{
    [Fact]
    public void A_value_this_run_used_is_replaced_wherever_it_appears()
    {
        var scope = new RedactionScope();
        scope.Remember("s3cr3t-token-value");

        var text = scope.Apply("called https://api/x?key=s3cr3t-token-value and got 200");

        text.Should().NotContain("s3cr3t-token-value");
        text.Should().Contain(Redactor.Mask);
        // Everything else survives — a report where half the values read «redacted» is one nobody
        // can review, which ends with someone switching redaction off.
        text.Should().Contain("https://api/x?key=");
        text.Should().Contain("got 200");
    }

    [Fact]
    public void Longer_values_are_replaced_before_shorter_ones_they_contain()
    {
        var scope = new RedactionScope();
        scope.Remember("abcd");
        scope.Remember("abcdefgh");

        // Replacing "abcd" first would leave "efgh" of the longer secret sitting in the output.
        scope.Apply("value=abcdefgh").Should().Be($"value={Redactor.Mask}");
    }

    [Fact]
    public void Very_short_values_are_not_remembered()
    {
        var scope = new RedactionScope();
        scope.Remember("1");
        scope.Remember("ab");

        // A secret whose value is "1" would turn every number in every response into «redacted»,
        // which destroys the diff the product exists to show.
        scope.Apply("the price is 1 and the id is ab").Should().Be("the price is 1 and the id is ab");
    }

    [Fact]
    public void A_JWT_is_redacted_even_though_ProofFlow_never_saw_it()
    {
        // This is the case literal replacement cannot cover: a token minted by the API under test
        // and returned in its response.
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        Redactor.Redact($"{{\"access\":\"{jwt}\"}}").Should().NotContain(jwt);
    }

    [Theory]
    [InlineData("sk-abcdefghijklmnopqrstuvwx")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("xoxb-1234567890-abcdefghij")]
    public void Provider_issued_keys_are_recognised_by_shape(string key) =>
        Redactor.Redact($"the key is {key} ok").Should().NotContain(key);

    [Fact]
    public void A_json_field_that_names_itself_is_redacted()
    {
        var redacted = Redactor.Redact("""{"clientSecret":"abc123xyz","name":"Widget"}""");

        redacted.Should().NotContain("abc123xyz");
        // The field name and the ordinary data are kept — the diff still has to be readable.
        redacted.Should().Contain("clientSecret");
        redacted.Should().Contain("Widget");
    }

    [Fact]
    public void Sensitive_headers_are_masked_whatever_they_contain()
    {
        var headers = new List<KeyValueEntry>
        {
            new("Authorization", "Bearer abcdefghijklmnop"),
            new("Cookie", "session=xyz"),
            new("Content-Type", "application/json"),
            new("X-Request-Id", "req-42"),
        };

        var redacted = Redactor.RedactHeaders(headers);

        redacted.Single(h => h.Name == "Authorization").Value.Should().Be(Redactor.Mask);
        redacted.Single(h => h.Name == "Cookie").Value.Should().Be(Redactor.Mask);
        // A header that is not a credential stays legible. Masking Content-Type would make the
        // response viewer unable to say why it chose the raw view.
        redacted.Single(h => h.Name == "Content-Type").Value.Should().Be("application/json");
        redacted.Single(h => h.Name == "X-Request-Id").Value.Should().Be("req-42");
    }

    [Fact]
    public void Header_names_are_matched_without_regard_to_case()
    {
        // HTTP/2 lowercases every header name, so a case-sensitive list stops working the moment
        // the API under test is upgraded.
        Redactor.IsSensitiveHeader("authorization").Should().BeTrue();
        Redactor.IsSensitiveHeader("Authorization").Should().BeTrue();
        Redactor.IsSensitiveHeader("AUTHORIZATION").Should().BeTrue();
    }

    [Fact]
    public void Ordinary_values_are_left_alone()
    {
        const string body = """{"id":21289,"name":"Study A","createdAt":"2026-08-06T10:00:00Z","score":12.5}""";

        Redactor.Redact(body).Should().Be(body);
    }

    [Fact]
    public void Null_and_empty_input_do_not_throw() =>
        Redactor.Redact(null).Should().BeEmpty();
}
