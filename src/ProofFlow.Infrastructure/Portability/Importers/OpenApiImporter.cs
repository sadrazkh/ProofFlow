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

                requests.Add(Operation(path, method, operation, root, secured, notes));
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
        string path, string method, JsonObject operation, JsonObject root, string? secured,
        List<string> notes)
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
            ContractJson = Contract(operation, root, notes),
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
    /// The success response's schema, with local <c>$ref</c>s resolved into it.
    ///
    /// Dereferenced rather than kept as references because the schema is stored on the endpoint and
    /// the document it came from is not: a <c>$ref</c> to <c>#/components/schemas/Product</c> would
    /// point at nothing the moment the file is closed.
    ///
    /// One 3.0-ism is translated — <c>nullable: true</c>, which JSON Schema spells as a type union
    /// — because it is the difference between «this field may legitimately be null» and a contract
    /// that fails on every row where it is.
    /// </summary>
    private static string? Contract(JsonObject operation, JsonObject root, List<string> notes)
    {
        if (operation["responses"] is not JsonObject responses) return null;

        var success = responses
            .Where(pair => int.TryParse(pair.Key, out var code) && code is >= 200 and < 300)
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .FirstOrDefault();

        if (success?["content"] is not JsonObject content) return null;

        var json = content.FirstOrDefault(pair =>
            pair.Key.Contains("json", StringComparison.OrdinalIgnoreCase));

        if (json.Value?["schema"] is not JsonNode schema) return null;

        var truncated = false;
        var resolved = Resolve(schema, root, [], depth: 0, ref truncated);

        if (truncated) notes.Add("import.note.deepSchema");

        return resolved?.ToJsonString();
    }

    /// <summary>How far a schema may nest before it is more likely a cycle than a shape.</summary>
    private const int MaxSchemaDepth = 12;

    private static JsonNode? Resolve(
        JsonNode? node, JsonObject root, HashSet<string> seen, int depth, ref bool truncated)
    {
        if (node is null) return null;

        if (depth > MaxSchemaDepth)
        {
            truncated = true;
            return null;
        }

        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (var item in array)
            {
                copy.Add(Resolve(item, root, seen, depth + 1, ref truncated));
            }
            return copy;
        }

        if (node is not JsonObject holder) return node.DeepClone();

        if (holder["$ref"]?.GetValue<string>() is { } reference)
        {
            // A type that contains itself — a comment with replies, a category with children — is
            // ordinary and must not be followed for ever. Cutting it leaves the rest checkable.
            if (!seen.Add(reference))
            {
                truncated = true;
                return null;
            }

            var target = Follow(reference, root);
            var expanded = Resolve(target, root, seen, depth + 1, ref truncated);

            seen.Remove(reference);
            return expanded;
        }

        var result = new JsonObject();

        foreach (var pair in holder)
        {
            // 3.0's nullable becomes a type union, which is what every JSON Schema validator reads.
            if (pair.Key == "nullable")
            {
                if (pair.Value?.GetValue<bool>() == true
                    && holder["type"]?.GetValue<string>() is { } only)
                {
                    result["type"] = new JsonArray(only, "null");
                }
                continue;
            }

            if (pair.Key == "type" && result.ContainsKey("type")) continue;

            // Vocabulary a validator would choke on or ignore, and that says nothing about shape.
            if (pair.Key is "discriminator" or "xml" or "externalDocs" or "example" or "deprecated")
            {
                continue;
            }

            result[pair.Key] = Resolve(pair.Value, root, seen, depth + 1, ref truncated);
        }

        // The union may have been written before «type» was reached, in which case the loop above
        // skipped it; if it was reached first, the nullable branch has overwritten it. Either way
        // what is left is one type declaration.
        if (holder["nullable"]?.GetValue<bool>() == true
            && holder["type"]?.GetValue<string>() is { } named
            && result["type"] is not JsonArray)
        {
            result["type"] = new JsonArray(named, "null");
        }

        return result;
    }

    /// <summary>Walks a local <c>#/a/b/c</c> pointer. External documents are not fetched.</summary>
    private static JsonNode? Follow(string reference, JsonObject root)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;

        JsonNode? at = root;

        foreach (var segment in reference[2..].Split('/'))
        {
            var name = segment.Replace("~1", "/").Replace("~0", "~");
            at = at is JsonObject step ? step[name] : null;
            if (at is null) return null;
        }

        return at;
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
