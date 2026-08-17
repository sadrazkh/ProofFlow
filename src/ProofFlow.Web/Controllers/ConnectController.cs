using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Contracts.Requests;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Variables;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Connecting an API, one question at a time.
///
/// Everything this page does could already be done: make a project, make an environment, make two
/// secrets, open the request lab, find the authorisation panel, fetch a token, send a request, keep
/// the answer. Seven screens, in an order nobody states, using words — environment, secret,
/// baseline — that mean nothing until you already know the product. For an API that needs a token
/// the sixth screen was the one that did not work, so most people never reached the seventh.
///
/// So this asks four questions and does the rest. It is not a shortcut around the model: what it
/// writes at the end is exactly an environment, its secrets, an authentication configuration and an
/// endpoint, all of which are then editable in the ordinary places. What it removes is having to
/// know that those were the four things you needed.
///
/// Nothing is written until the third step has gone green. A configuration that has never
/// successfully signed in is a configuration somebody will find out about later, from a scheduled
/// run, at night.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/connect")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class ConnectController(
    ProofFlowDbContext db,
    IHttpExecutor executor,
    EnvironmentContextBuilder contexts,
    ISecretCipher cipher,
    ICurrentUser me,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    /// <summary>
    /// The same flow, for somebody who has not got as far as a project.
    ///
    /// «Connect an API» is a sentence anyone can act on; «make a project, then an environment
    /// inside it» is two nouns to learn first. So this makes the project — named after the
    /// workspace, because that is a name they chose — and carries on.
    /// </summary>
    [HttpGet("/connect")]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> Anywhere(CancellationToken cancellationToken)
    {
        var existing = await db.Projects
            .Where(project => project.ArchivedAt == null)
            .OrderBy(project => project.CreatedAt)
            .Select(project => (Guid?)project.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } found) return Redirect($"/projects/{found}/connect");

        if (me.WorkspaceId is not { } workspaceId || !me.Can(Capability.ManageProject))
        {
            // Nothing to connect to and no permission to make somewhere to put it. The project list
            // says so in the words that page already uses, rather than this page inventing them.
            return Redirect("/projects");
        }

        var name = HttpContext.Items["WorkspaceName"] as string is { Length: > 0 } workspace
            ? workspace
            : "APIs";

        var project = new Project
        {
            WorkspaceId = workspaceId,
            Name = name,
            Slug = Slug.From(name, "project"),

            // Not localised, deliberately: a description is a column, and taking it from the
            // catalogue would freeze whichever language its creator was reading into a row
            // everybody else then sees.
            Description = "The APIs this workspace tests.",
            Accent = "violet",
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEntry("project.created", project.Id, nameof(Project), project.Id, project.Name),
            cancellationToken);

        return Redirect($"/projects/{project.Id}/connect");
    }

    [HttpGet("")]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> Index(
        Guid projectId, Guid? environment, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        var editing = environment is { } wanted
            ? await db.Environments
                .FirstOrDefaultAsync(e => e.Id == wanted && e.ProjectId == projectId, cancellationToken)
            : null;

        ViewData["Title"] = localizer["connect.title"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (project.Name, $"/projects/{projectId}"),
            (localizer["connect.title"].Value, null),
        };

        return View(new ConnectViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,

            // Offered as the first thing to try, because this application serves it and it needs a
            // token — so somebody with nothing of their own can still walk all four steps and see
            // what the end looks like.
            SampleBaseUrl = $"{Request.Scheme}://{Request.Host}/fake",

            Existing = editing is null ? null : Prefill(editing),
        });
    }

    /// <summary>
    /// Signs in and makes one call, and writes nothing.
    ///
    /// The whole of step three. It answers with what actually happened at each half — the token it
    /// found and where, or the server's own refusal — because «it didn't work» is the message this
    /// product exists not to produce.
    /// </summary>
    [HttpPost("try")]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> Try(
        Guid projectId, [FromBody] ConnectAttempt attempt, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken)) return NotFound();

        var baseUrl = (attempt.BaseUrl ?? string.Empty).Trim().TrimEnd('/');

        if (baseUrl.Length == 0)
        {
            return Json(ConnectResult.Refused(localizer["connect.noBaseUrl"].Value));
        }

        var (resolver, policy) = await ScopeAsync(projectId, attempt, cancellationToken);

        var auth = attempt.ToAuth();
        var signIn = new ConnectStepResult { Skipped = !auth.NeedsToken };
        IReadOnlyList<KeyValueEntry> headers = [];

        if (auth.SendsAnything)
        {
            // A cache of its own, thrown away with the request. Trying is the one moment when the
            // answer must come from the API rather than from something an earlier attempt left
            // behind — a stale token would report success for a password that no longer works.
            var outcome = await new EnvironmentAuthenticator(executor, new TokenCache())
                .HeadersAsync(auth, baseUrl, resolver, policy, $"try:{Guid.CreateVersion7()}",
                    cancellationToken);

            if (!outcome.Ok)
            {
                signIn.Ok = false;
                signIn.Problem = outcome.Problem;

                return Json(new ConnectResult { SignIn = signIn });
            }

            headers = outcome.Headers;
            signIn.Ok = true;

            // Shown, and deliberately: a token nobody can see is a token nobody can check the shape
            // of, and the commonest sign-in problem is a server that answered 200 with something
            // that is not a token at all. Only the first characters — enough to recognise, not
            // enough to reuse from a screenshot.
            signIn.Detail = auth.NeedsToken ? Preview(headers) : null;
        }

        var path = (attempt.Path ?? string.Empty).Trim();

        var request = new HttpRequestDefinition
        {
            Method = string.IsNullOrWhiteSpace(attempt.Method) ? "GET" : attempt.Method.ToUpperInvariant(),
            Url = EnvironmentAuthenticator.Combine(baseUrl, path),
        };

        var response = await executor.SendAsync(
            InheritedHeaders.Apply(request, headers, null), policy, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "request.sent", projectId, "Request", null, $"{request.Method} {request.Url}",
            new Dictionary<string, string?>
            {
                ["from"] = "connect",
                ["status"] = response.Succeeded ? response.StatusCode.ToString() : "failed",
            }), cancellationToken);

        return Json(new ConnectResult
        {
            SignIn = signIn,
            Call = new ConnectStepResult
            {
                // A 401 is not a success here even though the request completed. It is the exact
                // thing this page exists to get past, and calling it «done» would be the cruellest
                // possible moment to be imprecise.
                Ok = response.Succeeded && response.StatusCode is >= 200 and < 400,
                Problem = response.Succeeded ? null : response.Failure!.Message,
                StatusCode = response.Succeeded ? response.StatusCode : 0,
                Detail = response.Succeeded ? Shorten(response.Body, 600) : null,
                Url = response.ResolvedUrl,
            },
        });
    }

    /// <summary>
    /// Writes the four things: an environment, its secrets, the authentication, and the endpoint.
    ///
    /// Reached only after <see cref="Try"/> answered green, which the browser enforces and this
    /// does not re-check — re-running the call here would be a second, different attempt, and a
    /// «prove it» step whose proof is not the thing that gets saved proves nothing.
    /// </summary>
    [HttpPost("save")]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> Save(
        Guid projectId, [FromBody] ConnectAttempt attempt, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var name = string.IsNullOrWhiteSpace(attempt.Name) ? "Connected API" : attempt.Name.Trim();
        var baseUrl = (attempt.BaseUrl ?? string.Empty).Trim().TrimEnd('/');

        var editing = attempt.EnvironmentId is { } wanted
            ? await db.Environments
                .FirstOrDefaultAsync(e => e.Id == wanted && e.ProjectId == projectId, cancellationToken)
            : null;

        var environment = editing ?? await NewEnvironmentAsync(projectId, name, cancellationToken);

        environment.Name = name;
        environment.BaseUrl = baseUrl;
        environment.AllowPrivateNetwork = attempt.AllowPrivateNetwork;

        // Sealed, and the configuration keeps a reference rather than the value. Somebody who can
        // read the environment then learns that a password is involved and not what it is — and a
        // scheduled run at three in the morning still has one to use.
        environment.AuthenticationJson = (await SealAsync(attempt, project, environment, cancellationToken))
            .Write();

        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            editing is null ? "environment.created" : "environment.updated",
            projectId, nameof(ProjectEnvironment), environment.Id, environment.Name,
            new Dictionary<string, string?>
            {
                ["auth"] = attempt.ToAuth().Mode.ToString(),
                ["from"] = "connect",
            }), cancellationToken);

        // Only when the environment is new. Editing an existing one is usually a password that has
        // changed, and making a second copy of the same endpoint every time somebody fixes their
        // credentials would fill the list with duplicates.
        var endpointId = editing is null
            ? await EndpointAsync(attempt, project, environment, cancellationToken)
            : null;

        return Json(new
        {
            environmentId = environment.Id,
            endpointId,
            url = endpointId is { } id
                ? $"/projects/{projectId}/endpoints/{id}"
                : $"/projects/{projectId}/environments?selected={environment.Id}",
        });
    }

    // ---- assembly -------------------------------------------------------------------------------

    /// <summary>
    /// What the attempt resolves against, and what it is allowed to reach.
    ///
    /// A first connection has nothing behind it: the values were just typed and there is no
    /// environment yet, so the resolver is empty and the policy comes from the one checkbox on
    /// screen. Editing an existing one is the opposite — its credentials are stored as
    /// <c>{{secrets.…}}</c> references, and without its scopes the attempt would send the reference
    /// itself as the password and report a perfectly real 401.
    /// </summary>
    private async Task<(VariableResolver Resolver, UrlPolicy Policy)> ScopeAsync(
        Guid projectId, ConnectAttempt attempt, CancellationToken cancellationToken)
    {
        var environment = attempt.EnvironmentId is { } wanted
            ? await db.Environments
                .FirstOrDefaultAsync(e => e.Id == wanted && e.ProjectId == projectId, cancellationToken)
            : null;

        if (environment is null)
        {
            return (new VariableResolver(new VariableScopes(), new RedactionScope()),
                new UrlPolicy { AllowPrivateNetwork = attempt.AllowPrivateNetwork });
        }

        var context = await contexts.BuildAsync(environment, cancellationToken);

        // The environment's own limits and host list, but the checkbox as it is on screen: it is
        // being edited, and the value in the database is the one being changed.
        return (context.Resolver(),
            context.Policy with { AllowPrivateNetwork = attempt.AllowPrivateNetwork });
    }

    private async Task<ProjectEnvironment> NewEnvironmentAsync(
        Guid projectId, string name, CancellationToken cancellationToken)
    {
        var taken = await db.Environments
            .Where(e => e.ProjectId == projectId)
            .Select(e => e.Slug)
            .ToListAsync(cancellationToken);

        var environment = new ProjectEnvironment
        {
            WorkspaceId = me.WorkspaceId ?? Guid.Empty,
            ProjectId = projectId,
            Name = name,
            Slug = Slug.Unique(Slug.From(name, "environment"), taken),
            Kind = EnvironmentKind.Custom,
            SortOrder = taken.Count,
        };

        db.Environments.Add(environment);
        return environment;
    }

    /// <summary>
    /// Turns the typed credentials into secrets, and the configuration into one that names them.
    ///
    /// Only values that look like credentials become secrets — a username is not one, and sealing
    /// it would mean nobody can see who the tests sign in as without an audited reveal. What gets
    /// sealed is the password, the client secret, and a fixed header's whole value.
    /// </summary>
    private async Task<EnvironmentAuth> SealAsync(
        ConnectAttempt attempt, Project project, ProjectEnvironment environment,
        CancellationToken cancellationToken)
    {
        var auth = attempt.ToAuth();

        if (auth.Mode == AuthMode.None) return auth;

        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in auth.Credentials)
        {
            // KeepAsync hands back what it was given when there is nothing to seal, so the fallback
            // is only there to satisfy nullability — a credential value is never null here.
            credentials[pair.Key] = LooksSecret(pair.Key)
                ? (await KeepAsync(pair.Key, pair.Value) ?? pair.Value)
                : pair.Value;
        }

        return auth with
        {
            Credentials = credentials,
            ClientSecret = await KeepAsync("clientSecret", auth.ClientSecret),
            HeaderValue = auth.Mode == AuthMode.Header
                ? await KeepAsync("apiCredential", auth.HeaderValue)
                : auth.HeaderValue,
        };

        // Which fields are worth hiding. Named by what they are rather than by a list of exact
        // strings, because the field names belong to the server: «pass», «pwd» and «secret» are all
        // in production somewhere.
        static bool LooksSecret(string field) =>
            field.Contains("pass", StringComparison.OrdinalIgnoreCase)
            || field.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || field.Contains("token", StringComparison.OrdinalIgnoreCase)
            || field.Contains("key", StringComparison.OrdinalIgnoreCase);

        async Task<string?> KeepAsync(string wanted, string? value)
        {
            // Already a reference — this is an edit that did not touch the password. Sealing the
            // string «{{secrets.apiPassword}}» would produce a secret whose value is the name of
            // another secret, and a sign-in that fails for a reason nobody could read.
            if (string.IsNullOrWhiteSpace(value) || Reference(value)) return value;

            var boxed = cipher.Seal(value);

            var existing = await db.Secrets.FirstOrDefaultAsync(
                s => s.ProjectId == project.Id && s.EnvironmentId == environment.Id && s.Name == wanted,
                cancellationToken);

            if (existing is not null)
            {
                // Same name, new value: a password that changed. Making «apiPassword2» beside it
                // would leave the old one in the list, still decryptable, referenced by nothing.
                existing.Ciphertext = boxed.Ciphertext;
                existing.Nonce = boxed.Nonce;
                existing.Tag = boxed.Tag;
                existing.KeyVersion = boxed.KeyVersion;
                existing.Preview = Preview(value);

                return $"{{{{secrets.{wanted}}}}}";
            }

            var stored = wanted;
            var index = 2;

            while (await db.Secrets.AnyAsync(
                       s => s.ProjectId == project.Id && s.Name == stored, cancellationToken))
            {
                stored = $"{wanted}{index++}";
            }

            db.Secrets.Add(new Secret
            {
                WorkspaceId = environment.WorkspaceId,
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
                Name = stored,
                Description = localizer["connect.secretMade"].Value,
                Ciphertext = boxed.Ciphertext,
                Nonce = boxed.Nonce,
                Tag = boxed.Tag,
                KeyVersion = boxed.KeyVersion,
                Preview = Preview(value),
                CreatedByUserId = me.UserId ?? Guid.Empty,
            });

            return $"{{{{secrets.{stored}}}}}";
        }
    }

    /// <summary>The call that was proved, kept as an endpoint — so the flow ends on a Test button.</summary>
    private async Task<Guid?> EndpointAsync(
        ConnectAttempt attempt, Project project, ProjectEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!me.Can(Capability.RecordBaseline)) return null;

        var path = (attempt.Path ?? string.Empty).Trim();
        if (path.Length == 0) return null;

        var method = string.IsNullOrWhiteSpace(attempt.Method) ? "GET" : attempt.Method.ToUpperInvariant();
        var wanted = $"{method} {path}";
        var name = wanted;
        var index = 2;

        while (await db.Baselines.AnyAsync(
                   b => b.ProjectId == project.Id && b.Name == name, cancellationToken))
        {
            name = $"{wanted} ({index++})";
        }

        var endpoint = new Baseline
        {
            WorkspaceId = environment.WorkspaceId,
            ProjectId = project.Id,
            EnvironmentId = environment.Id,
            Name = name,
            Description = localizer["connect.endpointMade"].Value,

            // Through the variable, not the address that was typed — so this endpoint can be run
            // against staging tomorrow by choosing a different environment. And with no
            // Authorization header at all: that absence is the whole point, because the
            // environment carries the token now and this keeps working when the one just fetched
            // has expired.
            RequestJson = JsonSerializer.Serialize(
                new HttpRequestDefinition
                {
                    Method = method,
                    Url = "{{environment.baseUrl}}" + (path.StartsWith('/') ? path : $"/{path}"),
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),

            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Baselines.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.created", project.Id, nameof(Baseline), endpoint.Id, endpoint.Name,
            new Dictionary<string, string?> { ["from"] = "connect" }), cancellationToken);

        return endpoint.Id;
    }

    /// <summary>
    /// An existing environment, as the four steps would have collected it.
    ///
    /// Sealed values come back as the reference they are stored as, never as plaintext: the flow
    /// can send <c>{{secrets.apiPassword}}</c> through the resolver and prove it still works
    /// without the password ever reaching a browser. Anything stored raw is blanked instead —
    /// putting it on screen would be a reveal, and a reveal is an audited action that happens
    /// somewhere else.
    /// </summary>
    private static ConnectAttempt Prefill(ProjectEnvironment environment)
    {
        var auth = EnvironmentAuth.Read(environment.AuthenticationJson);

        var user = auth.Credentials.FirstOrDefault(pair => !Secretish(pair.Key));
        var password = auth.Credentials.FirstOrDefault(pair => Secretish(pair.Key));

        return new ConnectAttempt
        {
            EnvironmentId = environment.Id,
            Name = environment.Name,
            BaseUrl = environment.BaseUrl,
            AllowPrivateNetwork = environment.AllowPrivateNetwork,

            Kind = auth.Mode switch
            {
                AuthMode.Header => "header",
                AuthMode.SignIn => "signIn",
                AuthMode.OAuth2 => "oauth2",
                _ => "none",
            },

            HeaderName = auth.HeaderName,
            HeaderValue = Safe(auth.HeaderValue),

            TokenUrl = auth.TokenUrl,
            TokenMethod = auth.Method,
            BodyKind = auth.BodyKind,
            UserField = user.Key,
            UserValue = user.Value,
            PasswordField = password.Key,
            PasswordValue = Safe(password.Value),
            TokenPath = auth.TokenPath,
            UseHeaderName = auth.UseHeaderName,
            UseHeaderTemplate = auth.UseHeaderTemplate,

            Grant = auth.Grant,
            ClientId = auth.ClientId,
            ClientSecret = Safe(auth.ClientSecret),
            Scope = auth.Scope,
            CredentialsInHeader = auth.CredentialsInHeader,
        };

        static bool Secretish(string field) =>
            field.Contains("pass", StringComparison.OrdinalIgnoreCase)
            || field.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || field.Contains("token", StringComparison.OrdinalIgnoreCase)
            || field.Contains("key", StringComparison.OrdinalIgnoreCase);

        static string? Safe(string? value) => Reference(value) ? value : null;
    }

    /// <summary>Whether a stored value names a secret rather than being one.</summary>
    private static bool Reference(string? value) =>
        value is not null && value.Contains("{{", StringComparison.Ordinal);

    /// <summary>Enough of a token to recognise, not enough to use.</summary>
    private static string Preview(IReadOnlyList<KeyValueEntry> headers)
    {
        var value = headers.FirstOrDefault()?.Value ?? string.Empty;
        return value.Length <= 18 ? value : value[..18] + "…";
    }

    private static string Preview(string value) =>
        value.Length <= 4 ? new string('•', value.Length) : value[..2] + new string('•', 4);

    private static string Shorten(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Length <= limit ? text : text[..limit] + "…";
    }
}

