using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProofFlow.Domain.Runs;

namespace ProofFlow.TestEngine.Running;

/// <summary>
/// Getting a credential, and making the rest of the run use it.
///
/// Every node here does both halves. A bearer node that minted a token and left it in a socket
/// would be a node that appears to work and changes nothing — the person who dropped it on the
/// canvas meant "and now send that with everything", so that is what it does. <c>auth.setHeader</c>
/// stays for the cases the defaults do not cover: a different header name, a different prefix, or a
/// credential that should only apply inside one block.
///
/// Two rules hold throughout. A minted token is registered for redaction the moment it exists, so
/// it cannot reach a log or a stored body. And nothing here reads a credential from the graph — the
/// properties hold the name of a secret, and the value arrives already resolved.
/// </summary>
public sealed partial class NodeExecutors
{
    /// <summary>
    /// Headers that apply to every request from here on.
    ///
    /// Per-run state, which is why this class is created once per run. <see cref="Owner"/> is the
    /// container a header was scoped to, and it is dropped when that container ends.
    /// </summary>
    private readonly List<StandingHeader> _standing = [];

    private readonly Dictionary<string, string> _standingQuery = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _cookies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tokenCache = new(StringComparer.Ordinal);
    private readonly Lock _authGate = new();

    private bool _keepCookies = true;

    private sealed record StandingHeader(string Name, string Value, string? Owner);

    /// <summary>Called by the runner when a container ends, for headers scoped to it.</summary>
    internal void DropScope(string containerId)
    {
        lock (_authGate) _standing.RemoveAll(header => header.Owner == containerId);
    }

    internal Task<IReadOnlyList<JsonNode>> RowsAsync(string reference, CancellationToken cancellation) =>
        services.DataSetRowsAsync(reference, cancellation);

