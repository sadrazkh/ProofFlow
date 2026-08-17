using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProofFlow.Contracts.Requests;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.TestEngine.Http;

/// <summary>
/// Turns an environment's authentication into the headers a request carries.
///
/// It lives in the engine rather than in the infrastructure, and that placement is the point: the
/// agent runs on somebody else's network with no database, and it has to sign in the same way the
/// server does. Two implementations of «how do I get a token» would be two things to keep in step,
/// and the one that drifted would be the one nobody could reproduce.
///
/// Signing in goes through the same <see cref="IHttpExecutor"/> and the same <see cref="UrlPolicy"/>
/// as every other address here. A token endpoint is an address somebody typed, and an address
/// somebody typed is exactly what the guard exists for — an auth path that quietly bypassed it would
/// be the one hole in the wall, at the one place a credential is involved.
///
/// A failure to sign in is a failure, not a fallback. Sending the request anyway would produce a 401
/// that a comparison then reports as «the API changed», and the actual cause — a password that no
/// longer works — would be three screens away. This says so instead.
/// </summary>
public sealed class EnvironmentAuthenticator(IHttpExecutor executor, TokenCache cache)
{
    /// <summary>
    /// The headers to add, or the reason there are none.
    ///
    /// <paramref name="cacheKey"/> identifies the environment. It is combined with a fingerprint of
    /// the configuration, so editing a password invalidates the token that password bought rather
    /// than leaving a stale one working until it expires.
    /// </summary>
    public async Task<AuthOutcome> HeadersAsync(
        EnvironmentAuth auth,
        string? baseUrl,
        VariableResolver resolver,
        UrlPolicy policy,
        string cacheKey,
        CancellationToken cancellation = default)
    {
        if (!auth.SendsAnything) return AuthOutcome.Nothing;

        try
        {
            return auth.Mode switch
            {
                AuthMode.Header => Fixed(auth, resolver),
                AuthMode.SignIn or AuthMode.OAuth2 =>
                    await TokenAsync(auth, baseUrl, resolver, policy, cacheKey, cancellation),
                _ => AuthOutcome.Nothing,
            };
        }
        catch (VariableResolutionException ex)
        {
            // A reference that does not resolve — most often a secret somebody has not created yet.
            // Named, because «unauthorized» would send them to the API's logs instead of to ours.
            return AuthOutcome.Failed(ex.Message);
        }
    }

    private static AuthOutcome Fixed(EnvironmentAuth auth, VariableResolver resolver)
    {
        var name = resolver.Resolve(auth.HeaderName ?? string.Empty).Trim();
        var value = resolver.Resolve(auth.HeaderValue ?? string.Empty).Trim();

        if (name.Length == 0 || value.Length == 0)
        {
            return AuthOutcome.Failed("This environment is set to send a header, but it has no name or no value.");
        }

        return AuthOutcome.With([new KeyValueEntry(name, value)]);
    }

    private async Task<AuthOutcome> TokenAsync(
        EnvironmentAuth auth, string? baseUrl, VariableResolver resolver, UrlPolicy policy,
        string cacheKey, CancellationToken cancellation)
    {
        var key = $"{cacheKey}|{Fingerprint(auth, resolver)}";

        if (cache.TryGet(key, out var cached))
        {
            return AuthOutcome.With([Use(auth, resolver, cached!)]);
        }

        var url = Combine(baseUrl, resolver.Resolve(auth.TokenUrl ?? string.Empty));

        if (string.IsNullOrWhiteSpace(url))
        {
            return AuthOutcome.Failed("This environment signs in for a token, but no sign-in address is set.");
        }

        var request = auth.Mode == AuthMode.OAuth2
            ? OAuth(auth, url, resolver)
            : SignIn(auth, url, resolver);

        var response = await executor.SendAsync(request, policy, cancellation);

        if (!response.Succeeded)
        {
            return AuthOutcome.Failed($"Signing in failed: {response.Failure!.Message}");
        }

        if (response.StatusCode is < 200 or > 299)
        {
            // The server's own words. A login that answers «account locked» has already said what is
            // wrong, and tidying that away answers nothing.
            return AuthOutcome.Failed(
                $"Signing in was refused with {response.StatusCode}. {Shorten(response.Body, 300)}");
        }

        if (TokenReader.Read(response.Body, auth.TokenPath) is not { Length: > 0 } token)
        {
            return AuthOutcome.Failed(
                auth.TokenPath is { Length: > 0 } path
                    ? $"Signing in worked, but there is nothing at «{path}» in the answer."
                    : $"Signing in worked, but no token was found in the answer. {Shorten(response.Body, 200)}");
        }

        var lifetime = TokenReader.Lifetime(response.Body, auth.ExpiresInPath)
                       ?? EnvironmentAuth.DefaultLifetimeSeconds;

        cache.Set(key, token, TimeSpan.FromSeconds(Math.Clamp(lifetime, 5, 86_400)));

        return AuthOutcome.With([Use(auth, resolver, token)]);
    }

