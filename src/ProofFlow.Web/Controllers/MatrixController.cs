using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The same tests in more than one place, and what differs between them.
///
/// Two pages and two reads. The grid, which is a table of runs and fills in as they finish; and the
/// comparison, which puts one environment's answers against another's using the diff viewer the
/// baseline workbench already uses.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/matrix")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class MatrixController(
    ProofFlowDbContext db,
    MatrixService matrix,
    EnvironmentComparison comparison,
    ICurrentUser me,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    /// <summary>How many batches the list shows.</summary>
    public const int PageSize = 20;

    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        var batches = await db.RunBatches
            .Where(batch => batch.ProjectId == projectId)
            .OrderByDescending(batch => batch.CreatedAt)
            .Take(PageSize)
            .Select(batch => new BatchSummaryRow(
                batch.Id,
                batch.Name,
                batch.Total,
                db.Runs.Count(run => run.BatchId == batch.Id
                                     && run.Status == Domain.Runs.RunStatus.Passed),
                db.Runs.Count(run => run.BatchId == batch.Id
                                     && (run.Status == Domain.Runs.RunStatus.Failed
                                         || run.Status == Domain.Runs.RunStatus.Errored)),
                batch.CreatedAt,
                batch.FinishedAt))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, null);
        ViewData["Title"] = localizer["nav.matrix"].Value;

        return View(new MatrixListViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Batches = batches,
            CanRun = me.Can(Capability.RunTest),
            Scenarios = await db.Scenarios
                .Where(scenario => scenario.ProjectId == projectId && scenario.ArchivedAt == null)
                .OrderBy(scenario => scenario.Name)
                .Select(scenario => new MatrixChoice(scenario.Id, scenario.Name, false))
                .ToListAsync(cancellationToken),
            Environments = await db.Environments
                .Where(environment => environment.ProjectId == projectId)
                .OrderBy(environment => environment.SortOrder)
                .Select(environment => new MatrixChoice(
                    environment.Id, environment.Name, environment.IsProduction))
                .ToListAsync(cancellationToken),
        });
    }

    /// <summary>
    /// Starts a batch and sends the browser to its grid.
    ///
    /// A POST with the token: this reaches somebody else's API once per cell, and a GET that did
    /// that could be triggered by an image tag.
    /// </summary>
    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Start(
        Guid projectId, [FromForm] Guid[] scenarioIds, [FromForm] Guid[] environmentIds,
        [FromForm] string? name, CancellationToken cancellationToken)
    {
        try
        {
            var batch = await matrix.QueueAsync(
                projectId, scenarioIds, environmentIds, name, cancellation: cancellationToken);

            await audit.RecordAsync(
                new AuditEntry("matrix.started", projectId, "RunBatch", batch.Id,
                    $"{scenarioIds.Length}×{environmentIds.Length}"),
                cancellationToken);

            return RedirectToAction(nameof(Grid), new { projectId, id = batch.Id });
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index), new { projectId });
        }
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Grid(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        var batch = await db.RunBatches
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.ProjectId == projectId,
                cancellationToken);

        if (batch is null) return NotFound();

        var project = await db.Projects.FirstAsync(candidate => candidate.Id == projectId, cancellationToken);

        Breadcrumbs(project.Name, projectId, batch.Name ?? localizer["matrix.untitled"].Value);
        ViewData["Title"] = batch.Name ?? localizer["nav.matrix"].Value;

        return View(new MatrixGridViewModel
        {
            ProjectId = projectId,
            BatchId = batch.Id,
            Name = batch.Name,
        });
    }

    /// <summary>The grid itself, read on load and again while it fills.</summary>
    [HttpGet("{id:guid}/state")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> State(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        var owned = await db.RunBatches
            .AnyAsync(batch => batch.Id == id && batch.ProjectId == projectId, cancellationToken);

        if (!owned) return NotFound();

        var grid = await matrix.ReadAsync(id, cancellationToken);
        return grid is null ? NotFound() : Json(grid);
    }

    /// <summary>
    /// One scenario's answers in two environments.
    ///
    /// Computed on request rather than stored. The comparison is a reading of two runs that are
    /// already recorded, and a stored copy would be a third answer that could disagree with both.
    /// </summary>
    [HttpGet("{id:guid}/compare")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Compare(
        Guid projectId, Guid id, Guid scenarioId, Guid left, Guid right,
        CancellationToken cancellationToken)
    {
        var owned = await db.RunBatches
            .AnyAsync(batch => batch.Id == id && batch.ProjectId == projectId, cancellationToken);

        if (!owned) return NotFound();

        var result = await comparison.CompareAsync(id, scenarioId, left, right, cancellationToken);
        return result is null ? NotFound() : Json(result);
    }

    private void Breadcrumbs(string projectName, Guid projectId, string? batchName)
    {
        var crumbs = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (localizer["nav.matrix"].Value, batchName is null ? null : $"/projects/{projectId}/matrix"),
        };

        if (batchName is not null) crumbs.Add((batchName, null));
        ViewData["Breadcrumbs"] = crumbs;
    }
}