/// <summary>What the four steps have collected so far. The same shape for «try» and for «save».</summary>
public sealed record ConnectAttempt
{
    /// <summary>Set when the flow is editing an environment rather than making one.</summary>
    public Guid? EnvironmentId { get; init; }

    public string? Name { get; init; }
    public string? BaseUrl { get; init; }
    public bool AllowPrivateNetwork { get; init; }

    /// <summary>none · header · signIn · oauth2</summary>
    public string Kind { get; init; } = "signIn";

    public string? HeaderName { get; init; }
    public string? HeaderValue { get; init; }

    public string? TokenUrl { get; init; }
    public string TokenMethod { get; init; } = "POST";
    public string BodyKind { get; init; } = "json";
    public string? UserField { get; init; }
    public string? UserValue { get; init; }
    public string? PasswordField { get; init; }
    public string? PasswordValue { get; init; }
    public string? TokenPath { get; init; }
    public string UseHeaderName { get; init; } = "Authorization";
    public string UseHeaderTemplate { get; init; } = "Bearer {token}";

    public string? Grant { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? Scope { get; init; }
    public bool CredentialsInHeader { get; init; }

    /// <summary>The one call step three makes, and the endpoint step four keeps.</summary>
    public string? Path { get; init; }

    public string Method { get; init; } = "GET";

    public EnvironmentAuth ToAuth() => Kind switch
    {
        "header" => new EnvironmentAuth
        {
            Mode = AuthMode.Header,
            HeaderName = HeaderName,
            HeaderValue = HeaderValue,
        },

        "oauth2" => new EnvironmentAuth
        {
            Mode = AuthMode.OAuth2,
            TokenUrl = TokenUrl,
            Grant = Grant,
            ClientId = ClientId,
            ClientSecret = ClientSecret,
            Scope = Scope,
            CredentialsInHeader = CredentialsInHeader,
            Credentials = Fields(),
            TokenPath = TokenPath,
            UseHeaderName = UseHeaderName,
            UseHeaderTemplate = UseHeaderTemplate,
        },

        "signIn" => new EnvironmentAuth
        {
            Mode = AuthMode.SignIn,
            TokenUrl = TokenUrl,
            Method = TokenMethod,
            BodyKind = BodyKind,
            Credentials = Fields(),
            TokenPath = TokenPath,
            UseHeaderName = UseHeaderName,
            UseHeaderTemplate = UseHeaderTemplate,
        },

        _ => new EnvironmentAuth(),
    };

    private Dictionary<string, string> Fields()
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(UserField)) fields[UserField] = UserValue ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(PasswordField)) fields[PasswordField] = PasswordValue ?? string.Empty;

        return fields;
    }
}

public sealed record ConnectResult
{
    public ConnectStepResult SignIn { get; init; } = new();
    public ConnectStepResult? Call { get; init; }

    public static ConnectResult Refused(string problem) =>
        new() { SignIn = new ConnectStepResult { Ok = false, Problem = problem } };
}

public sealed class ConnectStepResult
{
    public bool Ok { get; set; }
    public bool Skipped { get; set; }
    public string? Problem { get; set; }
    public string? Detail { get; set; }
    public string? Url { get; set; }
    public int StatusCode { get; set; }
}
