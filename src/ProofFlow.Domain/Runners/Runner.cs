using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Runners;

/// <summary>
/// A machine somewhere else that runs tests on this workspace's behalf.
///
/// It exists for one reason: a hosted ProofFlow cannot reach an API that lives inside somebody's
/// network, and the answers people reach for otherwise are all worse — a VPN into the test tool, a
/// hole in a firewall, or a set of production credentials pasted into a form on the internet. A
/// runner inverts the direction. Nothing connects inwards; the agent reaches out, asks whether
/// there is work, does it, and reports.
///
/// Which means this row is a credential, and is treated like one. The token is stored only as a
/// hash and shown exactly once. The signing key is encrypted at rest, like a secret, because it is
/// one.
/// </summary>
public class Runner : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// The project it runs for, or null for the whole workspace.
    ///
    /// Narrow by default is the right instinct for a credential, but a runner is a machine on a
    /// network rather than a person, and one agent per project would mean a dozen processes on the
    /// same host. So it is a choice, made when it is enrolled.
    /// </summary>
    public Guid? ProjectId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The one-time code somebody types into the agent, as a hash.
    ///
    /// Short and typable rather than long and random, because it is read off a screen and typed
    /// into a terminal on another machine — and it is safe to be short because it lives for fifteen
    /// minutes, is used once, and buys nothing on its own but the right to become a runner that an
    /// administrator already created.
    /// </summary>
    public string? EnrollmentHash { get; set; }

    public DateTimeOffset? EnrollmentExpiresAt { get; set; }

    /// <summary>When the agent redeemed the code. Null while it is still waiting.</summary>
    public DateTimeOffset? EnrolledAt { get; set; }

    /// <summary>The long-lived credential the agent presents, as a hash. Never stored in the clear.</summary>
    public string? TokenHash { get; set; }

    /// <summary>The first characters of the token, so a person can tell two runners apart.</summary>
    public string TokenPreview { get; set; } = string.Empty;

    /// <summary>
    /// The key every job handed to this runner is signed with, encrypted at rest.
    ///
    /// Per runner rather than per installation: a key shared by every agent is a key whose loss
    /// means re-enrolling all of them, and one that lets any agent verify another's work.
    /// </summary>
    public string? SigningKeyCipher { get; set; }

    public string? SigningKeyNonce { get; set; }

    public string? SigningKeyTag { get; set; }

    /// <summary>Which master key sealed it. Recorded per row, so a rotation does not orphan it.</summary>
    public int SigningKeyVersion { get; set; } = 1;

    /// <summary>What the agent said it is — its host name and version. Reported, so never trusted.</summary>
    public string? Hostname { get; set; }

    public string? Version { get; set; }

    /// <summary>
    /// The last time it asked for work.
    ///
    /// This is the whole of the health model, and deliberately so. An agent that is polling is
    /// working; one that has not polled in five minutes is not, whatever it believes about itself.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// How long since a poll before a runner is called missing.
    ///
    /// Three times the agent's own interval, so one dropped request or a slow network does not turn
    /// a healthy agent amber on somebody's dashboard.
    /// </summary>
    public static readonly TimeSpan Missing = TimeSpan.FromMinutes(3);

    /// <summary>How long an enrollment code is good for. Long enough to walk to another machine.</summary>
    public static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Where this runner stands, as one word.
    ///
    /// Derived rather than stored, because a stored status is a status that goes stale the moment
    /// nothing writes to it — which for "has not been heard from" is precisely the case that
    /// matters.
    /// </summary>
    public RunnerState StateAt(DateTimeOffset now) =>
        RevokedAt is not null ? RunnerState.Revoked
        : EnrolledAt is null && EnrollmentExpiresAt < now ? RunnerState.Expired
        : EnrolledAt is null ? RunnerState.Waiting
        : LastSeenAt is null || now - LastSeenAt > Missing ? RunnerState.Missing
        : RunnerState.Ready;
}

/// <summary>The four states a runner can be in, each a word somebody can act on.</summary>
public enum RunnerState
{
    /// <summary>Created, code issued, nobody has enrolled yet.</summary>
    Waiting = 1,

    /// <summary>The code ran out before anybody used it. Issue another.</summary>
    Expired = 2,

    /// <summary>Enrolled, and asking for work. The only state where anything runs.</summary>
    Ready = 3,

    /// <summary>Enrolled, and has not been heard from. Something on that host is not running.</summary>
    Missing = 4,

    /// <summary>Withdrawn. Its token no longer opens anything.</summary>
    Revoked = 5,
}
