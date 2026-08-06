using System.Text.Json.Nodes;
using FluentAssertions;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.Tests;

public class VariableResolverTests
{
    private static VariableResolver Resolver(RedactionScope? redaction = null) =>
        new(new VariableScopes
        {
            Environment = new JsonObject
            {
                ["baseUrl"] = "https://api.example.com",
                ["apiVersion"] = "v2",
            },
            Secrets = new JsonObject { ["apiToken"] = "tok_abcdefghijkl" },
            Variables = new JsonObject { ["pageSize"] = 25, ["enabled"] = true },
            Steps = new JsonObject
            {
                ["login"] = new JsonObject
                {
                    ["response"] = new JsonObject { ["token"] = "session-123", ["expiresIn"] = 3600 },
                },
                ["categories"] = new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["items"] = new JsonArray
                        {
                            new JsonObject { ["id"] = 11, ["name"] = "Electronics" },
                            new JsonObject { ["id"] = 12, ["name"] = "Books" },
                        },
                    },
                },
            },
            Dataset = new JsonObject { ["current"] = new JsonObject { ["studyId"] = 21289 } },
            Run = new JsonObject { ["id"] = "run-7" },
        }, redaction);

    [Theory]
    [InlineData("{{environment.baseUrl}}", "https://api.example.com")]
    [InlineData("{{environment.baseUrl}}/orders", "https://api.example.com/orders")]
    [InlineData("{{ environment.baseUrl }}", "https://api.example.com")]
    [InlineData("Bearer {{secrets.apiToken}}", "Bearer tok_abcdefghijkl")]
    [InlineData("{{steps.login.response.token}}", "session-123")]
    [InlineData("{{steps.categories.response.items[0].id}}", "11")]
    [InlineData("{{steps.categories.response.items[1].name}}", "Books")]
    [InlineData("{{steps.categories.response.items[-1].name}}", "Books")]
    [InlineData("{{dataset.current.studyId}}", "21289")]
    [InlineData("{{run.id}}", "run-7")]
    [InlineData("/api/{{environment.apiVersion}}/studies/{{dataset.current.studyId}}", "/api/v2/studies/21289")]
    public void Substitutes_a_reference(string input, string expected) =>
        Resolver().Resolve(input).Should().Be(expected);

    [Fact]
    public void A_string_value_loses_its_quotes()
    {
        // Bearer "tok_abc" is not what anyone meant by Bearer {{secrets.apiToken}}.
        Resolver().Resolve("{{secrets.apiToken}}").Should().Be("tok_abcdefghijkl");
    }

    [Fact]
    public void A_reference_that_is_the_whole_value_keeps_its_type()
    {
        var resolver = Resolver();

        // An API that validates its body rejects "25" where it wants 25, and the person has no way
        // to say "but as a number" in a text box.
        resolver.ResolveTyped("{{vars.pageSize}}")!.GetValueKind()
            .Should().Be(System.Text.Json.JsonValueKind.Number);
        resolver.ResolveTyped("{{vars.enabled}}")!.GetValueKind()
            .Should().Be(System.Text.Json.JsonValueKind.True);
    }

    [Fact]
    public void A_reference_mixed_with_text_becomes_a_string()
    {
        Resolver().ResolveTyped("page-{{vars.pageSize}}")!.GetValue<string>().Should().Be("page-25");
    }

    [Fact]
    public void An_object_reference_keeps_its_shape()
    {
        var node = Resolver().ResolveTyped("{{steps.categories.response.items[0]}}");

        node.Should().BeOfType<JsonObject>();
        node!["name"]!.GetValue<string>().Should().Be("Electronics");
    }

    [Fact]
    public void A_missing_variable_is_an_error_and_not_an_empty_string()
    {
        // The whole reason this throws. Substituting nothing sends "Bearer " and produces a 401,
        // and the afternoon is spent looking at the wrong system.
        var resolve = () => Resolver().Resolve("Bearer {{secrets.missing}}");

        resolve.Should().Throw<VariableResolutionException>();
    }

    [Fact]
    public void The_error_says_what_was_available_instead()
    {
        var resolve = () => Resolver().Resolve("{{environment.baseURL}}");

        // A typo in a name is the most common failure here, and listing the real names ends it.
        resolve.Should().Throw<VariableResolutionException>()
            .WithMessage("*baseUrl*");
    }

    [Fact]
    public void An_unknown_scope_names_the_ones_that_exist()
    {
        var resolve = () => Resolver().Resolve("{{env.baseUrl}}");

        resolve.Should().Throw<VariableResolutionException>()
            .WithMessage("*environment*");
    }

    [Fact]
    public void Every_missing_reference_is_reported_at_once()
    {
        // Three missing variables should be three lines, not three round trips.
        var result = Resolver().TryResolve("{{vars.a}}/{{vars.b}}/{{vars.c}}");

        result.Unresolved.Should().HaveCount(3);
        result.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void An_index_past_the_end_says_how_many_there_were()
    {
        var resolve = () => Resolver().Resolve("{{steps.categories.response.items[9].id}}");

        resolve.Should().Throw<VariableResolutionException>().WithMessage("*2 item(s)*");
    }

    [Fact]
    public void Indexing_something_that_is_not_a_list_is_explained()
    {
        var resolve = () => Resolver().Resolve("{{environment.baseUrl[0]}}");

        resolve.Should().Throw<VariableResolutionException>().WithMessage("*not a list*");
    }

    [Fact]
    public void An_unresolved_reference_is_left_visible_rather_than_blanked()
    {
        // For the live preview in the request builder: showing the reference back is far more
        // useful than showing a gap where a value will be.
        var result = Resolver().TryResolve("{{vars.missing}}/orders");

        result.Text.Should().Be("{{vars.missing}}/orders");
    }

    [Fact]
    public void Resolving_a_secret_registers_it_for_redaction()
    {
        var redaction = new RedactionScope();

        Resolver(redaction).Resolve("Bearer {{secrets.apiToken}}");

        // This is the only moment the engine knows those characters are a credential.
        redaction.Values.Should().Contain("tok_abcdefghijkl");
        redaction.Apply("the token was tok_abcdefghijkl").Should().NotContain("tok_abcdefghijkl");
    }

    [Fact]
    public void Resolving_a_non_secret_does_not_register_it()
    {
        var redaction = new RedactionScope();

        Resolver(redaction).Resolve("{{environment.baseUrl}}");

        // Masking the base URL would make every report unreadable.
        redaction.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData("no references here")]
    [InlineData("")]
    [InlineData("{{}}")]
    [InlineData("{ not a reference }")]
    public void Text_without_a_valid_reference_passes_through(string input) =>
        Resolver().TryResolve(input).Text.Should().Be(input);

    [Fact]
    public void A_malformed_path_is_reported_rather_than_guessed()
    {
        var result = Resolver().TryResolve("{{steps.items[}}");

        result.Unresolved.Should().ContainSingle();
    }

    [Fact]
    public void A_step_publishes_its_result_for_later_steps()
    {
        var scopes = new VariableScopes();
        scopes.PublishStep("createProduct", new JsonObject
        {
            ["response"] = new JsonObject { ["id"] = 4711 },
        });

        new VariableResolver(scopes).Resolve("{{steps.createProduct.response.id}}").Should().Be("4711");
    }

    [Fact]
    public void FindAll_lists_the_references_in_a_string()
    {
        var found = VariableReference.FindAll("{{a.b}} and {{c.d[0]}}");

        found.Should().HaveCount(2);
        found[0].Scope.Should().Be("a");
        found[1].Path.Should().HaveCount(2);
    }
}
