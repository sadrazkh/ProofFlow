using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Scheduling;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// How a build agent identifies itself.
///
/// A separate scheme from the cookie, and that separation is the point. The cookie scheme
/// challenges by redirecting to a sign-in page, which for a pipeline means a 200 with a login form
/// where it expected JSON — a failure that reads as success to anything checking the status code.
/// This one answers 401 and stops.
///
/// The key is read from <c>Authorization: Bearer pf_…</c> or from <c>X-ProofFlow-Key</c>. Two
/// spellings because CI systems differ in what they will put in an Authorization header, and a
/// product that only accepts one of them is a product somebody cannot integrate on a Friday.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "ProofFlowApiKey";

    public const string HeaderName = "X-ProofFlow-Key";

    /// <summary>The claim carrying the key's own id, so an audit entry can name it.</summary>
    public const string KeyClaim = "pf:apikey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Presented();

        // No key at all is not a failure — it is a request for some other scheme to handle. A
        // failure here would turn every ordinary browser request into a logged authentication
        // error.
        if (presented is null) return AuthenticateResult.NoResult();

        var key = await keys.FindAsync(presented, Context.RequestAborted);

        // Deliberately the same answer for wrong, expired and revoked. Telling a caller which of
        // those it was tells somebody holding a stolen key what to try next.
        if (key is null) return AuthenticateResult.Fail("That key is not usable.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, key.Name),
            new(KeyClaim, key.Id.ToString()),
            new(HttpCurrentUser.WorkspaceClaim, key.WorkspaceId.ToString()),

            // A key runs tests and reads results. It cannot edit a scenario, approve a baseline or
            // reveal a secret — a credential that sits in a CI variable should not be able to do
            // anything a person would want to review afterwards.
            new(HttpCurrentUser.RoleClaim, nameof(WorkspaceRole.Runner)),
        };

        if (key.ProjectId is { } projectId) claims.Add(new Claim(ProjectClaim, projectId.ToString()));

        var identity = new ClaimsIdentity(claims, Scheme);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }

    /// <summary>Set when the key is scoped to one project, so the endpoint can refuse another.</summary>
    public const string ProjectClaim = "pf:project";

    private string? Presented()
    {
        if (Request.Headers.TryGetValue(HeaderName, out var direct)
            && !string.IsNullOrWhiteSpace(direct))
        {
            return direct.ToString();
        }

        var authorization = Request.Headers.Authorization.ToString();

        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>
    /// 401 with a WWW-Authenticate header, never a redirect.
    ///
    /// A pipeline that gets a 302 to a sign-in page and follows it receives a 200 and an HTML form,
    /// and anything checking only the status code concludes the tests passed.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Bearer realm=\"ProofFlow\"";

        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Who is asking, when the asker is a key rather than a person.
///
/// It is <see cref="ICurrentUser"/> like any other caller, which is what lets the services
/// underneath stay unaware that CI exists.
/// </summary>
public sealed class ApiKeyCurrentUser(ClaimsPrincipal principal) : ICurrentUser
{
    public Guid? UserId => null;

    public string DisplayName =>
        principal.FindFirstValue(ClaimTypes.Name) is { Length: > 0 } name ? name : "api key";

    public bool IsAuthenticated => true;

    public Guid? WorkspaceId =>
        Guid.TryParse(principal.FindFirstValue(HttpCurrentUser.WorkspaceClaim), out var id) ? id : null;

    public WorkspaceRole? Role => WorkspaceRole.Runner;

    public bool Can(Capability capability) => RoleCapabilities.Allows(WorkspaceRole.Runner, capability);
}
