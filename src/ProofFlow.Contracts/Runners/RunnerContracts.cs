namespace ProofFlow.Contracts.Runners;

/// <summary>
/// What an agent sends to become a runner.
///
/// The host name and version are reported by the agent about itself, which means they are useful
/// and not trustworthy — they go on a page so somebody can tell two machines apart, and nothing
/// depends on them.
/// </summary>
public sealed record EnrollRequest
{
    public required string Code { get; init; }
    public string? Hostname { get; init; }
    public string? Version { get; init; }
}

/// <summary>
/// The two secrets an agent receives, once, and never again.
///
/// It has to write both to disk. That is a real cost of this design and worth stating: a runner's
/// credentials live on a machine inside somebody's network, which is precisely why the token is
/// scoped to one workspace, why it can be revoked from the interface in one click, and why the
/// signing key is per runner rather than shared.
/// </summary>
public sealed record EnrollResponse
{
    public required Guid RunnerId { get; init; }
    public required string Name { get; init; }
    public required string Token { get; init; }
    public required string SigningKey { get; init; }

    /// <summary>How often to ask for work. Told by the server so it can be changed without a redeploy.</summary>
    public int PollSeconds { get; init; } = 60;
}

/// <summary>
/// One piece of work, and the proof it came from this installation.
///
/// The signature is over <see cref="Payload"/> exactly as it appears here — the agent recomputes it
/// with the key it was given at enrollment and refuses anything that does not match.
///
/// The point is not confidentiality; TLS already handles that. The point is that an agent runs
/// arbitrary HTTP requests against machines inside a private network, which makes "where did this
/// instruction come from" the most important question it ever asks. A signature lets it answer that
/// without trusting whatever proxy, gateway or sidecar happens to sit in the path.
/// </summary>
public sealed record SignedJob
{
    public required Guid JobId { get; init; }

    /// <summary>ISO 8601, so a replayed job from last week can be recognised as one.</summary>
    public required string IssuedAt { get; init; }

    /// <summary>The job itself, as JSON, signed byte for byte as written.</summary>
    public required string Payload { get; init; }

    /// <summary>HMAC-SHA256 over <see cref="Payload"/>, base64.</summary>
    public required string Signature { get; init; }
}

/// <summary>What an agent sends back when it has finished.</summary>
public sealed record JobResult
{
    public required Guid JobId { get; init; }
    public required string Status { get; init; }
    public string? Outcome { get; init; }
    public int Steps { get; init; }
    public int StepsFailed { get; init; }
    public int AssertionsPassed { get; init; }
    public int AssertionsFailed { get; init; }
    public double DurationMs { get; init; }
}
