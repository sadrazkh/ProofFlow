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

/// <summary>
/// Asking an authorisation server for a token, so a test can be written against a protected API.
///
/// Its own command rather than a request with the fields filled in, because the difference matters:
/// this one goes to a token endpoint, gets a short-lived credential back, and that credential is
/// the thing everything afterwards carries. A person doing it by hand ends up with a token pasted
/// into a header, expired by the afternoon, and no record of where it came from.
///
/// The grant names are OAuth 2's own — <c>client_credentials</c> and <c>password</c> — because they
/// are what the API's documentation will call them, and inventing friendlier ones would mean
/// translating back and forth at the exact moment somebody is comparing two screens.
/// </summary>
public sealed record TokenRequestCommand
{
    public Guid? EnvironmentId { get; init; }

    /// <summary>«client_credentials» or «password».</summary>
    public string Grant { get; init; } = "client_credentials";

    /// <summary>Where to ask. Relative to the environment's address, or absolute.</summary>
    public string TokenUrl { get; init; } = string.Empty;

    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? Scope { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>
    /// Whether the client credentials go in a Basic header rather than in the form.
    ///
    /// Both are in the specification and servers disagree about which they accept, which is exactly
    /// the kind of thing somebody loses an hour to. It is a switch rather than a guess.
    /// </summary>
    public bool CredentialsInHeader { get; init; }
}

/// <summary>What came back, or why it did not.</summary>
public sealed record TokenResult
{
    public required bool Succeeded { get; init; }

    /// <summary>The token itself. It has to reach the browser: that is what was asked for.</summary>
    public string? AccessToken { get; init; }

    public string? TokenType { get; init; }

    /// <summary>Seconds, as the server said. Null when it did not say.</summary>
    public int? ExpiresIn { get; init; }

    public int StatusCode { get; init; }

    /// <summary>A sentence for the reader, already in their language.</summary>
    public string? Problem { get; init; }

    /// <summary>What the server actually answered, for a problem no sentence covers.</summary>
    public string? Detail { get; init; }
}
