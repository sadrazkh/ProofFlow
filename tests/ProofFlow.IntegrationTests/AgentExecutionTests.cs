using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Agent;
using ProofFlow.Contracts.Runners;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.FakeApi;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Running;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The agent's half of a remote run, against a real server.
///
/// This is the test that says the remote path is not a pretence. It builds the same
/// <see cref="PackagedRunServices"/> the agent builds, from a package like the one the server sends,
/// and runs a real graph through the real engine to a real HTTP endpoint — no database anywhere.
///
/// The classes under test are the ones the agent ships with, not copies of them — the test project
/// references the agent for exactly that reason. What is assembled here is the wiring
/// <c>JobRunner</c> assembles, so a change that broke the agent breaks this.
/// </summary>
public sealed class AgentExecutionTests : IAsyncLifetime
{
    private WebApplication _api = null!;
    private ServiceProvider _http = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddFakeApi();

        _api = builder.Build();
        _api.MapFakeApi();

        await _api.StartAsync();
        _baseUrl = _api.Urls.First().TrimEnd('/');

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProofFlowHttpClients();
        _http = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _api.StopAsync();
        await _api.DisposeAsync();
        await _http.DisposeAsync();
    }

    [Fact]
    public async Task An_agent_runs_a_real_scenario_and_reports_what_happened()
    {
        var package = Package(Graph(
            [
                Node("n1", "core.start"),
                Node("n2", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/stable")),
                Node("n3", "assert.status", ("expected", "200")),
            ],
            [Flow("n1", "n2"), Flow("n2", "n3"), Data("n2", "response", "n3", "response")]));

        var (summary, sink, _) = await RunAsync(package);

        summary.Status.Should().Be(Domain.Runs.RunStatus.Passed);
        summary.AssertionsPassed.Should().Be(1);
        summary.AssertionsFailed.Should().Be(0);

        // The record the server will write. Three steps, in order, with the check's verdict on it.
        sink.Nodes.Should().HaveCount(3);
        sink.Nodes.Select(node => node.NodeKey)
            .Should().BeEquivalentTo(["core.start", "http.request", "assert.status"],
                options => options.WithStrictOrdering());

        sink.Nodes.Single(node => node.NodeKey == "assert.status")
            .Assertions.Should().ContainSingle().Which.Passed.Should().BeTrue();

        // And it really talked to the server: the body is in the step's output.
        sink.Nodes.Single(node => node.NodeKey == "http.request")
            .OutputJson.Should().Contain("stable");
    }

    [Fact]
    public async Task A_failing_check_comes_back_as_a_failure_and_not_as_an_error()
    {
        // "Your API is broken" and "our agent is broken" are different news.
        var package = Package(Graph(
            [
                Node("n1", "core.start"),
                Node("n2", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/status/500")),
                Node("n3", "assert.status", ("expected", "200")),
            ],
            [Flow("n1", "n2"), Flow("n2", "n3"), Data("n2", "response", "n3", "response")]));

        var (summary, sink, _) = await RunAsync(package);

        summary.Status.Should().Be(Domain.Runs.RunStatus.Failed);
        summary.AssertionsFailed.Should().Be(1);

        sink.Nodes.Single(node => node.NodeKey == "assert.status")
            .Assertions.Should().ContainSingle().Which.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task The_environments_policy_is_the_servers_and_the_agent_cannot_widen_it()
    {
        // The package says private networking is off, and the fake API is on loopback. The request
        // must be refused by the guard on the agent, not attempted.
        var package = Package(
            Graph(
                [
                    Node("n1", "core.start"),
                    Node("n2", "http.request",
                        ("method", "GET"), ("url", "{{environment.baseUrl}}/stable")),
                ],
                [Flow("n1", "n2")]),
            allowPrivateNetwork: false);

        var (summary, sink, _) = await RunAsync(package);

        summary.Status.Should().NotBe(Domain.Runs.RunStatus.Passed);

        sink.Nodes.Single(node => node.NodeKey == "http.request")
            .FailureMessage.Should().Contain("private");
    }

    [Fact]
    public async Task A_secret_the_package_carried_is_used_and_never_reported_back()
    {
        var package = Package(
            Graph(
                [
                    Node("n1", "core.start"),
                    // The secret goes into the path, so the endpoint echoes it back — which is the
                    // only way to prove the value both arrived and was hidden on the way home.
                    Node("n2", "http.request",
                        ("method", "GET"),
                        ("url", "{{environment.baseUrl}}/records/{{secrets.token}}")),
                ],
                [Flow("n1", "n2")]),
            secrets: new Dictionary<string, string> { ["token"] = "a-real-token-value" });

        var (_, sink, _) = await RunAsync(package);

        var request = sink.Nodes.Single(node => node.NodeKey == "http.request");

        // It reached the server — the request resolved and came back — and the value is not in what
        // goes to the server. A secret hidden only once it arrives has already been through this
        // machine's logs.
        request.Status.Should().Be("Passed");
        request.OutputJson.Should().NotContain("a-real-token-value");
        request.OutputJson.Should().Contain("redacted");
    }

    [Fact]
    public async Task A_data_set_in_the_package_drives_the_loop()
    {
        var package = Package(
            Graph(
                [
                    Node("n1", "core.start"),
                    Node("n2", "flow.forEachRow", ("dataSet", "ids")),
                    Node("n3", "http.request", ("method", "GET"),
                        ("url", "{{environment.baseUrl}}/records/{{row.id}}")) with { ParentId = "n2" },
                ],
                [Flow("n1", "n2")]),
            dataSets:
            [
                new JobDataSet
                {
                    Name = "ids",
                    Id = Guid.CreateVersion7(),
                    Rows = ["""{"id":"1"}""", """{"id":"2"}""", """{"id":"3"}"""],
                },
            ]);

        var (_, sink, _) = await RunAsync(package);

        // Three passes through the loop body, one per row — the rows travelled in the package and
        // nothing read a database to find them.
        sink.Nodes.Count(node => node.NodeKey == "http.request").Should().Be(3);
    }

    [Fact]
    public async Task A_baseline_in_the_package_is_compared_against()
    {
        var package = Package(
            Graph(
                [
                    Node("n1", "core.start"),
                    Node("n2", "http.request",
                        ("method", "GET"), ("url", "{{environment.baseUrl}}/stable")),
                    Node("n3", "baseline.compare", ("baseline", "stable")),
                ],
                [Flow("n1", "n2"), Flow("n2", "n3"), Data("n2", "response", "n3", "response")]),
            baselines:
            [
                new JobBaseline
                {
                    Name = "stable",
                    Id = Guid.CreateVersion7(),
                    ApprovedBody = """{"nothing":"changes","here":true}""",
                    RulesJson = "[]",
                },
            ]);

        var (summary, sink, _) = await RunAsync(package);

        // The approved answer travelled with the job, so the comparison ran — and found the real
        // response differs from the body above, which is the correct verdict.
        summary.Status.Should().Be(Domain.Runs.RunStatus.Failed);

        sink.Nodes.Should().Contain(node => node.NodeKey == "baseline.compare");
    }

    [Fact]
    public async Task What_a_run_captures_comes_back_for_the_server_to_file()
    {
        var package = Package(
            Graph(
                [
                    Node("n1", "core.start"),
                    Node("n2", "http.request",
                        ("method", "GET"), ("url", "{{environment.baseUrl}}/stable")),
                    Node("n3", "baseline.capture", ("baseline", "stable")),
                ],
                [Flow("n1", "n2"), Flow("n2", "n3"), Data("n2", "response", "n3", "response")]),
            baselines:
            [
                new JobBaseline { Name = "stable", Id = Guid.CreateVersion7() },
            ]);

        var (_, _, captures) = await RunAsync(package);

        // The agent cannot file anything, so it collects. The server writes it into the same review
        // queue a local run would have used.
        var capture = captures.Should().ContainSingle().Subject;

        capture.Baseline.Should().Be("stable");
        capture.Body.Should().Contain("Stable");

        // And nothing approves itself.
        capture.Approve.Should().BeFalse();
    }

    // ---- the agent's wiring, as the agent builds it ---------------------------------------------

    private async Task<(RunSummary Summary, CollectingSink Sink, IReadOnlyList<JobCapture> Captures)>
        RunAsync(JobPackage package)
    {
        var policy = new UrlPolicy
        {
            AllowedHosts = [],
            AllowPrivateNetwork = package.Environment?.AllowPrivateNetwork ?? false,
            MaxRedirects = package.Environment?.MaxRedirects ?? 5,
            MaxResponseBytes = (package.Environment?.MaxResponseKilobytes ?? 4096) * 1024L,
            Timeout = TimeSpan.FromSeconds(package.Environment?.TimeoutSeconds ?? 30),
        };

        var redaction = new RedactionScope();
        foreach (var secret in package.Secrets.Values) redaction.Remember(secret);

        var scopes = new ProofFlow.TestEngine.Variables.VariableScopes();

        if (package.Environment?.BaseUrl is { Length: > 0 } baseUrl)
        {
            scopes.Environment["baseUrl"] = System.Text.Json.Nodes.JsonValue.Create(baseUrl);
        }

        foreach (var (name, value) in package.Secrets)
        {
            scopes.Secrets[name] = System.Text.Json.Nodes.JsonValue.Create(value);
        }

        var services = new PackagedRunServices(package, Executor(), policy, redaction);
        var sink = new CollectingSink(redaction);

        var graph = Read(package.Definition);

        var summary = await new ScenarioRunner(new NodeExecutors(services), sink)
            .RunAsync(graph, new RunScopes(scopes, redaction));

        return (summary, sink, services.Captures);
    }

    private GuardedHttpExecutor Executor() =>
        new(_http.GetRequiredService<IHttpClientFactory>(),
            NullLogger<GuardedHttpExecutor>.Instance);

    private JobPackage Package(
        string definition,
        bool allowPrivateNetwork = true,
        IReadOnlyDictionary<string, string>? secrets = null,
        IReadOnlyList<JobDataSet>? dataSets = null,
        IReadOnlyList<JobBaseline>? baselines = null) =>
        new()
        {
            RunId = Guid.CreateVersion7(),
            ScenarioName = "test",
            Definition = definition,
            Environment = new JobEnvironment
            {
                Name = "Local",
                BaseUrl = $"{_baseUrl}/fake",
                AllowPrivateNetwork = allowPrivateNetwork,
            },
            Secrets = secrets ?? new Dictionary<string, string>(),
            DataSets = dataSets ?? [],
            Baselines = baselines ?? [],
        };

    private static string Graph(GraphNodeDto[] nodes, GraphEdgeDto[] edges) =>
        JsonSerializer.Serialize(new GraphDto { Nodes = nodes, Edges = edges },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static Graph Read(string definition)
    {
        var graph = JsonSerializer.Deserialize<GraphDto>(definition,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        return new Graph(
            [.. graph.Nodes.Select(node => new GraphNode(
                node.Id, node.Key, node.Name, node.Properties, node.ParentId, node.Disabled))],
            [.. graph.Edges.Select(edge => new GraphEdge(
                edge.FromId, edge.FromPort, edge.ToId, edge.ToPort))]);
    }

    private static GraphNodeDto Node(
        string id, string key, params (string Name, string Value)[] properties) =>
        new()
        {
            Id = id,
            Key = key,
            Name = id,
            Properties = properties.ToDictionary(pair => pair.Name, pair => (string?)pair.Value),
        };

    private static GraphEdgeDto Flow(string from, string to) =>
        new() { Id = $"{from}-{to}", FromId = from, FromPort = "out", ToId = to, ToPort = "in" };

    private static GraphEdgeDto Data(string from, string fromPort, string to, string toPort) =>
        new() { Id = $"{from}-{to}-d", FromId = from, FromPort = fromPort, ToId = to, ToPort = toPort };
}
