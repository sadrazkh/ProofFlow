using System.Text.Json;
using FluentAssertions;
using ProofFlow.Infrastructure.Portability;
using ProofFlow.Infrastructure.Portability.Importers;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Tests;

/// <summary>
/// A foreign file becomes a bundle, and the bundle importer is the only thing that writes.
///
/// What matters here is that each request becomes an <i>endpoint</i>. It used to become a scenario
/// of three steps, and these tests asserted that shape faithfully — which is why they all failed
/// the moment the shape changed, and why they are rewritten rather than removed. A collection of
/// two thousand requests is two thousand endpoints; a scenario is a chain, and none of those was
/// one.
///
/// The request itself has to survive the crossing whole: method, address, headers and body. An
/// import that produced a list of names and lost the payloads would look like it worked right up
/// until somebody pressed Test.
/// </summary>
public class ImportedBundleTests
{
    private static HttpRequestDefinition Request(Contracts.Portability.BundleBaseline endpoint) =>
        JsonSerializer.Deserialize<HttpRequestDefinition>(
            endpoint.RequestJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void A_curl_command_becomes_an_endpoint_carrying_the_whole_request()
    {
        var bundle = ImportedBundle.From(CurlImporter.Read(
            """curl -X POST https://api.example.com/products -H 'Accept: application/json' -d '{"name":"Anvil"}'"""));

        // Not a scenario. A single call is an endpoint, and the section for chains stays for chains.
        bundle.Scenarios.Should().BeEmpty();

        var endpoint = bundle.Baselines.Should().ContainSingle().Subject;
        var request = Request(endpoint);

        request.Method.Should().Be("POST");
        request.Url.Should().Be("https://api.example.com/products");
        request.Body!.Content.Should().Be("""{"name":"Anvil"}""");
        request.Headers.Should().ContainSingle(header => header.Name == "Accept");

        // Nothing has been sent, so there is nothing to approve. Inventing an answer here would be
        // deciding what correct looks like from a file somebody exported out of Postman.
        endpoint.Approved.Should().BeNull();
    }

    [Fact]
    public void The_base_url_becomes_an_environment_and_the_endpoint_points_at_it()
    {
        var bundle = ImportedBundle.From(CurlImporter.Read("curl https://api.example.com/products"));

        var environment = bundle.Environments.Should().ContainSingle().Subject;

        environment.Slug.Should().Be("imported");
        environment.BaseUrl.Should().Be("https://api.example.com");

        bundle.Baselines.Single().Environment.Should().Be("imported");
    }

    [Fact]
    public void A_folder_name_goes_into_the_endpoint_name_rather_than_being_dropped()
    {
        var bundle = ImportedBundle.From(PostmanImporter.Read("""
            {
              "info": { "name": "C", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "Products", "item": [ { "name": "List", "request": { "method": "GET", "url": "https://x.test/p" } } ] },
                { "name": "Orders", "item": [ { "name": "List", "request": { "method": "GET", "url": "https://x.test/o" } } ] }
              ]
            }
            """));

        bundle.Baselines.Select(endpoint => endpoint.Name)
            .Should().BeEquivalentTo(["Products · List", "Orders · List"]);

        // And the two are distinct, which is what stops the second one being skipped as a
        // collision with the first.
        bundle.Baselines.Select(endpoint => endpoint.Slug).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Two_requests_that_make_the_same_slug_both_survive()
    {
        var bundle = ImportedBundle.From(PostmanImporter.Read("""
            {
              "info": { "name": "C", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "List", "request": { "method": "GET", "url": "https://x.test/a" } },
                { "name": "list", "request": { "method": "GET", "url": "https://x.test/b" } }
              ]
            }
            """));

        bundle.Baselines.Should().HaveCount(2);
        bundle.Baselines.Select(endpoint => endpoint.Slug).Should().OnlyHaveUniqueItems();

        // Names too, and not only slugs: the unique index is on the name, so two rows the slug
        // rule kept apart can still be one row the database refuses.
        bundle.Baselines.Select(endpoint => endpoint.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void An_open_api_document_becomes_one_endpoint_per_operation()
    {
        var bundle = ImportedBundle.From(OpenApiImporter.Read("""
            {
              "openapi": "3.0.0",
              "info": { "title": "Catalog API" },
              "servers": [ { "url": "https://api.example.com" } ],
              "paths": {
                "/products": {
                  "get": { "summary": "List", "responses": { "200": {} } },
                  "post": { "summary": "Create", "responses": { "201": {} } }
                }
              }
            }
            """));

        bundle.Project.Name.Should().Be("Catalog API");
        bundle.Baselines.Should().HaveCount(2);

        // The document's success status is not thrown away. There is nowhere structural to put it
        // — an endpoint's expectation is the answer somebody approved, and nothing has been sent —
        // so it is said to the person who will send it.
        Description(bundle, "List").Should().NotContain("answers");
        Description(bundle, "Create").Should().Contain("201");

        static string? Description(Contracts.Portability.Bundle bundle, string name) =>
            bundle.Baselines.Single(endpoint => endpoint.Name == name).Description;
    }

    [Fact]
    public void The_secrets_a_file_needs_are_carried_as_names_and_nothing_else()
    {
        var bundle = ImportedBundle.From(CurlImporter.Read(
            "curl https://x.test/y -H 'Authorization: Bearer a-real-token'"));

        bundle.SecretsToSupply.Should().ContainSingle()
            .Which.Name.Should().Be("authorization");

        BundleJson.Write(bundle).Should().NotContain("a-real-token");
    }

    [Fact]
    public void A_form_body_survives_the_crossing_without_the_rows_somebody_switched_off()
    {
        var bundle = ImportedBundle.From(PostmanImporter.Read("""
            {
              "info": { "name": "C", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [ {
                "name": "Sign in",
                "request": {
                  "method": "POST",
                  "url": "https://x.test/login",
                  "body": {
                    "mode": "urlencoded",
                    "urlencoded": [
                      { "key": "grant type", "value": "password" },
                      { "key": "skip", "value": "1", "disabled": true }
                    ]
                  }
                }
              } ]
            }
            """));

        var request = Request(bundle.Baselines.Single());

        request.Body!.Kind.Should().Be(BodyKind.FormUrlEncoded);

        // The disabled row travels — the engine is what decides not to send it — but it travels
        // marked, so somebody opening the endpoint sees the row they switched off rather than
        // wondering where it went.
        request.Body.Form.Should().HaveCount(2);
        request.Body.Form.Should().ContainSingle(field => field.Name == "skip" && !field.Enabled);
        request.Body.Form.Should().ContainSingle(field => field.Name == "grant type" && field.Enabled);
    }
}
