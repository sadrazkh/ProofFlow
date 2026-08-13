using FluentAssertions;
using ProofFlow.Infrastructure.Portability.Importers;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Tests;

/// <summary>
/// A Postman collection, read the way one actually arrives: folders inside folders, a URL written
/// as a structure in one request and as a string in the next, a live token in a collection variable,
/// and scripts that are not going to be run.
/// </summary>
public class PostmanImportTests
{
    private const string Collection = """
        {
          "info": {
            "name": "Catalog",
            "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
          },
          "variable": [
            { "key": "baseUrl", "value": "https://api.example.com" },
            { "key": "authToken", "value": "a-live-token-nobody-meant-to-share" }
          ],
          "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{authToken}}" } ] },
          "item": [
            {
              "name": "Products",
              "item": [
                {
                  "name": "List",
                  "request": {
                    "method": "GET",
                    "header": [
                      { "key": "Accept", "value": "application/json" },
                      { "key": "X-Api-Key", "value": "another-real-one" },
                      { "key": "X-Debug", "value": "1", "disabled": true }
                    ],
                    "url": {
                      "raw": "{{baseUrl}}/products?page=1",
                      "protocol": "https",
                      "host": ["api", "example", "com"],
                      "path": ["products"]
                    }
                  },
                  "event": [ { "listen": "test", "script": { "exec": ["pm.test('ok', ...)"] } } ]
                },
                {
                  "name": "Create",
                  "request": {
                    "method": "POST",
                    "body": {
                      "mode": "raw",
                      "raw": "{\"name\":\"Anvil\"}",
                      "options": { "raw": { "language": "json" } }
                    },
                    "url": "{{baseUrl}}/products"
                  }
                }
              ]
            },
            {
              "name": "Sign in",
              "request": {
                "method": "POST",
                "auth": { "type": "basic", "basic": [ { "key": "username", "value": "alice" } ] },
                "body": {
                  "mode": "urlencoded",
                  "urlencoded": [
                    { "key": "grant_type", "value": "password" },
                    { "key": "scope", "value": "read", "disabled": true }
                  ]
                },
                "url": { "raw": "{{baseUrl}}/auth/login" }
              }
            }
          ]
        }
        """;

    [Fact]
    public void Folders_become_groups_and_every_leaf_becomes_a_request()
    {
        var imported = PostmanImporter.Read(Collection);

        imported.Refusal.Should().BeNull();
        imported.SuggestedName.Should().Be("Catalog");
        imported.Requests.Should().HaveCount(3);

        imported.Requests.Select(request => request.Name)
            .Should().BeEquivalentTo(["List", "Create", "Sign in"]);

        // Forty requests called "Get" would otherwise be forty scenarios called "Get".
        imported.Requests.Single(request => request.Name == "List").Group.Should().Be("Products");
        imported.Requests.Single(request => request.Name == "Sign in").Group.Should().BeNull();
    }

    [Fact]
    public void A_url_written_as_a_structure_and_one_written_as_a_string_both_arrive()
    {
        var imported = PostmanImporter.Read(Collection);

        Named(imported, "List").Request.Url.Should().Be("{{baseUrl}}/products?page=1");
        Named(imported, "Create").Request.Url.Should().Be("{{baseUrl}}/products");
    }

    [Fact]
    public void The_variable_syntax_is_the_same_syntax_so_it_survives_untouched()
    {
        // The reference in the URL is left exactly as written — Postman spells a variable the same
        // way this product does, which is the whole reason a collection crosses at all.
        Named(PostmanImporter.Read(Collection), "List")
            .Request.Url.Should().Contain("{{baseUrl}}");

        // And what it points at is the environment's address rather than a value beside it, so the
        // reference resolves on the first run instead of failing on an address nothing defines.
        PostmanImporter.Read(Collection).BaseUrl.Should().Be("https://api.example.com");
    }

    [Fact]
    public void A_token_in_a_collection_variable_becomes_a_secret_and_the_value_is_dropped()
    {
        // The most common way a live credential travels in one of these files.
        var imported = PostmanImporter.Read(Collection);

        imported.SecretsToSupply.Should().Contain("authToken");
        imported.Variables.Should().NotContain(variable => variable.Name == "authToken");

        // And nowhere in anything it produced.
        imported.Variables.Should().NotContain(v => v.Value.Contains("a-live-token"));
    }

    [Fact]
    public void An_api_key_header_gets_the_same_treatment()
    {
        var imported = PostmanImporter.Read(Collection);
        var headers = Named(imported, "List").Request.Headers;

        headers.Should().Contain(header =>
            header.Name == "X-Api-Key" && header.Value == "{{secrets.xApiKey}}");

        imported.SecretsToSupply.Should().Contain("xApiKey");
    }

    [Fact]
    public void A_disabled_row_stays_disabled_rather_than_being_deleted()
    {
        // Deleting a header somebody is experimenting with is how they lose the one that mattered.
        var headers = Named(PostmanImporter.Read(Collection), "List").Request.Headers;

        headers.Should().Contain(header => header.Name == "X-Debug" && !header.Enabled);

        var form = Named(PostmanImporter.Read(Collection), "Sign in").Request.Body!.Form;

        form.Should().Contain(field => field.Name == "scope" && !field.Enabled);
    }

    [Fact]
    public void Bodies_come_across_with_their_kind()
    {
        var imported = PostmanImporter.Read(Collection);

        var json = Named(imported, "Create").Request.Body!;
        json.Kind.Should().Be(BodyKind.Json);
        json.Content.Should().Be("""{"name":"Anvil"}""");

        var form = Named(imported, "Sign in").Request.Body!;
        form.Kind.Should().Be(BodyKind.FormUrlEncoded);
        form.Form.Should().HaveCount(2);
    }