    /// <summary>The header the token is carried in, with <c>{token}</c> filled in.</summary>
    private static KeyValueEntry Use(EnvironmentAuth auth, VariableResolver resolver, string token)
    {
        var name = resolver.Resolve(auth.UseHeaderName).Trim();
        var template = resolver.Resolve(auth.UseHeaderTemplate);

        return new KeyValueEntry(
            name.Length == 0 ? "Authorization" : name,
            template.Contains("{token}", StringComparison.Ordinal)
                ? template.Replace("{token}", token, StringComparison.Ordinal)

                // A template with no placeholder is almost certainly somebody who typed only the
                // prefix. Appending is what they meant; sending «Bearer» alone is not.
                : $"{template.TrimEnd()} {token}".Trim());
    }

    private static HttpRequestDefinition SignIn(
        EnvironmentAuth auth, string url, VariableResolver resolver)
    {
        var fields = auth.Credentials.ToDictionary(
            pair => resolver.Resolve(pair.Key),
            pair => resolver.Resolve(pair.Value),
            StringComparer.Ordinal);

        var form = auth.BodyKind.Equals("form", StringComparison.OrdinalIgnoreCase);

        return new HttpRequestDefinition
        {
            Method = string.IsNullOrWhiteSpace(auth.Method) ? "POST" : auth.Method.ToUpperInvariant(),
            Url = url,
            Body = form
                ? new RequestBody
                {
                    Kind = BodyKind.FormUrlEncoded,
                    Form = [.. fields.Select(pair => new KeyValueEntry(pair.Key, pair.Value))],
                }
                : new RequestBody
                {
                    Kind = BodyKind.Json,
                    Content = JsonSerializer.Serialize(fields),
                },
        };
    }

    private static HttpRequestDefinition OAuth(
        EnvironmentAuth auth, string url, VariableResolver resolver)
    {
        var clientId = resolver.Resolve(auth.ClientId ?? string.Empty);
        var clientSecret = resolver.Resolve(auth.ClientSecret ?? string.Empty);
        var scope = resolver.Resolve(auth.Scope ?? string.Empty);

        var form = new List<KeyValueEntry>
        {
            new("grant_type", auth.Grant == "password" ? "password" : "client_credentials"),
        };

        if (!string.IsNullOrWhiteSpace(scope)) form.Add(new KeyValueEntry("scope", scope));

        if (auth.Grant == "password")
        {
            foreach (var pair in auth.Credentials)
            {
                form.Add(new KeyValueEntry(resolver.Resolve(pair.Key), resolver.Resolve(pair.Value)));
            }
        }

        var headers = new List<KeyValueEntry>();

        if (auth.CredentialsInHeader && !string.IsNullOrWhiteSpace(clientId))
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

        return new HttpRequestDefinition
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = new RequestBody { Kind = BodyKind.FormUrlEncoded, Form = form },
        };
    }

    /// <summary>
    /// A relative sign-in path joined onto the base URL, and an absolute one left alone.
    ///
    /// The same rule the request builder uses, because an auth address is entered the same way and
    /// somebody who typed «/auth/login» in one place means the same thing in the other.
    /// </summary>
    public static string Combine(string? baseUrl, string path)
    {
        path = (path ?? string.Empty).Trim();

        if (path.Length == 0) return baseUrl ?? string.Empty;
        if (Uri.IsWellFormedUriString(path, UriKind.Absolute)) return path;
        if (string.IsNullOrWhiteSpace(baseUrl)) return path;

        return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    /// <summary>
    /// What the cached token was bought with.
    ///
    /// The resolved values, not the template: a token obtained with <c>{{secrets.apiPassword}}</c>
    /// has to stop being valid when that secret's value changes, and the template does not change
    /// when the secret does.
    /// </summary>
    private static string Fingerprint(EnvironmentAuth auth, VariableResolver resolver)
    {
        var parts = new List<string>
        {
            auth.Mode.ToString(),
            auth.TokenUrl ?? string.Empty,
            auth.Grant ?? string.Empty,
            Safe(auth.ClientId),
            Safe(auth.ClientSecret),
            Safe(auth.Scope),
        };

        foreach (var pair in auth.Credentials.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            parts.Add($"{pair.Key}={Safe(pair.Value)}");
        }

        // Joined on a unit separator, written as an escape rather than as the byte itself: a
        // raw control character in source is invisible, and the first tool that normalises
        // whitespace would turn the separator into nothing and fingerprint two different
        // configurations the same.
        var joined = string.Join('\u001f', parts);
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(joined));

        return Convert.ToHexStringLower(hash);

        string Safe(string? text)
        {
            // An unresolvable reference must not throw here — the caller wants a cache key, and the
            // real failure is reported when the sign-in is attempted.
            try { return resolver.Resolve(text ?? string.Empty); }
            catch (VariableResolutionException) { return text ?? string.Empty; }
        }
    }

    private static string Shorten(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= limit ? flat : flat[..limit] + "…";
    }
}

