using ProofFlow.TestEngine.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Authorization;
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
/// The same scenario in two places, and what differs between them.
///
/// Two real servers, not one server pretending to be two. The whole claim of this phase is that
/// ProofFlow can tell you staging and production disagree, and a test where both sides are the same
/// process answering the same handler cannot demonstrate that — it would pass just as well if the
/// comparison always said "identical".
///
/// So the second server returns the same shape with one field deliberately different, and the test
/// asserts the comparison finds that field and nothing else.
/// </summary>
public sealed class EnvironmentMatrixTests : IAsyncLifetime
{
    private WebApplication _alpha = null!;
    private WebApplication _beta = null!;
    private ServiceProvider _http = null!;
    private SqliteConnection _connection = null!;

    private string _alphaUrl = null!;
    private string _betaUrl = null!;

    private readonly Guid _workspaceId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    private Guid _projectId;
    private Guid _alphaId;
    private Guid _betaId;

    public async Task InitializeAsync()
    {
        _alpha = await FakeAsync();
        _alphaUrl = _alpha.Urls.First().TrimEnd('/');

        _beta = await DivergentAsync();
        _betaUrl = _beta.Urls.First().TrimEnd('/');

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
        await _alpha.StopAsync();
        await _alpha.DisposeAsync();
        await _beta.StopAsync();
        await _beta.DisposeAsync();
        await _http.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_batch_runs_every_scenario_in_every_environment()
    {
        await using var context = Db();

        var first = await ScenarioAsync(context, "fetch one", "/fake/records/1");
        var second = await ScenarioAsync(context, "fetch two", "/fake/records/2");

        var grid = await RunAsync(context, [first, second], [_alphaId, _betaId]);

        grid.Should().NotBeNull();
        grid!.Rows.Should().HaveCount(2);
        grid.Columns.Should().HaveCount(2);
        grid.Total.Should().Be(4);
        grid.Done.Should().Be(4);

        grid.Rows.Should().OnlyContain(row => row.Cells.Count == 2);
        grid.Rows.SelectMany(row => row.Cells).Should().OnlyContain(cell => cell != null);

        // Every cell is an ordinary run, which is what makes the console reachable from the grid.
        var runIds = grid.Rows.SelectMany(row => row.Cells).Select(cell => cell!.RunId).ToList();
        runIds.Should().OnlyHaveUniqueItems();
        (await context.Runs.CountAsync(run => runIds.Contains(run.Id))).Should().Be(4);
    }

    [Fact]
    public async Task The_grid_says_which_column_is_production()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "one column is live", "/fake/records/1");
        var grid = await RunAsync(context, [scenario], [_alphaId, _betaId]);

