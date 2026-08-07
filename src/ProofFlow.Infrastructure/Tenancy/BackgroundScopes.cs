using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;

namespace ProofFlow.Infrastructure.Tenancy;

/// <summary>
/// The scope background work runs under: no workspace, every workspace.
///
/// Used by the scheduler, the runner and the retention sweeper. Registered only in hosts that have
/// no requests, or resolved explicitly for one scoped operation — never as the web application's
/// default, because a request must not be able to reach across tenants however privileged the
/// person making it is.
/// </summary>
public sealed class SystemWorkspaceScope : IWorkspaceScope
{
    public Guid? WorkspaceId => null;

    public bool IsSystem => true;
}

/// <summary>
/// Pinned to one workspace, for background work that has already decided which tenant it acts for
/// — a scheduled run belongs to exactly one, and should not be able to see the others while it
/// executes.
/// </summary>
public sealed class FixedWorkspaceScope(Guid workspaceId) : IWorkspaceScope
{
    public Guid? WorkspaceId { get; } = workspaceId;

    public bool IsSystem => false;
}

/// <summary>
/// The workspace a piece of background work is acting for.
///
/// Scoped, and set once when the scope is made — before anything that reads it is resolved. It
/// exists because the tenant boundary is a query filter and the worker has no request to take a
/// tenant from: without this, a background run reads zero rows, does nothing, and reports success.
/// </summary>
public sealed class BackgroundWorkspace
{
    public Guid? WorkspaceId { get; private set; }

    public void ActFor(Guid workspaceId) => WorkspaceId = workspaceId;
}

/// <summary>
/// The identity background work acts as. Holds every capability, because it is not a person and
/// there is nothing to withhold from it — but it is never authenticated, so the audit trail
/// records it as "system" rather than attributing its actions to whoever last signed in.
/// </summary>
public sealed class SystemUser(Guid? workspaceId = null) : ICurrentUser
{
    public Guid? UserId => null;

    public string DisplayName => "system";

    public bool IsAuthenticated => false;

    public Guid? WorkspaceId { get; } = workspaceId;

    public WorkspaceRole? Role => WorkspaceRole.Owner;

    public bool Can(Capability capability) => true;
}
