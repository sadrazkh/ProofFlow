using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Workspaces;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Who is in the workspace, and the invitations that have not been taken up.
///
/// Workspace-scoped rather than project-scoped: membership is of the workspace, and a team page per
/// project would suggest otherwise.
/// </summary>
[Authorize]
[Route("team")]
public sealed class TeamController(
    ProofFlowDbContext db,
    TeamService team,
    UserManager<ProofFlowUser> users,
    SessionCookie session,
    AccountMail mail,
    ICurrentUser me,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        ViewData["Title"] = localizer["nav.team"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)> { (localizer["nav.team"].Value, null) };

        return View(new TeamViewModel
        {
            Members = await team.MembersAsync(workspaceId, cancellationToken),
            Invitations = await db.WorkspaceInvitations
                .Where(invitation => invitation.WorkspaceId == workspaceId
                                     && invitation.AcceptedAt == null
                                     && invitation.RevokedAt == null)
                .OrderByDescending(invitation => invitation.CreatedAt)
                .Select(invitation => new InvitationRow(
                    invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt))
                .ToListAsync(cancellationToken),
            CanManage = me.Can(Capability.ManageMembers),

            // Carried once, from the request that created it. There is no other way to see the
            // link — the token is stored only as a hash.
            IssuedLink = TempData["InviteLink"] as string,
        });
    }

    [HttpPost("invite")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageMembers)]
    public async Task<IActionResult> Invite(
        [FromForm] string email, [FromForm] WorkspaceRole role, CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        try
        {
            var (invitation, token) = await team.InviteAsync(workspaceId, email, role, cancellationToken);

            await audit.RecordAsync(
                new AuditEntry("team.invited", null, "WorkspaceInvitation", invitation.Id,
                    invitation.Email),
                cancellationToken);

            var link = mail.JoinLink(token);

            if (mail.CanSend)
            {
                var workspace = await db.Workspaces
                    .Where(candidate => candidate.Id == workspaceId)
                    .Select(candidate => candidate.Name)
                    .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

                var failure = await mail.InvitationAsync(
                    invitation.Email, workspace, me.DisplayName, link, cancellationToken);

                // Sent, or not — and if not, the link is handed over rather than lost. A relay that
                // was down for ten seconds should not cost somebody an invitation.
                if (failure is null) TempData.Success(localizer["team.invite.sent", invitation.Email]);
                else TempData["InviteLink"] = link;
            }
            else
            {
                // Shown rather than emailed, and the page says so. A link that claims to have been
                // sent when no mail server is configured is the worst of the three options.
                TempData["InviteLink"] = link;
            }
        }
        catch (InvalidOperationException ex)
        {
            TempData.Error(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("invitations/{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageMembers)]
    public async Task<IActionResult> RevokeInvitation(Guid id, CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        if (await team.RevokeInvitationAsync(workspaceId, id, cancellationToken))
        {
            await audit.RecordAsync(
                new AuditEntry("team.inviteRevoked", null, "WorkspaceInvitation", id), cancellationToken);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("members/{userId:guid}/role")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageMembers)]
    public async Task<IActionResult> ChangeRole(
        Guid userId, [FromForm] WorkspaceRole role, CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        // Read before the change, so the log can say who it was about. A row that names only the
        // new role leaves the reader to resolve a GUID against a person who may since have been
        // removed — which is precisely when somebody is reading the audit log.
        var who = await NameAsync(userId, cancellationToken);

        var refusal = await team.ChangeRoleAsync(workspaceId, userId, role, cancellationToken);

        if (refusal is not null) TempData.Error(localizer[refusal]);
        else
        {
            await audit.RecordAsync(
                new AuditEntry("team.roleChanged", null, "WorkspaceMember", userId,
                    $"{who} → {localizer[$"workspace.role_{role}"].Value}"),
                cancellationToken);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("members/{userId:guid}/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageMembers)]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        var who = await NameAsync(userId, cancellationToken);

        var refusal = await team.RemoveAsync(workspaceId, userId, cancellationToken);

        if (refusal is not null) TempData.Error(localizer[refusal]);
        else
        {
            await audit.RecordAsync(
                new AuditEntry("team.removed", null, "WorkspaceMember", userId, who), cancellationToken);
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Who somebody is, for the log. "—" rather than a GUID when the account is gone.</summary>
    private async Task<string> NameAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? "—";

    /// <summary>
    /// Following an invitation link.
    ///
    /// Signed in, deliberately: the invitation says which workspace, and the account says who. A
    /// link that created an account would be a link that creates accounts, which is a different and
    /// much more dangerous thing.
    /// </summary>
    [HttpGet("join")]
    [AllowAnonymous]
    public async Task<IActionResult> Join(string? token, CancellationToken cancellationToken)
    {
        var invitation = await team.FindAsync(token, cancellationToken);

        if (invitation is null)
        {
            TempData.Error(localizer["team.invite.notUsable"]);
            return Redirect("/");
        }

        if (me.UserId is not { } userId)
        {
            // Sent to sign in and back again. The token stays in the URL, which is where it came
            // from — putting it in the session would leave it there after the tab is closed.
            return Redirect($"/account/sign-in?returnUrl={Uri.EscapeDataString($"{Request.Path}{Request.QueryString}")}");
        }

        if (!await team.AcceptAsync(invitation, userId, cancellationToken))
        {
            TempData.Error(localizer["team.invite.notUsable"]);
            return Redirect("/");
        }

        // The workspace claim lives in the cookie, so it has to be reissued — otherwise the new
        // member belongs to a workspace their own session does not know about, and every page they
        // open is empty.
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is not null)
        {
            user.LastWorkspaceId = invitation.WorkspaceId;
            await users.UpdateAsync(user);
            await session.IssueAsync(user);
        }

        await audit.RecordAsync(
            new AuditEntry("team.joined", null, "WorkspaceInvitation", invitation.Id), cancellationToken);

        TempData.Success(localizer["team.invite.accepted"]);
        return Redirect("/");
    }
}
