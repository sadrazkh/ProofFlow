using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Starting runs, watching them, and reading them afterwards.
///
/// Starting one returns immediately with a queued run: the work happens on the worker, and the
/// browser is sent to a console that exists before anything has been done. The alternative — a
/// request that holds open until the scenario finishes — is a spinner that becomes a timeout on
/// exactly the runs worth watching.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/runs")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class RunsController(
    ProofFlowDbContext db,
    RunService runs,
    IRunQueue queue,
    ICurrentUser me,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    /// <summary>How many runs the history shows at once.</summary>
    public const int PageSize = 30;

    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        var rows = await db.Runs
            .Where(run => run.ProjectId == projectId)
            .OrderByDescending(run => run.CreatedAt)
            .Take(PageSize)
            .Select(run => new RunSummaryRow(
                run.Id,
                run.ScenarioId,
                db.Scenarios.Where(s => s.Id == run.ScenarioId).Select(s => s.Name).FirstOrDefault(),
                db.Environments.Where(e => e.Id == run.EnvironmentId).Select(e => e.Name).FirstOrDefault(),
                run.Status,
                run.Trigger,
                run.DurationMs,
                run.AssertionsPassed,
                run.AssertionsFailed,
                run.CreatedAt))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, null);
        ViewData["Title"] = localizer["nav.runs"].Value;

        return View(new RunListViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Runs = rows,
            CanRun = me.Can(Capability.RunTest),
        });
    }

    /// <summary>
    /// Queues a run and sends the browser to its console.
    ///
    /// A POST with the anti-forgery token, because starting a run reaches somebody else's API and a
    /// GET that did that could be triggered by an image tag.
    /// </summary>
    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Start(
        Guid projectId, Guid scenarioId, Guid? environmentId, string? fromNodeId,
        CancellationToken cancellationToken)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == scenarioId && candidate.ProjectId == projectId, cancellationToken);

        if (scenario is null) return NotFound();

        TestRun run;
        try
        {
            run = await runs.QueueAsync(
                scenarioId, environmentId, RunTrigger.Person, fromNodeId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction("Edit", "Scenarios", new { projectId, id = scenarioId });
        }

        await queue.EnqueueAsync(new QueuedRun(run.Id, run.WorkspaceId), cancellationToken);

        await audit.RecordAsync(new AuditEntry("run.started", projectId, "TestRun", run.Id, scenario.Name),
            cancellationToken);

        return RedirectToAction(nameof(Console), new { projectId, id = run.Id });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Console(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == id && candidate.ProjectId == projectId, cancellationToken);

        if (run is null) return NotFound();

        var project = await db.Projects
            .FirstAsync(candidate => candidate.Id == projectId, cancellationToken);

        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(candidate => candidate.Id == run.ScenarioId, cancellationToken);

        Breadcrumbs(project.Name, projectId, scenario?.Name);
        ViewData["Title"] = scenario?.Name ?? localizer["run.title"].Value;

        return View(new RunConsoleViewModel
        {
            ProjectId = projectId,
            RunId = run.Id,
            ScenarioId = run.ScenarioId,
            ScenarioName = scenario?.Name ?? string.Empty,
            Status = run.Status,
            CanCancel = me.Can(Capability.RunTest),

            // Read out of the run's own snapshot rather than the scenario as it is now. The graph
            // may have been edited since; what this run began at is a fact about this run.
            StartedFrom = StartedFrom(run),
        });
    }

    /// <summary>The name of the step a partial run began at, from the graph it was queued with.</summary>
    private static string? StartedFrom(TestRun run)
    {
        if (run.StartNodeId is not { Length: > 0 } from || run.DefinitionJson is null) return null;

        try
        {
            return JsonDocument.Parse(run.DefinitionJson).RootElement
                .GetProperty("nodes").EnumerateArray()
                .Where(node => node.TryGetProperty("id", out var id) && id.GetString() == from)
                .Select(node => node.TryGetProperty("name", out var name) ? name.GetString() : null)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            // The snapshot is unreadable, which the console will say about the graph anyway. Not a
            // reason to fail the whole page over a label.
            return null;
        }
    }

    /// <summary>
    /// Everything the console needs to draw itself, in one call.
    ///
    /// The graph, what has happened so far, and where the run is. One call rather than four,
    /// because the console has to be able to open on a run that finished last month and on one that
    /// started two seconds ago, and four requests give four different moments.
    /// </summary>
    [HttpGet("{id:guid}/state")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> State(
        Guid projectId, Guid id, long since, CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == id && candidate.ProjectId == projectId, cancellationToken);

        if (run is null) return NotFound();

        var nodes = await db.NodeRuns
            .Where(node => node.TestRunId == id)
            .OrderBy(node => node.SortOrder)
            .Select(node => new NodeRunRow(
                node.Id, node.NodeId, node.NodeName, node.NodeKey, node.Status.ToString(),
                node.Iteration, node.Attempt, node.DurationMs, node.TakenPort,
                node.FailureMessage, node.StartedAt))
            .ToListAsync(cancellationToken);

        var assertions = await db.AssertionResults
            .Where(result => db.NodeRuns
                .Any(node => node.Id == result.NodeRunId && node.TestRunId == id))
            .Select(result => new AssertionRow(
                result.NodeRunId, result.Description, result.Passed, result.Soft,
                result.Expected, result.Actual, result.Target))
            .ToListAsync(cancellationToken);

        // Paged from a sequence rather than an offset: lines keep arriving while the console reads,
        // and an offset would skip or repeat exactly the ones that arrived in between.
        var events = await db.RunEvents
            .Where(entry => entry.TestRunId == id && entry.Sequence > since)
            .OrderBy(entry => entry.Sequence)
            .Take(2000)
            .Select(entry => new RunEventRow(
                entry.Sequence, entry.Level.ToString(), entry.Message, entry.NodeId, entry.NodeName,
                entry.At, entry.DataJson))
            .ToListAsync(cancellationToken);

        return Json(new
        {
            status = run.Status.ToString(),
            outcome = run.Outcome,
            startedAt = run.StartedAt,
            finishedAt = run.FinishedAt,
            totals = new
            {
                steps = run.StepsRun,
                stepsFailed = run.StepsFailed,
                assertionsPassed = run.AssertionsPassed,
                assertionsFailed = run.AssertionsFailed,
                durationMs = run.DurationMs,
            },
            graph = run.DefinitionJson,
            nodes,
            assertions,
            events,
        });
    }

    /// <summary>
    /// Asks a run to stop.
    ///
    /// Asks rather than kills. The engine stops at its next step and its cleanup blocks still run,
    /// because a scenario that created twenty records and was stopped halfway has twenty records to
    /// remove — and the moment somebody presses stop is the moment that matters most.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Cancel(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == id && candidate.ProjectId == projectId, cancellationToken);

        if (run is null) return NotFound();

        if (run.Status is not (RunStatus.Queued or RunStatus.Running))
        {
            return Json(new { stopped = false, reason = localizer["run.alreadyFinished"].Value });
        }

        run.CancelledByUserId = me.UserId;
        await db.SaveChangesAsync(cancellationToken);

        var reached = queue.Cancel(id);

        await audit.RecordAsync(new AuditEntry("run.cancelled", projectId, "TestRun", id), cancellationToken);

        return Json(new { stopped = reached });
    }

    private void Breadcrumbs(string projectName, Guid projectId, string? runName)
    {
        var crumbs = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (localizer["nav.runs"].Value, runName is null ? null : $"/projects/{projectId}/runs"),
        };

        if (runName is not null) crumbs.Add((runName, null));
        ViewData["Breadcrumbs"] = crumbs;
    }
}
