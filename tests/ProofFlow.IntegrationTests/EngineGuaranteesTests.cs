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
using ProofFlow.Contracts.Capture;
using ProofFlow.Contracts.Requests;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.FakeApi;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Capture;
using ProofFlow.Infrastructure.Common;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Security;
using ProofFlow.Infrastructure.Tenancy;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The engine's word about status and speed.
///
/// Two holes closed here. Status codes were recorded and never compared, so a 500 whose error body
/// happened to hash-match the approved body counted as a pass — and a 200 carrying a 401's body did
/// too, which is why negative tests only half-worked. And nothing anywhere could say «this answer
/// is right but took nine seconds».
///
/// Everything runs against the fake API on a real socket, through the same guarded executor as
/// production. The status-hole test writes its approved sample by hand because the hole is precise:
/// same body, different status — a combination no ordinary approval produces on purpose.
/// </summary>
public sealed class EngineGuaranteesTests : IAsyncLifetime
{
    private WebApplication _server = null!;
    private ServiceProvider _http = null!;
    private SqliteConnection _connection = null!;
    private string _baseUrl = null!;

    private readonly Guid _workspaceId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private Guid _projectId;
    private Guid _environmentId;

    [Fact]
    public async Task A_500_with_a_matching_body_no_longer_passes()
    {
        await using var context = Db();

        // What /fake/status/500 answers, approved as if the API had said it with a 200 — the exact
        // combination the old judge waved through: hash equal, status ignored.
        var body = """{"code":500,"message":"Deliberate 500."}""";

        var (baselineId, versionId) = await EndpointAsync(
            context, "{{environment.baseUrl}}/fake/status/500");

        context.BaselineSamples.Add(new BaselineSample
        {
            WorkspaceId = _workspaceId,
            BaselineId = baselineId,
            Key = "row0",
            Body = body,
            StatusCode = 200,
            NormalizedHash = BaselineService.Hash(body, new ComparisonRuleSet([])),
        });
        await context.SaveChangesAsync();

        var session = await Capture(context).RunAsync(new StartCaptureCommand
        {
            BaselineId = baselineId,
            DataSetVersionId = versionId,
            EnvironmentId = _environmentId,
        });

        session.Status.Should().Be(CaptureSessionStatus.Completed);
        session.Differing.Should().Be(1, "an identical body with a different status is a change");

        var sample = await context.CaptureSamples.SingleAsync(s => s.CaptureSessionId == session.Id);

        sample.Differs.Should().BeTrue();
        sample.DiffSummaryJson.Should().Contain("Status",
            "the summary should name what actually moved");
    }

    [Fact]
    public async Task A_correct_answer_over_budget_is_slow_and_not_a_pass()
    {
        await using var context = Db();

        var (baselineId, versionId) = await EndpointAsync(
            context, "{{environment.baseUrl}}/fake/records/7");

        // A budget of one millisecond, which a real socket cannot beat. The answer's content will
        // match exactly — this test is about the clock, not the body.
        var record = await Executor().SendAsync(
            new HttpRequestDefinition { Method = "GET", Url = $"{_baseUrl}/fake/records/7" },
            new UrlPolicy { AllowPrivateNetwork = true });

        context.BaselineSamples.Add(new BaselineSample
        {
            WorkspaceId = _workspaceId,
            BaselineId = baselineId,
            Key = "row0",
            Body = record.Body!,
            StatusCode = 200,
            NormalizedHash = BaselineService.Hash(record.Body!, new ComparisonRuleSet([])),
        });

        var endpoint = await context.Baselines.FirstAsync(b => b.Id == baselineId);
        endpoint.MaxDurationMs = 1;
        await context.SaveChangesAsync();

        var session = await Capture(context).RunAsync(new StartCaptureCommand
        {
            BaselineId = baselineId,
            DataSetVersionId = versionId,
            EnvironmentId = _environmentId,
        });

        session.Status.Should().Be(CaptureSessionStatus.Completed);
        session.Differing.Should().Be(0, "the content is exactly right");
        session.Slow.Should().Be(1, "but the clock is part of the test now");

        var sample = await context.CaptureSamples.SingleAsync(s => s.CaptureSessionId == session.Id);

        sample.TooSlow.Should().BeTrue();
        sample.Differs.Should().BeFalse();
    }

