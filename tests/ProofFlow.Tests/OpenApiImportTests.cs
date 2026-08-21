using FluentAssertions;
using ProofFlow.Infrastructure.Portability.Importers;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Tests;

/// <summary>
/// An OpenAPI description, read the way somebody who wants to test the API needs it read.
///
/// Not validated — taken. The documents that arrive have missing fields, unresolved references and
/// vendor extensions in them, and a reader that refuses those is a reader nobody gets past.
/// </summary>
public class OpenApiImportTests
{
    private const string Document = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Catalog API", "version": "2.1.0", "description": "Products." },
          "servers": [
            { "url": "https://api.example.com/v2" },
            { "url": "https://staging.example.com/v2" }
          ],
          "components": {
            "securitySchemes": {
              "bearerAuth": { "type": "http", "scheme": "bearer" }
            }
          },
          "security": [ { "bearerAuth": [] } ],
          "paths": {
            "/products": {
              "get": {
                "summary": "List products",
                "tags": ["Products"],
                "responses": { "200": { "description": "ok" } }
              },
              "post": {
                "operationId": "createProduct",
                "tags": ["Products"],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "example": { "name": "Anvil", "price": 19 }
                    }
                  }
                },
                "responses": { "201": { "description": "made" }, "400": { "description": "no" } }
              }
            },
            "/products/{id}": {
              "get": {
                "summary": "One product",
                "parameters": [
                  { "name": "id", "in": "path", "required": true },
                  { "name": "X-Tenant", "in": "header" }
                ],
                "responses": { "200": { "description": "ok" }, "404": { "description": "gone" } }
              },
              "delete": {
                "summary": "Remove one",
                "responses": { "204": { "description": "gone" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void Every_operation_becomes_a_request()
    {
        var imported = OpenApiImporter.Read(Document);

        imported.Refusal.Should().BeNull();
        imported.SuggestedName.Should().Be("Catalog API");
        imported.BaseUrl.Should().Be("https://api.example.com/v2");
        imported.Requests.Should().HaveCount(4);

        imported.Requests.Select(request => request.Request.Method)
            .Should().BeEquivalentTo(["GET", "POST", "GET", "DELETE"]);
    }

    [Fact]
    public void A_second_server_is_reported_rather_than_silently_dropped()
    {
        OpenApiImporter.Read(Document).Notes.Should().Contain("import.note.manyServers");
    }

    [Fact]
    public void The_documented_success_status_is_the_one_that_gets_checked()
    {
        // The point of the whole feature: an endpoint documented as 201 arriving with a check for
        // 200 on it fails the first time it runs, and teaches somebody that imports do not work.
        var imported = OpenApiImporter.Read(Document);

        Named(imported, "createProduct").ExpectedStatus.Should().Be(201);
        Named(imported, "Remove one").ExpectedStatus.Should().Be(204);
        Named(imported, "List products").ExpectedStatus.Should().Be(200);
    }

    [Fact]
    public void A_path_parameter_stays_a_reference()
    {
        // {{id}} is something somebody can point at a data set. "42" is a test that passes once.
        Named(OpenApiImporter.Read(Document), "One product").Request.Url.Should().Be("/products/{id}");
    }

    [Fact]
    public void A_header_parameter_arrives_as_a_variable_reference()
    {
        var request = Named(OpenApiImporter.Read(Document), "One product").Request;

        request.Headers.Should().Contain(header =>
            header.Name == "X-Tenant" && header.Value == "{{X-Tenant}}");
    }

    [Fact]
    public void A_declared_security_scheme_becomes_a_secret_to_supply()
    {
        var imported = OpenApiImporter.Read(Document);

        imported.SecretsToSupply.Should().Contain("authorization");

        imported.Requests.Should().OnlyContain(request =>
            request.Request.Headers.Any(header =>
                header.Name == "Authorization" && header.Value == "{{secrets.authorization}}"));
    }

    [Fact]
    public void An_example_body_is_used_and_a_missing_one_is_said_out_loud()
    {
        var imported = OpenApiImporter.Read(Document);

        var body = Named(imported, "createProduct").Request.Body!;

        body.Kind.Should().Be(BodyKind.Json);
        body.Content.Should().Contain("Anvil");

        // And when the document has no example, the body is empty and the reader is told — rather
        // than a body invented from a schema, which is a guess that looks like a fact.
        var without = OpenApiImporter.Read("""
            {
              "openapi": "3.0.0",
              "info": { "title": "X" },
              "paths": {
                "/x": { "post": { "requestBody": { "content": { "application/json": {} } } } }
              }
            }
            """);

        without.Notes.Should().Contain("import.note.noExampleBody");
        without.Requests.Single().Request.Body!.Content.Should().Be("{\n}");
    }

    [Fact]
    public void Yaml_is_read_as_well_as_json()
    {
        // Most OpenAPI documents in the world are YAML, and telling somebody to convert their file
        // first is telling them to go away.
        var imported = OpenApiImporter.Read("""
            openapi: 3.0.1
            info:
              title: Orders API
            servers:
              - url: https://orders.example.com
            paths:
              /orders:
                get:
                  summary: List orders
                  responses:
                    '200':
                      description: ok
            """);

        imported.Refusal.Should().BeNull();
        imported.SuggestedName.Should().Be("Orders API");
        imported.BaseUrl.Should().Be("https://orders.example.com");
        imported.Requests.Should().ContainSingle().Which.Name.Should().Be("List orders");
    }

    [Fact]
    public void Swagger_two_is_refused_rather_than_half_read()
    {
        // It puts the body in a parameter and the host in three fields. Reading half of it would
        // produce requests that look right and are not.
        OpenApiImporter.Read("""{"swagger":"2.0","info":{"title":"X"},"paths":{}}""")
            .Refusal.Should().Be("import.swagger2");
    }

    [Theory]
    [InlineData("", "import.empty")]
    [InlineData("{ not json", "import.notJson")]
    [InlineData("""{"hello":"world"}""", "import.notOpenApi")]
    [InlineData("""{"openapi":"3.0.0","info":{"title":"X"},"paths":{}}""", "import.noPaths")]
    public void What_it_will_not_read_it_says_so_about(string text, string refusal)
    {
        OpenApiImporter.Read(text).Refusal.Should().Be(refusal);
    }

    [Fact]
    public void A_documents_promise_about_its_answer_is_kept_with_the_refs_resolved()
    {
        // The schema is the document's own claim about its API, and it is worth more than the
        // request: a recorded answer says what happened once, a contract says what was promised
        // always. It used to be read for the security scheme's name and then thrown away.
        var imported = OpenApiImporter.Read(Recursive);
        var contract = Named(imported, "One product").ContractJson;

        contract.Should().NotBeNull("the document made a promise and it should have been kept");

        // Resolved inline: the file it came from will not be there when the check runs.
        contract.Should().NotContain("$ref", "a reference to a closed document points at nothing");
        contract.Should().Contain("required");
        contract.Should().Contain("name");

        // 3.0's nullable is a type union everywhere else, and the difference is whether every row
        // with a null note fails.
        contract.Should().Contain("null", "nullable has to become something a validator reads");
        contract.Should().NotContain("nullable");

        // A type that contains itself is ordinary — a product with a parent product — and has to
        // be cut rather than followed for ever.
        imported.Notes.Should().Contain("import.note.deepSchema");
    }

    [Fact]
    public void An_endpoint_whose_document_promised_nothing_carries_no_contract()
    {
        // Most endpoints. A contract invented from a recorded answer would turn today's response
        // into a rule, which is the opposite of what a contract is.
        OpenApiImporter.Read(Document).Requests
            .Should().Contain(request => request.ContractJson == null,
                "a document with no response schema should produce endpoints without one");
    }

    /// <summary>A schema that references itself, which is the ordinary case a naive walk hangs on.</summary>
    private const string Recursive = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Catalog" },
          "components": {
            "schemas": {
              "Product": {
                "type": "object",
                "required": ["id", "name"],
                "properties": {
                  "id": { "type": "integer" },
                  "name": { "type": "string" },
                  "note": { "type": "string", "nullable": true },
                  "parent": { "$ref": "#/components/schemas/Product" }
                }
              }
            }
          },
          "paths": {
            "/products/{id}": {
              "get": {
                "summary": "One product",
                "responses": {
                  "200": {
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/Product" }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static ImportedRequest Named(Imported imported, string name) =>
        imported.Requests.Should().ContainSingle(request => request.Name == name).Subject;
}
