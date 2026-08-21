using System.Text.Json;
using ProofFlow.Application.Common;
using ProofFlow.Contracts.Portability;
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
            Baselines =
            [
                .. imported.Requests.Select(request => Endpoint(request, environments, used, titles)),
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

    private static BundleBaseline Endpoint(
        ImportedRequest imported, List<BundleEnvironment> environments,
        HashSet<string> used, HashSet<string> titles)
    {
        // The folder or tag goes into the name rather than being dropped. Forty requests called
        // "Get" would otherwise be forty endpoints called "Get".
        var title = string.IsNullOrWhiteSpace(imported.Group)
            ? imported.Name
            : $"{imported.Group} · {imported.Name}";

        // The name, made distinct and made to fit, because the database has an opinion about both.
        //
        // An endpoint's name is unique per project — and a real collection has two requests called
        // the same thing in the same folder more often than not, which produced two rows with two
        // different slugs and one name, and an import that died on «an error occurred while saving
        // the entity changes» partway through. Two hundred characters is the column; a folder path
        // five deep passes it without trying.
        title = Fit(title, 200);
        title = Unique(title, titles);

        return new BundleBaseline
        {
            Slug = Unique(Slug.From(title, "baseline"), used),
            Name = title,
            Description = Describe(imported),
            Environment = environments.Count > 0 ? EnvironmentSlug : null,

            // The request as the engine reads it, serialised the way the endpoint page expects to
            // find it — the same shape the request lab writes when somebody keeps a response.
            RequestJson = JsonSerializer.Serialize(imported.Request, Pairs),

            // The document's own promise about the answer, kept beside the request that asks
            // for it. Null for every importer but OpenAPI, which is the only one that has one.
            ContractJson = imported.ContractJson,

            // No approved answer. Nothing has been sent yet, so there is nothing to approve, and
            // an import that invented one would be an import that decided what correct looks like
            // on the strength of a file somebody exported from Postman.
            Approved = null,
        };
    }

    /// <summary>
    /// The description, with the document's success status appended when it is not the obvious one.
    ///
    /// An OpenAPI document that says an operation answers 204 is telling somebody something they
    /// will otherwise find out by recording a 204 and wondering whether it was meant. There is
    /// nowhere better to put it: an endpoint's expectation is the answer that was approved, and
    /// nothing has been sent yet, so this is a note to the person who will send it.
    /// </summary>
    private static string? Describe(ImportedRequest imported)
    {
        if (imported.ExpectedStatus is 200 or 0) return imported.Description;

        var note = $"The document says this answers {imported.ExpectedStatus}.";

        return string.IsNullOrWhiteSpace(imported.Description)
            ? note
            : string.Join("\n\n", imported.Description, note);
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