    [Fact]
    public async Task A_broken_promise_is_found_even_when_the_recorded_answer_still_matches()
    {
        // The contract answers a different question from the baseline. The baseline says «this is
        // what it returned last time»; the contract says «this is what it said it would always
        // return». A rule can silence the first — that is what rules are for — and must not be
        // able to silence the second.
        await using var context = Db();

        var (baselineId, versionId) = await EndpointAsync(
            context, "{{environment.baseUrl}}/fake/records/7");

        var record = await Executor().SendAsync(
            new HttpRequestDefinition { Method = "GET", Url = $"{_baseUrl}/fake/records/7" },
            new UrlPolicy { AllowPrivateNetwork = true });

        // The approved answer is exactly what the API returns, so the body diff is silent.
        context.BaselineSamples.Add(new BaselineSample
        {
            WorkspaceId = _workspaceId,
            BaselineId = baselineId,
            Key = "row0",
            Body = record.Body!,
            StatusCode = 200,
            NormalizedHash = BaselineService.Hash(record.Body!, new ComparisonRuleSet([])),
        });

        var endpoint = await context.Baselines.FirstAsync(b => b.Id == baselineId);

        // And a contract the API does not honour: the fake API's «score» is a number.
        endpoint.ContractJson = """
            {"type":"object","properties":{"score":{"type":"string"}},"required":["score"]}
            """;

        // A rule that would hide the field if this were an ordinary difference — «score changes,
        // stop checking it». It must not reach the promise.
        context.BaselineRules.Add(new BaselineRule
        {
            WorkspaceId = _workspaceId,
            BaselineId = baselineId,
            Path = "$.score",
            Matcher = nameof(MatcherKind.Ignore),
            Enabled = true,
        });

        await context.SaveChangesAsync();

        var session = await Capture(context).RunAsync(new StartCaptureCommand
        {
            BaselineId = baselineId,
            DataSetVersionId = versionId,
            EnvironmentId = _environmentId,
        });

        session.Status.Should().Be(CaptureSessionStatus.Completed);
        session.Differing.Should().Be(1, "the API is not honouring what its document promised");

        var sample = await context.CaptureSamples.SingleAsync(s => s.CaptureSessionId == session.Id);

        sample.Differs.Should().BeTrue();
        sample.DiffSummaryJson.Should().Contain("Contract");
        sample.FailureMessage.Should().Contain("score",
            "the reader should not have to open the sample to learn which promise broke");
    }

    [Fact]
    public async Task A_bare_request_is_sent_without_the_environment_signing_it_in()
    {
        // The engine half of «without a token, this should refuse»: Bare short-circuits
        // inheritance in the one function all five senders share.
        var merged = InheritedHeaders.Apply(
            new HttpRequestDefinition { Url = "https://x.test/thing", Bare = true },
            [new KeyValueEntry("Authorization", "Bearer the-environments-token")],
            """{"Accept":"application/json"}""");

        merged.Headers.Should().BeEmpty("bare means bare — no auth, no defaults");

        // And end to end: an environment configured to sign in, a bare request through a sweep,
        // and the protected path's own 401 — recorded, not failed, because the server answered.
        await using var context = Db();
        await SetAuthAsync(context);

        var (baselineId, versionId) = await EndpointAsync(
            context, "{{environment.baseUrl}}/fake/categories", bare: true);

        var session = await Capture(context).RunAsync(new StartCaptureCommand
        {
            BaselineId = baselineId,
            DataSetVersionId = versionId,
            EnvironmentId = _environmentId,
        });

        session.Status.Should().Be(CaptureSessionStatus.Completed);

        var sample = await context.CaptureSamples.SingleAsync(s => s.CaptureSessionId == session.Id);

        sample.StatusCode.Should().Be(401,
            "the environment knows how to sign in and must not have");
        sample.Body.Should().Contain("missing_token", "the server's own refusal, kept as data");
    }

