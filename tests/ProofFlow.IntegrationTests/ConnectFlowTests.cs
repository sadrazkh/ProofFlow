using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Requests;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.FakeApi;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The four steps, walked the way a person walks them.
///
/// The complaint this answers was «our APIs have auth and it doesn't work». Everything the product
/// needed already existed and was spread over seven screens in an order nobody states, and the one
/// screen that had to work — fetching a token — spoke only OAuth2 form grants, so a JSON login came
/// back 400.
///
/// So these drive the real endpoints against a real API that refuses without a bearer token and
/// mints a different one every time. Nothing is stubbed: if the sign-in does not happen, the
/// assertions get a real 401 from a real server, and the last test would fail on the endpoint the
/// flow itself created.
/// </summary>
public sealed class ConnectFlowTests(ProofFlowApplication app)
    : IClassFixture<ProofFlowApplication>, IAsyncLifetime
{
    private const string Password = "a-long-enough-password";

    private WebApplication _api = null!;
    private string _apiUrl = null!;

    public async Task InitializeAsync()
    {
        // Its own port rather than the test host's pipe: the connect flow sends through the same
        // guarded executor as everything else, and that opens a socket. A TestServer handler would
        // make this a test of a fake.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddFakeApi();

        _api = builder.Build();
        _api.MapFakeApi();
        await _api.StartAsync();

        _apiUrl = _api.Urls.First().TrimEnd('/') + "/fake";
    }

    public async Task DisposeAsync()
    {
        await _api.StopAsync();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Signing_in_and_calling_are_reported_as_two_separate_things()
    {
        var (client, projectId) = await SignedInAsync();

        var result = await TryAsync(client, projectId, Attempt());

        result.SignIn.Ok.Should().BeTrue("the login should work, but: {0}", result.SignIn.Problem);

        // The token it found, so somebody can see the server answered with a token rather than with
        // a login page that happened to be 200.
        result.SignIn.Detail.Should().StartWith("Bearer tok_");

        result.Call.Should().NotBeNull();
        result.Call!.Ok.Should().BeTrue("the call should be authorised, but: {0}", result.Call.Problem);
        result.Call.StatusCode.Should().Be(200);
        result.Call.Url.Should().Be($"{_apiUrl}/categories");

        // The server's own answer, so somebody can see they reached the thing they meant to.
        result.Call.Detail.Should().Contain("\"items\"");
    }

    [Fact]
    public async Task The_same_call_without_credentials_is_not_reported_as_working()
    {
        // The control. Every other test here would pass just as well against an endpoint that never
        // needed a token, and the difference is the entire feature.
        var (client, projectId) = await SignedInAsync();

        var result = await TryAsync(client, projectId, Attempt() with { Kind = "none" });

        result.SignIn.Skipped.Should().BeTrue();

        // A completed request is not a success. Calling a 401 «done» would be the cruellest possible
        // moment for this page to be imprecise.
        result.Call!.Ok.Should().BeFalse();
        result.Call.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task A_wrong_password_stops_at_the_sign_in_and_quotes_the_server()
    {
        var (client, projectId) = await SignedInAsync();

        var result = await TryAsync(client, projectId,
            Attempt() with { PasswordValue = "not-the-password" });

        result.SignIn.Ok.Should().BeFalse();
        result.SignIn.Problem.Should().Contain("401");
        result.SignIn.Problem.Should().Contain("invalid_credentials", "the server's own words");

        // And the call is never made. Sending it anyway would produce a second, unrelated 401 and
        // leave two red lines on screen for one mistake.
        result.Call.Should().BeNull();
    }

    [Fact]
    public async Task A_sign_in_that_was_never_proved_writes_nothing()
    {
        var (client, projectId) = await SignedInAsync();

        await TryAsync(client, projectId, Attempt() with { PasswordValue = "not-the-password" });

        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        (await db.Environments.IgnoreQueryFilters().CountAsync(e => e.ProjectId == projectId))
            .Should().Be(0, "trying must not write anything at all");

        (await db.Secrets.IgnoreQueryFilters().CountAsync(s => s.ProjectId == projectId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Keeping_it_writes_an_environment_that_signs_itself_in()
    {
        var (client, projectId) = await SignedInAsync();

        var attempt = Attempt() with { Name = "Orders API" };

        (await TryAsync(client, projectId, attempt)).Call!.Ok.Should().BeTrue();

        var saved = await SaveAsync(client, projectId, attempt);

        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var environment = await db.Environments.IgnoreQueryFilters()
            .SingleAsync(e => e.Id == saved.EnvironmentId);

        environment.Name.Should().Be("Orders API");
        environment.BaseUrl.Should().Be(_apiUrl);
        environment.AllowPrivateNetwork.Should().BeTrue("the address is loopback and was ticked");

        var auth = EnvironmentAuth.Read(environment.AuthenticationJson);

        auth.Mode.Should().Be(AuthMode.SignIn);
        auth.TokenUrl.Should().Be("/auth/login");
        auth.Credentials["username"].Should().Be("demo");

        // The password is named, not repeated. Somebody who can open the environment learns that a
        // password is involved and not what it is, and a scheduled run at three in the morning
        // still has one to use.
        auth.Credentials["password"].Should().Be("{{secrets.password}}");

        var secret = await db.Secrets.IgnoreQueryFilters()
            .SingleAsync(s => s.ProjectId == projectId && s.Name == "password");

        secret.EnvironmentId.Should().Be(environment.Id);

        var cipher = scope.ServiceProvider.GetRequiredService<ISecretCipher>();
        cipher.Open(new SealedSecret(secret.Ciphertext, secret.Nonce, secret.Tag, secret.KeyVersion))
            .Should().Be("demo-password", "the sealed value has to be the one that works");
    }

    [Fact]
    public async Task The_endpoint_it_makes_carries_no_token_and_passes_anyway()
    {
        var (client, projectId) = await SignedInAsync();

        var attempt = Attempt();
        (await TryAsync(client, projectId, attempt)).Call!.Ok.Should().BeTrue();

        var saved = await SaveAsync(client, projectId, attempt);

        saved.EndpointId.Should().NotBeNull("the call that was proved should be kept");
        saved.Url.Should().Be($"/projects/{projectId}/endpoints/{saved.EndpointId}");

        using (var scope = app.Services.CreateScope())
        {
            var endpoint = await Db(scope.ServiceProvider).Baselines.IgnoreQueryFilters()
                .SingleAsync(b => b.Id == saved.EndpointId);

            var request = JsonDocument.Parse(endpoint.RequestJson!).RootElement;

            request.GetProperty("method").GetString().Should().Be("GET");

            // Through the variable rather than the address that was typed, so the same endpoint
            // can be pointed at staging tomorrow by choosing a different environment.
            request.GetProperty("url").GetString().Should().Be("{{environment.baseUrl}}/categories");

            // The absence is the whole point. A header with a token in it would work today and
            // start failing overnight for a reason that has nothing to do with the API.
            request.GetProperty("headers").EnumerateArray()
                .Select(header => header.GetProperty("name").GetString())
                .Should().NotContain("Authorization");
        }

        // And the ordinary Test path reaches the API and is let in, with nobody having typed a
        // token anywhere in this test. This is the assertion the whole feature exists for.
        var compare = await client.PostAsJsonAsync(
            $"/projects/{projectId}/endpoints/{saved.EndpointId}/compare", new { });

        compare.StatusCode.Should().Be(HttpStatusCode.OK);

        var diff = (await compare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("diff");

        diff.GetProperty("statusCode").GetInt32().Should().Be(200,
            "the environment's own sign-in should have authorised this");

        // There is nothing to compare against yet, which is the honest state of a brand-new
        // endpoint: the page shows the answer and offers to keep it as the first version. What
        // matters is that the reason is that and not a refusal.
        var failure = diff.GetProperty("failureMessage").GetString();

        failure.Should().NotContain("401");
        failure.Should().NotContain("token");
    }

    [Fact]
    public async Task Changing_the_password_reseals_the_secret_it_already_had()
    {
        var (client, projectId) = await SignedInAsync();

        var attempt = Attempt();
        (await TryAsync(client, projectId, attempt)).Call!.Ok.Should().BeTrue();

        var first = await SaveAsync(client, projectId, attempt);

        // The environment's editor is the same four steps, arriving with the reference in the
        // password box. Proving it again resolves that reference rather than sending it as a word.
        var again = attempt with
        {
            EnvironmentId = first.EnvironmentId,
            PasswordValue = "{{secrets.password}}",
        };

        var proof = await TryAsync(client, projectId, again);

        proof.SignIn.Ok.Should().BeTrue(
            "a stored reference must resolve, but: {0}", proof.SignIn.Problem);

        await SaveAsync(client, projectId, again);

        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        // One secret, not two. Sealing the reference would have produced «password2» holding the
        // string «{{secrets.password}}», and a sign-in that fails for a reason nobody could read.
        (await db.Secrets.IgnoreQueryFilters().CountAsync(s => s.ProjectId == projectId))
            .Should().Be(1);

        // And one environment: saving again edits, it does not accumulate.
        (await db.Environments.IgnoreQueryFilters().CountAsync(e => e.ProjectId == projectId))
            .Should().Be(1);

        var environment = await db.Environments.IgnoreQueryFilters()
            .SingleAsync(e => e.Id == first.EnvironmentId);

        EnvironmentAuth.Read(environment.AuthenticationJson).Credentials["password"]
            .Should().Be("{{secrets.password}}");

        // Editing keeps the endpoint the first pass made rather than adding another beside it.
        (await db.Baselines.IgnoreQueryFilters().CountAsync(b => b.ProjectId == projectId))
            .Should().Be(1);
    }

    [Fact]
    public async Task The_page_offers_the_practice_api_this_application_serves()
    {
        var (client, projectId) = await SignedInAsync();

        var html = await client.GetStringAsync($"/projects/{projectId}/connect");

        // Somebody with nothing of their own can still walk all four steps, which is the only way
        // to find out what the end looks like without first having credentials to hand.
        html.Should().Contain("data-island=\"connect-api\"");
        html.Should().Contain("/fake");
    }

    // ---- scaffolding ------------------------------------------------------------------------------

    /// <summary>The shape the fake API actually takes: a JSON login, and a path that needs it.</summary>
    private ConnectBody Attempt() => new()
    {
        BaseUrl = _apiUrl,
        AllowPrivateNetwork = true,
        Kind = "signIn",
        TokenUrl = "/auth/login",
        UserField = "username",
        UserValue = "demo",
        PasswordField = "password",
        PasswordValue = "demo-password",
        Method = "GET",
        Path = "/categories",
    };

    private static async Task<TryBody> TryAsync(HttpClient client, Guid projectId, ConnectBody attempt)
    {
        var response = await client.PostAsJsonAsync($"/projects/{projectId}/connect/try", attempt);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<TryBody>())!;
    }

    private static async Task<SavedBody> SaveAsync(HttpClient client, Guid projectId, ConnectBody attempt)
    {
        var response = await client.PostAsJsonAsync($"/projects/{projectId}/connect/save", attempt);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<SavedBody>())!;
    }

    private sealed record ConnectBody
    {
        public Guid? EnvironmentId { get; init; }
        public string? Name { get; init; }
        public string? BaseUrl { get; init; }
        public bool AllowPrivateNetwork { get; init; }
        public string Kind { get; init; } = "signIn";
        public string? TokenUrl { get; init; }
        public string? UserField { get; init; }
        public string? UserValue { get; init; }
        public string? PasswordField { get; init; }
        public string? PasswordValue { get; init; }
        public string Method { get; init; } = "GET";
        public string? Path { get; init; }
    }

    private sealed record TryBody(StepBody SignIn, StepBody? Call);

    private sealed record StepBody(
        bool Ok, bool Skipped, string? Problem, string? Detail, string? Url, int StatusCode);

    private sealed record SavedBody(Guid EnvironmentId, Guid? EndpointId, string Url);

    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    private async Task<(HttpClient Client, Guid ProjectId)> SignedInAsync()
    {
        var email = $"connect-{Guid.CreateVersion7():N}@proofflow.test";
        Guid projectId;

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

            var user = new ProofFlowUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Connector",
            };

            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

            var db = Db(scope.ServiceProvider);

            var workspace = new Workspace
            {
                Name = "Connect workspace",
                Slug = $"ws-{Guid.CreateVersion7():N}"[..20],
                CreatedByUserId = user.Id,
            };
            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                JoinedAt = DateTimeOffset.UtcNow,
            });

            // Empty on purpose: every environment, secret and endpoint these tests assert on has to
            // have been made by the flow itself.
            var project = new Project
            {
                WorkspaceId = workspace.Id,
                Name = "Connect project",
                Slug = $"p-{Guid.CreateVersion7():N}"[..20],
            };
            db.Projects.Add(project);

            await db.SaveChangesAsync();

            user.LastWorkspaceId = workspace.Id;
            await users.UpdateAsync(user);

            projectId = project.Id;
        }

        var client = app.CreateClient(NoRedirect);
        await SignInAsync(client, email);

        return (client, projectId);
    }

    private static async Task SignInAsync(HttpClient client, string email)
    {
        var response = await client.GetAsync("/account/sign-in");
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        match.Success.Should().BeTrue("the sign-in page should render an antiforgery token");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["RememberMe"] = "true",
            ["__RequestVerificationToken"] = match.Groups[1].Value,
        });

        (await client.PostAsync("/account/sign-in", form)).StatusCode
            .Should().Be(HttpStatusCode.Redirect, "sign-in should succeed");
    }
}
