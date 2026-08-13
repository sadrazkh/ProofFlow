using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Requests;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Environments;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Variables;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The request laboratory: build one request, send it, look at what came back.
///
/// It is the smallest complete version of what this product does, and deliberately the first thing
/// built on top of the engine. Everything later — the capture wizard, the HTTP node on the canvas —
/// is this same request definition with something else driving it.
///
/// Nothing is persisted. A scratch request belongs to the person typing it, not to the project, and
/// saving every experiment would fill the project with things nobody meant to keep. The browser
/// remembers the last one per project so a refresh does not lose work.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/request")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class RequestLabController(
    ProofFlowDbContext db,
    EnvironmentContextBuilder environments,
    IHttpExecutor executor,
    IAuditLog audit,
    IClock clock,
    ICurrentUser me,
    IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var list = await db.Environments
            .Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .Select(e => new RequestLabEnvironment(e.Id, e.Name, e.BaseUrl, e.IsProduction))
            .ToListAsync(cancellationToken);

        ViewData["Title"] = localizer["request.title"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (project.Name, $"/projects/{project.Id}"),
            (localizer["request.title"].Value, null),
        };

        return View(new RequestLabViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Environments = list,
            CanRun = me.Can(Capability.RunTest),
            // Checked here as well as on the endpoint. The button is hidden for somebody who
            // cannot record one, because a control that always fails is worse than no control.
            CanRecordBaseline = me.Can(Capability.RecordBaseline),
        });
    }

    /// <summary>
    /// The names a reference may use, so the builder can mark one red before it is sent.
    ///
    /// Names, never values. The browser needs to know that <c>apiToken</c> exists; it does not need
    /// to know what it is, and this endpoint is reachable by anyone who can view the project.
    /// </summary>
    [HttpGet("variables")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Variables(
        Guid projectId, Guid? environmentId, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var secrets = await db.Secrets
            .Where(s => s.ProjectId == projectId
                        && (s.EnvironmentId == null || s.EnvironmentId == environmentId))
            .Select(s => s.Name)
            .ToListAsync(cancellationToken);

        var variables = await db.Variables
            .Where(v => v.ProjectId == projectId
                        && (v.EnvironmentId == null || v.EnvironmentId == environmentId))
            .Select(v => v.Name)
            .ToListAsync(cancellationToken);

        if (environmentId is { } id)
        {
            var environment = await db.Environments
                .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId, cancellationToken);

            if (environment is not null)
                names.AddRange(["name", "slug", "baseUrl", "kind", "isProduction"]);
        }

        return Json(new VariableNamesDto(names, [.. variables.Distinct()], [.. secrets.Distinct()]));
    }

    [HttpPost("send")]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Send(
        Guid projectId, [FromBody] SendRequestCommand command, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        ProjectEnvironment? environment = null;
        if (command.EnvironmentId is { } environmentId)
        {
            environment = await db.Environments
                .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, cancellationToken);

            if (environment is null) return NotFound();
        }

        // No environment means no base URL, no variables and no secrets — and, importantly, the
        // default policy, which refuses private addresses. Sending without one is allowed because
        // a bare absolute URL is a legitimate first thing to try.
        var context = environment is null ? null : await environments.BuildAsync(environment, cancellationToken);
        var resolver = context?.Resolver() ?? new VariableResolver(new VariableScopes());
        var policy = context?.Policy ?? new UrlPolicy();

        var unresolved = new List<UnresolvedDto>();

        var url = Resolve(resolver, Combine(environment?.BaseUrl, command.Url), unresolved);
        var query = ResolveAll(resolver, command.Query, unresolved);
        var headers = ResolveAll(resolver, command.Headers, unresolved);
        var body = command.Body is null ? null : Resolve(resolver, command.Body, unresolved);

        if (unresolved.Count > 0)
        {
            // Refused before the socket opens. Sending "Bearer " with the token missing produces a
            // 401 that looks like the API's fault and costs somebody an afternoon.
            return Json(new SendRequestResult
            {
                Succeeded = false,
                Method = command.Method,
                ResolvedUrl = url,
                FailureKind = "Unresolved",
                FailureMessage = localizer["request.unresolved"].Value,
                Unresolved = unresolved,
            });
        }

        var definition = new HttpRequestDefinition
        {
            Method = command.Method,
            Url = url,
            Query = [.. query.Select(q => new KeyValueEntry(q.Name, q.Value, q.Enabled))],
            Headers = [.. headers.Select(h => new KeyValueEntry(h.Name, h.Value, h.Enabled))],
            Body = ToBody(command.BodyKind, body),
        };

        var result = await executor.SendAsync(definition, policy, cancellationToken);

        await MarkSecretsUsedAsync(projectId, environment?.Id, context, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "request.sent", projectId, "Request", null, $"{command.Method} {Trim(url)}",
            new Dictionary<string, string?>
            {
                ["environment"] = environment?.Name,
                ["status"] = result.Succeeded ? result.StatusCode.ToString() : result.Failure!.Kind.ToString(),
            }), cancellationToken);

        return Json(ToDto(result, context));
    }

    /// <summary>
    /// Asks an authorisation server for a token.
    ///
    /// Through the same executor and the same URL policy as everything else here, which is the
    /// point: a token endpoint is an address somebody types, and an address somebody types is the
    /// thing the guard exists for. An auth flow that quietly bypassed it would be the one hole in
    /// the wall, at the one place a credential is involved.
    ///
    /// What comes back reaches the browser in the clear, and that is deliberate rather than an
    /// oversight — a token nobody can see is a token nobody can put in a header. Keeping it is the
    /// next thing offered, and keeping it means sealing it as a secret like any other.
    /// </summary>
    [HttpPost("token")]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Token(
        Guid projectId, [FromBody] TokenRequestCommand command, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        ProjectEnvironment? environment = null;
        if (command.EnvironmentId is { } environmentId)
        {
            environment = await db.Environments
                .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, cancellationToken);

            if (environment is null) return NotFound();
        }

        var context = environment is null ? null : await environments.BuildAsync(environment, cancellationToken);
        var resolver = context?.Resolver() ?? new VariableResolver(new VariableScopes());
        var policy = context?.Policy ?? new UrlPolicy();

        // The whole form resolves, so a client secret can be «{{secrets.clientSecret}}» rather than
        // a value typed into a box on a screen somebody is sharing.
        var unresolved = new List<UnresolvedDto>();

        var url = Resolve(resolver, Combine(environment?.BaseUrl, command.TokenUrl), unresolved);
        var clientId = Resolve(resolver, command.ClientId ?? string.Empty, unresolved);
        var clientSecret = Resolve(resolver, command.ClientSecret ?? string.Empty, unresolved);
        var scope = Resolve(resolver, command.Scope ?? string.Empty, unresolved);
        var username = Resolve(resolver, command.Username ?? string.Empty, unresolved);
        var password = Resolve(resolver, command.Password ?? string.Empty, unresolved);

        if (string.IsNullOrWhiteSpace(url))
        {
            return Json(new TokenResult { Succeeded = false, Problem = localizer["auth.noTokenUrl"].Value });
        }

        if (unresolved.Count > 0)
        {
            return Json(new TokenResult
            {
                Succeeded = false,
                Problem = localizer["request.unresolved"].Value,
                Detail = string.Join(", ", unresolved.Select(u => u.Reference)),
            });
        }

        var form = new List<KeyValueEntry>
        {
            new("grant_type", command.Grant == "password" ? "password" : "client_credentials"),
        };

        if (!string.IsNullOrWhiteSpace(scope)) form.Add(new KeyValueEntry("scope", scope));

        if (command.Grant == "password")
        {
            form.Add(new KeyValueEntry("username", username));
            form.Add(new KeyValueEntry("password", password));
        }

        var headers = new List<KeyValueEntry>();

        // Both are in the specification and servers disagree about which they accept. The switch is
        // there because guessing wrong produces a 401 that says nothing about which half was wrong.
        if (command.CredentialsInHeader && !string.IsNullOrWhiteSpace(clientId))
        {
            var pair = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

            headers.Add(new KeyValueEntry("Authorization", $"Basic {pair}"));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(clientId)) form.Add(new KeyValueEntry("client_id", clientId));
            if (!string.IsNullOrWhiteSpace(clientSecret))
                form.Add(new KeyValueEntry("client_secret", clientSecret));
        }

        var result = await executor.SendAsync(
            new HttpRequestDefinition
            {
                Method = "POST",
                Url = url,
                Headers = headers,
                Body = new RequestBody { Kind = BodyKind.FormUrlEncoded, Form = form },
            },
            policy,
            cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "request.token", projectId, "Request", null, Trim(url),
            new Dictionary<string, string?>
            {
                ["environment"] = environment?.Name,
                ["grant"] = command.Grant,
                ["status"] = result.Succeeded ? result.StatusCode.ToString() : "failed",
            }), cancellationToken);

        if (!result.Succeeded)
        {
            return Json(new TokenResult { Succeeded = false, Problem = result.Failure!.Message });
        }

        if (result.StatusCode is < 200 or > 299)
        {
            return Json(new TokenResult
            {
                Succeeded = false,
                StatusCode = result.StatusCode,
                Problem = localizer["auth.refused", result.StatusCode].Value,

                // The server own words. A token endpoint that says «invalid_scope» has already
                // answered the question, and hiding it to keep the message tidy answers nothing.
                Detail = Shorten(result.Body, 400),
            });
        }

        return Read(result.Body, result.StatusCode);
    }

    /// <summary>
    /// Reads a token out of whatever the server sent back.
    ///
    /// The field names are the specification, and the fallbacks are what real servers send: some
    /// answer with «token», some with «id_token», and one popular framework nests the lot under
    /// «data». Guessing here is worth doing because the alternative is telling somebody their
    /// working token endpoint is not one.
    /// </summary>
    private IActionResult Read(string body, int statusCode)
    {
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(body);
            var node = root?["data"] is System.Text.Json.Nodes.JsonObject nested ? nested : root;

            var token = Field(node, "access_token") ?? Field(node, "accessToken")
                ?? Field(node, "token") ?? Field(node, "id_token");

            if (string.IsNullOrWhiteSpace(token))
            {
                return Json(new TokenResult
                {
                    Succeeded = false,
                    StatusCode = statusCode,
                    Problem = localizer["auth.noToken"].Value,
                    Detail = Shorten(body, 400),
                });
            }

            var expires = node?["expires_in"] ?? node?["expiresIn"];

            return Json(new TokenResult
            {
                Succeeded = true,
                StatusCode = statusCode,
                AccessToken = token,
                TokenType = Field(node, "token_type") ?? Field(node, "tokenType") ?? "Bearer",
                ExpiresIn = expires is not null && int.TryParse(expires.ToString(), out var seconds)
                    ? seconds
                    : null,
            });
        }
        catch (System.Text.Json.JsonException)
        {
            return Json(new TokenResult
            {
                Succeeded = false,
                StatusCode = statusCode,
                Problem = localizer["auth.notJson"].Value,
                Detail = Shorten(body, 400),
            });
        }
    }

    private static string? Field(System.Text.Json.Nodes.JsonNode? node, string name) =>
        node?[name]?.ToString() is { Length: > 0 } value ? value : null;

    private static string Shorten(string? text, int limit) =>
        text is null ? string.Empty : text.Length <= limit ? text : text[..limit] + "…";

    /// <summary>
    /// Joins a relative path onto the environment's base URL.
    ///
    /// Left alone when the path is already absolute, because someone pasting a full URL into the
    /// box means that URL — not the base with a URL glued onto the end of it.
    /// </summary>
    private static string Combine(string? baseUrl, string path)
    {
        path = path.Trim();

        if (path.Length == 0) return baseUrl ?? string.Empty;
        if (Uri.IsWellFormedUriString(path, UriKind.Absolute)) return path;
        if (path.StartsWith("{{", StringComparison.Ordinal)) return path;
        if (string.IsNullOrWhiteSpace(baseUrl)) return path;

        return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private static string Resolve(VariableResolver resolver, string text, List<UnresolvedDto> into)
    {
        var result = resolver.TryResolve(text);
        foreach (var item in result.Unresolved)
            into.Add(new UnresolvedDto(item.Reference, item.Explanation));

        return result.Text;
    }

    private static List<KeyValueDto> ResolveAll(
        VariableResolver resolver, IReadOnlyList<KeyValueDto> entries, List<UnresolvedDto> into) =>
        [.. entries
            .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => e with { Value = Resolve(resolver, e.Value, into) })];

    private static RequestBody? ToBody(string? kind, string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;

        return new RequestBody
        {
            Kind = Enum.TryParse<BodyKind>(kind, ignoreCase: true, out var parsed) ? parsed : BodyKind.Json,
            Content = content,
        };
    }

    /// <summary>
    /// Records that this environment's secrets were used, if any were.
    ///
    /// "Last used" is what tells somebody a secret is safe to rotate, or that one nobody has
    /// touched in a year is still wired into something.
    /// </summary>
    private async Task MarkSecretsUsedAsync(
        Guid projectId, Guid? environmentId, EnvironmentContext? context, CancellationToken cancellationToken)
    {
        if (context is null || context.Redaction.Values.Count == 0) return;

        var secrets = await db.Secrets
            .Where(s => s.ProjectId == projectId && (s.EnvironmentId == null || s.EnvironmentId == environmentId))
            .ToListAsync(cancellationToken);

        foreach (var secret in secrets) secret.LastUsedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static SendRequestResult ToDto(HttpExchangeResult result, EnvironmentContext? context)
    {
        // Everything the browser is about to render goes through the run's redaction scope first.
        // The response body is the dangerous one: an API that echoes an Authorization header into
        // an error message would otherwise put a live token on screen and into any screenshot of it.
        string Clean(string? value) => context?.Redaction.Apply(value) ?? value ?? string.Empty;

        return new SendRequestResult
        {
            Succeeded = result.Succeeded,
            Method = result.Method,
            ResolvedUrl = Clean(result.ResolvedUrl),
            StatusCode = result.StatusCode,
            ReasonPhrase = result.ReasonPhrase,
            ResponseHeaders = [.. result.ResponseHeaders.Select(h =>
                new KeyValueDto(h.Name, ProofFlow.TestEngine.Redaction.Redactor.IsSensitiveHeader(h.Name)
                    ? ProofFlow.TestEngine.Redaction.Redactor.Mask
                    : Clean(h.Value)))],
            SentHeaders = [.. result.SentHeaders.Select(h => new KeyValueDto(h.Name, h.Value))],
            Body = Clean(result.Body),
            ContentType = result.ContentType,
            BodyBytes = result.BodyBytes,
            DurationMs = result.Duration.TotalMilliseconds,
            Attempts = result.Attempts,
            RedirectChain = [.. result.RedirectChain.Select(Clean)],
            FailureKind = result.Failure?.Kind.ToString(),
            FailureMessage = result.Failure?.Message,
            FailureDetail = Clean(result.Failure?.Detail),
        };
    }

    private static string Trim(string value) => value.Length <= 200 ? value : value[..200] + "…";
}
