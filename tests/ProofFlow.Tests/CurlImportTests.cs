using FluentAssertions;
using ProofFlow.Infrastructure.Portability.Importers;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Tests;

/// <summary>
/// What a browser's "copy as cURL" actually produces, read back.
///
/// The examples here are the shapes that arrive in practice rather than the ones a parser is easy
/// to write against: continuations, single quotes around a URL with an ampersand in it, a JSON body
/// with escaped quotes inside double quotes, and a bearer token somebody forgot was in there.
/// </summary>
public class CurlImportTests
{
    [Fact]
    public void The_simplest_one()
    {
        var imported = CurlImporter.Read("curl https://api.example.com/products");

        imported.Refusal.Should().BeNull();

        var request = imported.Requests.Should().ContainSingle().Subject.Request;

        request.Method.Should().Be("GET");
        request.Url.Should().Be("https://api.example.com/products");
        imported.BaseUrl.Should().Be("https://api.example.com");
    }

    [Fact]
    public void A_body_without_a_method_is_a_post()
    {
        // curl's own rule, and the one people rely on when they type it by hand.
        var request = One("""curl https://api.example.com/products -d '{"name":"Anvil"}'""");

        request.Method.Should().Be("POST");
        request.Body!.Kind.Should().Be(BodyKind.Json);
        request.Body.Content.Should().Be("""{"name":"Anvil"}""");
    }

    [Fact]
    public void A_command_spread_over_several_lines_is_one_command()
    {
        var request = One("""
            curl 'https://api.example.com/products?page=2&size=10' \
              -X PATCH \
              -H 'Content-Type: application/json' \
              -H 'Accept-Language: fa' \
              --data-raw '{"price":19}'
            """);

        request.Method.Should().Be("PATCH");

        // The query string survived. A splitter that ignored quotes would have cut it at the
        // ampersand and produced a request for page 2 of nothing.
        request.Url.Should().Be("https://api.example.com/products?page=2&size=10");

        request.Headers.Should().HaveCount(2);
        request.Headers.Should().Contain(header => header.Name == "Accept-Language" && header.Value == "fa");
        request.Body!.Content.Should().Be("""{"price":19}""");
    }

    [Fact]
    public void Escaped_quotes_inside_a_double_quoted_body_survive()
    {
        // How Windows shells produce it, and the case a naive splitter mangles.
        var request = One("""curl https://api.example.com/x -d "{\"name\":\"Anvil\"}" """);

        request.Body!.Content.Should().Be("""{"name":"Anvil"}""");
    }

    [Fact]
    public void A_token_in_the_command_becomes_a_reference_and_is_not_kept()
    {
        var imported = CurlImporter.Read(
            "curl https://api.example.com/me -H 'Authorization: Bearer eyJhbGciOi.REAL.TOKEN'");

        var request = imported.Requests.Single().Request;

        // The header is still there — the request needs it — but it points at a secret.
        request.Headers.Should().ContainSingle()
            .Which.Value.Should().Be("{{secrets.authorization}}");

        imported.SecretsToSupply.Should().Contain("authorization");

        // And the value they pasted is nowhere at all.
        request.Headers.Should().NotContain(header => header.Value.Contains("REAL.TOKEN"));
    }

    [Theory]
    [InlineData("X-Api-Key", "xApiKey")]
    [InlineData("x-auth-token", "xAuthToken")]
    [InlineData("Cookie", "cookie")]
    [InlineData("X-Session-Secret", "xSessionSecret")]
    public void Anything_that_smells_like_a_credential_gets_the_same_treatment(
        string header, string secret)
    {
        var imported = CurlImporter.Read($"curl https://x.test/y -H '{header}: the-real-value'");

        imported.SecretsToSupply.Should().Contain(secret);
        imported.Requests.Single().Request.Headers.Single().Value.Should().Be($"{{{{secrets.{secret}}}}}");
    }

    [Fact]
    public void An_ordinary_header_that_merely_contains_a_word_is_left_alone()
    {
        // "x-request-id" is not a credential, and an importer that redacted it would be teaching
        // people to ignore what it says.
        var request = One("curl https://x.test/y -H 'X-Request-Id: abc-123'");

        request.Headers.Single().Value.Should().Be("abc-123");
    }

    [Fact]
    public void Basic_credentials_keep_the_name_and_lose_the_password()
    {
        var imported = CurlImporter.Read("curl https://x.test/y -u alice:hunter2");

        var authentication = imported.Requests.Single().Request.Authentication!;

        authentication.Kind.Should().Be(AuthenticationKind.Basic);
        authentication.Username.Should().Be("alice");
        authentication.Password.Should().Be("{{secrets.password}}");

        imported.Notes.Should().Contain("import.note.basicPassword");
    }

    [Fact]
    public void Turning_off_certificate_checks_is_reported_rather_than_obeyed()
    {
        // It is a decision about an environment, made by somebody who meant it, on the page where
        // that decision lives.
        var imported = CurlImporter.Read("curl -k https://self-signed.test/y");

        imported.Notes.Should().Contain("import.note.insecure");
        imported.Requests.Should().ContainSingle();
    }

    [Fact]
    public void Form_fields_become_a_multipart_body()
    {
        var request = One("curl https://x.test/upload -F 'name=Anvil' -F 'weight=16'");

        request.Body!.Kind.Should().Be(BodyKind.Multipart);
        request.Body.Form.Should().HaveCount(2);
        request.Method.Should().Be("POST");
    }

    [Fact]
    public void Repeated_data_flags_are_joined_the_way_curl_joins_them()
    {
        var request = One("curl https://x.test/y -d 'a=1' -d 'b=2'");

        request.Body!.Content.Should().Be("a=1&b=2");
        request.Body.Kind.Should().Be(BodyKind.FormUrlEncoded);
    }

    [Fact]
    public void Flags_that_take_a_value_do_not_swallow_the_url()
    {
        var request = One("curl --max-time 30 -o out.json https://x.test/y");

        request.Url.Should().Be("https://x.test/y");
    }

    [Fact]
    public void An_unknown_flag_is_reported_rather_than_guessed_at()
    {
        var imported = CurlImporter.Read("curl --no-such-flag https://x.test/y");

        imported.Notes.Should().Contain("import.note.unknownFlag");

        // And the URL is still found, which is what would have been lost by assuming the flag
        // takes a value.
        imported.Requests.Single().Request.Url.Should().Be("https://x.test/y");
    }

    [Theory]
    [InlineData("", "import.empty")]
    [InlineData("   ", "import.empty")]
    [InlineData("wget https://x.test", "import.notCurl")]
    [InlineData("curl -X POST", "import.noUrl")]
    public void What_it_will_not_read_it_says_so_about(string command, string refusal)
    {
        CurlImporter.Read(command).Refusal.Should().Be(refusal);
    }

    [Fact]
    public void An_empty_quoted_argument_is_still_an_argument()
    {
        // -d '' is a real thing people write, and dropping it turns a POST into a GET.
        var request = One("curl -d '' https://x.test");

        request.Method.Should().Be("POST");
        request.Url.Should().Be("https://x.test");
        request.Body!.Content.Should().BeEmpty();
    }

    private static HttpRequestDefinition One(string command)
    {
        var imported = CurlImporter.Read(command);

        imported.Refusal.Should().BeNull();

        return imported.Requests.Should().ContainSingle().Subject.Request;
    }
}
