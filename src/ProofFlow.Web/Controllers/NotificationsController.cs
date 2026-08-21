using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure.Identity;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The bell's one verb: everything up to now has been seen.
///
/// A timestamp on the reader, not flags on the rows — the rows belong to the workspace, and what
/// one person has read is a fact about that person.
/// </summary>
[Authorize]
[Route("notifications")]
public sealed class NotificationsController(
    UserManager<ProofFlowUser> users, ICurrentUser me, IClock clock) : Controller
{
    [HttpPost("seen")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Seen(string? back)
    {
        if (me.UserId is { } userId
            && await users.FindByIdAsync(userId.ToString()) is { } user)
        {
            user.NotificationsSeenAt = clock.UtcNow;
            await users.UpdateAsync(user);
        }

        // Back to the page the menu was open on. Only a local path — a Referer-shaped value that
        // leaves this site is not somewhere a POST response should send anybody.
        return Redirect(Url.IsLocalUrl(back) ? back! : "/");
    }
}
