using FluentAssertions;
using ProofFlow.Infrastructure.Portability;
using ProofFlow.Infrastructure.Portability.Importers;

namespace ProofFlow.Tests;

/// <summary>
/// A foreign file becomes a bundle, and the bundle importer is the only thing that writes.
///
/// What matters here is that each request becomes a scenario somebody could press Run on: three
/// steps, wired together, with a check on the end. A scenario with nothing to assert passes whatever
/// the API does, which is worse than no scenario at all.
/// </summary>
public class ImportedBundleTests
{
    [Fact]
    public void A_curl_command_becomes_a_scenario_with_a_check_on_it()
    {
        var bundle = ImportedBundle.From(CurlImporter.Read(
            """curl -X POST https://api.example.com/products -H 'Accept: application/json' -d '{"name":"Anvil"}'"""));

        var scenario = bundle.Scenarios.Should().ContainSingle().Subject;

        scenario.Graph.Nodes.Select(node => node.Key)
            .Should().BeEquivalentTo(["core.start", "http.request", "assert.status"],
                options => options.WithStrictOrdering());

        // Three edges: the two that order the steps, and the data edge without which the check has
        // nothing to look at.
        scenario.Graph.Edges.Should().HaveCount(3);
        scenario.Graph.Edges.Should().ContainSingle(edge =>
            edge.FromPort == "response" && edge.ToPort == "response");

        var request = scenario.Graph.Nodes[1];

        request.Properties["method"].Should().Be("POST");
        request.Properties["url"].Should().Be("https://api.example.com/products");
        request.Properties["bodyKind"].Should().Be("json");
        request.Properties["body"].Should().Be("""{"name":"Anvil"}""");

        // Headers are written in the shape the runner reads back.
        request.Properties["headers"].Should().Contain("\"name\":\"Accept\"");
    }

    [Fact]
    public void The_base_url_becomes_an_environment_and_the_scenario_points_at_it()
    {
        var bundle = ImportedBundle.From(CurlImporter.Read("curl https://api.example.com/products"));

        var environment = bundle.Environments.Should().ContainSingle().Subject;

        environment.Slug.Should().Be("imported");
        environment.BaseUrl.Should().Be("https://api.example.com");

        bundle.Scenarios.Single().Environment.Should().Be("imported");
    }

    [Fact]
    public void A_folder_name_goes_into_the_scenario_name_rather_than_being_dropped()
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

        bundle.Scenarios.Select(scenario => scenario.Name)
            .Should().BeEquivalentTo(["Products · List", "Orders · List"]);

        // And the two are distinct, which is what stops the second one being skipped as a
        // collision with the first.
        bundle.Scenarios.Select(scenario => scenario.Slug).Should().OnlyHaveUniqueItems();
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

        bundle.Scenarios.Should().HaveCount(2);
        bundle.Scenarios.Select(scenario => scenario.Slug).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void An_open_api_document_becomes_one_scenario_per_operation_with_its_own_status()
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
        bundle.Scenarios.Should().HaveCount(2);

        Expected(bundle, "List").Should().Be("200");
        Expected(bundle, "Create").Should().Be("201");

        static string? Expected(Contracts.Portability.Bundle bundle, string name) =>
            bundle.Scenarios.Single(scenario => scenario.Name == name)
                .Graph.Nodes.Single(node => node.Key == "assert.status")
                .Properties["expected"];
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
    public void A_form_body_is_written_the_way_the_runner_will_send_it()
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

        var request = bundle.Scenarios.Single().Graph.Nodes.Single(node => node.Key == "http.request");

        request.Properties["bodyKind"].Should().Be("form");

        // Encoded, and without the row somebody switched off.
        request.Properties["body"].Should().Be("grant%20type=password");
    }
}
