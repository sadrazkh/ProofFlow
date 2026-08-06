using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Web.Controllers;

[Route("settings")]
public sealed class SettingsController(ProofFlowDbContext db, ICurrentUser me) : Controller
{
    /// <summary>
    /// Switches the interface language and returns the reader to the page they were reading.
    ///
    /// A GET, deliberately: this changes a display preference, not data, and it has to be usable
    /// from a plain link in a menu — including on the sign-in page, where there is no session to
    /// hold a form token.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("language")]
    public async Task<IActionResult> Language(string culture, string? returnUrl, CancellationToken cancellationToken)
    {
        if (culture is not ("fa" or "en")) return BadRequest();

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
            });

        // Also stored on the account, so the choice follows the person to another browser.
        if (me.UserId is { } userId)
        {
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.PreferredCulture, culture), cancellationToken);
        }

        return Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl!) : Redirect("/");
    }

    /// <summary>
    /// Records the theme the browser has already applied. Fire-and-forget from the client's point
    /// of view — the local setting took effect before this was called, so a failure here costs a
    /// preference on the next device, not the current page.
    /// </summary>
    [Authorize]
    [HttpPost("theme")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Theme([FromBody] ThemeRequest request, CancellationToken cancellationToken)
    {
        if (request.Choice is not ("light" or "dark" or "system")) return BadRequest();
        if (me.UserId is not { } userId) return Unauthorized();

        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ThemeChoice, request.Choice), cancellationToken);

        return NoContent();
    }

    public sealed record ThemeRequest(string Choice);
}
