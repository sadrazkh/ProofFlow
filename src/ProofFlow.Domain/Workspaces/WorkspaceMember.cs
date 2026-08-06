using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Workspaces;

/// <summary>
/// One person's membership of one workspace, and the role that membership carries.
///
/// The user id is a bare <see cref="Guid"/> with no navigation property on purpose: the identity
/// tables belong to ASP.NET Identity, which lives in Infrastructure. A navigation here would drag
/// the whole identity stack into the domain.
/// </summary>
public class WorkspaceMember : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public Guid UserId { get; set; }

    public WorkspaceRole Role { get; set; } = WorkspaceRole.Viewer;

    /// <summary>Set when the row was created by an invitation the person has not yet accepted.</summary>
    public DateTimeOffset? InvitedAt { get; set; }

    public DateTimeOffset? JoinedAt { get; set; }
}
