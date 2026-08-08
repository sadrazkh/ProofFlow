using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Writing the sign-in cookie, including the workspace and role claims.
///
/// Its own service because more than one thing needs to write it. Signing in is the obvious one;
/// accepting an invitation is the other, and a person who has just joined a workspace and lands on
/// a page that shows them nothing — because their cookie still says they belong to no workspace —
/// would reasonably conclude the product is broken.
///
/// Claims rather than a per-request lookup: <c>Can()</c> is called dozens of times rendering one
/// page. The cost is that a role change lands on the next sign-in or workspace switch, which is
/// stated in <see cref="HttpCurrentUser"/> and is why this exists as something callable.
/// </summary>
public sealed class SessionCookie(
    ProofFlowDbContext db,
    SignInManager<ProofFlowUser> signIn,
    IHttpContextAccessor accessor)
{
    public async Task IssueAsync(ProofFlowUser user, bool rememberMe = false)
    {
        var context = accessor.HttpContext
            ?? throw new InvalidOperationException("There is no request to sign in.");

        var workspaceId = user.LastWorkspaceId;

        var membership = await db.WorkspaceMembers
            .IgnoreQueryFilters() // No workspace is established yet — that is what this reads.
            .Where(member => member.UserId == user.Id)
            .OrderByDescending(member => member.WorkspaceId == workspaceId)
            .ThenBy(member => member.CreatedAt)
            .FirstOrDefaultAsync();

        var identity = await signIn.CreateUserPrincipalAsync(user);

        if (identity.Identity is ClaimsIdentity claims)
        {
            claims.AddClaim(new Claim("pf:name", user.DisplayName ?? user.Email ?? "unknown"));

            if (membership is not null)
            {
                claims.AddClaim(new Claim(HttpCurrentUser.WorkspaceClaim,
                    membership.WorkspaceId.ToString()));
                claims.AddClaim(new Claim(HttpCurrentUser.RoleClaim, membership.Role.ToString()));
            }
        }

        await context.SignInAsync(IdentityConstants.ApplicationScheme, identity,
            new AuthenticationProperties { IsPersistent = rememberMe });

        // SignInAsync writes the cookie for the *next* request; it does not change who this one is
        // running as. Without this line, anything after sign-in still sees an anonymous principal —
        // which is why the audit entry for signing in used to be dropped for having no workspace,
        // and why the tenant filter would have returned nothing to any query that followed.
        context.User = identity;
    }
}
