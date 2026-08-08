using ProofFlow.Domain.Runners;

namespace ProofFlow.Web.ViewModels;

public sealed class RunnerListViewModel
{
    public required IReadOnlyList<RunnerRowViewModel> Runners { get; init; }

    /// <summary>The code just issued, or null. Shown once and never retrievable again.</summary>
    public string? IssuedCode { get; init; }

    public string? IssuedFor { get; init; }

    public DateTimeOffset? IssuedExpiresAt { get; init; }

    /// <summary>The address to point an agent at, as this reader reached it.</summary>
    public required string PublicUrl { get; init; }
}

public sealed class RunnerRowViewModel
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required RunnerState State { get; init; }

    public string? Hostname { get; init; }

    public string? Version { get; init; }

    public string? TokenPreview { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public DateTimeOffset? EnrolledAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string Tone => ToneFor(State);

    /// <summary>
    /// Which of the status colours a state wears.
    ///
    /// Ready is the only green, and Missing is amber rather than red on purpose: an agent that has
    /// stopped polling is usually a host that was rebooted, not a security event, and colouring it
    /// the same as a revoked credential would teach people to ignore the colour that matters.
    ///
    /// Here rather than in each page, because a runner shown amber on one screen and red on another
    /// is two runners as far as the reader is concerned.
    /// </summary>
    public static string ToneFor(RunnerState state) => state switch
    {
        RunnerState.Ready => "pass",
        RunnerState.Missing => "warn",
        RunnerState.Waiting => "running",
        RunnerState.Expired => "idle",
        _ => "fail",
    };

    /// <summary>True while a code is outstanding and could still be redeemed.</summary>
    public bool AwaitingCode => State == RunnerState.Waiting;

    /// <summary>True when a fresh code would help — nobody enrolled, and the last one ran out.</summary>
    public bool CanReissue => State is RunnerState.Waiting or RunnerState.Expired;

    public bool CanRevoke => State != RunnerState.Revoked;
}
