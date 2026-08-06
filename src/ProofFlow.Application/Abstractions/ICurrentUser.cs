using ProofFlow.Domain.Authorization;

namespace ProofFlow.Application.Abstractions;

/// <summary>
/// Who is asking. Implemented by the web host from the request principal, and by the worker as a
/// system identity that belongs to no workspace.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    string DisplayName { get; }

    bool IsAuthenticated { get; }

    /// <summary>The workspace this request is acting inside, if any.</summary>
    Guid? WorkspaceId { get; }

    WorkspaceRole? Role { get; }

    bool Can(Capability capability);
}

/// <summary>
/// The tenant boundary the database enforces.
///
/// Separate from <see cref="ICurrentUser"/> because background work has no user but still has a
/// scope — and because the failure this prevents is specific and quiet: a sweeper or a webhook
/// running with an empty scope reads zero rows, does nothing, and reports success.
/// <see cref="IsSystem"/> exists so that case is stated rather than inferred from a null.
/// </summary>
public interface IWorkspaceScope
{
    Guid? WorkspaceId { get; }

    /// <summary>
    /// True when the caller is platform machinery that legitimately spans workspaces. Query
    /// filters are bypassed only for these callers, and only where the code says so.
    /// </summary>
    bool IsSystem { get; }
}
