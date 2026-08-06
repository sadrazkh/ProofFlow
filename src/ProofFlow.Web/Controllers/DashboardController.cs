using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

[Authorize]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class DashboardController(
    ProofFlowDbContext db,
    ICurrentUser me,
    Microsoft.Extensions.Localization.IStringLocalizer localizer) : Controller
{
    [HttpGet("/")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Nobody has joined a workspace yet — usually a fresh install where the invitation flow
        // has not run. Send them somewhere that explains it rather than to an empty dashboard.
        if (me.WorkspaceId is null) return Redirect("/account/denied");

        var projects = await db.Projects
            .Where(p => p.ArchivedAt == null)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(6)
            .Select(p => new ProjectCardViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Description = p.Description,
                Accent = p.Accent,
                IsArchived = false,
            })
            .ToListAsync(cancellationToken);

        var totalProjects = await db.Projects.CountAsync(p => p.ArchivedAt == null, cancellationToken);

        ViewData["Title"] = null;
        // Not "ProofFlow" — the wordmark is already six centimetres away in the sidebar, and a
        // breadcrumb that repeats it tells the reader nothing about where they are.
        ViewData["Breadcrumbs"] = new List<(string, string?)> { (localizer["nav.dashboard"].Value, null) };

        return View(new DashboardViewModel
        {
            DisplayName = me.DisplayName,
            Projects = projects,
            TotalProjects = totalProjects,
            // Runs, pass rate and approvals arrive with the phases that produce them. Zero here is
            // the truth on a fresh install, and the empty state is what a reader actually sees.
            TotalRuns = 0,
            PassRatePercent = 0,
            FailingCount = 0,
            AwaitingApproval = 0,
        });
    }
}