/// <summary>Either headers to add, or the reason a request must not be sent.</summary>
public sealed record AuthOutcome(IReadOnlyList<KeyValueEntry> Headers, string? Problem)
{
    public static readonly AuthOutcome Nothing = new([], null);

    public static AuthOutcome With(IReadOnlyList<KeyValueEntry> headers) => new(headers, null);

    public static AuthOutcome Failed(string problem) => new([], problem);

    public bool Ok => Problem is null;
}

/// <summary>
/// Tokens, held for as long as the server said they are good for.
///
/// A singleton, because the point is that a sweep of two thousand rows signs in once. Bounded, so a
/// workspace with many environments cannot grow it without limit — and bounded by eviction of what
/// expired first, because that is both the cheapest thing to lose and the thing that was about to
/// be useless anyway.
/// </summary>
public sealed class TokenCache
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _held = new();

    private const int Limit = 256;

    public bool TryGet(string key, out string? token)
    {
        if (_held.TryGetValue(key, out var entry) && entry.Until > DateTimeOffset.UtcNow)
        {
            token = entry.Token;
            return true;
        }

        // Expired entries are removed on the way past rather than swept: this is the only code that
        // ever looks at them, so anything else would be a timer to maintain.
        if (entry.Token is not null) _held.TryRemove(key, out _);

        token = null;
        return false;
    }

    public void Set(string key, string token, TimeSpan lifetime)
    {
        // Two thirds of what the server promised. A token that expires between «still valid» and
        // «arrives at the API» is a 401 nobody can reproduce, and clock skew makes that likelier
        // than the arithmetic suggests.
        var until = DateTimeOffset.UtcNow.Add(lifetime * 0.66);

        _held[key] = (token, until);

        if (_held.Count > Limit) Trim();
    }

    /// <summary>Forgets everything for one environment, whatever it was bought with.</summary>
    public void Forget(string cacheKeyPrefix)
    {
        foreach (var key in _held.Keys.Where(k => k.StartsWith(cacheKeyPrefix, StringComparison.Ordinal)))
        {
            _held.TryRemove(key, out _);
        }
    }

    private void Trim()
    {
        foreach (var entry in _held.OrderBy(pair => pair.Value.Until).Take(_held.Count - Limit + 32))
        {
            _held.TryRemove(entry.Key, out _);
        }
    }
}

/// <summary>
/// Finds the token in whatever the server sent back.
///
/// The named field first when there is one, then the four names that specifications and real servers
/// use, then the same four inside a «data» wrapper because one popular framework nests everything.
/// Guessing is worth doing here: the alternative is telling somebody their working login is not one.
/// </summary>
public static class TokenReader
{
    private static readonly string[] Names =
        ["access_token", "accessToken", "token", "id_token"];

    public static string? Read(string? body, string? path)
    {
        var root = Parse(body);
        if (root is null) return null;

        if (!string.IsNullOrWhiteSpace(path)) return Text(Walk(root, path));

        var node = root["data"] is JsonObject nested ? nested : root;

        foreach (var name in Names)
        {
            if (Text(node?[name]) is { Length: > 0 } found) return found;
        }

        return null;
    }

    /// <summary>How long the token is good for, in seconds, when the server says.</summary>
    public static int? Lifetime(string? body, string? path)
    {
        var root = Parse(body);
        if (root is null) return null;

        var node = string.IsNullOrWhiteSpace(path)
            ? (root["data"] is JsonObject nested ? nested : root)?["expires_in"]
              ?? (root["data"] is JsonObject nested2 ? nested2 : root)?["expiresIn"]
            : Walk(root, path);

        return Text(node) is { Length: > 0 } text && int.TryParse(text, out var seconds) && seconds > 0
            ? seconds
            : null;
    }

    private static JsonNode? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    /// <summary>A dotted path. Array indexes are not supported and have never been needed.</summary>
    private static JsonNode? Walk(JsonNode root, string path)
    {
        var node = root;

        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (node is not JsonObject holder || !holder.TryGetPropertyValue(part, out node)) return null;
        }

        return node;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,

        // A numeric expiry, and a token some server decided to send unquoted.
        JsonValue value => value.ToString(),
        _ => null,
    };
}
