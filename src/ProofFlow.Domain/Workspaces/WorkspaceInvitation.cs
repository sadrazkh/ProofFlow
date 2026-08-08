using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Workspaces;

/// <summary>
/// An offer to join, addressed to an email rather than to an account.
///
/// Addressed to an email because the person being invited usually does not have an account yet —
/// that is what being invited means. A membership row cannot be created for somebody who is not a
/// user, so the offer lives here until they accept, and only then does it become a membership.
///
/// The token is stored as a hash, the same rule as an API key: a link that can be recovered from
/// the database is a link somebody can walk in through.
/// </summary>
public class WorkspaceInvitation : Entity, IWorkspaceOwned
{
    /// <summary>How long an unaccepted invitation stays good.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    /// <summary>Stored lower-cased, because that is how it will be matched at sign-up.</summary>
    public required string Email { get; set; }

    public WorkspaceRole Role { get; set; } = WorkspaceRole.Viewer;

    /// <summary>SHA-256 of the token in the link. The token itself is never stored.</summary>
    public required string Hash { get; set; }

    public Guid? InvitedByUserId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>Who accepted it. Not necessarily who it was addressed to — worth recording.</summary>
    public Guid? AcceptedByUserId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsOpen(DateTimeOffset now) =>
        AcceptedAt is null && RevokedAt is null && ExpiresAt > now;
}