    // ---- scaffolding ----------------------------------------------------------------------------

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

        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Truth", Slug = "truth" });

        var project = new Project { WorkspaceId = _workspaceId, Name = "Guarded", Slug = "guarded" };
        context.Projects.Add(project);

        var environment = new ProjectEnvironment
        {
            WorkspaceId = _workspaceId,
            ProjectId = project.Id,
            Name = "Local",
            Slug = "local",
            BaseUrl = _baseUrl,
            AllowPrivateNetwork = true,
        };
        context.Environments.Add(environment);

        await context.SaveChangesAsync();

        _projectId = project.Id;
        _environmentId = environment.Id;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        await _http.DisposeAsync();
        _connection.Dispose();
    }

    /// <summary>One endpoint, one single-row data set — the smallest sweep there is.</summary>
    private async Task<(Guid BaselineId, Guid VersionId)> EndpointAsync(
        ProofFlowDbContext context, string url, bool bare = false)
    {
        var baseline = new Baseline
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            EnvironmentId = _environmentId,
            Name = $"guarantee {Guid.CreateVersion7()}",
            CreatedByUserId = _userId,
            RequestJson = JsonSerializer.Serialize(new { method = "GET", url, bare }),
        };
        context.Baselines.Add(baseline);

        var set = new DataSet
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = $"one {Guid.CreateVersion7()}",
            KeyColumn = "key",
            CreatedByUserId = _userId,
        };
        context.DataSets.Add(set);

        var version = new DataSetVersion
        {
            WorkspaceId = _workspaceId,
            DataSetId = set.Id,
            Number = 1,
            ColumnsJson = """["key"]""",
            RowCount = 1,
            CreatedByUserId = _userId,
        };
        context.DataSetVersions.Add(version);

        context.DataSetRows.Add(new DataSetRow
        {
            WorkspaceId = _workspaceId,
            DataSetVersionId = version.Id,
            Ordinal = 0,
            Key = "row0",
            ValuesJson = """{"key":"row0"}""",
        });

        await context.SaveChangesAsync();
        return (baseline.Id, version.Id);
    }

    private async Task SetAuthAsync(ProofFlowDbContext context)
    {
        var environment = await context.Environments.FirstAsync(e => e.Id == _environmentId);

        environment.AuthenticationJson = new EnvironmentAuth
        {
            Mode = AuthMode.SignIn,
            TokenUrl = "/fake/auth/login",
            Method = "POST",
            BodyKind = "json",
            Credentials = new Dictionary<string, string>
            {
                ["username"] = "demo",
                ["password"] = "demo-password",
            },
        }.Write();

        await context.SaveChangesAsync();
    }

    private CaptureService Capture(ProofFlowDbContext context) => new(
        context,
        Builder(context),
        Executor(),
        new EnvironmentAuthenticator(Executor(), new TokenCache()),
        new FixedUser(_workspaceId, _userId),
        new SystemClock());

    private EnvironmentContextBuilder Builder(ProofFlowDbContext context) => new(
        context,
        new AesGcmSecretCipher(Configuration(), NullLogger<AesGcmSecretCipher>.Instance),
        NullLogger<EnvironmentContextBuilder>.Instance);

    private GuardedHttpExecutor Executor() =>
        new(_http.GetRequiredService<IHttpClientFactory>(), NullLogger<GuardedHttpExecutor>.Instance);

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

    private sealed class FixedUser(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? WorkspaceId => workspaceId;
        public string DisplayName => "Truth";
        public string? Email => "truth@example.test";
        public ProofFlow.Domain.Authorization.WorkspaceRole? Role =>
            ProofFlow.Domain.Authorization.WorkspaceRole.Owner;
        public bool IsAuthenticated => true;
        public bool Can(ProofFlow.Domain.Authorization.Capability capability) => true;
    }
}
