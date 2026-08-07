using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The guided path from an endpoint to a regression test.
///
/// One page hosting one island. The wizard's state lives in the browser rather than here, because
/// a half-finished wizard is not something the project should own — it would fill the project with
/// abandoned attempts nobody meant to keep.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/wizard")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class WizardController(
    ProofFlowDbContext db, ICurrentUser me, IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        ViewData["Title"] = localizer["wizard.title"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (project.Name, $"/projects/{projectId}"),
            (localizer["wizard.title"].Value, null),
        };

        return View(new WizardViewModel
        {
            ProjectId = projectId,
            Environments = await db.Environments
                .Where(e => e.ProjectId == projectId)
                .OrderBy(e => e.SortOrder)
                .Select(e => new WizardEnvironment(e.Id, e.Name, e.BaseUrl, e.IsProduction))
                .ToListAsync(cancellationToken),
            Baselines = await db.Baselines
                .Where(b => b.ProjectId == projectId && b.ArchivedAt == null)
                .OrderBy(b => b.Name)
                .Select(b => new WizardBaseline(b.Id, b.Name))
                .ToListAsync(cancellationToken),
            DataSets = await db.DataSets
                .Where(d => d.ProjectId == projectId && d.ArchivedAt == null)
                .OrderBy(d => d.Name)
                .Select(d => new WizardDataSet(
                    d.Id,
                    d.Name,
                    d.CurrentVersionId,
                    db.DataSetVersions.Where(v => v.Id == d.CurrentVersionId)
                        .Select(v => v.RowCount).FirstOrDefault()))
                .ToListAsync(cancellationToken),
            CanRun = me.Can(Capability.RunTest),
            CanManage = me.Can(Capability.ManageDataSet),
        });
    }
}
