using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Requests;
using ProofFlow.Domain.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.Infrastructure.Environments;

/// <summary>
/// Turns a stored environment into the things the engine needs: a URL policy and a set of variable
/// scopes.
///
/// This is the seam between "rows in a database" and "an engine that knows nothing about a
/// database". It is also the only place secrets are decrypted, which makes it the only place worth
/// auditing for that.
/// </summary>
public sealed class EnvironmentContextBuilder(
    ProofFlowDbContext db,
    ISecretCipher cipher,
    ILogger<EnvironmentContextBuilder> logger)
{
    public async Task<EnvironmentContext> BuildAsync(
        Guid environmentId, CancellationToken cancellationToken = default)
    {
        var environment = await db.Environments
            .FirstOrDefaultAsync(e => e.Id == environmentId, cancellationToken)
            ?? throw new InvalidOperationException($"No environment {environmentId} in this workspace.");

        return await BuildAsync(environment, cancellationToken);
    }

    public async Task<EnvironmentContext> BuildAsync(
        ProjectEnvironment environment, CancellationToken cancellationToken = default)
    {
        var redaction = new RedactionScope();

        var scopes = new VariableScopes
        {
            Environment = EnvironmentNode(environment),
            Variables = await VariablesAsync(environment, cancellationToken),
            Secrets = await SecretsAsync(environment, redaction, cancellationToken),
        };

        return new EnvironmentContext(
            environment,
            Policy(environment),
            scopes,
            redaction,
            EnvironmentAuth.Read(environment.AuthenticationJson));
    }

    /// <summary>
    /// The policy this environment's requests run under.
    ///
    /// The allowed-host list is widened to include the base URL's own host. Without that, filling
    /// in the list at all would immediately break the environment it was meant to protect, and the
    /// first thing anyone would do is empty it again.
    /// </summary>
    public static UrlPolicy Policy(ProjectEnvironment environment)
    {
        var allowed = Lines(environment.AllowedHosts).ToList();

        if (allowed.Count > 0
            && Uri.TryCreate(environment.BaseUrl, UriKind.Absolute, out var baseUri)
            && !allowed.Any(pattern => UrlGuard.HostMatches(baseUri.DnsSafeHost, pattern)))
        {
            allowed.Add(baseUri.DnsSafeHost);
        }

        return new UrlPolicy
        {
            AllowedHosts = allowed,
            AllowPrivateNetwork = environment.AllowPrivateNetwork,
            AllowInvalidCertificate = environment.AllowInvalidCertificate,
            MaxRedirects = Math.Clamp(environment.MaxRedirects, 0, 20),
            MaxResponseBytes = (long)Math.Clamp(environment.MaxResponseKilobytes, 1, 262_144) * 1024,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(environment.TimeoutSeconds, 1, 600)),
        };
    }

    private static JsonObject EnvironmentNode(ProjectEnvironment environment)
    {
        var node = new JsonObject
        {
            ["name"] = environment.Name,
            ["slug"] = environment.Slug,
            // Without the trailing slash, so "{{environment.baseUrl}}/orders" cannot produce "//".
            ["baseUrl"] = environment.BaseUrl?.TrimEnd('/') ?? string.Empty,
            ["kind"] = environment.Kind.ToString(),
            ["isProduction"] = environment.IsProduction,
        };

        if (!string.IsNullOrWhiteSpace(environment.DefaultHeadersJson))
        {
            try
            {
                node["headers"] = JsonNode.Parse(environment.DefaultHeadersJson);
            }
            catch (JsonException)
            {
                // A malformed header blob must not stop the environment loading. It shows in the
                // editor as invalid; failing here would make the environment unopenable.
                node["headers"] = new JsonObject();
            }
        }

        return node;
    }

    private async Task<JsonObject> VariablesAsync(
        ProjectEnvironment environment, CancellationToken cancellationToken)
    {
        var rows = await db.Variables
            .Where(v => v.ProjectId == environment.ProjectId
                        && (v.EnvironmentId == null || v.EnvironmentId == environment.Id))
            .ToListAsync(cancellationToken);

        var node = new JsonObject();

        // Project-wide first, then the environment's own on top: two levels, and the more specific
        // one wins. Ordering the query would not be enough — the overwrite has to be deliberate.
        foreach (var variable in rows.Where(v => v.EnvironmentId is null))
            node[variable.Name] = variable.Value;

        foreach (var variable in rows.Where(v => v.EnvironmentId is not null))
            node[variable.Name] = variable.Value;

        return node;
    }

    private async Task<JsonObject> SecretsAsync(
        ProjectEnvironment environment, RedactionScope redaction, CancellationToken cancellationToken)
    {
        var rows = await db.Secrets
            .Where(s => s.ProjectId == environment.ProjectId
                        && (s.EnvironmentId == null || s.EnvironmentId == environment.Id))
            .ToListAsync(cancellationToken);

        var node = new JsonObject();

        foreach (var secret in rows.OrderBy(s => s.EnvironmentId.HasValue))
        {
            try
            {
                var value = cipher.Open(new SealedSecret(
                    secret.Ciphertext, secret.Nonce, secret.Tag, secret.KeyVersion));

                node[secret.Name] = value;

                // Registered before the value can reach a request, so it is masked everywhere
                // afterwards even if the run fails halfway through building the step.
                redaction.Remember(value);
            }
            catch (Exception ex)
            {
                // One unreadable secret must not stop a run that does not use it. The reference
                // then fails to resolve with a message naming the secret, which is far more
                // useful than a decryption stack trace at startup.
                logger.LogError(ex, "Secret {Name} in environment {Environment} could not be decrypted.",
                    secret.Name, environment.Slug);
            }
        }

        return node;
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty)
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Everything one environment contributes to a run.</summary>
public sealed record EnvironmentContext(
    ProjectEnvironment Environment,
    UrlPolicy Policy,
    VariableScopes Scopes,
    RedactionScope Redaction,
    EnvironmentAuth Auth)
{
    public VariableResolver Resolver() => new(Scopes, Redaction);

    /// <summary>
    /// What identifies this environment's tokens in the cache.
    ///
    /// The id rather than the slug: a slug can be edited, and a token cached under the old one would
    /// go on being used by an environment somebody has since pointed somewhere else.
    /// </summary>
    public string TokenKey => Environment.Id.ToString();
}
