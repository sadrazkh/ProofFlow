using ProofFlow.TestEngine.Http;
using System.Text.Json;
using System.Xml.Linq;
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
using ProofFlow.Infrastructure.Scheduling;
using ProofFlow.Infrastructure.Security;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// Schedules, keys and the report a build system reads.
///
/// The parts of step nineteen that cannot be tested with a fake anything: a cron expression has to
/// produce a real instant, a key has to be unrecoverable from the database, and the XML has to be
/// the shape every CI tool already parses — because "we emit JUnit" is only true if a reader agrees.
/// </summary>
public sealed class ScheduleAndCiTests : IAsyncLifetime
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

    // ---- schedules -----------------------------------------------------------------------------

    [Fact]
    public async Task A_saved_schedule_knows_when_it_is_next_due()
    {
        await using var context = Db();
        var scenario = await ScenarioAsync(context, "nightly");

        var schedule = await Schedules(context).SaveAsync(
            _projectId, null, "Nightly", "0 6 * * *", "Asia/Tehran",
            [scenario], [_environmentId], enabled: true);

        schedule.NextRunAt.Should().NotBeNull();
        schedule.Problem.Should().BeNull();

        // 06:00 in Tehran, which is 02:30 UTC. Stored as an instant so "what is due" is an index
        // scan rather than parsing every expression on every tick.
        schedule.NextRunAt!.Value.UtcDateTime.Hour.Should().Be(2);
        schedule.NextRunAt.Value.UtcDateTime.Minute.Should().Be(30);
    }

    [Fact]
    public async Task A_schedule_with_an_unreadable_expression_says_so_rather_than_never_firing()
    {
        await using var context = Db();
        var scenario = await ScenarioAsync(context, "broken");

        var schedule = await Schedules(context).SaveAsync(
            _projectId, null, "Broken", "not a cron", "UTC",
            [scenario], [_environmentId], enabled: true);

        schedule.Problem.Should().Be("cron.unreadable");
        schedule.NextRunAt.Should().BeNull();
    }

    [Fact]
    public async Task A_missed_schedule_fires_once_when_it_comes_back_not_once_per_missed_hour()
    {
        // The decision that matters most in the scheduler. A catch-up storm against somebody's
        // production API is a far worse failure than a missed window.
        await using var context = Db();
        var scenario = await ScenarioAsync(context, "hourly");

        var schedule = await Schedules(context).SaveAsync(
            _projectId, null, "Hourly", "0 * * * *", "UTC",
            [scenario], [_environmentId], enabled: true);

        // Pretend the process was down for a day.
        var now = DateTimeOffset.UtcNow;
        schedule.NextRunAt = now.AddDays(-1);
        await context.SaveChangesAsync();

        ScheduleService.Advance(schedule, now);

        // One occurrence ahead, not twenty-four behind.
        schedule.NextRunAt.Should().BeAfter(now);
        schedule.NextRunAt.Should().BeBefore(now.AddHours(1).AddMinutes(1));
    }

    [Fact]
    public async Task A_schedule_cannot_be_pointed_at_another_projects_scenario()
    {
        await using var context = Db();

        var other = new Project { WorkspaceId = _workspaceId, Name = "Other", Slug = "other" };
        context.Projects.Add(other);
        await context.SaveChangesAsync();

        var stranger = new TestScenario
        {
            WorkspaceId = _workspaceId,
            ProjectId = other.Id,
            Name = "not yours",
            CreatedByUserId = _userId,
        };

        context.Scenarios.Add(stranger);
        await context.SaveChangesAsync();

        var save = async () => await Schedules(context).SaveAsync(
            _projectId, null, "Sneaky", "0 6 * * *", "UTC",
            [stranger.Id], [_environmentId], enabled: true);

        await save.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- keys ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_key_is_returned_once_and_never_stored()
    {
        await using var context = Db();

        var (key, secret) = await Keys(context).IssueAsync(_workspaceId, null, "CI", null);

        secret.Should().StartWith("pf_");
        secret.Length.Should().BeGreaterThan(20);

        // Nothing in the row is the secret, and nothing in the row can produce it.
        key.Hash.Should().NotBe(secret);
        key.Preview.Should().HaveLength("pf_".Length + ApiKeyService.PreviewLength);
        secret.Should().StartWith(key.Preview);

        var stored = await context.ApiKeys.AsNoTracking().FirstAsync(row => row.Id == key.Id);
        stored.Hash.Should().NotContain(secret);
    }

    [Fact]
    public async Task A_key_is_found_by_its_secret_and_nothing_else()
    {
        await using var context = Db();
        var (key, secret) = await Keys(context).IssueAsync(_workspaceId, null, "CI", null);

        (await Keys(context).FindAsync(secret))!.Id.Should().Be(key.Id);

        (await Keys(context).FindAsync(secret + "x")).Should().BeNull();
        (await Keys(context).FindAsync(key.Preview)).Should().BeNull();
        (await Keys(context).FindAsync("pf_nonsense")).Should().BeNull();
        (await Keys(context).FindAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_or_expired_key_stops_working()
    {
        await using var context = Db();
        var service = Keys(context);

        var (revoked, revokedSecret) = await service.IssueAsync(_workspaceId, null, "old", null);
        await service.RevokeAsync(_workspaceId, revoked.Id);

        (await service.FindAsync(revokedSecret)).Should().BeNull();

        var (_, expiredSecret) = await service.IssueAsync(
            _workspaceId, null, "lapsed", DateTimeOffset.UtcNow.AddDays(-1));

        (await service.FindAsync(expiredSecret)).Should().BeNull();

        // Revoked, not deleted: the audit trail names the key that started a run.
        (await context.ApiKeys.CountAsync()).Should().Be(2);
    }

    // ---- the report ----------------------------------------------------------------------------

    [Fact]
    public async Task A_passing_run_produces_a_suite_a_build_system_can_read()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "healthy", "200");
        var run = await RunAsync(context, scenario);

        run.Status.Should().Be(RunStatus.Passed, "the outcome was: {0}", run.Outcome);

        var document = await new JUnitReport(context).ForRunAsync(run.Id);
        document.Should().NotBeNull();

        var root = document!.Root!;
        root.Name.LocalName.Should().Be("testsuites");

        var suite = root.Elements("testsuite").Should().ContainSingle().Subject;
        suite.Attribute("name")!.Value.Should().Contain("healthy").And.Contain("Local");
        suite.Attribute("failures")!.Value.Should().Be("0");
        suite.Attribute("errors")!.Value.Should().Be("0");

        var cases = suite.Elements("testcase").ToList();
        cases.Should().NotBeEmpty("a suite with no cases reads in CI as a pass");
        cases.Should().OnlyContain(entry => !entry.Elements("failure").Any());

        // Seconds with a point, invariant. On a comma-decimal machine "0,138" is a number every
        // JUnit reader in existence gets wrong.
        foreach (var entry in cases)
        {
            entry.Attribute("time")!.Value.Should().NotContain(",");
            double.Parse(entry.Attribute("time")!.Value,
                System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(0);
        }

        // Always ISO Gregorian, whatever the interface language. A build server reading a Jalali
        // date would either fail or parse the wrong year.
        var timestamp = suite.Attribute("timestamp")!.Value;
        DateTime.TryParseExact(timestamp, "yyyy-MM-ddTHH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_check_becomes_a_failure_a_build_can_go_red_on()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "wrong expectation", "418");
        var run = await RunAsync(context, scenario);

        run.Status.Should().Be(RunStatus.Failed);

        var document = await new JUnitReport(context).ForRunAsync(run.Id);
        var suite = document!.Root!.Element("testsuite")!;

        suite.Attribute("failures")!.Value.Should().Be("1");

        var failure = suite.Elements("testcase")
            .SelectMany(entry => entry.Elements("failure"))
            .Should().ContainSingle().Subject;

        failure.Attribute("message")!.Value.Should().Contain("418");
        failure.Value.Should().Contain("418");
    }

    [Fact]
    public async Task A_quarantined_scenario_reports_its_failure_as_a_skip()
    {
        // The whole meaning of quarantine: it still runs, still reports, and stops failing the
        // build. Deleting it would take its coverage away and nobody would notice.
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "unreliable", "418");
        var run = await RunAsync(context, scenario);

        await new FlakyDetector(context, new SystemClock())
            .QuarantineAsync(_projectId, scenario, true, "flaps", _userId);

        var document = await new JUnitReport(context).ForRunAsync(run.Id);
        var suite = document!.Root!.Element("testsuite")!;

        suite.Attribute("failures")!.Value.Should().Be("0");
        suite.Attribute("skipped")!.Value.Should().Be("1");

        suite.Elements("testcase").SelectMany(entry => entry.Elements("skipped"))
            .Should().ContainSingle()
            .Which.Attribute("message")!.Value.Should().Contain("Quarantined");
    }

    [Fact]
    public async Task A_run_that_never_got_going_is_still_in_the_report()
    {
        // A broken scenario that produced no cases would read in CI as an empty suite — which is to
        // say, as a pass.
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "unreadable", "200");
        var service = Service(context);
        var queued = await service.QueueAsync(scenario, _environmentId, RunTrigger.Api);

        queued.DefinitionJson = "{ this is not json";
        await context.SaveChangesAsync();

        await service.ExecuteAsync(queued.Id);

        var document = await new JUnitReport(context).ForRunAsync(queued.Id);
        var suite = document!.Root!.Element("testsuite")!;

        suite.Attribute("tests")!.Value.Should().Be("1");
        suite.Attribute("errors")!.Value.Should().Be("1");
    }

    [Fact]
    public async Task A_batch_becomes_one_document_whose_failures_say_which_environment()
    {
        await using var context = Db();

        var second = await AddEnvironmentAsync(context, "Second");
        var scenario = await ScenarioAsync(context, "everywhere", "200");

        var batch = await Matrix(context).QueueAsync(_projectId, [scenario], [_environmentId, second]);

        var service = Service(context);
        foreach (var id in await context.Runs.Where(run => run.BatchId == batch.Id)
                     .Select(run => run.Id).ToListAsync())
        {
            await service.ExecuteAsync(id);
        }

        var document = await new JUnitReport(context).ForBatchAsync(batch.Id);
        var suites = document!.Root!.Elements("testsuite").ToList();

        suites.Should().HaveCount(2);
        suites.Select(suite => suite.Attribute("name")!.Value)
            .Should().Contain(name => name.Contains("Local"))
            .And.Contain(name => name.Contains("Second"));
    }

    // ---- flakiness -----------------------------------------------------------------------------

    [Fact]
    public async Task A_test_that_passes_and_fails_without_changing_is_flaky()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "flapper", "200");
        var version = await context.ScenarioVersions.FirstAsync(v => v.ScenarioId == scenario);

        // Three runs of the same version in the same environment: two pass, one fails. That is
        // exactly the shape the detector is for.
        await AddRunAsync(context, scenario, version.Id, _environmentId, RunStatus.Passed);
        await AddRunAsync(context, scenario, version.Id, _environmentId, RunStatus.Failed);
        await AddRunAsync(context, scenario, version.Id, _environmentId, RunStatus.Passed);

        var found = await new FlakyDetector(context, new SystemClock()).ForProjectAsync(_projectId);

        var entry = found.Should().ContainSingle().Subject;
        entry.Name.Should().Be("flapper");
        entry.Runs.Should().Be(3);
        entry.Failed.Should().Be(1);
        entry.Rate.Should().BeApproximately(1.0 / 3, 0.001);
    }

    [Fact]
    public async Task A_test_that_only_ever_fails_is_broken_rather_than_flaky()
    {
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "just broken", "200");
        var version = await context.ScenarioVersions.FirstAsync(v => v.ScenarioId == scenario);

        for (var index = 0; index < 4; index++)
        {
            await AddRunAsync(context, scenario, version.Id, _environmentId, RunStatus.Failed);
        }

        (await new FlakyDetector(context, new SystemClock()).ForProjectAsync(_projectId))
            .Should().BeEmpty("a test that always fails is a regression, not a flake");
    }

    [Fact]
    public async Task Two_runs_are_not_enough_to_call_something_flaky()
    {
        // One pass and one failure is the most ordinary thing in the world — somebody broke it and
        // fixed it. Calling that flaky would make the label meaningless.
        await using var context = Db();

        var scenario = await ScenarioAsync(context, "coin", "200");
        var version = await context.ScenarioVersions.FirstAsync(v => v.ScenarioId == scenario);

        await AddRunAsync(context, scenario, version.Id, _environmentId, RunStatus.Passed);
        await AddRunAsync(context, scenario, version.Id, _environmentId, RunStatus.Failed);

        (await new FlakyDetector(context, new SystemClock()).ForProjectAsync(_projectId))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Passing_in_one_environment_and_failing_in_another_is_not_flakiness()
    {
        // It is the answer the matrix exists to give, and labelling it flaky would tell somebody to
        // quarantine a test that is working perfectly.
        await using var context = Db();

        var second = await AddEnvironmentAsync(context, "Second");
        var scenario = await ScenarioAsync(context, "environment-dependent", "200");
        var version = await context.ScenarioVersions.FirstAsync(v => v.ScenarioId == scenario);

        for (var index = 0; index < 3; index++)
        {
            await AddRunAsync(context, scenario, version.Id, _environmentId, RunStatus.Passed);
            await AddRunAsync(context, scenario, version.Id, second, RunStatus.Failed);
        }

        (await new FlakyDetector(context, new SystemClock()).ForProjectAsync(_projectId))
            .Should().BeEmpty();
    }

    // ---- setup ---------------------------------------------------------------------------------

    private static GraphNodeDto Node(string id, string key, params (string Name, string Value)[] properties) =>
        new()
        {
            Id = id,
            Key = key,
            Name = id,
            Properties = properties.ToDictionary(pair => pair.Name, pair => (string?)pair.Value),
        };

    private static GraphEdgeDto Edge(string from, string to, string fromPort = "out", string toPort = "in") =>
        new() { Id = $"{from}-{to}-{fromPort}", FromId = from, FromPort = fromPort, ToId = to, ToPort = toPort };

    private async Task<Guid> ScenarioAsync(
        ProofFlowDbContext context, string name, string expected = "200")
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

        await Graphs(context).SaveAsync(scenario, new GraphDto
        {
            Nodes =
            [
                Node("start", "core.start"),
                Node("call", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/fake/records/1")),
                Node("check", "assert.status", ("expected", expected)),
            ],
            Edges =
            [
                Edge("start", "call"),
                Edge("call", "check"),
                Edge("call", "check", "response", "response"),
            ],
        });

        return scenario.Id;
    }

    private async Task<TestRun> RunAsync(ProofFlowDbContext context, Guid scenarioId)
    {
        var service = Service(context);
        var queued = await service.QueueAsync(scenarioId, _environmentId, RunTrigger.Api);

        await service.ExecuteAsync(queued.Id);

        return await context.Runs.FirstAsync(run => run.Id == queued.Id);
    }

    /// <summary>A finished run written straight in — the detector reads history, not behaviour.</summary>
    private async Task AddRunAsync(
        ProofFlowDbContext context, Guid scenarioId, Guid versionId, Guid environmentId,
        RunStatus status)
    {
        context.Runs.Add(new TestRun
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            ScenarioId = scenarioId,
            ScenarioVersionId = versionId,
            EnvironmentId = environmentId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();
    }

    private ScheduleService Schedules(ProofFlowDbContext context) =>
        new(context, new SystemClock(), new FixedUser(_workspaceId, _userId));

    private ApiKeyService Keys(ProofFlowDbContext context) =>
        new(context, new SystemClock(), new FixedUser(_workspaceId, _userId));

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
        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "CI", Slug = "ci" });

        var project = new Project { WorkspaceId = _workspaceId, Name = "Catalog", Slug = "catalog" };
        context.Projects.Add(project);
        await context.SaveChangesAsync();

        _projectId = project.Id;
        _environmentId = await AddEnvironmentAsync(context, "Local");
    }

    private async Task<Guid> AddEnvironmentAsync(ProofFlowDbContext context, string name)
    {
        var order = await context.Environments.CountAsync(e => e.ProjectId == _projectId);

        var environment = new ProjectEnvironment
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = name,
            Slug = name.ToLowerInvariant(),
            BaseUrl = _baseUrl,
            SortOrder = order,
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
        public string DisplayName => "CI";
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
