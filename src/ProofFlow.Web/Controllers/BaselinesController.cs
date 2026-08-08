using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Baselines;
using ProofFlow.Contracts.Requests;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Environments;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Variables;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Baselines: capturing what correct looked like, and finding out what moved since.
///
/// The whole loop lives here because it is one loop — capture, approve, replay, review, accept the
/// part that was meant and reject the part that was not. Splitting it across controllers would put
/// the seams in places nobody thinks in.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/baselines")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class BaselinesController(
    ProofFlowDbContext db,
    BaselineService baselines,
    Separation separation,
    ApprovalInbox inbox,
    EnvironmentContextBuilder environments,
    IHttpExecutor executor,
    ICurrentUser me,
    IAuditLog audit,
    ComparisonScratch scratch,
    IStringLocalizer localizer) : Controller
{
    private static CompareResponseDto Failed(string message, double durationMs = 0) => new()
    {
        Diff = new DiffResultDto
        {
            Matches = false,
            Rows = [],
            Counts = new Dictionary<string, int>(),
            FindingIndexes = [],
            FailureMessage = message,
            DurationMs = durationMs,
        },
    };

    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var rows = await db.Baselines
            .Where(b => b.ProjectId == projectId && b.ArchivedAt == null)
            .OrderBy(b => b.Name)
            .Select(b => new BaselineSummary(
                b.Id,
                b.Name,
                b.Description,
                b.Environment == null ? null : b.Environment.Name,
                db.BaselineVersions.Count(v => v.BaselineId == b.Id),
                db.BaselineVersions
                    .Where(v => v.BaselineId == b.Id)
                    .OrderByDescending(v => v.Number)
                    .Select(v => v.Status)
                    .FirstOrDefault(),
                b.UpdatedAt))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, null);
        ViewData["Title"] = localizer["nav.baselines"].Value;

        return View(new BaselineListViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Baselines = rows,
            CanRecord = me.Can(Capability.RecordBaseline),
        });
    }

    [HttpGet("{baselineId:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Details(
        Guid projectId, Guid baselineId, CancellationToken cancellationToken)
    {
        var baseline = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == baselineId && b.ProjectId == projectId, cancellationToken);
        if (baseline is null) return NotFound();

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);

        var versions = await db.BaselineVersions
            .Where(v => v.BaselineId == baselineId)
            .OrderByDescending(v => v.Number)
            .Select(v => new BaselineVersionRow(
                v.Id, v.Number, v.Status, v.Description, v.CreatedAt, v.ApprovedAt,
                v.StatusCode, v.Body.Length, v.RejectionReason))
            .ToListAsync(cancellationToken);

        var rules = await db.BaselineRules
            .Where(r => r.BaselineId == baselineId)
            .OrderBy(r => r.SortOrder)
            .Select(r => new RuleDto
            {
                Id = r.Id, Path = r.Path, Matcher = r.Matcher, Text = r.Text,
                Number = r.Number, Number2 = r.Number2, Note = r.Note, Enabled = r.Enabled,
            })
            .ToListAsync(cancellationToken);

        var environmentList = await db.Environments
            .Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.SortOrder)
            .Select(e => new RequestLabEnvironment(e.Id, e.Name, e.BaseUrl, e.IsProduction))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, baseline.Name);
        ViewData["Title"] = baseline.Name;

        return View(new BaselineDetailViewModel
        {
            ProjectId = projectId,
            Baseline = baseline,
            Versions = versions,
            Rules = rules,
            Environments = environmentList,
            CanRecord = me.Can(Capability.RecordBaseline),
            CanApprove = me.Can(Capability.ApproveBaseline),
            CanRun = me.Can(Capability.RunTest),
        });
    }

    /// <summary>Stores a response from the request lab as a new baseline's first version.</summary>
    [HttpPost("capture")]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> Capture(
        Guid projectId, [FromBody] CaptureBaselineCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return ValidationProblem(localizer["error.required"].Value);

        var name = command.Name.Trim();

        if (await db.Baselines.AnyAsync(b => b.ProjectId == projectId && b.Name == name, cancellationToken))
            return ValidationProblem(localizer["baseline.nameTaken", name].Value);

        var baseline = new Baseline
        {
            WorkspaceId = me.WorkspaceId!.Value,
            ProjectId = projectId,
            EnvironmentId = command.EnvironmentId,
            Name = name,
            Description = command.Description,
            RequestJson = command.RequestJson,
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Baselines.Add(baseline);
        await db.SaveChangesAsync(cancellationToken);

        var version = await baselines.CaptureAsync(
            baseline, command.Body, command.ContentType, command.StatusCode, command.Headers, cancellationToken);

        // Approved on capture, and this is a deliberate choice rather than an oversight. The first
        // version is not a *change* to anything — there is nothing to review it against — and
        // forcing an approval step before the baseline can ever be used would make the first run
        // of every new test fail for administrative reasons. Every version after this one is
        // proposed, not approved.
        await baselines.ApproveAsync(version, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.created", projectId, nameof(Baseline), baseline.Id, baseline.Name), cancellationToken);

        return Json(new { baselineId = baseline.Id, versionId = version.Id, url = $"/projects/{projectId}/baselines/{baseline.Id}" });
    }

    /// <summary>
    /// Creates a baseline from a request alone, with no captured response.
    ///
    /// This is how a sample-based test starts, and it needs its own door. The request for one
    /// necessarily contains <c>{{dataset.current.…}}</c>, which cannot be sent from the request lab
    /// because there is no current row there — so "send it, then save what came back" is a path
    /// that does not exist for this kind of test.
    ///
    /// What it produces is a baseline with no version: no whole-response answer, because there
    /// isn't one. The answers live per input in <c>BaselineSamples</c>, written by approving what a
    /// sweep captured.
    /// </summary>
    [HttpPost("define")]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> Define(
        Guid projectId, [FromBody] DefineBaselineCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return ValidationProblem(localizer["error.required"].Value);

        var name = command.Name.Trim();

        if (await db.Baselines.AnyAsync(b => b.ProjectId == projectId && b.Name == name, cancellationToken))
            return ValidationProblem(localizer["baseline.nameTaken", name].Value);

        var baseline = new Baseline
        {
            WorkspaceId = me.WorkspaceId!.Value,
            ProjectId = projectId,
            EnvironmentId = command.EnvironmentId,
            Name = name,
            Description = command.Description,
            RequestJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                method = string.IsNullOrWhiteSpace(command.Method) ? "GET" : command.Method,
                url = command.Url ?? string.Empty,
            }),
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Baselines.Add(baseline);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.created", projectId, nameof(Baseline), baseline.Id, baseline.Name), cancellationToken);

        return Json(new
        {
            baselineId = baseline.Id,
            url = $"/projects/{projectId}/baselines/{baseline.Id}",
        });
    }

    /// <summary>
    /// Replays the baseline's request and compares what comes back with what was approved.
    /// </summary>
    [HttpPost("{baselineId:guid}/compare")]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Compare(
        Guid projectId, Guid baselineId, [FromBody] CompareCommand command, CancellationToken cancellationToken)
    {
        var baseline = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == baselineId && b.ProjectId == projectId, cancellationToken);
        if (baseline is null) return NotFound();

        var request = ReadRequest(baseline);
        if (request is null) return Json(Failed(localizer["baseline.noRequest"].Value));

        var environmentId = command.EnvironmentId ?? baseline.EnvironmentId;
        ProjectEnvironment? environment = null;

        if (environmentId is { } id)
        {
            environment = await db.Environments
                .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId, cancellationToken);
        }

        var context = environment is null ? null : await environments.BuildAsync(environment, cancellationToken);
        var resolver = context?.Resolver() ?? new VariableResolver(new VariableScopes());
        var policy = context?.Policy ?? new UrlPolicy();

        HttpRequestDefinition resolved;
        try
        {
            resolved = request with
            {
                Url = resolver.Resolve(request.Url),
                Headers = [.. request.Headers.Select(h => h with { Value = resolver.Resolve(h.Value) })],
                Body = request.Body is null ? null : request.Body with
                {
                    Content = resolver.Resolve(request.Body.Content ?? string.Empty),
                },
            };
        }
        catch (VariableResolutionException ex)
        {
            return Json(Failed(ex.Message));
        }

        var response = await executor.SendAsync(resolved, policy, cancellationToken);

        if (!response.Succeeded)
        {
            return Json(Failed(response.Failure!.Message, response.Duration.TotalMilliseconds));
        }

        // Redacted before anything else touches it, so a secret that came back in the response
        // cannot reach the diff, the suggestions, or the next baseline version.
        var body = context?.Redaction.Apply(response.Body) ?? response.Body;

        var diff = await baselines.CompareAsync(
            baseline, body, response.StatusCode, response.Duration.TotalMilliseconds, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.compared", projectId, nameof(Baseline), baseline.Id, baseline.Name,
            new Dictionary<string, string?> { ["matches"] = diff.Matches ? "true" : "false" }), cancellationToken);

        // Held for the accept step, which must merge from the response the reader actually looked
        // at rather than from a second call that may return something else.
        scratch.Hold(me.UserId ?? Guid.Empty, baselineId,
            new HeldResponse(body, response.ContentType, response.StatusCode));

        return Json(new CompareResponseDto
        {
            Diff = diff,
            // Only worth proposing when something actually differs: a green comparison with a list
            // of fields to stop checking is an invitation to weaken a test that just passed.
            Suggestions = diff.Matches
                ? []
                : await baselines.SuggestAsync(baselineId, body, cancellationToken),
        });
    }

    [HttpPost("{baselineId:guid}/suggestions")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Suggestions(
        Guid projectId, Guid baselineId, [FromBody] BodyCommand command, CancellationToken cancellationToken)
    {
        var baseline = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == baselineId && b.ProjectId == projectId, cancellationToken);
        if (baseline is null) return NotFound();

        return Json(await baselines.SuggestAsync(baselineId, command.Body ?? string.Empty, cancellationToken));
    }

    [HttpPost("{baselineId:guid}/rules")]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> SaveRules(
        Guid projectId, Guid baselineId, [FromBody] IReadOnlyList<RuleDto> rules,
        CancellationToken cancellationToken)
    {
        var baseline = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == baselineId && b.ProjectId == projectId, cancellationToken);
        if (baseline is null) return NotFound();

        var existing = await db.BaselineRules
            .Where(r => r.BaselineId == baselineId).ToListAsync(cancellationToken);

        db.BaselineRules.RemoveRange(existing);

        var order = 0;
        foreach (var rule in rules.Where(r => !string.IsNullOrWhiteSpace(r.Path)))
        {
            db.BaselineRules.Add(new BaselineRule
            {
                WorkspaceId = baseline.WorkspaceId,
                BaselineId = baselineId,
                Path = rule.Path.Trim(),
                Matcher = rule.Matcher,
                Text = rule.Text,
                Number = rule.Number,
                Number2 = rule.Number2,
                Note = rule.Note,
                Enabled = rule.Enabled,
                SortOrder = order++,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.rulesChanged", projectId, nameof(Baseline), baselineId, baseline.Name,
            new Dictionary<string, string?> { ["rules"] = order.ToString() }), cancellationToken);

        return Json(new { saved = order });
    }

    /// <summary>Accepts some of the last comparison's changes as the next, proposed version.</summary>
    [HttpPost("{baselineId:guid}/accept")]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> Accept(
        Guid projectId, Guid baselineId, [FromBody] AcceptChangesCommand command,
        CancellationToken cancellationToken)
    {
        var baseline = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == baselineId && b.ProjectId == projectId, cancellationToken);
        if (baseline is null) return NotFound();

        var userId = me.UserId ?? Guid.Empty;

        if (scratch.Take(userId, baselineId) is not { } held)
        {
            // The comparison is gone — half an hour, a restart, or a response too large to hold.
            // Asking for it again is better than merging from a fresh call the reader never saw.
            return ValidationProblem(localizer["baseline.compareExpired"].Value);
        }

        var version = await baselines.AcceptAsync(
            baseline, held.Body, held.ContentType, held.StatusCode, command, cancellationToken);

        // The response has been folded into a version; keeping it would let a second accept build
        // a second proposal from the same stale bytes.
        scratch.Release(userId, baselineId);

        await audit.RecordAsync(new AuditEntry(
            "baseline.versionProposed", projectId, nameof(BaselineVersion), version.Id,
            $"{baseline.Name} v{version.Number}",
            new Dictionary<string, string?>
            {
                ["accepted"] = command.AcceptedPaths.Count.ToString(),
                ["newRules"] = command.NewRules.Count.ToString(),
            }), cancellationToken);

        return Json(new { versionId = version.Id, number = version.Number });
    }

    /// <summary>
    /// Everything in this project waiting on a decision, in one list.
    ///
    /// One list because that is the point: a proposed version lives on a baseline page and a
    /// captured sample lives in a review queue, and a reviewer who has to visit both to find out
    /// whether they have anything to do is a reviewer who checks neither.
    /// </summary>
    [HttpGet("/projects/{projectId:guid}/approvals")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Approvals(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        ViewData["Title"] = localizer["approval.title"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (project.Name, $"/projects/{projectId}"),
            (localizer["approval.title"].Value, null),
        };

        return View("Approvals", new ApprovalViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Inbox = await inbox.ReadAsync(projectId, cancellationToken),
            CanApprove = me.Can(Capability.ApproveBaseline),
        });
    }

    [HttpPost("{baselineId:guid}/versions/{versionId:guid}/approve")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ApproveBaseline)]
    public async Task<IActionResult> Approve(
        Guid projectId, Guid baselineId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await db.BaselineVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.BaselineId == baselineId, cancellationToken);
        if (version is null) return NotFound();

        // Nobody approves their own recording while somebody else could. The check is here rather
        // than in the service because the refusal has to be said in the reader's language.
        if (await separation.RefusalAsync(version.CreatedByUserId, cancellationToken) is { } refusal)
        {
            TempData.Error(localizer[refusal]);
            return Redirect($"/projects/{projectId}/baselines/{baselineId}");
        }

        await baselines.ApproveAsync(version, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.approved", projectId, nameof(BaselineVersion), versionId,
            $"v{version.Number}"), cancellationToken);

        TempData.Success(localizer["baseline.approved", version.Number]);
        return Redirect($"/projects/{projectId}/baselines/{baselineId}");
    }

    [HttpPost("{baselineId:guid}/versions/{versionId:guid}/reject")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ApproveBaseline)]
    public async Task<IActionResult> Reject(
        Guid projectId, Guid baselineId, Guid versionId, string? reason, CancellationToken cancellationToken)
    {
        var version = await db.BaselineVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.BaselineId == baselineId, cancellationToken);
        if (version is null) return NotFound();

        await baselines.RejectAsync(version, reason, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.rejected", projectId, nameof(BaselineVersion), versionId,
            $"v{version.Number}", new Dictionary<string, string?> { ["reason"] = reason }), cancellationToken);

        TempData.Info(localizer["baseline.rejected", version.Number]);
        return Redirect($"/projects/{projectId}/baselines/{baselineId}");
    }

    private static HttpRequestDefinition? ReadRequest(Baseline baseline)
    {
        if (string.IsNullOrWhiteSpace(baseline.RequestJson)) return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<HttpRequestDefinition>(
                baseline.RequestJson,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void Breadcrumbs(string projectName, Guid projectId, string? baselineName)
    {
        var crumbs = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (localizer["nav.baselines"].Value, baselineName is null ? null : $"/projects/{projectId}/baselines"),
        };

        if (baselineName is not null) crumbs.Add((baselineName, null));
        ViewData["Breadcrumbs"] = crumbs;
    }
}

public sealed record CaptureBaselineCommand
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public Guid? EnvironmentId { get; init; }
    public required string Body { get; init; }
    public string? ContentType { get; init; }
    public int StatusCode { get; init; }
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>The request, so the baseline can be replayed rather than only remembered.</summary>
    public string? RequestJson { get; init; }
}

public sealed record CompareCommand(Guid? EnvironmentId);

public sealed record DefineBaselineCommand
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Method { get; init; }
    public string? Url { get; init; }
    public Guid? EnvironmentId { get; init; }
}

public sealed record BodyCommand(string? Body);
