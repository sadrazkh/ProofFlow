using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Runs;
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

                // Counted here rather than left at zero. A card that says "0 environments" about a
                // project with four is the false-zero the design contract exists to forbid, and it
                // is the first thing anybody reads about a project.
                EnvironmentCount = db.Environments.Count(e => e.ProjectId == p.Id),
                ScenarioCount = db.Scenarios.Count(s => s.ProjectId == p.Id),
                BaselineCount = db.Baselines.Count(b => b.ProjectId == p.Id),
                IsArchived = false,
            })
            .ToListAsync(cancellationToken);

        var totalProjects = await db.Projects.CountAsync(p => p.ArchivedAt == null, cancellationToken);

        // Over the last fortnight rather than for ever. A pass rate across a year of history barely
        // moves, which makes it a number nobody looks at twice; what a reader wants to know on
        // opening this page is whether things are going wrong now.
        var since = DateTimeOffset.UtcNow.AddDays(-14);

        var verdicts = await db.Runs
            .Where(run => run.CreatedAt >= since
                          && (run.Status == RunStatus.Passed || run.Status == RunStatus.Failed
                              || run.Status == RunStatus.Errored))
            .GroupBy(run => run.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var totalRuns = verdicts.Sum(row => row.Count);
        var passed = verdicts.Where(row => row.Status == RunStatus.Passed).Sum(row => row.Count);

        var awaiting = await db.BaselineVersions
            .CountAsync(version => version.Status == BaselineStatus.PendingApproval, cancellationToken);

        // Across every project, newest first. The panel is on the dashboard rather than in a
        // project because the question it answers — did anything break while I was away — is not a
        // question about one project.
        var recent = await db.Runs
            .OrderByDescending(run => run.CreatedAt)
            .Take(6)
            .Select(run => new RecentRunRow(
                run.Id,
                run.ProjectId,
                db.Projects.Where(p => p.Id == run.ProjectId).Select(p => p.Name).FirstOrDefault()!,
                db.Scenarios.Where(s => s.Id == run.ScenarioId).Select(s => s.Name).FirstOrDefault()!,
                db.Environments.Where(e => e.Id == run.EnvironmentId).Select(e => e.Name).FirstOrDefault(),
                run.Status,
                run.CreatedAt))
            .ToListAsync(cancellationToken);

        // The checklist's four facts. Cheap Any() queries — the ticks are the data, so there is no
        // per-step state to keep and nothing that can disagree with what the workspace contains.
        var hasEnvironment = await db.Environments.AnyAsync(cancellationToken);
        var hasEndpoint = await db.Baselines.AnyAsync(b => b.ApprovedVersionId != null, cancellationToken);
        var hasRun = await db.Runs.AnyAsync(cancellationToken);
        var first = projects.FirstOrDefault()?.Id;

        // And, once the checklist has gone, at most one nudge. Ordered by how much the reader is
        // currently missing out on: tests nobody runs are worth more than tests nobody hears about.
        string? hint = null;
        string? hintHref = null;

        if (hasRun && first is { } project)
        {
            if (!await db.RunSchedules.AnyAsync(schedule => schedule.Enabled, cancellationToken))
            {
                hint = "schedule";
                hintHref = $"/projects/{project}/schedules";
            }
            else if (!await db.Projects.AnyAsync(
                         p => p.NotifyByEmail || p.WebhookUrl != null, cancellationToken))
            {
                hint = "notify";
                hintHref = $"/projects/{project}/settings";
            }
        }

        ViewData["Title"] = null;
        // Not "ProofFlow" — the wordmark is already six centimetres away in the sidebar, and a
        // breadcrumb that repeats it tells the reader nothing about where they are.
        ViewData["Breadcrumbs"] = new List<(string, string?)> { (localizer["nav.dashboard"].Value, null) };

        return View(new DashboardViewModel
        {
            DisplayName = me.DisplayName,
            Projects = projects,
            TotalProjects = totalProjects,
            TotalRuns = totalRuns,

            // Rounded down. 99% on a fortnight with one failure in it is a rounding that hides the
            // failure, and this tile exists to not do that.
            PassRatePercent = totalRuns == 0 ? 0 : (int)Math.Floor(passed * 100.0 / totalRuns),
            FailingCount = totalRuns - passed,
            AwaitingApproval = awaiting,

            HasEnvironment = hasEnvironment,
            HasEndpoint = hasEndpoint,
            HasRun = hasRun,
            FirstProjectId = first,
            RecentRuns = recent,
            Hint = hint,
            HintHref = hintHref,
        });
    }
}
