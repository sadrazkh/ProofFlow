using System.Security.Claims;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Who is asking, read from the request.
///
/// The workspace and role come from claims written at sign-in and refreshed when the user switches
/// workspace — not from a database lookup per call. That makes <see cref="Can"/> free, which
/// matters because it is called dozens of times while rendering one page. The trade is that a role
/// change takes effect on the member's next sign-in or workspace switch; <c>WorkspaceSwitcher</c>
/// re-issues the cookie, so the window is a switch, not a session.
/// </summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string WorkspaceClaim = "pf:workspace";
    public const string RoleClaim = "pf:role";

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string DisplayName =>
        Principal?.FindFirstValue("pf:name")
        ?? Principal?.FindFirstValue(ClaimTypes.Name)
        ?? Principal?.FindFirstValue(ClaimTypes.Email)
        ?? "unknown";

    public Guid? WorkspaceId =>
        Guid.TryParse(Principal?.FindFirstValue(WorkspaceClaim), out var id) ? id : null;

    public WorkspaceRole? Role =>
        Enum.TryParse<WorkspaceRole>(Principal?.FindFirstValue(RoleClaim), out var role) ? role : null;

    public bool Can(Capability capability) =>
        Role is { } role && RoleCapabilities.Allows(role, capability);
}

/// <summary>
/// The tenant boundary for a request: whatever workspace the signed-in user is currently inside.
///
/// Never a system scope. A request cannot ask to see across workspaces, however privileged the
/// person making it — crossing that line is a job for background machinery, which runs with
/// <see cref="SystemWorkspaceScope"/> and is not reachable from a URL.
/// </summary>
public sealed class HttpWorkspaceScope(ICurrentUser currentUser) : IWorkspaceScope
{
    public Guid? WorkspaceId => currentUser.WorkspaceId;

    public bool IsSystem => false;
}
