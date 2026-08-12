using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Ai;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

[Route("settings")]
public sealed class SettingsController(
    ProofFlowDbContext db,
    ICurrentUser me,
    ISecretCipher cipher,
    IAuditLog audit,
    Microsoft.Extensions.Localization.IStringLocalizer localizer) : Controller
{
    /// <summary>
    /// The workspace's own settings. Today that is one thing: which model writes a test.
    ///
    /// Its own page rather than a card on the team page, because a key with somebody's money behind
    /// it is not a thing to find while looking for who joined last week.
    /// </summary>
    [Authorize(Policy = Policies.ManageMembers)]
    [HttpGet("workspace")]
    [ServiceFilter<WorkspaceContextFilter>]
    public async Task<IActionResult> Workspace(CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        var workspace = await db.Workspaces
            .FirstOrDefaultAsync(candidate => candidate.Id == workspaceId, cancellationToken);

        if (workspace is null) return NotFound();

        ViewData["Title"] = localizer["workspaceSettings.title"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["workspaceSettings.title"].Value, null),
        };

        return View(new WorkspaceSettingsViewModel
        {
            Name = workspace.Name,
            AiBaseUrl = workspace.AiBaseUrl,
            AiModel = workspace.AiModel,
            AiKeyPreview = workspace.AiKeyPreview,
            DefaultBaseUrl = ScenarioAuthor.DefaultBaseUrl,
            DefaultModel = ScenarioAuthor.DefaultModel,
        });
    }

    /// <summary>
    /// Saves them. An empty key box leaves the stored key alone.
    ///
    /// Because the page cannot show the key back — it is encrypted and there is no reveal for it —
    /// an empty box has to mean "leave it", or saving the model name would silently delete the key.
    /// Clearing it deliberately is its own button.
    /// </summary>
    [Authorize(Policy = Policies.ManageMembers)]
    [HttpPost("workspace")]
    [ValidateAntiForgeryToken]
    [ServiceFilter<WorkspaceContextFilter>]
    public async Task<IActionResult> Workspace(
        [FromForm] string? aiBaseUrl, [FromForm] string? aiModel, [FromForm] string? aiKey,
        [FromForm] bool forget, CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        var workspace = await db.Workspaces
            .FirstOrDefaultAsync(candidate => candidate.Id == workspaceId, cancellationToken);

        if (workspace is null) return NotFound();

        workspace.AiBaseUrl = string.IsNullOrWhiteSpace(aiBaseUrl) ? null : aiBaseUrl.Trim().TrimEnd('/');
        workspace.AiModel = string.IsNullOrWhiteSpace(aiModel) ? null : aiModel.Trim();

        if (forget)
        {
            workspace.AiKeyCipher = null;
            workspace.AiKeyNonce = null;
            workspace.AiKeyTag = null;
            workspace.AiKeyPreview = null;
        }
        else if (!string.IsNullOrWhiteSpace(aiKey))
        {
            var sealed_ = cipher.Seal(aiKey.Trim());

            workspace.AiKeyCipher = sealed_.Ciphertext;
            workspace.AiKeyNonce = sealed_.Nonce;
            workspace.AiKeyTag = sealed_.Tag;
            workspace.AiKeyVersion = sealed_.KeyVersion;

            // The last four, the same as a secret's preview: enough to tell two keys apart and
            // useless to anybody who did not already have it.
            workspace.AiKeyPreview = aiKey.Trim() is { Length: >= 8 } full ? full[^4..] : null;
        }

        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEntry(forget ? "workspace.aiKeyRemoved" : "workspace.aiChanged", null,
                "Workspace", workspaceId, workspace.Name),
            cancellationToken);

        TempData.Success(localizer["workspaceSettings.saved"]);

        return RedirectToAction(nameof(Workspace));
    }

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
