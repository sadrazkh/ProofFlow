using System.Text.Json;
using System.Text.Json.Nodes;
using ProofFlow.TestEngine.Http;
using YamlDotNet.Serialization;

namespace ProofFlow.Infrastructure.Portability.Importers;

/// <summary>
/// Turns an OpenAPI description into a request per operation.
///
/// The document is read as a document rather than through a model of the specification, and that is
/// the decision worth explaining. A typed OpenAPI reader refuses files that are almost valid, and
/// almost valid is what real ones are: a missing <c>info.version</c>, a <c>$ref</c> to a file that
/// was not shipped, a vendor extension where a schema should be. Somebody trying to test an API does
/// not care that its description has a flaw — they care whether the paths came through.
///
/// So this walks the tree, takes what it recognises, and says what it left. YAML is accepted as well
/// as JSON, because most OpenAPI documents in the world are YAML and telling somebody to convert
/// their file first is telling them to go away.
/// </summary>
public static class OpenApiImporter
{
    private static readonly string[] Methods =
        ["get", "post", "put", "patch", "delete", "head", "options"];

    /// <summary>How many operations it will take. A description, not a directory.</summary>
    public const int MaxOperations = 300;

    public static Imported Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Imported.Refused("import.empty");

        var (root, refusal) = Parse(text);
        if (root is null) return Imported.Refused(refusal ?? "import.notJson");

        // Both 3.x and 2.0 name themselves, and neither is worth guessing at: a file with neither
        // key is something else that happens to be YAML.
        var version = root["openapi"]?.GetValue<string>() ?? root["swagger"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version)) return Imported.Refused("import.notOpenApi");

        if (version.StartsWith('2'))
        {
            // Swagger 2 puts the body in a parameter and the host in three separate fields. It is a
            // different format wearing the same name, and half-reading it would produce requests
            // that look right and are not.
            return Imported.Refused("import.swagger2");
        }

        if (root["paths"] is not JsonObject paths) return Imported.Refused("import.noPaths");

        var notes = new List<string>();
        var requests = new List<ImportedRequest>();

        var baseUrl = Server(root, notes);
        var secured = Secured(root);

        foreach (var (path, item) in paths)
        {
            if (item is not JsonObject operations) continue;

            foreach (var method in Methods)
            {
                if (operations[method] is not JsonObject operation) continue;

                if (requests.Count >= MaxOperations)
                {
                    notes.Add("import.note.tooManyOperations");
                    goto done;
                }

                requests.Add(Operation(path, method, operation, secured, notes));
            }
        }

    done:

        if (requests.Count == 0) return Imported.Refused("import.noPaths");

        return new Imported
        {
            SuggestedName = root["info"]?["title"]?.GetValue<string>(),
            Description = root["info"]?["description"]?.GetValue<string>(),
            BaseUrl = baseUrl,
            Requests = requests,
            SecretsToSupply = secured is null ? [] : ["authorization"],
            Notes = [.. notes.Distinct(StringComparer.Ordinal)],
        };
    }

    private static ImportedRequest Operation(
        string path, string method, JsonObject operation, string? secured, List<string> notes)
    {
        var headers = new List<KeyValueEntry>();

        if (secured is not null)
        {
            headers.Add(new KeyValueEntry("Authorization", "{{secrets.authorization}}"));
        }

        // Path parameters become variable references rather than being filled in with an example.
        // {{petId}} in a URL is something the reader can point at a data set; "42" is a test that
        // passes once.
        var url = path;
        var body = (RequestBody?)null;

        if (operation["parameters"] is JsonArray parameters)
        {
            foreach (var parameter in parameters.OfType<JsonObject>())
            {
                var name = parameter["name"]?.GetValue<string>();
                var location = parameter["in"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(name)) continue;

                if (location == "header" && !Credentials.IsCredential(name))
                {
                    headers.Add(new KeyValueEntry(name, $"{{{{{name}}}}}"));
                }
            }
        }

        if (operation["requestBody"]?["content"] is JsonObject content)
        {
            var json = content.FirstOrDefault(pair =>
                pair.Key.Contains("json", StringComparison.OrdinalIgnoreCase));

            if (json.Value is JsonObject media)
            {
                // An example if the document has one, and an empty object if it does not. A body
                // generated from a schema is a guess that looks like a fact.
                var example = media["example"] ?? media["examples"]?.AsObject().FirstOrDefault().Value?["value"];

                body = new RequestBody
                {
                    Kind = BodyKind.Json,
                    Content = example?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                              ?? "{\n}",
                };

                if (example is null) notes.Add("import.note.noExampleBody");
            }
            else if (content.Count > 0)
            {
                notes.Add("import.note.nonJsonBody");
            }
        }

        return new ImportedRequest
        {
            Name = operation["summary"]?.GetValue<string>()
                   ?? operation["operationId"]?.GetValue<string>()
                   ?? $"{method.ToUpperInvariant()} {path}",
            Group = (operation["tags"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(),
            Description = operation["description"]?.GetValue<string>(),
            ExpectedStatus = Success(operation),
            Request = new HttpRequestDefinition
            {
                Method = method.ToUpperInvariant(),
                Url = url,
                Headers = headers,
                Body = body,
            },
        };
    }

    /// <summary>
    /// The status the document calls success.
    ///
    /// Read rather than assumed: an endpoint documented as returning 201 or 204 would otherwise
    /// arrive with a check for 200 on it, which fails the first time it runs and teaches somebody
    /// that imported tests do not work.
    /// </summary>
    private static int Success(JsonObject operation)
    {
        if (operation["responses"] is not JsonObject responses) return 200;

        var codes = responses
            .Select(pair => int.TryParse(pair.Key, out var code) ? code : 0)
            .Where(code => code is >= 200 and < 300)
            .Order()
            .ToList();

        return codes.Count > 0 ? codes[0] : 200;
    }

    private static string? Server(JsonObject root, List<string> notes)
    {
        if (root["servers"] is not JsonArray servers || servers.Count == 0) return null;

        var url = servers[0]?["url"]?.GetValue<string>();

        if (servers.Count > 1) notes.Add("import.note.manyServers");

        // A server URL may itself contain {variables}. Left as they are: they read as ProofFlow
        // references and the environment page is where somebody fills them in.
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    /// <summary>The name of the first security scheme, or null when the document declares none.</summary>
    private static string? Secured(JsonObject root)
    {
        if (root["security"] is JsonArray global && global.Count > 0)
        {
            return (global[0] as JsonObject)?.FirstOrDefault().Key;
        }

        return (root["components"]?["securitySchemes"] as JsonObject)?.FirstOrDefault().Key;
    }

    /// <summary>
    /// Reads JSON, or YAML, into the same tree.
    ///
    /// YAML first when it does not start with a brace, because that is the cheap and reliable
    /// discriminator and neither parser gives a useful error about the other's syntax.
    /// </summary>
    private static (JsonObject? Root, string? Refusal) Parse(string text)
    {
        var trimmed = text.TrimStart();

        if (trimmed.StartsWith('{'))
        {
            try
            {
                return (JsonNode.Parse(text) as JsonObject, null);
            }
            catch (JsonException)
            {
                return (null, "import.notJson");
            }
        }

        try
        {
            var yaml = new DeserializerBuilder().Build().Deserialize<object?>(text);
            if (yaml is null) return (null, "import.empty");

            var json = new SerializerBuilder().JsonCompatible().Build().Serialize(yaml);

            return (JsonNode.Parse(json) as JsonObject, null);
        }
        catch (Exception exception) when (exception is YamlDotNet.Core.YamlException or JsonException)
        {
            return (null, "import.notYaml");
        }
    }
}
