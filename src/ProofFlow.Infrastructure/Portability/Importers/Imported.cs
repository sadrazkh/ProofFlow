using ProofFlow.TestEngine.Http;

namespace ProofFlow.Infrastructure.Portability.Importers;

/// <summary>
/// What a foreign file turned into, before any of it is written down.
///
/// One shape for all three sources — a cURL command, an OpenAPI document, a Postman collection —
/// because what they have in common is the only part this product needs: some requests, somewhere
/// to send them, and a list of the things that could not be carried across.
///
/// <see cref="Notes"/> is not decoration. Every importer silently drops something — an OpenAPI
/// security scheme, a Postman pre-request script, a cURL flag about TLS — and an import that says
/// nothing about it produces a suite that quietly does less than the file it came from.
/// </summary>
public sealed record Imported
{
    /// <summary>What to call the project, from the document's own title.</summary>
    public string? SuggestedName { get; init; }

    public string? Description { get; init; }

    /// <summary>The first server, if the document names one. Becomes the environment's base URL.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Variables the document declares, minus anything that looked like a credential.</summary>
    public IReadOnlyList<ImportedVariable> Variables { get; init; } = [];

    public IReadOnlyList<ImportedRequest> Requests { get; init; } = [];

    /// <summary>
    /// Secrets the reader has to create, named after what was found.
    ///
    /// A token in a file somebody pasted is still a token. It is not written into a scenario, not
    /// stored, and not shown back — the reference is kept and the value is dropped on the floor.
    /// </summary>
    public IReadOnlyList<string> SecretsToSupply { get; init; } = [];

    /// <summary>
    /// The values behind those names, for the person who asks for them.
    ///
    /// Held apart from everything else on purpose. It is never written into a scenario, never put
    /// in a bundle, and never carried into an export — a bundle that could hold a token would be a
    /// token in every file anybody commits. It exists for one path: somebody importing their own
    /// collection, on the import page, who ticks the box that says bring them.
    ///
    /// When they do, the values are sealed as secrets exactly like one typed into the secrets page.
    /// When they do not, this is read by nobody and goes out of scope with the request.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecretValues { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What was left behind, as resource keys. Shown to the reader before they confirm.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Why nothing could be read at all, or null.</summary>
    public string? Refusal { get; init; }

    public static Imported Refused(string refusal) => new() { Refusal = refusal };
}

public sealed record ImportedVariable(string Name, string Value, string? Description = null);

public sealed record ImportedRequest
{
    public required string Name { get; init; }

    /// <summary>A folder in Postman, a tag in OpenAPI. Used only to keep the names readable.</summary>
    public string? Group { get; init; }

    public string? Description { get; init; }

    public required HttpRequestDefinition Request { get; init; }

    /// <summary>The status the document says is the success case. 200 when it does not say.</summary>
    public int ExpectedStatus { get; init; } = 200;

    /// <summary>
    /// The shape the document promises the success response has, as a JSON Schema, or null.
    ///
    /// The document's own claim about its API, kept rather than discarded. It is what makes a
    /// «the field is still called total and is still a number» check possible without anybody
    /// having first recorded an answer — and it survives a change to the recorded answer, because
    /// it is a statement about the contract rather than about one response.
    /// </summary>
    public string? ContractJson { get; init; }
}
