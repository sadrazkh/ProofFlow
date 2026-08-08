using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Runners;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runners;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The machines that run tests on this workspace's behalf.
///
/// Workspace-scoped rather than per project, because a runner is a machine on a network: one agent
/// usually serves everything reachable from where it sits, and a page per project would suggest
/// otherwise.
///
/// The whole controller needs ManageRunner rather than only the writes. Enrolling a machine is
/// administration, and the one thing a reader without that capability would actually want to know —
/// whether the agent behind their environment is up — is answered where they are asking it, on the
/// environment itself.
/// </summary>
[Authorize(Policy = Policies.ManageRunner)]
[Route("runners")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class RunnersController(
    ProofFlowDbContext db,
    RunnerService runners,
    ICurrentUser me,
    IClock clock,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        ViewData["Title"] = localizer["runner.title"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)> { (localizer["runner.title"].Value, null) };

        var now = clock.UtcNow;

        // Only the code travels across the redirect, because only the code is unrecoverable. Which
        // runner it was for is an id, and everything else about that runner — its name, when the
        // code runs out — is read from the row below, so the card and the list cannot disagree.
        var issuedCode = TempData["RunnerCode"] as string;
        var issuedFor = TempData["RunnerId"] as Guid?;

        var rows = await db.Runners
            .Where(runner => runner.WorkspaceId == workspaceId)
            .OrderBy(runner => runner.RevokedAt != null)
            .ThenBy(runner => runner.Name)
            .ToListAsync(cancellationToken);

        var issued = issuedCode is null
            ? null
            : rows.FirstOrDefault(runner => runner.Id == issuedFor);

        return View(new RunnerListViewModel
        {
            Runners =
            [
                .. rows.Select(runner => new RunnerRowViewModel
                {
                    Id = runner.Id,
                    Name = runner.Name,
                    Description = runner.Description,
                    State = runner.StateAt(now),
                    Hostname = runner.Hostname,
                    Version = runner.Version,
                    TokenPreview = runner.TokenPreview,
                    LastSeenAt = runner.LastSeenAt,
                    EnrolledAt = runner.EnrolledAt,
                    ExpiresAt = runner.EnrollmentExpiresAt,
                }),
            ],

            // Shown once. Only a hash is stored, so this is the one moment the code exists
            // anywhere it can be read.
            IssuedCode = issued is null ? null : issuedCode,
            IssuedFor = issued?.Name,
            IssuedExpiresAt = issued?.EnrollmentExpiresAt,

            // The address an agent is pointed at. Read from the request so somebody behind a proxy
            // is shown the address they actually reach, not the one this process happens to bind.
            PublicUrl = $"{Request.Scheme}://{Request.Host}",
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageRunner)]
    public async Task<IActionResult> Create(
        [FromForm] string name, [FromForm] string? description, CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData.Error(localizer["runner.needsName"]);
            return RedirectToAction(nameof(Index));
        }

        var (runner, code) = await runners.CreateAsync(
            workspaceId, null, name, description, cancellationToken);

        await audit.RecordAsync(
            new AuditEntry("runner.created", null, nameof(Runner), runner.Id, runner.Name),
            cancellationToken);

        Issue(runner, code);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/code")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageRunner)]
    public async Task<IActionResult> Reissue(Guid id, CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        var code = await runners.ReissueAsync(workspaceId, id, cancellationToken);

        if (code is null)
        {
            TempData.Error(localizer["runner.cannotReissue"]);
            return RedirectToAction(nameof(Index));
        }

        var runner = await db.Runners.FirstAsync(candidate => candidate.Id == id, cancellationToken);

        await audit.RecordAsync(
            new AuditEntry("runner.codeIssued", null, nameof(Runner), id, runner.Name),
            cancellationToken);

        Issue(runner, code);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageRunner)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        var name = await db.Runners
            .Where(runner => runner.Id == id)
            .Select(runner => runner.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (await runners.RevokeAsync(workspaceId, id, cancellationToken))
        {
            await audit.RecordAsync(
                new AuditEntry("runner.revoked", null, nameof(Runner), id, name), cancellationToken);
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Carries the code to the page that shows it, once.
    ///
    /// Two values, both primitives the TempData serializer handles without ceremony. Nothing else
    /// about the runner travels: the page is about to read the row anyway.
    /// </summary>
    private void Issue(Runner runner, string code)
    {
        TempData["RunnerCode"] = code;
        TempData["RunnerId"] = runner.Id;
    }
}
