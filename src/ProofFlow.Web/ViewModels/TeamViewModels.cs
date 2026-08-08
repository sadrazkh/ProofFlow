using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Workspaces;

namespace ProofFlow.Web.ViewModels;

/// <summary>An invitation nobody has taken up yet.</summary>
public sealed record InvitationRow(
    Guid Id,
    string Email,
    WorkspaceRole Role,
    DateTimeOffset ExpiresAt);

public sealed class TeamViewModel
{
    public required IReadOnlyList<TeamMember> Members { get; init; }

    public required IReadOnlyList<InvitationRow> Invitations { get; init; }

    public bool CanManage { get; init; }

    /// <summary>
    /// A link just created, shown once.
    ///
    /// Only the hash is stored, so this is the one moment it exists — and the page says so, rather
    /// than letting somebody close the tab expecting to come back for it.
    /// </summary>
    public string? IssuedLink { get; init; }

    /// <summary>Every role, so the page can show what each one can do rather than just its name.</summary>
    public static readonly WorkspaceRole[] Assignable =
    [
        WorkspaceRole.Admin,
        WorkspaceRole.TestDesigner,
        WorkspaceRole.Reviewer,
        WorkspaceRole.Runner,
        WorkspaceRole.Viewer,
    ];
}

public sealed class ApprovalViewModel
{
    public required Guid ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required ApprovalInboxView Inbox { get; init; }

    public bool CanApprove { get; init; }
}
