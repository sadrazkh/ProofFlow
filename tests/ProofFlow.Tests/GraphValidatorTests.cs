using FluentAssertions;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Tests;

/// <summary>
/// Whether a drawing is a test yet.
///
/// Every case here is a mistake the canvas draws perfectly happily. A step with no address, an
/// assertion nothing reaches, two steps sharing a name so a reference means either of them — the
/// picture looks finished in all three, and the run is wrong in all three.
/// </summary>
public class GraphValidatorTests
{
    private static GraphNode Node(
        string id, string key, string name, params (string Name, string? Value)[] properties) =>
        new(id, key, name, properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));

    private static GraphNode Start(string id = "s") => Node(id, "core.start", "Start");

    private static GraphNode Request(string id, string name, string? url = "https://example.test/x") =>
        Node(id, "http.request", name, ("method", "GET"), ("url", url));

    private static IReadOnlyList<GraphProblem> Check(
        IEnumerable<GraphNode> nodes, params GraphEdge[] edges) =>
        GraphValidator.Validate(new Graph([.. nodes], edges));

    [Fact]
    public void A_complete_two_step_scenario_has_nothing_to_say_about_it()
    {
        var problems = Check(
            [Start(), Request("a", "fetch")],
            new GraphEdge("s", "out", "a", "in"));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void An_empty_canvas_is_not_an_error()
    {
        // Somebody who has just opened a new scenario has not made a mistake, and telling them so
        // is how a validator gets ignored by the time it matters.
        Check([]).Should().BeEmpty();
    }

    [Fact]
    public void A_graph_with_no_start_says_nothing_would_run()
    {
        var problems = Check([Request("a", "fetch")]);

        problems.Should().Contain(p => p.Code == "noStart" && p.Severity == GraphSeverity.Error);
    }

    [Fact]
    public void Two_starts_is_an_error_on_the_second_one()
    {
        var problems = Check([Start("s1"), Start("s2")]);

        var problem = problems.Should().ContainSingle(p => p.Code == "twoStarts").Subject;
        problem.NodeId.Should().Be("s2", "the first one is not the mistake");
    }

    [Fact]
    public void A_step_missing_something_it_needs_says_which_step_and_which_field()
    {
        var problems = Check(
            [Start(), Request("a", "fetch", url: null)],
            new GraphEdge("s", "out", "a", "in"));

        var problem = problems.Should().ContainSingle(p => p.Code == "missingProperty").Subject;
        problem.NodeId.Should().Be("a");
        problem.Property.Should().Be("url");
        problem.Arguments.Should().Contain("fetch", "the sentence names the step, not the node id");
    }

    [Fact]
    public void A_property_that_is_not_being_asked_for_is_not_required()
    {
        // The body is required for a POST and absent from the form for a GET. Demanding it either
        // way is how a validator teaches people to ignore it.
        var get = Node("a", "http.request", "fetch",
            ("method", "GET"), ("url", "https://example.test/x"), ("bodyKind", "none"));

        Check([Start(), get], new GraphEdge("s", "out", "a", "in"))
            .Should().NotContain(p => p.Property == "body");

        var post = Node("a", "http.request", "create",
            ("method", "POST"), ("url", "https://example.test/x"), ("bodyKind", "json"));

        Check([Start(), post], new GraphEdge("s", "out", "a", "in"))
            .Should().Contain(p => p.Property == "body");
    }

    [Fact]
    public void A_disabled_step_is_not_nagged_about()
    {
        var disabled = new GraphNode("a", "http.request", "fetch",
            new Dictionary<string, string?>(), Disabled: true);

        Check([Start(), disabled], new GraphEdge("s", "out", "a", "in"))
            .Should().NotContain(p => p.Code == "missingProperty");
    }

    [Fact]
    public void Two_steps_with_one_name_would_make_a_reference_ambiguous()
    {
        var problems = Check(
            [Start(), Request("a", "login"), Request("b", "login")],
            new GraphEdge("s", "out", "a", "in"),
            new GraphEdge("a", "out", "b", "in"));

        var problem = problems.Should().ContainSingle(p => p.Code == "duplicateName").Subject;
        problem.Arguments.Should().ContainSingle().Which.Should().Be("login");
    }

    [Fact]
    public void A_step_nothing_leads_to_is_a_warning_and_not_a_refusal()
    {
        // Halfway through drawing a canvas this is the normal state, so it cannot block a save —
        // but an assertion nothing reaches is an assertion that silently never runs.
        var problems = Check(
            [Start(), Request("a", "fetch"), Request("b", "stranded")],
            new GraphEdge("s", "out", "a", "in"));

        var problem = problems.Should().ContainSingle(p => p.Code == "unreachable").Subject;
        problem.Severity.Should().Be(GraphSeverity.Warning);
        problem.NodeId.Should().Be("b");
    }

    [Fact]
    public void A_comment_is_not_stranded()
    {
        var comment = Node("c", "core.comment", "note", ("text", "why this exists"));

        Check([Start(), comment]).Should().NotContain(p => p.Code == "unreachable");
    }

    [Fact]
    public void Connections_that_come_back_round_would_never_finish()
    {
        var problems = Check(
            [Start(), Request("a", "one"), Request("b", "two")],
            new GraphEdge("s", "out", "a", "in"),
            new GraphEdge("a", "out", "b", "in"),
            new GraphEdge("b", "out", "a", "in"));

        var problem = problems.Should().ContainSingle(p => p.Code == "cycle").Subject;
        problem.Severity.Should().Be(GraphSeverity.Error);
        problem.Arguments.Should().Contain("one", "it names the step the loop comes back to");
    }

    [Fact]
    public void A_node_pointing_at_itself_is_a_cycle_too()
    {
        Check([Start(), Request("a", "one")],
                new GraphEdge("s", "out", "a", "in"),
                new GraphEdge("a", "out", "a", "in"))
            .Should().Contain(p => p.Code == "cycle");
    }

    [Fact]
    public void Plugging_a_list_where_a_response_is_wanted_is_refused_before_the_run()
    {
        // The check the canvas runs while an edge is being dragged, so the drop is refused rather
        // than the mistake reported afterwards.
        var count = Node("c", "data.count", "how many");
        var assert = Node("v", "assert.status", "is it ok", ("expected", "200"));

        var problems = Check(
            [Start(), count, assert],
            new GraphEdge("c", "count", "v", "response"));

        var problem = problems.Should().ContainSingle(p => p.Code == "typeMismatch").Subject;
        // The two type names travel as arguments; the web layer turns them into words, so the
        // sentence a Persian reader sees is Persian rather than English with Persian around it.
        problem.Arguments.Should().Equal(["how many", "Number", "is it ok", "Response"]);
        problem.NodeId.Should().Be("v");
    }

    [Fact]
    public void Control_and_data_are_not_interchangeable()
    {
        // The start's control output into a socket that wants a response. Both ends exist; what
        // does not is a meaning for the edge.
        var extract = Node("e", "data.extractJsonPath", "take the id", ("path", "$.id"));

        Check([Start(), extract], new GraphEdge("s", "out", "e", "response"))
            .Should().Contain(p => p.Code == "portKindMismatch");
    }

    [Fact]
    public void An_edge_into_an_output_socket_says_the_socket_is_not_there()
    {
        // `response` on an HTTP step is an output. Connecting to it is not a type error — it is a
        // connection to something that is not a socket on that side at all.
        Check([Start(), Request("a", "fetch")], new GraphEdge("s", "out", "a", "response"))
            .Should().Contain(p => p.Code == "unknownPort");
    }

    [Fact]
    public void An_input_that_must_be_connected_says_so_when_it_is_not()
    {
        var extract = Node("e", "data.extractJsonPath", "take the id", ("path", "$.id"));

        var problems = Check([Start(), extract], new GraphEdge("s", "out", "e", "in"));

        var problem = problems.Should().ContainSingle(p => p.Code == "missingInput").Subject;
        problem.Port.Should().Be("response");
        problem.Arguments.Should().Contain("take the id");
    }

    [Fact]
    public void A_connection_to_a_step_that_is_gone_is_reported_rather_than_thrown()
    {
        Check([Start()], new GraphEdge("s", "out", "vanished", "in"))
            .Should().Contain(p => p.Code == "danglingEdge");
    }

    [Fact]
    public void A_node_type_from_a_newer_version_says_so()
    {
        var unknown = Node("x", "future.thing", "from tomorrow");

        var problem = Check([Start(), unknown])
            .Should().ContainSingle(p => p.Code == "unknownType").Subject;

        problem.Arguments.Should().ContainSingle().Which.Should().Be("future.thing");
    }

    [Fact]
    public void A_credential_cannot_be_fed_from_plain_text()
    {
        // auth.setHeader wants a Secret. A template produces Text, and letting that through would
        // mean a credential could be assembled from anything on the canvas.
        var template = Node("t", "data.template", "make it", ("template", "Bearer abc"));
        var header = Node("h", "auth.setHeader", "use it", ("header", "Authorization"));

        Check([Start(), template, header], new GraphEdge("t", "text", "h", "token"))
            .Should().Contain(p => p.Code == "typeMismatch" && p.NodeId == "h");
    }

    [Fact]
    public void A_real_login_and_check_scenario_validates_clean()
    {
        var login = Node("l", "auth.login", "login",
            ("url", "{{environment.baseUrl}}/auth/login"),
            ("username", "demo"), ("password", "demoPassword"), ("tokenPath", "$.accessToken"));

        var header = Node("h", "auth.setHeader", "use the token",
            ("header", "Authorization"), ("prefix", "Bearer "), ("scope", "run"));

        var fetch = Request("f", "list records", "{{environment.baseUrl}}/records/1");

        var status = Node("a", "assert.status", "responded", ("expected", "200"));

        var problems = Check(
            [Start(), login, header, fetch, status],
            new GraphEdge("s", "out", "l", "in"),
            new GraphEdge("l", "out", "h", "in"),
            new GraphEdge("l", "token", "h", "token"),
            new GraphEdge("h", "out", "f", "in"),
            new GraphEdge("f", "out", "a", "in"),
            new GraphEdge("f", "response", "a", "response"));

        problems.Should().BeEmpty("this is the scenario the product is for: {0}",
            string.Join(" | ", problems.Select(p => $"{p.Code} {string.Join(',', p.Arguments)}")));
    }
}