    private void Install(string name, string value, string? owner)
    {
        lock (_authGate)
        {
            _standing.RemoveAll(header =>
                string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase)
                && header.Owner == owner);

            _standing.Add(new StandingHeader(name, value, owner));
        }
    }

    /// <summary>
    /// Folds the standing credentials into one request.
    ///
    /// The node's own headers win. Somebody who typed an Authorization header on a request meant
    /// that request to use it, whatever an auth node earlier in the graph decided.
    /// </summary>
    private (string Url, List<(string Name, string Value)> Headers) Dress(
        string url, IReadOnlyList<(string Name, string Value)> own)
    {
        var headers = new List<(string Name, string Value)>();

        lock (_authGate)
        {
            foreach (var header in _standing)
            {
                if (own.Any(pair => string.Equals(pair.Name, header.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                headers.Add((header.Name, header.Value));
            }

            if (_cookies.Count > 0 && !own.Any(pair =>
                    string.Equals(pair.Name, "Cookie", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(("Cookie",
                    string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}"))));
            }

            if (_standingQuery.Count > 0)
            {
                var query = string.Join("&", _standingQuery.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

                url += (url.Contains('?') ? "&" : "?") + query;
            }
        }

        headers.AddRange(own);
        return (url, headers);
    }

    /// <summary>Keeps the cookies a response set, when the jar is on.</summary>
    private void Harvest(HttpNodeResult result)
    {
        if (!_keepCookies) return;

        foreach (var (name, value) in result.Headers)
        {
            if (!string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;

            var pair = value.Split(';', 2)[0];
            var at = pair.IndexOf('=');
            if (at <= 0) continue;

            lock (_authGate) _cookies[pair[..at].Trim()] = pair[(at + 1)..].Trim();
        }
    }

    // ---- the credentials themselves ------------------------------------------------------------

    private NodeOutcome Basic(NodeContext context)
    {
        var user = context.Property("username") ?? string.Empty;
        var password = context.Property("password") ?? string.Empty;

        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

        context.Remember(password);
        context.Remember(credential);
        Install("Authorization", $"Basic {credential}", null);

        return NodeOutcome.Ok(("token", JsonValue.Create(credential)));
    }

    private NodeOutcome Bearer(NodeContext context)
    {
        var token = context.Property("token");
        if (string.IsNullOrWhiteSpace(token)) return NodeOutcome.Failed("No token was given.");

        context.Remember(token);
        Install("Authorization", $"Bearer {token}", null);

        return NodeOutcome.Ok(("token", JsonValue.Create(token)));
    }

    private NodeOutcome ApiKey(NodeContext context)
    {
        var name = context.Property("name");
        var value = context.Property("value");

        if (string.IsNullOrWhiteSpace(name)) return NodeOutcome.Failed("The key has no name.");
        if (string.IsNullOrWhiteSpace(value)) return NodeOutcome.Failed("The key has no value.");

        context.Remember(value);

        switch (context.Property("placement"))
        {
            case "query":
                lock (_authGate) _standingQuery[name] = value;
                break;

            case "cookie":
                lock (_authGate) _cookies[name] = value;
                break;

            default:
                Install(name, value, null);
                break;
        }

        return NodeOutcome.Ok(("token", JsonValue.Create(value)));
    }

    private NodeOutcome SetHeader(NodeContext context)
    {
        var token = context.Input("token")?.ToString();
        if (string.IsNullOrEmpty(token)) return NodeOutcome.Failed("No credential was plugged in.");

        var header = context.Property("header") ?? "Authorization";
        var prefix = context.Property("prefix") ?? string.Empty;

        // "branch" means the enclosing block. At the top level there is no block to leave, so it
        // behaves as "run" — which is what it looks like on the canvas, too.
        var owner = context.Property("scope") == "branch" ? context.Node.ParentId : null;

        context.Remember(token);
        Install(header, prefix + token, owner);

        return NodeOutcome.Ok();
    }

    private NodeOutcome CookieJar(NodeContext context)
    {
        if (context.Property("action") == "clear")
        {
            lock (_authGate) _cookies.Clear();
            _keepCookies = false;
            context.Log(RunEventLevel.Info, "Cookies cleared.", null);
            return NodeOutcome.Ok();
        }

        _keepCookies = true;
        return NodeOutcome.Ok();
    }

    private static NodeOutcome SignHmac(NodeContext context)
    {
        var payload = Encoding.UTF8.GetBytes(context.Property("payload") ?? string.Empty);
        var secret = context.Property("secret");

        if (string.IsNullOrEmpty(secret)) return NodeOutcome.Failed("No signing secret was named.");

        context.Remember(secret);

        var key = Encoding.UTF8.GetBytes(secret);
        var signature = context.Property("algorithm") == "sha512"
            ? HMACSHA512.HashData(key, payload)
            : HMACSHA256.HashData(key, payload);

        return NodeOutcome.Ok(("signature", JsonValue.Create(Convert.ToHexStringLower(signature))));
    }

    // ---- OAuth ---------------------------------------------------------------------------------

    private Task<NodeOutcome> ClientCredentials(NodeContext context) =>
        TokenAsync(context, new Dictionary<string, string?>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = context.Property("clientId"),
            ["client_secret"] = context.Property("clientSecret"),
            ["scope"] = context.Property("scope"),
        });

    private Task<NodeOutcome> PasswordGrant(NodeContext context) =>
        TokenAsync(context, new Dictionary<string, string?>
        {
            ["grant_type"] = "password",
            ["username"] = context.Property("username"),
            ["password"] = context.Property("password"),
            ["client_id"] = context.Property("clientId"),
            ["scope"] = context.Property("scope"),
        });

    private Task<NodeOutcome> RefreshGrant(NodeContext context) =>
        TokenAsync(context, new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = context.Property("refreshToken"),
            ["client_id"] = context.Property("clientId"),
        });

    /// <summary>
    /// The token endpoint, as all three grants use it.
    ///
    /// Form-encoded, because that is what the specification says and what every server implements;
    /// the answer is read for <c>access_token</c>, which is the one field they all agree on.
    /// </summary>
    private async Task<NodeOutcome> TokenAsync(NodeContext context, Dictionary<string, string?> form)
    {
        var url = context.Property("tokenUrl");
        if (string.IsNullOrWhiteSpace(url)) return NodeOutcome.Failed("This step has no token address.");

        foreach (var value in form.Values) context.Remember(value);

        var cacheKey = $"{url}|{string.Join('&', form.Select(pair => $"{pair.Key}={pair.Value}"))}";

        if (context.Flag("cache"))
        {
            lock (_authGate)
            {
                if (_tokenCache.TryGetValue(cacheKey, out var cached))
                {
                    context.Log(RunEventLevel.Debug, "Reusing the token from earlier in this run.", null);
                    Install("Authorization", $"Bearer {cached}", null);
                    return NodeOutcome.Ok(("token", JsonValue.Create(cached)));
                }
            }
        }

        var body = string.Join('&', form
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

        var result = await services.SendAsync(
            new HttpNodeRequest("POST", url,
                [("Content-Type", "application/x-www-form-urlencoded")], body, "form", null),
            context.Cancellation);

        if (!result.Succeeded || result.StatusCode >= 400)
        {
            // The body is not repeated: a rejected token request answers with the client secret's
            // fate, and sometimes with the secret.
            return NodeOutcome.Failed(
                $"The token could not be fetched ({(result.Succeeded ? result.StatusCode.ToString() : result.Failure)}).");
        }

        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(result.Body);
        }
        catch (JsonException)
        {
            return NodeOutcome.Failed("The token endpoint did not answer with JSON.");
        }

        var token = answer?["access_token"]?.ToString();
        if (string.IsNullOrEmpty(token)) return NodeOutcome.Failed("The answer held no access_token.");

        context.Remember(token);
        Install("Authorization", $"Bearer {token}", null);

        if (context.Flag("cache")) lock (_authGate) _tokenCache[cacheKey] = token;

        return NodeOutcome.Ok(("token", JsonValue.Create(token)));
    }

    /// <summary>
    /// Signing in the ordinary way: post a username and password, read the token out of the answer.
    ///
    /// The path is a property because no two APIs agree on where the token lives, and asking
    /// somebody to add three extract nodes for the most common step in every scenario is the kind
    /// of thing this product exists to avoid.
    /// </summary>
    private async Task<NodeOutcome> Login(NodeContext context)
    {
        var url = context.Property("url");
        if (string.IsNullOrWhiteSpace(url)) return NodeOutcome.Failed("This step has no address.");

        var password = context.Property("password");
        context.Remember(password);

        var payload = new JsonObject
        {
            ["username"] = context.Property("username"),
            ["password"] = password,
        };

        var (dressed, headers) = Dress(url, [("Content-Type", "application/json")]);

        var result = await services.SendAsync(
            new HttpNodeRequest("POST", dressed, headers, payload.ToJsonString(), "json", null),
            context.Cancellation);

        if (!result.Succeeded) return NodeOutcome.Failed(result.Failure ?? "Signing in did not complete.");

        Harvest(result);
        var response = Response(result);

        if (result.StatusCode >= 400)
        {
            return NodeOutcome.Failed($"Signing in was refused ({result.StatusCode}).",
                new Dictionary<string, JsonNode?> { ["response"] = response });
        }

        var token = Read(response["body"], context.Property("tokenPath") ?? "$.accessToken")?.ToString();

        if (string.IsNullOrEmpty(token))
        {
            return NodeOutcome.Failed(
                $"There is no «{context.Property("tokenPath")}» in what came back.",
                new Dictionary<string, JsonNode?> { ["response"] = response });
        }

        context.Remember(token);
        Install("Authorization", $"Bearer {token}", null);

        return NodeOutcome.Ok(("token", JsonValue.Create(token)), ("response", response));
    }
}
