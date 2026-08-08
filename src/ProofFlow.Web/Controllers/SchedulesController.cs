using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;
using ProofFlow.Infrastructure.Scheduling;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Standing instructions, and the tests that cannot make up their minds.
///
/// One page, because they are read together: somebody looking at what runs every morning is
/// usually there because one of those runs keeps going red for no reason.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/schedules")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class SchedulesController(
    ProofFlowDbContext db,
    ScheduleService schedules,
    FlakyDetector flaky,
    ICurrentUser me,
    IAuditLog audit,
    Dates dates,
    IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        var rows = await db.RunSchedules
            .Where(schedule => schedule.ProjectId == projectId)
            .OrderBy(schedule => schedule.Name)
            .Select(schedule => new ScheduleRow(
                schedule.Id,
                schedule.Name,
                schedule.Cron,
                schedule.TimeZoneId,
                schedule.Enabled,
                schedule.NextRunAt,
                schedule.LastRunAt,
                schedule.LastBatchId,
                schedule.Problem,
                schedule.Scenarios.Count,
                schedule.Environments.Count))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId);
        ViewData["Title"] = localizer["nav.schedules"].Value;

        return View(new ScheduleListViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Schedules = rows,
            Flaky = await flaky.ForProjectAsync(projectId, cancellationToken),
            CanEdit = me.Can(Capability.ManageProject),
            CanQuarantine = me.Can(Capability.EditTest),

            // The reader's own zone, offered first. A schedule written by somebody in Tehran should
            // default to Tehran rather than to UTC, which nobody's morning is measured in.
            ViewerZone = dates.Viewer.Id,
            Presets = CronSchedule.Presets,
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

    [HttpPost("save")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Save(
        Guid projectId, [FromForm] Guid? id, [FromForm] string name, [FromForm] string cron,
        [FromForm] string timeZoneId, [FromForm] Guid[] scenarioIds, [FromForm] Guid[] environmentIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var schedule = await schedules.SaveAsync(
                projectId, id, name, cron, timeZoneId, scenarioIds, environmentIds,
                enabled: true, cancellationToken);

            await audit.RecordAsync(
                new AuditEntry("schedule.saved", projectId, "RunSchedule", schedule.Id, schedule.Name),
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost("{id:guid}/enabled")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Enabled(
        Guid projectId, Guid id, [FromForm] bool enabled, CancellationToken cancellationToken)
    {
        if (await schedules.SetEnabledAsync(projectId, id, enabled, cancellationToken))
        {
            await audit.RecordAsync(
                new AuditEntry(enabled ? "schedule.enabled" : "schedule.disabled",
                    projectId, "RunSchedule", id),
                cancellationToken);
        }

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Delete(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        if (await schedules.DeleteAsync(projectId, id, cancellationToken))
        {
            await audit.RecordAsync(
                new AuditEntry("schedule.deleted", projectId, "RunSchedule", id), cancellationToken);
        }

        return RedirectToAction(nameof(Index), new { projectId });
    }

    /// <summary>
    /// Puts a scenario in or out of quarantine.
    ///
    /// Under <see cref="Capability.EditTest"/> rather than a capability of its own: deciding a test
    /// may not fail the build is a change to what the test means, and whoever can rewrite it can
    /// already do that.
    /// </summary>
    [HttpPost("quarantine")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditTest)]
    public async Task<IActionResult> Quarantine(
        Guid projectId, [FromForm] Guid scenarioId, [FromForm] bool quarantined,
        [FromForm] string? reason, CancellationToken cancellationToken)
    {
        if (await flaky.QuarantineAsync(
                projectId, scenarioId, quarantined, reason, me.UserId, cancellationToken))
        {
            await audit.RecordAsync(
                new AuditEntry(quarantined ? "scenario.quarantined" : "scenario.released",
                    projectId, "TestScenario", scenarioId, reason),
                cancellationToken);
        }

        return RedirectToAction(nameof(Index), new { projectId });
    }

    private void Breadcrumbs(string projectName, Guid projectId)
    {
        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (localizer["nav.schedules"].Value, null),
        };
    }
}
