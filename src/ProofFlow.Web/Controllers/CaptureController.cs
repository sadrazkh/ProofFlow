using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Baselines;
using ProofFlow.Contracts.Capture;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Capture;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Capture;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Sweeps and the queue they produce.
///
/// The sweep runs inside the request for now, which is honest about what exists rather than
/// pretending at a job queue: the limit on the start command is what keeps that reasonable, and
/// moving it to the background worker is Phase H's job, at which point this controller enqueues
/// instead of running.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/captures")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class CaptureController(
    ProofFlowDbContext db,
    CaptureService capture,
    ICurrentUser me,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var sessions = await db.CaptureSessions
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.StartedAt)
            .Take(50)
            .Select(s => new CaptureSummary(
                s.Id,
                db.Baselines.Where(b => b.Id == s.BaselineId).Select(b => b.Name).FirstOrDefault() ?? "",
                db.DataSetVersions
                    .Where(v => v.Id == s.DataSetVersionId)
                    .Select(v => v.DataSet!.Name)
                    .FirstOrDefault() ?? "",
                db.DataSetVersions
                    .Where(v => v.Id == s.DataSetVersionId)
                    .Select(v => v.Number)
                    .FirstOrDefault(),
                s.Mode,
                s.Status,
                s.TotalRows,
                s.Differing,
                s.Failed,
                db.CaptureSamples.Count(sample =>
                    sample.CaptureSessionId == s.Id && sample.Status == SampleStatus.Captured),
                s.StartedAt))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, null);
        ViewData["Title"] = localizer["nav.captures"].Value;

        return View(new CaptureListViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Sessions = sessions,
            CanRun = me.Can(Capability.RunTest),
            HasBaselines = await db.Baselines.AnyAsync(b => b.ProjectId == projectId, cancellationToken),
            HasDataSets = await db.DataSets.AnyAsync(d => d.ProjectId == projectId, cancellationToken),
        });
    }

    [HttpGet("{sessionId:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Details(
        Guid projectId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.CaptureSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.ProjectId == projectId, cancellationToken);
        if (session is null) return NotFound();

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        var baseline = await db.Baselines.FirstAsync(b => b.Id == session.BaselineId, cancellationToken);

        var version = await db.DataSetVersions
            .Where(v => v.Id == session.DataSetVersionId)
            .Select(v => new { v.Number, Name = v.DataSet!.Name })
            .FirstAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, baseline.Name);
        ViewData["Title"] = localizer["capture.title"].Value;

        return View(new CaptureDetailViewModel
        {
            ProjectId = projectId,
            Session = session,
            BaselineName = baseline.Name,
            DataSetName = version.Name,
            DataSetVersion = version.Number,
            CanReview = me.Can(Capability.RecordBaseline),
        });
    }

    /// <summary>Runs a sweep and returns the session it produced.</summary>
    [HttpPost("start")]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Start(
        Guid projectId, [FromBody] StartCaptureCommand command, CancellationToken cancellationToken)
    {
        var session = await capture.RunAsync(command, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "capture.started", projectId, nameof(CaptureSession), session.Id, session.Mode.ToString(),
            new Dictionary<string, string?>
            {
                ["rows"] = session.TotalRows.ToString(),
                ["differing"] = session.Differing.ToString(),
                ["failed"] = session.Failed.ToString(),
            }), cancellationToken);

        return Json(new
        {
            sessionId = session.Id,
            url = $"/projects/{projectId}/captures/{session.Id}",
            totalRows = session.TotalRows,
            differing = session.Differing,
            failed = session.Failed,
            status = session.Status.ToString(),
        });
    }

    /// <summary>The queue, filtered and paged. Never the bodies — those are fetched one at a time.</summary>
    [HttpGet("{sessionId:guid}/samples")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Samples(
        Guid projectId, Guid sessionId, string? status, bool? differing, int skip, int take,
        CancellationToken cancellationToken)
    {
        var session = await db.CaptureSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.ProjectId == projectId, cancellationToken);
        if (session is null) return NotFound();

        var query = db.CaptureSamples.Where(s => s.CaptureSessionId == sessionId);

        if (Enum.TryParse<SampleStatus>(status, out var wanted)) query = query.Where(s => s.Status == wanted);
        if (differing == true) query = query.Where(s => s.Differs);

        var rows = await query
            .OrderBy(s => s.Ordinal)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take <= 0 ? 100 : take, 1, 500))
            .Select(s => new
            {
                s.Id, s.Key, s.Ordinal, s.Status, s.Differs, s.StatusCode,
                s.DurationMs, s.FailureMessage, s.DiffSummaryJson, s.ReviewNote,
            })
            .ToListAsync(cancellationToken);

        var counts = await db.CaptureSamples
            .Where(s => s.CaptureSessionId == sessionId)
            .GroupBy(s => s.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return Json(new
        {
            session = new CaptureSessionDto
            {
                Id = session.Id,
                Mode = session.Mode.ToString(),
                Status = session.Status.ToString(),
                TotalRows = session.TotalRows,
                Completed = session.Completed,
                Differing = session.Differing,
                Failed = session.Failed,
                StoppedReason = session.StoppedReason,
                Counts = counts.ToDictionary(c => c.Status.ToString(), c => c.Count),
            },
            total = await query.CountAsync(cancellationToken),
            rows = rows.Select(r => new SampleRowDto
            {
                Id = r.Id,
                Key = r.Key,
                Ordinal = r.Ordinal,
                Status = r.Status.ToString(),
                Differs = r.Differs,
                StatusCode = r.StatusCode,
                DurationMs = r.DurationMs,
                FailureMessage = r.FailureMessage,
                ReviewNote = r.ReviewNote,
                DiffCounts = Counts(r.DiffSummaryJson),
            }),
        });
    }

    /// <summary>One sample's full diff, built when somebody actually opens it.</summary>
    [HttpGet("{sessionId:guid}/samples/{sampleId:guid}/diff")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Diff(
        Guid projectId, Guid sessionId, Guid sampleId, CancellationToken cancellationToken)
    {
        // The project is checked too, not only the session. The tenant filter already stops a
        // cross-workspace read; this stops one project's address returning another project's
        // sample, which is the same mistake one level down.
        var owns = await db.CaptureSamples
            .AnyAsync(s => s.Id == sampleId
                           && s.CaptureSessionId == sessionId
                           && s.Session!.ProjectId == projectId, cancellationToken);
        if (!owns) return NotFound();

        var diff = await capture.DiffAsync(sampleId, cancellationToken);

        if (diff is null)
        {
            return Json(new DiffResultDto
            {
                Matches = false, Rows = [], Counts = new Dictionary<string, int>(), FindingIndexes = [],
                FailureMessage = localizer["capture.noResponse"].Value,
            });
        }

        return Json(BaselineService.Flatten(diff, localizer["capture.approvedAnswer"].Value, 200, 0));
    }

    [HttpPost("{sessionId:guid}/review")]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> Review(
        Guid projectId, Guid sessionId, [FromBody] ReviewSamplesCommand command,
        CancellationToken cancellationToken)
    {
        var owns = await db.CaptureSessions
            .AnyAsync(s => s.Id == sessionId && s.ProjectId == projectId, cancellationToken);
        if (!owns) return NotFound();

        int changed;
        try
        {
            changed = await capture.ReviewAsync(sessionId, command, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return ValidationProblem(ex.Message);
        }

        await audit.RecordAsync(new AuditEntry(
            $"capture.{command.Status.ToLowerInvariant()}", projectId, nameof(CaptureSample), sessionId,
            command.Status,
            new Dictionary<string, string?> { ["samples"] = changed.ToString() }), cancellationToken);

        return Json(new { reviewed = changed });
    }

    private static IReadOnlyDictionary<string, int> Counts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, int>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                   ?? new Dictionary<string, int>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, int>();
        }
    }

    private void Breadcrumbs(string projectName, Guid projectId, string? sessionName)
    {
        var crumbs = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (localizer["nav.captures"].Value, sessionName is null ? null : $"/projects/{projectId}/captures"),
        };

        if (sessionName is not null) crumbs.Add((sessionName, null));
        ViewData["Breadcrumbs"] = crumbs;
    }
}