        grid!.Columns.Should().ContainSingle(column => column.IsProduction);
        grid.Columns.Single(column => column.IsProduction).Name.Should().Be("Beta");
    }

    [Fact]
    public async Task A_batch_over_one_environment_still_works()
    {
        // The degenerate case, because it is the one somebody reaches by accident: a grid of one
        // column is a list, and it should be a list rather than an error.
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "just here", "/fake/records/1");
        var grid = await RunAsync(context, [scenario], [_alphaId]);

        grid!.Columns.Should().HaveCount(1);
        grid.Rows.Should().ContainSingle();
        grid.State.Should().Be(nameof(BatchState.Passed));
    }

    [Fact]
    public async Task Comparing_two_environments_finds_the_field_that_differs()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "same call, two places", "/fake/records/4");
        var batch = await QueueAndRunAsync(context, [scenario], [_alphaId, _betaId]);

        var result = await new EnvironmentComparison(context)
            .CompareAsync(batch, scenario, _alphaId, _betaId);

        result.Should().NotBeNull();
        result!.LeftName.Should().Be("Alpha");
        result.RightName.Should().Be("Beta");

        var step = result.Steps.Should().ContainSingle().Subject;

        step.Diff.Matches.Should().BeFalse("the second server returns a different name");
        step.LeftStatus.Should().Be(200);
        step.RightStatus.Should().Be(200);

        // Exactly one field, and the right one. A comparison that reports the whole document as
        // changed is a comparison nobody reads twice.
        var changed = step.Diff.Rows
            .Where(row => row.Kind == "Changed")
            .Select(row => row.Path)
            .ToList();

        changed.Should().ContainSingle().Which.Should().Contain("name");
    }

    [Fact]
    public async Task Two_environments_answering_the_same_thing_compare_as_identical()
    {
        // The other half, and the one that catches a comparison that always finds something: the
        // same server on both sides has to come back silent.
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "both the same", "/fake/records/7");
        var twin = await AddEnvironmentAsync(context, "Alpha twin", _alphaUrl, production: false);

        var batch = await QueueAndRunAsync(context, [scenario], [_alphaId, twin]);

        var result = await new EnvironmentComparison(context)
            .CompareAsync(batch, scenario, _alphaId, twin);

        result!.Steps.Should().ContainSingle().Which.Diff.Matches.Should().BeTrue();
        result.OnlyLeft.Should().BeEmpty();
        result.OnlyRight.Should().BeEmpty();
    }

    [Fact]
    public async Task A_step_only_one_side_reached_is_reported_rather_than_ignored()
    {
        // A branch that went one way in one environment and the other way in the other. Silently
        // comparing only the steps they share would hide the most important difference there is.
        await using var context = Db();

        var graph = new GraphDto
        {
            Nodes =
            [
                Node("start", "core.start"),
                Node("call", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/fake/records/4")),
                Node("branch", "flow.if",
                    ("condition", "{{steps.call.response.body.name}} == \"Record 4\"")),
                Node("only-alpha", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/fake/records/5")),
            ],
            Edges =
            [
                Edge("start", "call"),
                Edge("call", "branch"),
                Edge("branch", "only-alpha", "true"),
            ],
        };

        var scenario = await ScenarioAsync(context, "a fork in the road", graph);
        var batch = await QueueAndRunAsync(context, [scenario], [_alphaId, _betaId]);

        var result = await new EnvironmentComparison(context)
            .CompareAsync(batch, scenario, _alphaId, _betaId);

        result!.OnlyLeft.Should().Contain("only-alpha");
        result.OnlyRight.Should().BeEmpty();
    }

    [Fact]
    public async Task A_batch_refuses_to_start_more_runs_than_the_ceiling()
    {
        await using var context = Db();

        var scenarios = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            scenarios.Add(await ScenarioAsync(context, $"scenario {index}", "/fake/records/1"));
        }

        var environments = new List<Guid> { _alphaId, _betaId };
        for (var index = 0; index < 20; index++)
        {
            environments.Add(await AddEnvironmentAsync(context, $"env {index}", _alphaUrl, false));
        }

        var start = async () => await Matrix(context).QueueAsync(_projectId, scenarios, environments);

        await start.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains(MatrixService.MaxCells.ToString()));

        // And nothing was written. A refusal that leaves a half-built batch behind is worse than
        // one that runs.
        (await context.RunBatches.CountAsync()).Should().Be(0);
    }

    // ---- setup ---------------------------------------------------------------------------------

    private static async Task<WebApplication> FakeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddFakeApi();

        var app = builder.Build();
        app.MapFakeApi();
        await app.StartAsync();

        return app;
    }

    /// <summary>
    /// A second server that answers the same shape with one field different.
    ///
    /// Deliberately minimal and deliberately separate: this is what "the other environment" means,
    /// and the difference has to be real for the comparison to have found anything.
    /// </summary>
    private static async Task<WebApplication> DivergentAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.MapGet("/fake/records/{id}", (string id) =>
        {
            if (!int.TryParse(id, out var number))
                return Results.Json(new { error = "not_a_number", id }, statusCode: 400);

            return Results.Ok(new
            {
                id = number,
                name = $"Item {number}",
                score = Math.Round(number * 1.5, 2),
                active = number % 3 != 0,
                tags = new[] { number % 2 == 0 ? "even" : "odd", $"band-{number / 10}" },
                nested = new { depth = number % 5, label = $"group {number % 4}" },
            });
        });

        await app.StartAsync();
        return app;
    }

    private static GraphNodeDto Node(string id, string key, params (string Name, string Value)[] properties) =>
        new()
        {
            Id = id,
            Key = key,
            Name = id,
            Properties = properties.ToDictionary(pair => pair.Name, pair => (string?)pair.Value),
        };

    private static GraphEdgeDto Edge(string from, string to, string fromPort = "out") =>
        new() { Id = $"{from}-{to}-{fromPort}", FromId = from, FromPort = fromPort, ToId = to, ToPort = "in" };

    private Task<Guid> ScenarioAsync(ProofFlowDbContext context, string name, string path) =>
        ScenarioAsync(context, name, new GraphDto
        {
            Nodes =
            [
                Node("start", "core.start"),
                Node("call", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}" + path)),
            ],
            Edges = [Edge("start", "call")],
        });

    private async Task<Guid> ScenarioAsync(ProofFlowDbContext context, string name, GraphDto graph)
    {
        var scenario = new TestScenario
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = name,
            CreatedByUserId = _userId,
        };

        context.Scenarios.Add(scenario);
        await context.SaveChangesAsync();

        await Graphs(context).SaveAsync(scenario, graph);
        return scenario.Id;
    }

    /// <summary>
    /// Queues a batch and carries every run out.
    ///
    /// Run here rather than by the worker: the worker is a hosted service and this test is about
    /// the matrix, so the runs are executed directly and the grid is read afterwards.
    /// </summary>
    private async Task<Guid> QueueAndRunAsync(
        ProofFlowDbContext context, IReadOnlyList<Guid> scenarios, IReadOnlyList<Guid> environments)
    {
        var batch = await Matrix(context).QueueAsync(_projectId, scenarios, environments);

        var runs = await context.Runs
            .Where(run => run.BatchId == batch.Id)
            .Select(run => run.Id)
            .ToListAsync();

        var service = Service(context);
        foreach (var runId in runs) await service.ExecuteAsync(runId);

        return batch.Id;
    }

    private async Task<Contracts.Runs.MatrixDto?> RunAsync(
        ProofFlowDbContext context, IReadOnlyList<Guid> scenarios, IReadOnlyList<Guid> environments)
    {
        var batch = await QueueAndRunAsync(context, scenarios, environments);
        return await Matrix(context).ReadAsync(batch);
    }

    private MatrixService Matrix(ProofFlowDbContext context) => new(
        context, Service(context), new ChannelRunQueue(),
        new FixedUser(_workspaceId, _userId), new SystemClock());

    private RunService Service(ProofFlowDbContext context) => new(
        context,
        Graphs(context),
        new ScenarioGraphSnapshots(),
        new EnvironmentContextBuilder(
            context,
            new AesGcmSecretCipher(Configuration(), NullLogger<AesGcmSecretCipher>.Instance),
            NullLogger<EnvironmentContextBuilder>.Instance),
        new BaselineService(context, new FixedUser(_workspaceId, _userId), new SystemClock()),
        new GuardedHttpExecutor(_http.GetRequiredService<IHttpClientFactory>(),
            NullLogger<GuardedHttpExecutor>.Instance),
        new EnvironmentAuthenticator(
            new GuardedHttpExecutor(_http.GetRequiredService<IHttpClientFactory>(),
                NullLogger<GuardedHttpExecutor>.Instance),
            new TokenCache()),
        new NoWatchers(),
        new FixedUser(_workspaceId, _userId),
        new SystemClock(),
        NullLogger<RunService>.Instance);

    private ScenarioGraphService Graphs(ProofFlowDbContext context) => new(
        context, new FixedUser(_workspaceId, _userId), new SystemClock(), new PlainProblems());

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
        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Matrix", Slug = "matrix" });

        var project = new Project { WorkspaceId = _workspaceId, Name = "Catalog", Slug = "catalog" };
        context.Projects.Add(project);
        await context.SaveChangesAsync();

        _projectId = project.Id;
        _alphaId = await AddEnvironmentAsync(context, "Alpha", _alphaUrl, production: false);
        _betaId = await AddEnvironmentAsync(context, "Beta", _betaUrl, production: true);
    }

    private async Task<Guid> AddEnvironmentAsync(
        ProofFlowDbContext context, string name, string baseUrl, bool production)
    {
        var order = await context.Environments.CountAsync(e => e.ProjectId == _projectId);

        var environment = new ProjectEnvironment
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = name,
            Slug = name.ToLowerInvariant().Replace(' ', '-'),
            BaseUrl = baseUrl,
            IsProduction = production,
            SortOrder = order,
            // Both servers listen on loopback, which the guard refuses unless told not to.
            AllowPrivateNetwork = true,
        };

        context.Environments.Add(environment);
        await context.SaveChangesAsync();

        return environment.Id;
    }

    private sealed class FixedUser(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? WorkspaceId => workspaceId;
        public string DisplayName => "Matrix";
        public WorkspaceRole? Role => WorkspaceRole.Owner;
        public bool IsAuthenticated => true;
        public bool Can(Capability capability) => true;
    }

    private sealed class PlainProblems : IProblemText
    {
        public string For(TestEngine.Nodes.GraphProblem problem) =>
            problem.Arguments.Count == 0
                ? problem.Code
                : $"{problem.Code}: {string.Join(", ", problem.Arguments)}";
    }
}
