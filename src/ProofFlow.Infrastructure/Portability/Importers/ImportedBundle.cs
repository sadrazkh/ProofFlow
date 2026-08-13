using System.Text.Json;
using ProofFlow.Application.Common;
using ProofFlow.Contracts.Portability;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Infrastructure.Portability.Importers;

/// <summary>
/// Turns what an importer read into a bundle, so a foreign file arrives through the same door as
/// one of this product's own.
///
/// The alternative — a second write path per source — is three places for the collision rules to
/// disagree, three places to forget a workspace id, and three things to keep in step with the
/// domain. Everything foreign becomes a bundle, and the bundle importer is the only thing that
/// writes.
///
/// Each request becomes a scenario of three steps: start, the request, and a check on the status.
/// That last one is the difference between an import that produces a list of addresses and one that
/// produces tests — a scenario with nothing to assert passes whatever the API does.
/// </summary>
public static class ImportedBundle
{
    private static readonly JsonSerializerOptions Pairs = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The environment an import creates when the file names a server.</summary>
    public const string EnvironmentSlug = "imported";

    public static Bundle From(Imported imported, string? projectName = null)
    {
        var name = projectName
            ?? (string.IsNullOrWhiteSpace(imported.SuggestedName) ? "Imported" : imported.SuggestedName);

        var environments = new List<BundleEnvironment>();

        if (!string.IsNullOrWhiteSpace(imported.BaseUrl) || imported.Variables.Count > 0)
        {
            environments.Add(new BundleEnvironment
            {
                Slug = EnvironmentSlug,
                Name = "Imported",
                Kind = "Custom",
                BaseUrl = imported.BaseUrl,
                TimeoutSeconds = 30,
                MaxRedirects = 5,
                MaxResponseKilobytes = 4096,
                Variables =
                [
                    .. imported.Variables.Select(variable =>
                        new BundleVariable
                        {
                            Name = variable.Name,
                            Value = variable.Value,
                            Description = variable.Description,
                        }),
                ],
            });
        }

        var used = new HashSet<string>(StringComparer.Ordinal);

        // Names as well as slugs, because the unique index is on the name.
        var titles = new HashSet<string>(StringComparer.Ordinal);

        return new Bundle
        {
            Project = new BundleProject
            {
                Name = name,
                Slug = Slug.From(name, "project"),
                Description = imported.Description,
            },
            Environments = environments,
            Scenarios =
            [
                .. imported.Requests.Select(request => Scenario(request, environments, used, titles)),
            ],
            SecretsToSupply =
            [
                .. imported.SecretsToSupply.Select(secret => new BundleSecretName
                {
                    Name = secret,
                    Environment = environments.Count > 0 ? EnvironmentSlug : null,
                }),
            ],
        };
    }

    private static BundleScenario Scenario(
        ImportedRequest imported, List<BundleEnvironment> environments,
        HashSet<string> used, HashSet<string> titles)
    {
        var request = imported.Request;

        // The folder or tag goes into the name rather than being dropped. Forty requests called
        // "Get" would otherwise be forty scenarios called "Get".
        var title = string.IsNullOrWhiteSpace(imported.Group)
            ? imported.Name
            : $"{imported.Group} · {imported.Name}";

        // The name, made distinct and made to fit, because the database has an opinion about both.
        //
        // A scenario's name is unique per project — and a real collection has two requests called
        // the same thing in the same folder more often than not, which produced two rows with two
        // different slugs and one name, and an import that died on «an error occurred while saving
        // the entity changes» partway through. Two hundred characters is the column; a folder path
        // five deep passes it without trying.
        title = Fit(title, 200);
        title = Unique(title, titles);

        return new BundleScenario
        {
            Slug = Unique(Slug.From(title, "scenario"), used),
            Name = title,
            Description = imported.Description,
            Environment = environments.Count > 0 ? EnvironmentSlug : null,
            Graph = new GraphDto
            {
                Nodes =
                [
                    new GraphNodeDto
                    {
                        Id = "n1", Key = "core.start", Name = "start", X = 0, Y = 0,
                    },
                    new GraphNodeDto
                    {
                        Id = "n2",
                        Key = "http.request",
                        Name = imported.Name,
                        X = 240,
                        Y = 0,
                        Properties = Properties(request),
                    },
                    new GraphNodeDto
                    {
                        Id = "n3",
                        Key = "assert.status",
                        Name = "status",
                        X = 480,
                        Y = 0,
                        Properties = new Dictionary<string, string?>
                        {
                            ["expected"] = imported.ExpectedStatus.ToString(),
                        },
                    },
                ],
                Edges =
                [
                    new GraphEdgeDto { Id = "e1", FromId = "n1", FromPort = "out", ToId = "n2", ToPort = "in" },
                    new GraphEdgeDto { Id = "e2", FromId = "n2", FromPort = "out", ToId = "n3", ToPort = "in" },

                    // The data edge, without which the check has nothing to look at.
                    new GraphEdgeDto
                    {
                        Id = "e3", FromId = "n2", FromPort = "response", ToId = "n3", ToPort = "response",
                    },
                ],
            },
        };
    }

    private static Dictionary<string, string?> Properties(HttpRequestDefinition request)
    {
        var properties = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["method"] = request.Method,
            ["url"] = request.Url,
        };

        if (request.Headers.Count > 0)
        {
            properties["headers"] = JsonSerializer.Serialize(
                request.Headers.Select(header => new
                {
                    name = header.Name,
                    value = header.Value,
                    enabled = header.Enabled,
                }),
                Pairs);
        }

        if (request.Query.Count > 0)
        {
            properties["query"] = JsonSerializer.Serialize(
                request.Query.Select(entry => new
                {
                    name = entry.Name,
                    value = entry.Value,
                    enabled = entry.Enabled,
                }),
                Pairs);
        }

        if (request.Body is { } body && body.Kind != BodyKind.None)
        {
            properties["bodyKind"] = body.Kind switch
            {
                BodyKind.Json or BodyKind.GraphQl => "json",
                BodyKind.FormUrlEncoded or BodyKind.Multipart => "form",
                BodyKind.Xml => "raw",
                _ => "text",
            };

            properties["body"] = body.Form.Count > 0
                ? string.Join('&', body.Form
                    .Where(field => field.Enabled)
                    .Select(field => $"{Uri.EscapeDataString(field.Name)}={Uri.EscapeDataString(field.Value)}"))
                : body.Content ?? string.Empty;
        }

        // Authentication is not a node property — it belongs to the environment, where a reference
        // to a secret can be resolved once for every step rather than copied onto each of them.
        // What survives the crossing is the header the importer already wrote.

        return properties;
    }

    /// <summary>
    /// Keeps slugs distinct inside one import.
    ///
    /// Two operations called "List" in different folders make the same slug, and the importer skips
    /// anything whose slug it has already seen — so without this the second one silently vanishes.
    /// </summary>
    private static string Unique(string wanted, HashSet<string> used)
    {
        if (used.Add(wanted)) return wanted;

        var next = 2;
        while (!used.Add($"{wanted}-{next}")) next++;

        return $"{wanted}-{next}";
    }

    /// <summary>
    /// Cuts a name down to what the column holds, keeping the end rather than the beginning.
    ///
    /// The end is the part that identifies it: «Orders / Payments / Refunds / Get by id» truncated
    /// from the right leaves four scenarios called «Orders / Payments / Refun…», and truncated from
    /// the left leaves four that can be told apart.
    /// </summary>
    private static string Fit(string text, int limit) =>
        text.Length <= limit ? text : "…" + text[^(limit - 1)..];
}
