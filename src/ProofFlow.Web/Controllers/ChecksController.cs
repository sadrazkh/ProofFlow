using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Capture;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Every endpoint, checked at once.
///
/// One press and one page. The press is on the endpoint list, where somebody has just finished
/// adding paths and is looking at all of them; the page is here, because "what happened when I
/// pressed that" is a different question from "what is this endpoint", and forty answers do not
/// fit on the page of one of them.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/checks")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class ChecksController(
    ProofFlowDbContext db,
    CheckService checks,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    /// <summary>
    /// Check everything in the project.
    ///
    /// No environment picker and no endpoint picker. Each endpoint goes to the environment it is
    /// already tied to — which is exactly what pressing its own button does — so the press asks
    /// nobody a question they would have to open another page to answer.
    /// </summary>
    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Start(Guid projectId, CancellationToken cancellationToken)
    {
        try
        {
            var batch = await checks.QueueAsync(
                projectId, [], [], null, RunTrigger.Person, cancellationToken);

            await audit.RecordAsync(
                new AuditEntry("checks.started", projectId, nameof(Domain.Capture.CheckBatch),
                    batch.Id, batch.Total.ToString()),
                cancellationToken);

            return RedirectToAction(nameof(Batch), new { projectId, id = batch.Id });
        }
        catch (InvalidOperationException ex)
        {
            // The service's own words: "there is nothing here to check yet", or the count that went
            // over the ceiling. Both are things the reader can act on, so neither is swallowed.
            TempData["Error"] = ex.Message;
            return Redirect($"/projects/{projectId}/endpoints");
        }
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Batch(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        var batch = await db.CheckBatches
            .FirstOrDefaultAsync(b => b.Id == id && b.ProjectId == projectId, cancellationToken);

        if (batch is null) return NotFound();

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);

        var title = batch.Name ?? localizer["checks.title"].Value;

        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (project.Name, $"/projects/{projectId}"),
            (localizer["nav.endpoints"].Value, $"/projects/{projectId}/endpoints"),
            (title, null),
        };

        ViewData["Title"] = title;

        return View(new CheckBatchViewModel
        {
            ProjectId = projectId,
            BatchId = batch.Id,
            Name = batch.Name,
        });
    }

    /// <summary>The rows themselves, read on load and again while the checks are carried out.</summary>
    [HttpGet("{id:guid}/state")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> State(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        var owned = await db.CheckBatches
            .AnyAsync(b => b.Id == id && b.ProjectId == projectId, cancellationToken);

        if (!owned) return NotFound();

        var batch = await checks.ReadAsync(id, cancellationToken);
        return batch is null ? NotFound() : Json(batch);
    }
}
