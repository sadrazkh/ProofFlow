using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.FakeApi;
using ProofFlow.Infrastructure.Http;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The executor against a real socket and a real server.
///
/// Everything below this line is where a mocked <c>HttpClient</c> stops being evidence: redirect
/// handling, the response-size cap, the connect-time address check and the retry counter all live
/// in the transport, and a fake transport would simply agree with whatever the code does.
/// </summary>
public sealed class HttpExecutionTests : IAsyncLifetime
{
    private WebApplication _server = null!;
    private ServiceProvider _services = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddFakeApi();

        _server = builder.Build();
        _server.MapFakeApi();
        await _server.StartAsync();

        _baseUrl = _server.Urls.First();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProofFlowHttpClients();
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        await _services.DisposeAsync();
    }

    /// <summary>
    /// The fake API listens on loopback, so every test here needs private networking allowed —
    /// which is itself worth stating: the guard's default refuses it.
    /// </summary>
    private UrlPolicy Policy(Action<UrlPolicyBuilder>? configure = null)
    {
        var builder = new UrlPolicyBuilder { AllowPrivateNetwork = true };
        configure?.Invoke(builder);
        return builder.Build();
    }

    private IHttpExecutor Executor() =>
        new GuardedHttpExecutor(
            _services.GetRequiredService<IHttpClientFactory>(),
            NullLogger<GuardedHttpExecutor>.Instance);

    [Fact]
    public async Task A_get_returns_the_body_and_the_status()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = $"{_baseUrl}/fake/stable" }, Policy());

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.StatusCode.Should().Be(200);
        result.IsJson.Should().BeTrue();
        JsonNode.Parse(result.Body)!["name"]!.GetValue<string>().Should().Be("Stable");
        result.Duration.Should().BePositive();
    }

    [Fact]
    public async Task A_post_sends_its_body_and_the_response_can_be_read_back()
    {
        var login = await Executor().SendAsync(new HttpRequestDefinition
        {
            Method = "POST",
            Url = $"{_baseUrl}/fake/auth/login",
            Body = new RequestBody
            {
                Kind = BodyKind.Json,
                Content = """{"username":"demo","password":"demo-password"}""",
            },
        }, Policy());

        login.StatusCode.Should().Be(200);

        var token = JsonNode.Parse(login.Body)!["accessToken"]!.GetValue<string>();
        token.Should().StartWith("tok_");

        // And the token actually works — which is what makes a login step provable rather than
        // merely successful.
        var me = await Executor().SendAsync(new HttpRequestDefinition
        {
            Url = $"{_baseUrl}/fake/auth/me",
            Headers = [new KeyValueEntry("Authorization", $"Bearer {token}")],
        }, Policy());

        me.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task A_request_without_a_token_is_refused_by_the_fake_api()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = $"{_baseUrl}/fake/auth/me" }, Policy());

        // An API that accepts anything cannot demonstrate that an authentication step did something.
        result.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Redirects_are_followed_and_recorded()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = $"{_baseUrl}/fake/redirect/3" }, Policy());

        result.StatusCode.Should().Be(200);
        // Recorded, not just followed: a report that shows only the final address hides the hop
        // that mattered.
        result.RedirectChain.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_redirect_loop_stops_at_the_limit()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = $"{_baseUrl}/fake/redirect/50" },
            Policy(p => p.MaxRedirects = 3));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Kind.Should().Be(HttpFailureKind.TooManyRedirects);
    }

    [Fact]
    public async Task A_redirect_to_a_forbidden_address_is_refused()
    {
        // The reason redirects are followed by hand. HttpClient's own redirect would run no policy
        // at all, so a single 302 walks past every check made on the original URL — and the
        // interesting destination is the metadata endpoint, which is refused even here where
        // private networking is allowed.
        var result = await Executor().SendAsync(
            new HttpRequestDefinition
            {
                Url = $"{_baseUrl}/fake/redirect/1?to=http://169.254.169.254/latest/meta-data/",
            },
            Policy());

        result.Succeeded.Should().BeFalse();
        result.Failure!.Kind.Should().Be(HttpFailureKind.BlockedByPolicy);
    }

    [Fact]
    public async Task A_response_larger_than_the_cap_is_refused_rather_than_buffered()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = $"{_baseUrl}/fake/large?kilobytes=2048" },
            Policy(p => p.MaxResponseBytes = 64 * 1024));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Kind.Should().Be(HttpFailureKind.ResponseTooLarge);
        // The message has to name the setting, because the fix is a number in the environment.
        result.Failure.Message.Should().Contain("environment settings");
    }

    [Fact]
    public async Task A_timeout_is_reported_as_a_timeout()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = $"{_baseUrl}/fake/slow?ms=3000" },
            Policy(p => p.Timeout = TimeSpan.FromMilliseconds(400)));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Kind.Should().Be(HttpFailureKind.Timeout);
    }

    [Fact]
    public async Task Retries_run_and_the_attempt_count_is_kept()
    {
        var key = Guid.CreateVersion7().ToString("N");

        var result = await Executor().SendAsync(new HttpRequestDefinition
        {
            Url = $"{_baseUrl}/fake/flaky/{key}?failFor=2",
            Retry = new RetryPolicy { MaxAttempts = 4, DelayMilliseconds = 10, RetryOnStatus = [503] },
        }, Policy());

        result.StatusCode.Should().Be(200);
        // The count is the whole point. A retry that succeeds after two failures is not the same
        // event as one that succeeded immediately, and a green tick that hides it turns a flaky
        // endpoint into a healthy one.
        result.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task A_request_to_the_metadata_endpoint_never_leaves()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = "http://169.254.169.254/latest/meta-data/iam/security-credentials/" },
            Policy());

        result.Succeeded.Should().BeFalse();
        result.Failure!.Kind.Should().Be(HttpFailureKind.BlockedByPolicy);
        result.Failure.Message.Should().Contain("credentials");
    }

    [Fact]
    public async Task Loopback_is_refused_when_the_environment_has_not_allowed_it()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = $"{_baseUrl}/fake/stable" },
            new UrlPolicy());

        result.Succeeded.Should().BeFalse();
        result.Failure!.Kind.Should().Be(HttpFailureKind.BlockedByPolicy);
    }

    [Fact]
    public async Task A_host_outside_the_allowed_list_is_refused()
    {
        var result = await Executor().SendAsync(
            new HttpRequestDefinition { Url = $"{_baseUrl}/fake/stable" },
            Policy(p => p.AllowedHosts = ["api.example.com"]));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("allowed list");
    }

    [Fact]
    public async Task Sent_headers_are_redacted_in_the_result()
    {
        var result = await Executor().SendAsync(new HttpRequestDefinition
        {
            Url = $"{_baseUrl}/fake/stable",
            Headers = [new KeyValueEntry("Authorization", "Bearer super-secret-token-value")],
        }, Policy());

        // The result is what gets stored, exported and attached to tickets.
        result.SentHeaders.Single(h => h.Name == "Authorization").Value.Should().Be(Redactor.Mask);
    }

    [Fact]
    public async Task A_full_login_then_read_chain_works_with_variables()
    {
        // The smallest version of the acceptance scenario: one step's output feeding the next,
        // through the variable resolver, with the secret registered for redaction on the way.
        var scopes = new VariableScopes
        {
            Environment = new JsonObject { ["baseUrl"] = _baseUrl },
            Secrets = new JsonObject { ["password"] = "demo-password" },
        };
        var redaction = new RedactionScope();
        var resolver = new VariableResolver(scopes, redaction);

        var login = await Executor().SendAsync(new HttpRequestDefinition
        {
            Method = "POST",
            Url = resolver.Resolve("{{environment.baseUrl}}/fake/auth/login"),
            Body = new RequestBody
            {
                Kind = BodyKind.Json,
                Content = resolver.Resolve("""{"username":"demo","password":"{{secrets.password}}"}"""),
            },
        }, Policy());

        scopes.PublishStep("login", new JsonObject { ["response"] = JsonNode.Parse(login.Body) });

        var categories = await Executor().SendAsync(new HttpRequestDefinition
        {
            Url = resolver.Resolve("{{environment.baseUrl}}/fake/categories"),
            Headers =
            [
                new KeyValueEntry("Authorization",
                    resolver.Resolve("Bearer {{steps.login.response.accessToken}}")),
            ],
        }, Policy());

        categories.StatusCode.Should().Be(200);
        JsonNode.Parse(categories.Body)!["items"]!.AsArray().Should().HaveCount(3);

        redaction.Apply("the password was demo-password").Should().NotContain("demo-password");
    }

    /// <summary>Mutable shim, because UrlPolicy is a record with init-only properties.</summary>
    private sealed class UrlPolicyBuilder
    {
        public IReadOnlyList<string> AllowedHosts { get; set; } = [];
        public bool AllowPrivateNetwork { get; set; }
        public int MaxRedirects { get; set; } = 5;
        public long MaxResponseBytes { get; set; } = 4L * 1024 * 1024;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        public UrlPolicy Build() => new()
        {
            AllowedHosts = AllowedHosts,
            AllowPrivateNetwork = AllowPrivateNetwork,
            MaxRedirects = MaxRedirects,
            MaxResponseBytes = MaxResponseBytes,
            Timeout = Timeout,
        };
    }
}
