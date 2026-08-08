using System.Text.Json;
using System.Text.Json.Nodes;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Infrastructure.Portability.Importers;

/// <summary>
/// Turns a Postman collection into requests.
///
/// The happy accident here is that Postman writes variables as <c>{{name}}</c> and so does this
/// product, so a URL survives the crossing untouched. Almost nothing else does.
///
/// What a collection contains and this does not import: pre-request scripts, test scripts, and
/// everything else written in JavaScript. That is not an omission to fix later — running arbitrary
/// script from a file somebody was handed is the thing this product exists not to need, and the
/// brief forbids test logic living in code. What can be expressed as a step becomes a step; the rest
/// is reported so nobody thinks it came across.
/// </summary>
public static class PostmanImporter
{
    /// <summary>How many requests it will take out of one collection.</summary>
    public const int MaxRequests = 300;

    public static Imported Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Imported.Refused("import.empty");

        JsonObject? root;

        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return Imported.Refused("import.notJson");
        }

        if (root is null) return Imported.Refused("import.notJson");

        var schema = root["info"]?["schema"]?.GetValue<string>();

        if (schema is null || !schema.Contains("getpostman.com", StringComparison.OrdinalIgnoreCase))
        {
            return Imported.Refused("import.notPostman");
        }

        // v1 nests differently and has no "item" tree at all. Refused rather than half-read.
        if (schema.Contains("v1", StringComparison.OrdinalIgnoreCase))
        {
            return Imported.Refused("import.postmanV1");
        }

        var notes = new List<string>();
        var secrets = new List<string>();
        var requests = new List<ImportedRequest>();

        Walk(root["item"] as JsonArray, group: null, requests, secrets, notes);

        if (requests.Count == 0) return Imported.Refused("import.noRequests");

        return new Imported
        {
            SuggestedName = root["info"]?["name"]?.GetValue<string>(),
            Description = root["info"]?["description"]?.GetValue<string>()
                          ?? root["info"]?["description"]?["content"]?.GetValue<string>(),
            BaseUrl = null,
            Variables = Variables(root, secrets),
            Requests = requests,
            SecretsToSupply = [.. secrets.Distinct(StringComparer.Ordinal)],
            Notes = [.. notes.Distinct(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Walks the folder tree.
    ///
    /// A Postman folder is an item with an <c>item</c> array instead of a <c>request</c>, and they
    /// nest to any depth. The folder name is kept as a group so that forty requests called "Get"
    /// are not forty scenarios called "Get".
    /// </summary>
    private static void Walk(
        JsonArray? items, string? group,
        List<ImportedRequest> requests, List<string> secrets, List<string> notes)
    {
        if (items is null) return;

        foreach (var entry in items.OfType<JsonObject>())
        {
            if (requests.Count >= MaxRequests)
            {
                notes.Add("import.note.tooManyRequests");
                return;
            }

            var name = entry["name"]?.GetValue<string>() ?? "request";

            if (entry["item"] is JsonArray children)
            {
                Walk(children, group is null ? name : $"{group} / {name}", requests, secrets, notes);
                continue;
            }

            if (entry["request"] is not JsonObject request) continue;

            if (entry["event"] is JsonArray events && events.Count > 0)
            {
                notes.Add("import.note.scripts");
            }

            requests.Add(new ImportedRequest
            {
                Name = name,
                Group = group,
                Description = Text(entry["request"]?["description"]),
                Request = Request(request, secrets, notes),
            });
        }
    }

    private static HttpRequestDefinition Request(
        JsonObject request, List<string> secrets, List<string> notes)
    {
        var headers = new List<KeyValueEntry>();

        if (request["header"] is JsonArray declared)
        {
            foreach (var header in declared.OfType<JsonObject>())
            {
                var name = header["name"]?.GetValue<string>() ?? header["key"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Postman keeps disabled rows, and so does this product — deleting a header
                // somebody is experimenting with is how they lose the one that mattered.
                var enabled = header["disabled"]?.GetValue<bool>() != true;

                if (Credentials.IsCredential(name))
                {
                    headers.Add(new KeyValueEntry(name, Credentials.Reference(name), enabled));
                    secrets.Add(Credentials.SecretName(name));
                    continue;
                }

                headers.Add(new KeyValueEntry(name, Text(header["value"]) ?? string.Empty, enabled));
            }
        }

        var (authentication, authSecrets) = Authentication(request["auth"] as JsonObject, notes);
        secrets.AddRange(authSecrets);

        return new HttpRequestDefinition
        {
            Method = request["method"]?.GetValue<string>()?.ToUpperInvariant() ?? "GET",
            Url = Url(request["url"]),
            Headers = headers,
            Body = Body(request["body"] as JsonObject, notes),
            Authentication = authentication,
        };
    }

    /// <summary>
    /// A Postman URL is either a string or a structure. Both appear in real exports of the same
    /// version, because the app writes the structure and people paste the string.
    /// </summary>
    private static string Url(JsonNode? url)
    {
        if (url is null) return string.Empty;
        if (url is JsonValue value) return value.GetValue<string>();

        if (url["raw"]?.GetValue<string>() is { Length: > 0 } raw) return raw;

        var host = url["host"] is JsonArray hosts
            ? string.Join('.', hosts.Select(part => part?.GetValue<string>()))
            : url["host"]?.GetValue<string>() ?? string.Empty;

        var path = url["path"] is JsonArray parts
            ? string.Join('/', parts.Select(part => part?.GetValue<string>()))
            : url["path"]?.GetValue<string>() ?? string.Empty;

        var protocol = url["protocol"]?.GetValue<string>();
        var origin = string.IsNullOrWhiteSpace(protocol) ? host : $"{protocol}://{host}";

        return string.IsNullOrEmpty(path) ? origin : $"{origin}/{path}";
    }

    private static RequestBody? Body(JsonObject? body, List<string> notes)
    {
        if (body is null) return null;

        return body["mode"]?.GetValue<string>() switch
        {
            "raw" => Raw(body),

            "urlencoded" => new RequestBody
            {
                Kind = BodyKind.FormUrlEncoded,
                Form = Pairs(body["urlencoded"] as JsonArray),
            },

            "formdata" => new RequestBody
            {
                Kind = BodyKind.Multipart,
                Form = Pairs(body["formdata"] as JsonArray),
            },

            "graphql" => new RequestBody
            {
                Kind = BodyKind.GraphQl,
                Content = Text(body["graphql"]?["query"]),
            },

            // A file body is a path on somebody else's machine. Nothing useful crosses.
            "file" => Note(notes, "import.note.fileBody"),

            _ => null,
        };

        static RequestBody Raw(JsonObject body)
        {
            var content = Text(body["raw"]) ?? string.Empty;
            var language = body["options"]?["raw"]?["language"]?.GetValue<string>();

            var kind = language switch
            {
                "json" => BodyKind.Json,
                "xml" => BodyKind.Xml,
                "graphql" => BodyKind.GraphQl,
                _ => content.TrimStart().StartsWith('{') || content.TrimStart().StartsWith('[')
                    ? BodyKind.Json
                    : BodyKind.Text,
            };

            return new RequestBody { Kind = kind, Content = content };
        }

        static RequestBody? Note(List<string> notes, string note)
        {
            notes.Add(note);
            return null;
        }
    }

    private static IReadOnlyList<KeyValueEntry> Pairs(JsonArray? rows) =>
        rows is null
            ? []
            :
            [
                .. rows.OfType<JsonObject>()
                    .Where(row => row["key"] is not null)
                    .Select(row => new KeyValueEntry(
                        row["key"]!.GetValue<string>(),
                        Text(row["value"]) ?? string.Empty,
                        row["disabled"]?.GetValue<bool>() != true)),
            ];

    private static (AuthenticationSpec?, IReadOnlyList<string>) Authentication(
        JsonObject? auth, List<string> notes)
    {
        if (auth is null) return (null, []);

        switch (auth["type"]?.GetValue<string>())
        {
            case "bearer":
                return (new AuthenticationSpec
                {
                    Kind = AuthenticationKind.Bearer,
                    Token = "{{secrets.bearerToken}}",
                }, ["bearerToken"]);

            case "basic":
                return (new AuthenticationSpec
                {
                    Kind = AuthenticationKind.Basic,
                    Username = Setting(auth, "basic", "username") ?? "{{username}}",
                    Password = "{{secrets.password}}",
                }, ["password"]);

            case "apikey":
                var key = Setting(auth, "apikey", "key") ?? "X-Api-Key";

                return (new AuthenticationSpec
                {
                    Kind = AuthenticationKind.ApiKey,
                    HeaderName = key,
                    ApiKey = $"{{{{secrets.{Credentials.SecretName(key)}}}}}",
                    KeyLocation = Setting(auth, "apikey", "in") == "query"
                        ? ApiKeyLocation.Query
                        : ApiKeyLocation.Header,
                }, [Credentials.SecretName(key)]);

            case "oauth2":
                // The flow, the token endpoint and the scopes are worth carrying; the token in the
                // file is somebody's live session. This says so rather than pretending.
                notes.Add("import.note.oauth2");
                return (null, []);

            case null or "noauth":
                return (null, []);

            default:
                notes.Add("import.note.unknownAuth");
                return (null, []);
        }
    }

    /// <summary>Reads one entry out of Postman's array-of-key-value-objects shape.</summary>
    private static string? Setting(JsonObject auth, string type, string key) =>
        (auth[type] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(entry => entry["key"]?.GetValue<string>() == key)?["value"]
            ?.GetValue<string>();

    private static IReadOnlyList<ImportedVariable> Variables(JsonObject root, List<string> secrets)
    {
        if (root["variable"] is not JsonArray declared) return [];

        var variables = new List<ImportedVariable>();

        foreach (var entry in declared.OfType<JsonObject>())
        {
            var name = entry["key"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // A collection variable called "token" holding a live token is the most common way a
            // credential travels in one of these files. The name comes across; the value does not.
            if (Credentials.IsCredential(name))
            {
                secrets.Add(Credentials.SecretName(name));
                continue;
            }

            variables.Add(new ImportedVariable(name, Text(entry["value"]) ?? string.Empty));
        }

        return variables;
    }

    /// <summary>
    /// Reads a value that Postman writes as either a string or an object with a <c>content</c>
    /// field, which it does inconsistently for descriptions in particular.
    /// </summary>
    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.TryGetValue<string>(out var text) ? text : value.ToJsonString(),
        JsonObject obj => obj["content"]?.GetValue<string>(),
        _ => node.ToJsonString(),
    };
}
