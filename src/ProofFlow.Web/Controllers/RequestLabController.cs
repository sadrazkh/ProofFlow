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
