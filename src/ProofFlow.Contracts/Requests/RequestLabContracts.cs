namespace ProofFlow.Contracts.Requests;

/// <summary>
/// What the request builder posts when someone presses Send.
///
/// A wire contract rather than the engine's own <c>HttpRequestDefinition</c>: this one is shaped
/// by what a form can produce and has to survive being versioned, while the engine's is shaped by
/// what an executor needs. Keeping them apart means a field can be added to the builder without
/// changing the thing that runs a saved scenario from six months ago.
/// </summary>
public sealed record SendRequestCommand
{
    public Guid? EnvironmentId { get; init; }
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = string.Empty;
    public IReadOnlyList<KeyValueDto> Query { get; init; } = [];
    public IReadOnlyList<KeyValueDto> Headers { get; init; } = [];
    public string? BodyKind { get; init; }
    public string? Body { get; init; }
}

public sealed record KeyValueDto(string Name, string Value, bool Enabled = true);

/// <summary>
/// The response, as the viewer needs it.
///
/// Everything here has already been through redaction. The builder shows what was actually sent,
/// and "what was actually sent" includes an Authorization header — so the value that reaches the
/// browser is the masked one, not the real token, even though the browser is the one that supplied
/// the reference in the first place.
/// </summary>
public sealed record SendRequestResult
{
    public required bool Succeeded { get; init; }
    public string? ResolvedUrl { get; init; }
    public required string Method { get; init; }
    public int StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyList<KeyValueDto> ResponseHeaders { get; init; } = [];
    public IReadOnlyList<KeyValueDto> SentHeaders { get; init; } = [];
    public string Body { get; init; } = string.Empty;
    public string? ContentType { get; init; }
    public long BodyBytes { get; init; }
    public double DurationMs { get; init; }
    public int Attempts { get; init; }
    public IReadOnlyList<string> RedirectChain { get; init; } = [];

    /// <summary>Set when nothing came back. <see cref="FailureKind"/> chooses which advice to show.</summary>
    public string? FailureKind { get; init; }
    public string? FailureMessage { get; init; }
    public string? FailureDetail { get; init; }

    /// <summary>References in the request that could not be resolved, with the reason for each.</summary>
    public IReadOnlyList<UnresolvedDto> Unresolved { get; init; } = [];
}

public sealed record UnresolvedDto(string Reference, string Explanation);

/// <summary>
/// The names a request may refer to, for the live check in the builder.
///
/// Names only. A secret's value never leaves the server for this purpose — the builder needs to
/// know that <c>apiToken</c> exists so it can stop marking the reference red, and knowing it
/// exists is the whole of what it needs.
/// </summary>
public sealed record VariableNamesDto(
    IReadOnlyList<string> Environment,
    IReadOnlyList<string> Variables,
    IReadOnlyList<string> Secrets);