    [Fact]
    public void Basic_authentication_keeps_the_name_and_loses_the_password()
    {
        var authentication = Named(PostmanImporter.Read(Collection), "Sign in").Request.Authentication!;

        authentication.Kind.Should().Be(AuthenticationKind.Basic);
        authentication.Username.Should().Be("alice");
        authentication.Password.Should().Be("{{secrets.password}}");
    }

    [Fact]
    public void Scripts_are_reported_and_not_run()
    {
        // Running JavaScript out of a file somebody was handed is the thing this product exists not
        // to need. Saying so is the whole of the obligation.
        PostmanImporter.Read(Collection).Notes.Should().Contain("import.note.scripts");
    }

    [Theory]
    [InlineData("", "import.empty")]
    [InlineData("{ not json", "import.notJson")]
    [InlineData("""{"info":{"name":"X"}}""", "import.notPostman")]
    [InlineData("""{"info":{"schema":"https://schema.getpostman.com/json/collection/v1.0.0/collection.json"}}""", "import.postmanV1")]
    [InlineData("""{"info":{"schema":"https://schema.getpostman.com/json/collection/v2.1.0/collection.json"},"item":[]}""", "import.noRequests")]
    public void What_it_will_not_read_it_says_so_about(string json, string refusal)
    {
        PostmanImporter.Read(json).Refusal.Should().Be(refusal);
    }

    private static ImportedRequest Named(Imported imported, string name) =>
        imported.Requests.Should().ContainSingle(request => request.Name == name).Subject;

    [Fact]
    public void Authentication_declared_once_at_the_top_reaches_every_request()
    {
        // The shape almost every real collection has: one auth block at the root and requests that
        // say nothing about it. Reading only the per-request block brought a whole API across with
        // no credential on any of it, and a 401 on the first run with nothing to point at.
        var imported = PostmanImporter.Read(Collection);

        imported.Requests.Should().NotBeEmpty();

        // Everything that says nothing about auth takes the collection's, and the one request that
        // declares its own keeps it.
        imported.Requests
            .Where(request => !request.Name.Contains("Sign in", StringComparison.Ordinal))
            .Should().OnlyContain(
                request => request.Request.Authentication != null
                           && request.Request.Authentication.Kind == AuthenticationKind.Bearer,
                "the collection declares bearer auth and nothing overrides it");

        imported.Requests.Single(request => request.Name.Contains("Sign in", StringComparison.Ordinal))
            .Request.Authentication!.Kind.Should().Be(AuthenticationKind.Basic);
    }

    [Fact]
    public void A_folder_can_override_what_the_collection_said()
    {
        const string json = """
            {
              "info": {
                "name": "Mixed",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{t}}" } ] },
              "item": [
                {
                  "name": "Public",
                  "auth": { "type": "noauth" },
                  "item": [
                    { "name": "Ping", "request": { "method": "GET", "url": "https://api.example.com/ping" } }
                  ]
                },
                {
                  "name": "Private",
                  "item": [
                    { "name": "Me", "request": { "method": "GET", "url": "https://api.example.com/me" } }
                  ]
                }
              ]
            }
            """;

        var imported = PostmanImporter.Read(json);

        imported.Requests.Single(r => r.Name.Contains("Ping")).Request.Authentication
            .Should().BeNull("the folder said noauth");

        imported.Requests.Single(r => r.Name.Contains("Me")).Request.Authentication!.Kind
            .Should().Be(AuthenticationKind.Bearer, "nothing overrode the collection");
    }

    [Fact]
    public void An_environment_export_is_read_rather_than_refused()
    {
        // A different file with a different shape, and the one people hand over alongside the
        // collection: it holds the addresses the collection refers to. Refusing it as «not Postman»
        // was true and useless.
        const string json = """
            {
              "id": "9d1f",
              "name": "Staging",
              "values": [
                { "key": "baseUrl", "value": "https://staging.example.com", "enabled": true },
                { "key": "pageSize", "value": "25", "enabled": true },
                { "key": "apiToken", "value": "a-live-token", "enabled": true },
                { "key": "unused", "value": "x", "enabled": false }
              ],
              "_postman_variable_scope": "environment"
            }
            """;

        var imported = PostmanImporter.Read(json);

        imported.Refusal.Should().BeNull();
        imported.SuggestedName.Should().Be("Staging");
        imported.BaseUrl.Should().Be("https://staging.example.com");

        imported.Variables.Select(variable => variable.Name)
            .Should().Contain("pageSize").And.NotContain("unused", "it was switched off")
            .And.NotContain("apiToken", "a credential is a secret to supply, not a variable");

        imported.SecretsToSupply.Should().Contain("apiToken");

        // And the value never travels, whatever else does.
        imported.Variables.Should().NotContain(variable => variable.Value.Contains("a-live-token"));
    }

    [Fact]
    public void The_collections_base_url_becomes_the_environments_address()
    {
        // Almost every collection declares «baseUrl» and writes every URL as {{baseUrl}}/…. Leaving
        // it among the variables imported a project with no environment in it: nothing to run
        // against, and a first run that fails on an address it could not resolve.
        var imported = PostmanImporter.Read(Collection);

        imported.BaseUrl.Should().Be("https://api.example.com");

        imported.Variables.Should().NotContain(
            variable => variable.Name == "baseUrl",
            "it is the environment now, not a value beside it");
    }
}
