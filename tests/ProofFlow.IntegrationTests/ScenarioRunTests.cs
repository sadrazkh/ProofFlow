using ProofFlow.TestEngine.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Runs;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Domain.Workspaces;
using ProofFlow.FakeApi;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Common;
using ProofFlow.Infrastructure.Environments;

using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;
using ProofFlow.Infrastructure.Scenarios;
using ProofFlow.Infrastructure.Security;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// A scenario carried out end to end: a real graph, a real server, a real database.
///
/// The engine's own tests prove the shapes; this proves the joins. A run has to take the graph
/// from rows, send real requests through the guard, write node runs and log lines somebody can read
/// afterwards, and end in a state that matches what happened — and every one of those is a seam
/// where a passing unit test can sit on top of a broken product.
/// </summary>
public sealed class ScenarioRunTests : IAsyncLifetime
{
    private WebApplication _server = null!;
    private ServiceProvider _http = null!;
    private SqliteConnection _connection = null!;
    private string _baseUrl = null!;

    private readonly Guid _workspaceId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private Guid _projectId;
    private Guid _environmentId;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddFakeApi();

        _server = builder.Build();
        _server.MapFakeApi();
        await _server.StartAsync();
        _baseUrl = _server.Urls.First().TrimEnd('/');

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProofFlowHttpClients();
        _http = services.BuildServiceProvider();

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        await using var context = Db();
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        await _http.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_request_and_a_check_run_and_are_recorded()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "healthy", Straight());
        var run = await RunAsync(context, scenario);

        run.Status.Should().Be(RunStatus.Passed, "the outcome was: {0}", run.Outcome);
        run.AssertionsPassed.Should().Be(1);
        run.AssertionsFailed.Should().Be(0);
        run.StepsRun.Should().BeGreaterThan(0);
        run.FinishedAt.Should().NotBeNull();

        var nodes = await context.NodeRuns
            .Where(node => node.TestRunId == run.Id)
            .OrderBy(node => node.SortOrder)
            .ToListAsync();

        nodes.Select(node => node.NodeName).Should().Contain(["call", "check"]);
        nodes.Should().OnlyContain(node => node.FinishedAt != null);

        var events = await context.RunEvents.Where(entry => entry.TestRunId == run.Id).ToListAsync();
        events.Should().NotBeEmpty("the console has nothing to show otherwise");
        events.Select(entry => entry.Sequence).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_run_keeps_the_graph_it_ran_so_a_later_edit_cannot_rewrite_history()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "snapshot", Straight());
        var run = await RunAsync(context, scenario);

        run.DefinitionJson.Should().NotBeNullOrWhiteSpace();
        run.DefinitionJson.Should().Contain("assert.status");

        // The scenario is edited underneath, the way somebody edits it after reading a failure: the
        // check is taken out and saved through the same path the canvas uses.
        var entity = await context.Scenarios.FirstAsync(candidate => candidate.Id == scenario);

        await Graphs(context).SaveAsync(entity, new GraphDto
        {
            Nodes = [Node("start", "core.start")],
            Edges = [],
        });

        var reread = await context.Runs.FirstAsync(candidate => candidate.Id == run.Id);
        reread.DefinitionJson.Should().Contain("assert.status", "the run's copy is its own");
    }

    [Fact]
    public async Task A_failed_check_fails_the_run_and_says_which_check()
    {
        await using var context = Db();

        var graph = Straight();
        var check = graph.Nodes.First(node => node.Key == "assert.status");
        graph = graph with
        {
            Nodes = [.. graph.Nodes.Select(node =>
                node == check ? Node("check", "assert.status", ("expected", "418")) : node)],
        };

        var scenario = await ScenarioAsync(context, "wrong expectation", graph);
        var run = await RunAsync(context, scenario);

        run.Status.Should().Be(RunStatus.Failed);
        run.AssertionsFailed.Should().Be(1);

        var assertion = await context.AssertionResults
            .Where(result => context.NodeRuns
                .Any(node => node.Id == result.NodeRunId && node.TestRunId == run.Id))
            .SingleAsync();

        assertion.Passed.Should().BeFalse();
        assertion.Description.Should().Contain("418");
        assertion.Actual.Should().Be("200");
    }

    [Fact]
    public async Task A_branch_records_which_way_it_went()
    {
        await using var context = Db();

        var graph = new GraphDto
        {
            Nodes =
            [
                Node("start", "core.start"),
                Node("call", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/fake/records/1")),
                Node("branch", "flow.if",
                    ("condition", "{{steps.call.response.statusCode}} == 200")),
                Node("ok", "core.checkpoint", ("name", "it answered")),
                Node("bad", "core.checkpoint", ("name", "it did not")),
            ],
            Edges =
            [
                Edge("start", "call"),
                Edge("call", "branch"),
                Edge("branch", "ok", "true"),
                Edge("branch", "bad", "false"),
            ],
        };

        var scenario = await ScenarioAsync(context, "branching", graph);
        var run = await RunAsync(context, scenario);

        run.Status.Should().Be(RunStatus.Passed, "the outcome was: {0}", run.Outcome);

        var names = await context.NodeRuns
            .Where(node => node.TestRunId == run.Id)
            .Select(node => node.NodeName)
            .ToListAsync();

        names.Should().Contain("ok").And.NotContain("bad");
    }

    [Fact]
    public async Task A_data_set_loop_runs_once_per_row_against_the_real_endpoint()
    {
        await using var context = Db();
        await DataSetAsync(context, ["1", "2", "3"]);

        var graph = new GraphDto
        {
            Nodes =
            [
                Node("start", "core.start"),
                Node("rows", "flow.forEachRow", ("dataSet", "products")),
                Inside("rows", Node("call", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/fake/records/{{dataset.current.id}}"))),
                Inside("rows", Node("check", "assert.status", ("expected", "200"))),
            ],
            Edges = [Edge("start", "rows"), Edge("call", "check"), Data("call", "response", "check", "response")],
        };

        var scenario = await ScenarioAsync(context, "every row", graph);
        var run = await RunAsync(context, scenario);

        run.Status.Should().Be(RunStatus.Passed, "the outcome was: {0}", run.Outcome);
        run.AssertionsPassed.Should().Be(3, "one check per row");

        var calls = await context.NodeRuns
            .Where(node => node.TestRunId == run.Id && node.NodeName == "call")
            .OrderBy(node => node.Iteration)
            .ToListAsync();

        calls.Should().HaveCount(3);
        calls.Select(node => node.Iteration).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task A_secret_used_by_a_step_does_not_reach_the_log()
    {
        // The rule the whole product turns on. A run log is the artefact people forward, and a
        // token that reached it is a token that has left the building.
        await using var context = Db();

        var graph = new GraphDto
        {
            Nodes =
            [
                Node("start", "core.start"),
                Node("signIn", "auth.bearer", ("token", "{{secrets.apiToken}}")),
                Node("call", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/fake/records/1")),
                Node("show", "core.log", ("message", "sent {{secrets.apiToken}}")),
            ],
            Edges = [Edge("start", "signIn"), Edge("signIn", "call"), Edge("call", "show")],
        };

        var scenario = await ScenarioAsync(context, "secret handling", graph);
        var run = await RunAsync(context, scenario);

        var lines = await context.RunEvents
            .Where(entry => entry.TestRunId == run.Id)
            .Select(entry => entry.Message)
            .ToListAsync();

        lines.Should().NotContain(message => message.Contains(TokenValue));
        lines.Should().Contain(message => message.Contains("sent"), "the line itself is still there");
    }

    [Fact]
    public async Task A_run_that_is_stopped_ends_as_cancelled_and_keeps_what_it_had()
    {
        await using var context = Db();

        var graph = new GraphDto
        {
            Nodes =
            [
                Node("start", "core.start"),
                Node("first", "core.checkpoint", ("name", "before")),
                Node("wait", "core.delay", ("duration", "30s")),
                Node("never", "core.checkpoint", ("name", "after")),
            ],
            Edges = [Edge("start", "first"), Edge("first", "wait"), Edge("wait", "never")],
        };

        var scenario = await ScenarioAsync(context, "stoppable", graph);
        var service = Service(context);
        var queued = await service.QueueAsync(scenario, _environmentId, RunTrigger.Person);

        using var stopping = new CancellationTokenSource();
        var running = service.ExecuteAsync(queued.Id, stopping.Token);

        await Task.Delay(400);
        await stopping.CancelAsync();
        await running;

        var run = await context.Runs.FirstAsync(candidate => candidate.Id == queued.Id);
        run.Status.Should().Be(RunStatus.Cancelled);

        var names = await context.NodeRuns
            .Where(node => node.TestRunId == run.Id)
            .Select(node => node.NodeName)
            .ToListAsync();

        names.Should().Contain("first", "what ran before the stop is kept");
        names.Should().NotContain("after");
    }

    [Fact]
    public async Task A_graph_that_cannot_be_read_errors_rather_than_hanging()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "unreadable", Straight());
        var service = Service(context);
        var queued = await service.QueueAsync(scenario, _environmentId, RunTrigger.Person);

        queued.DefinitionJson = "{ this is not json";
        await context.SaveChangesAsync();

        await service.ExecuteAsync(queued.Id);

        var run = await context.Runs.FirstAsync(candidate => candidate.Id == queued.Id);

        // Errored, not Failed: "your API is broken" and "our runner is broken" send somebody to
        // different places.
        run.Status.Should().Be(RunStatus.Errored);
        run.FinishedAt.Should().NotBeNull();
    }

    // ---- setup ---------------------------------------------------------------------------------

    private const string TokenValue = "super-secret-token-value";

    private static GraphDto Straight() => new()
    {
        Nodes =
        [
            Node("start", "core.start"),
            Node("call", "http.request",
                ("method", "GET"), ("url", "{{environment.baseUrl}}/fake/records/1")),
            Node("check", "assert.status", ("expected", "200")),
        ],
        Edges = [Edge("start", "call"), Edge("call", "check"), Data("call", "response", "check", "response")],
    };

    private static GraphNodeDto Node(string id, string key, params (string Name, string Value)[] properties) =>
        new()
        {
            Id = id,
            Key = key,
            Name = id,
            Properties = properties.ToDictionary(pair => pair.Name, pair => (string?)pair.Value),
        };

    private static GraphNodeDto Inside(string parent, GraphNodeDto node) => node with { ParentId = parent };

    private static GraphEdgeDto Edge(string from, string to, string fromPort = "out") =>
        new() { Id = $"{from}-{to}-{fromPort}", FromId = from, FromPort = fromPort, ToId = to, ToPort = "in" };

    private static GraphEdgeDto Data(string from, string fromPort, string to, string toPort) =>
        new() { Id = $"{from}-{to}-{fromPort}", FromId = from, FromPort = fromPort, ToId = to, ToPort = toPort };

    private async Task<Guid> ScenarioAsync(ProofFlowDbContext context, string name, GraphDto graph)
    {
        var scenario = new TestScenario
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = name,
            EnvironmentId = _environmentId,
            CreatedByUserId = _userId,
        };

        context.Scenarios.Add(scenario);
        await context.SaveChangesAsync();

        await Graphs(context).SaveAsync(scenario, graph);
        return scenario.Id;
    }

    private async Task<TestRun> RunAsync(ProofFlowDbContext context, Guid scenarioId)
    {
        var service = Service(context);
        var queued = await service.QueueAsync(scenarioId, _environmentId, RunTrigger.Person);

        await service.ExecuteAsync(queued.Id);

        return await context.Runs.FirstAsync(run => run.Id == queued.Id);
    }

    private RunService Service(ProofFlowDbContext context) => new(
        context,
        Graphs(context),
        new ScenarioGraphSnapshots(),
        Environments(context),
        new BaselineService(context, new FixedUser(_workspaceId, _userId), new SystemClock()),
        new GuardedHttpExecutor(_http.GetRequiredService<IHttpClientFactory>(),
            NullLogger<GuardedHttpExecutor>.Instance),
        new NoWatchers(),
        new FixedUser(_workspaceId, _userId),
        new SystemClock(),
        NullLogger<RunService>.Instance);

    private ScenarioGraphService Graphs(ProofFlowDbContext context) => new(
        context, new FixedUser(_workspaceId, _userId), new SystemClock(), new PlainProblems());

    private EnvironmentContextBuilder Environments(ProofFlowDbContext context) => new(
        context,
        new AesGcmSecretCipher(Configuration(), NullLogger<AesGcmSecretCipher>.Instance),
        NullLogger<EnvironmentContextBuilder>.Instance);

    private static Microsoft.Extensions.Configuration.IConfiguration Configuration() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProofFlow:MasterKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

    private ProofFlowDbContext Db()
    {
        var options = new DbContextOptionsBuilder<SqliteProofFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new SqliteProofFlowDbContext(options, new FixedWorkspaceScope(_workspaceId));
    }

    private async Task SeedAsync(ProofFlowDbContext context)
    {
        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Runs", Slug = "runs" });

        var project = new Project { WorkspaceId = _workspaceId, Name = "Catalog", Slug = "catalog" };
        context.Projects.Add(project);

        var environment = new ProjectEnvironment
        {
            WorkspaceId = _workspaceId,
            ProjectId = project.Id,
            Name = "Local",
            Slug = "local",
            BaseUrl = _baseUrl,
            // The fake API listens on loopback, which the guard refuses unless it is told not to.
            AllowPrivateNetwork = true,
        };
        context.Environments.Add(environment);

        await context.SaveChangesAsync();

        _projectId = project.Id;
        _environmentId = environment.Id;

        var sealedValue = new AesGcmSecretCipher(Configuration(), NullLogger<AesGcmSecretCipher>.Instance)
            .Seal(TokenValue);

        context.Secrets.Add(new Secret
        {
            WorkspaceId = _workspaceId,
            ProjectId = project.Id,
            EnvironmentId = environment.Id,
            Name = "apiToken",
            Ciphertext = sealedValue.Ciphertext,
            Nonce = sealedValue.Nonce,
            Tag = sealedValue.Tag,
            KeyVersion = sealedValue.KeyVersion,
            Preview = TokenValue[^4..],
            CreatedByUserId = _userId,
        });

        await context.SaveChangesAsync();
    }

    private async Task DataSetAsync(ProofFlowDbContext context, string[] ids)
    {
        var set = new DataSet
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = "products",
            KeyColumn = "id",
            CreatedByUserId = _userId,
        };

        context.DataSets.Add(set);

        var version = new DataSetVersion
        {
            WorkspaceId = _workspaceId,
            DataSetId = set.Id,
            Number = 1,
            ColumnsJson = """["id"]""",
            RowCount = ids.Length,
            CreatedByUserId = _userId,
        };

        context.DataSetVersions.Add(version);

        for (var index = 0; index < ids.Length; index++)
        {
            context.DataSetRows.Add(new DataSetRow
            {
                WorkspaceId = _workspaceId,
                DataSetVersionId = version.Id,
                Ordinal = index,
                Key = ids[index],
                ValuesJson = JsonSerializer.Serialize(new { id = ids[index] }),
            });
        }

        await context.SaveChangesAsync();
    }

    private sealed class FixedUser(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? WorkspaceId => workspaceId;
        public string DisplayName => "Runner";
        public WorkspaceRole? Role => WorkspaceRole.Owner;
        public bool IsAuthenticated => true;
        public bool Can(Capability capability) => true;
    }

    /// <summary>Codes rather than sentences, which is what the engine deals in.</summary>
    private sealed class PlainProblems : IProblemText
    {
        public string For(ProofFlow.TestEngine.Nodes.GraphProblem problem) =>
            problem.Arguments.Count == 0
                ? problem.Code
                : $"{problem.Code}: {string.Join(", ", problem.Arguments)}";
    }
}
