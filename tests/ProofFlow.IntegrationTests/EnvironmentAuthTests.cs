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
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.FakeApi;
using ProofFlow.Infrastructure.Capture;
using ProofFlow.Infrastructure.Common;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Security;
using ProofFlow.Infrastructure.Tenancy;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// An API that needs a token, tested without anybody typing a token.
///
/// This is the thing the product promised and did not do. <c>AuthenticationJson</c> had sat on the
/// environment since the first migration with a comment saying it applies to every request, and
/// nothing wrote it and nothing read it — so «our APIs have auth» meant putting an
/// <c>Authorization</c> header on every endpoint and every step by hand, with a token that expires.
///
/// Everything here runs against the fake API in this repository, which refuses without a bearer
/// token and mints a different one every time. Nothing is stubbed: if the sign-in does not really
/// happen, the assertions get a real 401 from a real server.
/// </summary>
public sealed class EnvironmentAuthTests : IAsyncLifetime
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
    public async Task A_json_login_produces_a_header_that_a_protected_endpoint_accepts()
    {
        await using var context = Db();
        await SetAuthAsync(context, SignIn());

        var environment = await Context(context);

        var outcome = await Authenticator().HeadersAsync(
            environment.Auth, environment.Environment.BaseUrl, environment.Resolver(),
            environment.Policy, environment.TokenKey);

        outcome.Ok.Should().BeTrue("signing in should work, but: {0}", outcome.Problem);

        var header = outcome.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("Authorization");
        header.Value.Should().StartWith("Bearer tok_");

        // And the server agrees. A header that looks right and is refused is the failure this
        // whole test exists to catch, so it is asked rather than assumed.
        var response = await Executor().SendAsync(
            new HttpRequestDefinition
            {
                Method = "GET",
                Url = $"{_baseUrl}/fake/categories",
                Headers = [header],
            },
            environment.Policy);

        response.StatusCode.Should().Be(200, "the token the login returned should be accepted");
    }

    [Fact]
    public async Task Without_authentication_the_same_endpoint_refuses()
    {
        // The control. A test that only asserts 200 cannot tell «the token worked» from «this
        // endpoint never needed one», and the difference is the entire feature.
        await using var context = Db();
        var environment = await Context(context);

        var response = await Executor().SendAsync(
            new HttpRequestDefinition { Method = "GET", Url = $"{_baseUrl}/fake/categories" },
            environment.Policy);

        response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Every_row_of_a_sweep_is_authorised_without_a_header_being_configured()
    {
        await using var context = Db();
        await SetAuthAsync(context, SignIn());

        var (baselineId, versionId) = await ProtectedEndpointAsync(context);

        var session = await Capture(context).RunAsync(new StartCaptureCommand
        {
            BaselineId = baselineId,
            DataSetVersionId = versionId,
            EnvironmentId = _environmentId,
        });

        session.Status.Should().Be(CaptureSessionStatus.Completed);

        var samples = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == session.Id)
            .ToListAsync();

        samples.Should().HaveCount(3);

        // Every row, not most of them — and the endpoint's stored request carries no Authorization
        // header at all, so a 200 here can only have come from the environment.
        samples.Should().OnlyContain(s => s.StatusCode == 200,
            "every row should be authorised, but: {0}",
            string.Join(" | ", samples.Select(s => $"{s.Key}: {s.StatusCode} {s.FailureMessage}")));

        // That the sweep signs in once rather than once per row is a separate property, and it is
        // asserted where it can actually be observed — in the cache test below. Counting logins
        // from out here would need the fake API to keep a tally, and a test that asserted it from
        // response bodies would be asserting something it cannot see.
        samples.Should().OnlyContain(s => s.Body != null);
    }

    [Fact]
    public async Task A_wrong_password_stops_the_sweep_and_says_so()
    {
        await using var context = Db();
        await SetAuthAsync(context, SignIn() with
        {
            Credentials = new Dictionary<string, string>
            {
                ["username"] = "demo",
                ["password"] = "not-the-password",
            },
        });

        var (baselineId, versionId) = await ProtectedEndpointAsync(context);

        var session = await Capture(context).RunAsync(new StartCaptureCommand
        {
            BaselineId = baselineId,
            DataSetVersionId = versionId,
            EnvironmentId = _environmentId,
        });

        // Not «three rows differed». Sending the requests anyway would report three 401s as a
        // change in the API, and the actual cause — a password that no longer works — would be
        // nowhere on the screen.
        session.Status.Should().Be(CaptureSessionStatus.Failed);
        session.StoppedReason.Should().Contain("401");
        session.StoppedReason.Should().Contain("invalid_credentials", "the server's own words");

        (await context.CaptureSamples.CountAsync()).Should().Be(0, "nothing should have been sent");
    }

    [Fact]
    public async Task A_step_that_sets_its_own_authorization_keeps_it()
    {
        // Inheritance, not override. A test about permissions signs in as somebody else on purpose,
        // and an environment that overwrote that header would make that test impossible to write.
        var request = new HttpRequestDefinition
        {
            Method = "GET",
            Url = "https://x.test/thing",
            Headers = [new KeyValueEntry("Authorization", "Bearer somebody-else")],
        };

        var merged = InheritedHeaders.Apply(
            request,
            [new KeyValueEntry("Authorization", "Bearer the-environments-token")],
            """{"Accept":"application/json"}""");

        merged.Headers.Should().ContainSingle(h => h.Name == "Authorization")
            .Which.Value.Should().Be("Bearer somebody-else");

        // And the default header it did not set is still added.
        merged.Headers.Should().ContainSingle(h => h.Name == "Accept");
    }

    [Fact]
    public async Task A_changed_password_is_not_papered_over_by_the_cached_token()
    {
        await using var context = Db();
        await SetAuthAsync(context, SignIn());

        var cache = new TokenCache();
        var first = await Sign(context, cache);

        first.Ok.Should().BeTrue("{0}", first.Problem);

        // The same configuration again comes back cached, which is the point of the cache: a sweep
        // across two thousand inputs signs in once.
        var again = await Sign(context, cache);

        again.Headers.Single().Value.Should().Be(first.Headers.Single().Value,
            "an unchanged configuration should reuse its token");

        // Now the credential changes to one the server refuses. The cache is keyed on a
        // fingerprint of the resolved values, so this must fail — a cache keyed only on the
        // environment would hand back the working token and report success about a password that
        // no longer works, which is the failure that would be discovered weeks later.
        await SetAuthAsync(context, SignIn() with
        {
            Credentials = new Dictionary<string, string>
            {
                ["username"] = "demo",
                ["password"] = "changed-yesterday",
            },
        });

        var third = await Sign(context, cache);

        third.Ok.Should().BeFalse("the new password is wrong and the old token must not stand in");
        third.Problem.Should().Contain("401");

        async Task<AuthOutcome> Sign(ProofFlowDbContext db, TokenCache held)
        {
            var environment = await Context(db);

            return await new EnvironmentAuthenticator(Executor(), held).HeadersAsync(
                environment.Auth, environment.Environment.BaseUrl, environment.Resolver(),
                environment.Policy, environment.TokenKey);
        }
    }

    // ---- setup ---------------------------------------------------------------------------------

    /// <summary>The shape the flow writes: a JSON login against the fake API's own endpoint.</summary>
    private static EnvironmentAuth SignIn() => new()
    {
        Mode = AuthMode.SignIn,
        TokenUrl = "/fake/auth/login",
        Method = "POST",
        BodyKind = "json",
        Credentials = new Dictionary<string, string>
        {
            ["username"] = "demo",

            // Through a secret, as the flow stores it — so this also proves a reference resolves
            // inside the auth configuration rather than only inside a request.
            ["password"] = "{{secrets.apiPassword}}",
        },
        ExpiresInPath = "expiresIn",
    };

    private async Task SetAuthAsync(ProofFlowDbContext context, EnvironmentAuth auth)
    {
        var environment = await context.Environments.FirstAsync(e => e.Id == _environmentId);
        environment.AuthenticationJson = auth.Write();
        await context.SaveChangesAsync();
    }

    private async Task<EnvironmentContext> Context(ProofFlowDbContext context) =>
        await Builder(context).BuildAsync(_environmentId);

    private EnvironmentContextBuilder Builder(ProofFlowDbContext context) => new(
        context,
        new AesGcmSecretCipher(Configuration(), NullLogger<AesGcmSecretCipher>.Instance),
        NullLogger<EnvironmentContextBuilder>.Instance);

    private EnvironmentAuthenticator Authenticator() => new(Executor(), new TokenCache());

    private GuardedHttpExecutor Executor() =>
        new(_http.GetRequiredService<IHttpClientFactory>(),
            NullLogger<GuardedHttpExecutor>.Instance);

    private CaptureService Capture(ProofFlowDbContext context) => new(
        context,
        Builder(context),
        Executor(),
        Authenticator(),
        new FixedUser(_workspaceId, _userId),
        new SystemClock());

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

    /// <summary>An endpoint on a path the fake API refuses without a token, and three inputs.</summary>
    private async Task<(Guid BaselineId, Guid VersionId)> ProtectedEndpointAsync(ProofFlowDbContext context)
    {
        var baseline = new Baseline
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            EnvironmentId = _environmentId,
            Name = $"categories {Guid.CreateVersion7()}",
            CreatedByUserId = _userId,
            RequestJson = JsonSerializer.Serialize(new
            {
                method = "GET",
                url = "{{environment.baseUrl}}/fake/categories?shuffle={{dataset.current.shuffle}}",
            }),
        };

        context.Baselines.Add(baseline);

        var set = new DataSet
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = $"flags {Guid.CreateVersion7()}",
            KeyColumn = "shuffle",
            CreatedByUserId = _userId,
        };
        context.DataSets.Add(set);

        var version = new DataSetVersion
        {
            WorkspaceId = _workspaceId,
            DataSetId = set.Id,
            Number = 1,
            ColumnsJson = """["shuffle"]""",
            RowCount = 3,
            CreatedByUserId = _userId,
        };
        context.DataSetVersions.Add(version);

        var ordinal = 0;

        foreach (var value in new[] { "false", "true", "false" })
        {
            context.DataSetRows.Add(new DataSetRow
            {
                WorkspaceId = _workspaceId,
                DataSetVersionId = version.Id,
                Ordinal = ordinal,
                Key = $"row{ordinal++}",
                ValuesJson = JsonSerializer.Serialize(new { shuffle = value }),
            });
        }

        await context.SaveChangesAsync();
        return (baseline.Id, version.Id);
    }

    private async Task SeedAsync(ProofFlowDbContext context)
    {
        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Auth", Slug = "auth" });

        var project = new Project { WorkspaceId = _workspaceId, Name = "Secured", Slug = "secured" };
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

        var sealedPassword = new AesGcmSecretCipher(
            Configuration(), NullLogger<AesGcmSecretCipher>.Instance).Seal("demo-password");

        context.Secrets.Add(new Secret
        {
            WorkspaceId = _workspaceId,
            ProjectId = project.Id,
            EnvironmentId = environment.Id,
            Name = "apiPassword",
            Ciphertext = sealedPassword.Ciphertext,
            Nonce = sealedPassword.Nonce,
            Tag = sealedPassword.Tag,
            KeyVersion = sealedPassword.KeyVersion,
            Preview = "word",
            CreatedByUserId = _userId,
        });

        await context.SaveChangesAsync();

        _projectId = project.Id;
        _environmentId = environment.Id;
    }

    private sealed class FixedUser(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? WorkspaceId => workspaceId;
        public string DisplayName => "Auth";
        public string? Email => "auth@example.test";
        public WorkspaceRole? Role => WorkspaceRole.Owner;
        public bool IsAuthenticated => true;
        public bool Can(Capability capability) => true;
    }
}
