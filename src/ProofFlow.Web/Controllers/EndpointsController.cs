using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Baselines;
using ProofFlow.Contracts.Capture;
using ProofFlow.Contracts.Requests;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Environments;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Capture;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Variables;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// One endpoint, and everything anybody wants to do to it.
///
/// This page exists because the simple job had no home. Call one address, keep what came back,
/// run it again against a list of different inputs, press one button to find out whether it still
/// does what it did — every piece of that already worked, and it was spread across five entries in
/// the sidebar named after the mechanisms rather than the job: Baselines, Data sets, Captures,
/// Review queue, Guided setup. Somebody who wanted to check an endpoint had to know that «the
/// endpoint» was a baseline, that the inputs were a data set, that pressing the button made a
/// capture session, and that the answers were reviewed somewhere else again.
///
/// So: no new entity. A <see cref="Baseline"/> *is* an endpoint — it carries the request, the
/// environment, what correct looks like, and the rules for comparing. The only thing added is
/// which inputs it is checked against, which used to be answered afresh every time somebody
/// started a sweep. What changed is the naming and the shape of the page.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/endpoints")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class EndpointsController(
    ProofFlowDbContext db,
    BaselineService baselines,
    CaptureService capture,
    Separation separation,
    ApprovalInbox inbox,
    EnvironmentContextBuilder environments,
    IHttpExecutor executor,
    EnvironmentAuthenticator authenticator,
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

    // ---- the list ---------------------------------------------------------------------------

    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, int? page, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var query = db.Baselines.Where(b => b.ProjectId == projectId && b.ArchivedAt == null);

        var total = await query.CountAsync(cancellationToken);
        var current = Paging.Clamp(page, Paging.DefaultPageSize, total);

        // Only this page's rows leave the database. The version count, the input count and the
        // last result are correlated subqueries rather than includes: with an imported collection
        // this list is thousands of rows long, and an Include would drag every version of every
        // one of them across to render twenty-five.
        var rows = await query
            .OrderBy(b => b.Name)
            .Skip((current - 1) * Paging.DefaultPageSize)
            .Take(Paging.DefaultPageSize)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Description,
                b.RequestJson,
                b.UpdatedAt,
                EnvironmentName = b.Environment == null ? null : b.Environment.Name,
                DataSetName = b.DataSet == null ? null : b.DataSet.Name,
                InputCount = b.DataSet == null
                    ? 0
                    : db.DataSetVersions
                        .Where(v => v.Id == b.DataSet.CurrentVersionId)
                        .Select(v => v.RowCount)
                        .FirstOrDefault(),
                VersionCount = db.BaselineVersions.Count(v => v.BaselineId == b.Id),
                LatestStatus = db.BaselineVersions
                    .Where(v => v.BaselineId == b.Id)
                    .OrderByDescending(v => v.Number)
                    .Select(v => v.Status)
                    .FirstOrDefault(),
                Last = db.CaptureSessions
                    .Where(s => s.BaselineId == b.Id)
                    .OrderByDescending(s => s.StartedAt)
                    .Select(s => new { s.TotalRows, s.Differing, s.Failed, s.Unmatched, s.StartedAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, null);
        ViewData["Title"] = localizer["nav.endpoints"].Value;

        return View(new EndpointListViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Endpoints = [.. rows.Select(row =>
            {
                var request = Stored(row.RequestJson);
                return new EndpointSummary(
                    row.Id,
                    row.Name,
                    row.Description,
                    request?.Method ?? "GET",
                    request?.Url ?? string.Empty,
                    row.EnvironmentName,
                    row.DataSetName,
                    row.InputCount,
                    row.VersionCount,
                    row.LatestStatus,
                    row.Last is null
                        ? null
                        : new EndpointLastResult(
                            row.Last.TotalRows, row.Last.Differing, row.Last.Failed, row.Last.Unmatched,
                            row.Last.StartedAt),
                    row.UpdatedAt);
            })],
            Page = new Paging
            {
                Page = current,
                PageSize = Paging.DefaultPageSize,
                Total = total,
                Path = $"/projects/{projectId}/endpoints",
            },
            CanRecord = me.Can(Capability.RecordBaseline),
        });
    }

    // ---- one endpoint -----------------------------------------------------------------------

    [HttpGet("{endpointId:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Details(
        Guid projectId, Guid endpointId, CancellationToken cancellationToken)
    {
        var endpoint = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == endpointId && b.ProjectId == projectId, cancellationToken);
        if (endpoint is null) return NotFound();

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);

        var versions = await db.BaselineVersions
            .Where(v => v.BaselineId == endpointId)
            .OrderByDescending(v => v.Number)
            .Select(v => new BaselineVersionRow(
                v.Id, v.Number, v.Status, v.Description, v.CreatedAt, v.ApprovedAt,
                v.StatusCode, v.Body.Length, v.RejectionReason))
            .ToListAsync(cancellationToken);

        var rules = await db.BaselineRules
            .Where(r => r.BaselineId == endpointId)
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

        // Every set in the project, so the inputs can be chosen here rather than on another page.
        var dataSets = await db.DataSets
            .Where(d => d.ProjectId == projectId && d.ArchivedAt == null)
            .OrderBy(d => d.Name)
            .Select(d => new EndpointDataSetOption(
                d.Id,
                d.Name,
                db.DataSetVersions
                    .Where(v => v.Id == d.CurrentVersionId)
                    .Select(v => (int?)v.RowCount)
                    .FirstOrDefault() ?? 0))
            .ToListAsync(cancellationToken);

        var lastTest = await db.CaptureSessions
            .Where(s => s.BaselineId == endpointId)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => new EndpointTestSummary(
                s.Id, s.Status, s.TotalRows, s.Completed, s.Differing, s.Failed, s.Unmatched,
                s.StartedAt))
            .FirstOrDefaultAsync(cancellationToken);

        var request = Stored(endpoint.RequestJson);

        Breadcrumbs(project.Name, projectId, endpoint.Name);
        ViewData["Title"] = endpoint.Name;

        return View(new EndpointDetailViewModel
        {
            ProjectId = projectId,
            Endpoint = endpoint,
            Method = request?.Method ?? "GET",
            Url = request?.Url ?? string.Empty,
            Versions = versions,
            Rules = rules,
            Environments = environmentList,
            DataSets = dataSets,
            LastTest = lastTest,
            CanRecord = me.Can(Capability.RecordBaseline),
            CanApprove = me.Can(Capability.ApproveBaseline),
            CanRun = me.Can(Capability.RunTest),
        });
    }

    // ---- making one -------------------------------------------------------------------------

    /// <summary>
    /// The form for an endpoint that cannot be sent from the request lab.
    ///
    /// Most endpoints arrive by sending a request and keeping the answer, which is the right way
    /// round. This is the other case, and it is not rare: an endpoint checked against a list of
    /// inputs has <c>{{dataset.current.id}}</c> in its address, and there is no current row in the
    /// request lab — so «send it, then save what came back» is a path that does not exist for it.
    ///
    /// The nine-step wizard used to be where this lived. Four of its nine steps were this form.
    /// </summary>
    [HttpGet("new")]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> New(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        Breadcrumbs(project.Name, projectId, localizer["endpoint.define"].Value);
        ViewData["Title"] = localizer["endpoint.define"].Value;

        return View(await FormAsync(projectId, new EndpointFormViewModel { ProjectId = projectId },
            cancellationToken));
    }

    [HttpPost("new")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> Create(
        Guid projectId, EndpointFormViewModel model, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var name = model.Name?.Trim() ?? string.Empty;

        if (name.Length > 0
            && await db.Baselines.AnyAsync(b => b.ProjectId == projectId && b.Name == name, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Name), localizer["baseline.nameTaken", name].Value);
        }

        if (!ModelState.IsValid)
        {
            Breadcrumbs(project.Name, projectId, localizer["endpoint.define"].Value);
            ViewData["Title"] = localizer["endpoint.define"].Value;
            return View("New", await FormAsync(projectId, model, cancellationToken));
        }

        // Checked against this project rather than only against the workspace, for the same reason
        // as on the detail page: a form field carrying an id is a form field somebody can retype.
        if (model.DataSetId is { } setId
            && !await db.DataSets.AnyAsync(d => d.Id == setId && d.ProjectId == projectId, cancellationToken))
        {
            return NotFound();
        }

        var endpoint = new Baseline
        {
            WorkspaceId = me.WorkspaceId!.Value,
            ProjectId = projectId,
            EnvironmentId = model.EnvironmentId,
            DataSetId = model.DataSetId,
            Name = name,
            Description = model.Description,
            RequestJson = JsonSerializer.Serialize(
                new HttpRequestDefinition
                {
                    Method = string.IsNullOrWhiteSpace(model.Method) ? "GET" : model.Method,
                    Url = model.Url?.Trim() ?? string.Empty,
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Baselines.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.created", projectId, nameof(Baseline), endpoint.Id, endpoint.Name), cancellationToken);

        TempData.Success(localizer["endpoint.defined", endpoint.Name]);
        return Redirect($"/projects/{projectId}/endpoints/{endpoint.Id}");
    }

    /// <summary>Stores a response from the request lab as a new endpoint's first version.</summary>
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

        var endpoint = new Baseline
        {
            WorkspaceId = me.WorkspaceId!.Value,
            ProjectId = projectId,
            EnvironmentId = command.EnvironmentId,
            Name = name,
            Description = command.Description,
            RequestJson = command.RequestJson,
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Baselines.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);

        var version = await baselines.CaptureAsync(
            endpoint, command.Body, command.ContentType, command.StatusCode, command.Headers, cancellationToken);

        // Approved on capture, and this is a deliberate choice rather than an oversight. The first
        // version is not a *change* to anything — there is nothing to review it against — and
        // forcing an approval step before it can ever be used would make the first run of every
        // new test fail for administrative reasons. Every version after this one is proposed.
        await baselines.ApproveAsync(version, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.created", projectId, nameof(Baseline), endpoint.Id, endpoint.Name), cancellationToken);

        return Json(new
        {
            baselineId = endpoint.Id,
            versionId = version.Id,
            url = $"/projects/{projectId}/endpoints/{endpoint.Id}",
        });
    }

    /// <summary>
    /// Creates an endpoint from a request alone, with no captured response.
    ///
    /// This is how a sample-based test starts, and it needs its own door. The request for one
    /// necessarily contains <c>{{dataset.current.…}}</c>, which cannot be sent from the request lab
    /// because there is no current row there — so "send it, then save what came back" is a path
    /// that does not exist for this kind of test.
    ///
    /// What it produces is an endpoint with no version: no whole-response answer, because there
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

        var endpoint = new Baseline
        {
            WorkspaceId = me.WorkspaceId!.Value,
            ProjectId = projectId,
            EnvironmentId = command.EnvironmentId,
            DataSetId = command.DataSetId,
            Name = name,
            Description = command.Description,
            RequestJson = JsonSerializer.Serialize(new
            {
                method = string.IsNullOrWhiteSpace(command.Method) ? "GET" : command.Method,
                url = command.Url ?? string.Empty,
            }),
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Baselines.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.created", projectId, nameof(Baseline), endpoint.Id, endpoint.Name), cancellationToken);

        return Json(new
        {
            baselineId = endpoint.Id,
            url = $"/projects/{projectId}/endpoints/{endpoint.Id}",
        });
    }

    // ---- the four sections of the page --------------------------------------------------------

    /// <summary>The request: method, address, and which environment it goes to.</summary>
    [HttpPost("{endpointId:guid}/request")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> SaveRequest(
        Guid projectId, Guid endpointId, string method, string url, Guid? environmentId,
        CancellationToken cancellationToken)
    {
        var endpoint = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == endpointId && b.ProjectId == projectId, cancellationToken);
        if (endpoint is null) return NotFound();

        // Only the two fields this form owns are written back. The stored request may carry
        // headers, a body and an authentication block that the request lab put there, and a form
        // that serialised its own two fields over the top would silently delete all of it.
        var existing = Stored(endpoint.RequestJson) ?? new HttpRequestDefinition();

        endpoint.RequestJson = JsonSerializer.Serialize(
            existing with
            {
                Method = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant(),
                Url = url?.Trim() ?? string.Empty,
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        endpoint.EnvironmentId = environmentId;

        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.requestChanged", projectId, nameof(Baseline), endpoint.Id, endpoint.Name), cancellationToken);

        TempData.Success(localizer["endpoint.requestSaved"]);
        return Redirect($"/projects/{projectId}/endpoints/{endpointId}");
    }

    /// <summary>The inputs: which set of rows the Test button sweeps across, or none.</summary>
    [HttpPost("{endpointId:guid}/inputs")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> SaveInputs(
        Guid projectId, Guid endpointId, Guid? dataSetId, CancellationToken cancellationToken)
    {
        var endpoint = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == endpointId && b.ProjectId == projectId, cancellationToken);
        if (endpoint is null) return NotFound();

        // Checked against this project, not only against the workspace: the tenant filter already
        // stops a cross-workspace pairing, and this stops one project's endpoint being pointed at
        // another project's rows, which is the same mistake one level down.
        if (dataSetId is { } id
            && !await db.DataSets.AnyAsync(d => d.Id == id && d.ProjectId == projectId, cancellationToken))
        {
            return NotFound();
        }

        endpoint.DataSetId = dataSetId;
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.inputsChanged", projectId, nameof(Baseline), endpoint.Id, endpoint.Name,
            new Dictionary<string, string?> { ["dataSet"] = dataSetId?.ToString() }), cancellationToken);

        TempData.Success(localizer["endpoint.inputsSaved"]);
        return Redirect($"/projects/{projectId}/endpoints/{endpointId}");
    }

    /// <summary>
    /// The Test button.
    ///
    /// One press, and what happens depends on whether the endpoint has inputs — which is the whole
    /// reason the pairing is stored on it. With a set, this sweeps the request across every row in
    /// its current version and compares each answer with the one approved for that row. Without
    /// one, there is nothing to sweep and the honest thing is to say so rather than to run a
    /// zero-row sweep that reports «0 passed» and looks like a success.
    /// </summary>
    [HttpPost("{endpointId:guid}/test")]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Test(
        Guid projectId, Guid endpointId, [FromBody] TestEndpointCommand? command,
        CancellationToken cancellationToken)
    {
        var endpoint = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == endpointId && b.ProjectId == projectId, cancellationToken);
        if (endpoint is null) return NotFound();

        if (endpoint.DataSetId is not { } dataSetId)
            return ValidationProblem(localizer["endpoint.test.noInputs"].Value);

        var versionId = await db.DataSets
            .Where(d => d.Id == dataSetId)
            .Select(d => d.CurrentVersionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (versionId is not { } dataSetVersionId)
            return ValidationProblem(localizer["endpoint.test.noRows"].Value);

        var session = await capture.RunAsync(
            new StartCaptureCommand
            {
                BaselineId = endpointId,
                DataSetVersionId = dataSetVersionId,
                EnvironmentId = command?.EnvironmentId ?? endpoint.EnvironmentId,
                Mode = "Regression",
                Limit = command?.Limit,
            },
            cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "capture.started", projectId, nameof(CaptureSession), session.Id, session.Mode.ToString(),
            new Dictionary<string, string?>
            {
                ["rows"] = session.TotalRows.ToString(),
                ["differing"] = session.Differing.ToString(),
                ["unmatched"] = session.Unmatched.ToString(),
                ["failed"] = session.Failed.ToString(),
            }), cancellationToken);

        return Json(new
        {
            sessionId = session.Id,
            totalRows = session.TotalRows,
            completed = session.Completed,
            differing = session.Differing,
            failed = session.Failed,
            unmatched = session.Unmatched,
            status = session.Status.ToString(),
            stoppedReason = session.StoppedReason,
        });
    }

    /// <summary>
    /// The other half of the Test button: send it once, and compare the whole answer.
    ///
    /// This is what an endpoint with no inputs does, and it is also how a version is proposed —
    /// the response is held so that accepting some of what changed merges from the bytes the
    /// reader actually looked at rather than from a second call that may return something else.
    /// </summary>
    [HttpPost("{endpointId:guid}/compare")]
    [Authorize(Policy = Policies.RunTest)]
    public async Task<IActionResult> Compare(
        Guid projectId, Guid endpointId, [FromBody] CompareCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == endpointId && b.ProjectId == projectId, cancellationToken);
        if (endpoint is null) return NotFound();

        var request = Stored(endpoint.RequestJson);
        if (request is null) return Json(Failed(localizer["baseline.noRequest"].Value));

        var environmentId = command.EnvironmentId ?? endpoint.EnvironmentId;
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

        // The environment's authentication, the same as everywhere else. Without it an endpoint
        // against an API that needs a token could only be compared by somebody pasting one into a
        // header that then expired.
        if (context is not null)
        {
            var outcome = await authenticator.HeadersAsync(
                context.Auth, environment?.BaseUrl, resolver, policy, context.TokenKey,
                cancellationToken);

            if (!outcome.Ok) return Json(Failed(outcome.Problem!));

            resolved = InheritedHeaders.Apply(
                resolved, outcome.Headers, environment?.DefaultHeadersJson);
        }

        var response = await executor.SendAsync(resolved, policy, cancellationToken);

        if (!response.Succeeded)
        {
            return Json(Failed(response.Failure!.Message, response.Duration.TotalMilliseconds));
        }

        // Redacted before anything else touches it, so a secret that came back in the response
        // cannot reach the diff, the suggestions, or the next version.
        var body = context?.Redaction.Apply(response.Body) ?? response.Body;

        var diff = await baselines.CompareAsync(
            endpoint, body, response.StatusCode, response.Duration.TotalMilliseconds, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.compared", projectId, nameof(Baseline), endpoint.Id, endpoint.Name,
            new Dictionary<string, string?> { ["matches"] = diff.Matches ? "true" : "false" }), cancellationToken);

        scratch.Hold(me.UserId ?? Guid.Empty, endpointId,
            new HeldResponse(body, response.ContentType, response.StatusCode));

        return Json(new CompareResponseDto
        {
            Diff = diff,
            // Only worth proposing when something actually differs: a green comparison with a list
            // of fields to stop checking is an invitation to weaken a test that just passed.
            Suggestions = diff.Matches
                ? []
                : await baselines.SuggestAsync(endpointId, body, cancellationToken),
        });
    }

    [HttpPost("{endpointId:guid}/suggestions")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Suggestions(
        Guid projectId, Guid endpointId, [FromBody] BodyCommand command, CancellationToken cancellationToken)
    {
        var owns = await db.Baselines
            .AnyAsync(b => b.Id == endpointId && b.ProjectId == projectId, cancellationToken);
        if (!owns) return NotFound();

        return Json(await baselines.SuggestAsync(endpointId, command.Body ?? string.Empty, cancellationToken));
    }

    [HttpPost("{endpointId:guid}/rules")]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> SaveRules(
        Guid projectId, Guid endpointId, [FromBody] IReadOnlyList<RuleDto> rules,
        CancellationToken cancellationToken)
    {
        var endpoint = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == endpointId && b.ProjectId == projectId, cancellationToken);
        if (endpoint is null) return NotFound();

        var existing = await db.BaselineRules
            .Where(r => r.BaselineId == endpointId).ToListAsync(cancellationToken);

        db.BaselineRules.RemoveRange(existing);

        var order = 0;
        foreach (var rule in rules.Where(r => !string.IsNullOrWhiteSpace(r.Path)))
        {
            db.BaselineRules.Add(new BaselineRule
            {
                WorkspaceId = endpoint.WorkspaceId,
                BaselineId = endpointId,
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
            "baseline.rulesChanged", projectId, nameof(Baseline), endpointId, endpoint.Name,
            new Dictionary<string, string?> { ["rules"] = order.ToString() }), cancellationToken);

        return Json(new { saved = order });
    }

    /// <summary>Accepts some of the last comparison's changes as the next, proposed version.</summary>
    [HttpPost("{endpointId:guid}/accept")]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> Accept(
        Guid projectId, Guid endpointId, [FromBody] AcceptChangesCommand command,
        CancellationToken cancellationToken)
    {
        var endpoint = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == endpointId && b.ProjectId == projectId, cancellationToken);
        if (endpoint is null) return NotFound();

        var userId = me.UserId ?? Guid.Empty;

        if (scratch.Take(userId, endpointId) is not { } held)
        {
            // The comparison is gone — half an hour, a restart, or a response too large to hold.
            // Asking for it again is better than merging from a fresh call the reader never saw.
            return ValidationProblem(localizer["baseline.compareExpired"].Value);
        }

        var version = await baselines.AcceptAsync(
            endpoint, held.Body, held.ContentType, held.StatusCode, command, cancellationToken);

        // The response has been folded into a version; keeping it would let a second accept build
        // a second proposal from the same stale bytes.
        scratch.Release(userId, endpointId);

        await audit.RecordAsync(new AuditEntry(
            "baseline.versionProposed", projectId, nameof(BaselineVersion), version.Id,
            $"{endpoint.Name} v{version.Number}",
            new Dictionary<string, string?>
            {
                ["accepted"] = command.AcceptedPaths.Count.ToString(),
                ["newRules"] = command.NewRules.Count.ToString(),
            }), cancellationToken);

        return Json(new { versionId = version.Id, number = version.Number });
    }

    // ---- what the last test found -------------------------------------------------------------

    /// <summary>One row per input, filtered and paged. Never the bodies — those are fetched one at
    /// a time, because two thousand of them is not a page.</summary>
    [HttpGet("{endpointId:guid}/tests/{sessionId:guid}/samples")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Samples(
        Guid projectId, Guid endpointId, Guid sessionId, string? status, bool? differing,
        int skip, int take, CancellationToken cancellationToken)
    {
        var session = await db.CaptureSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId
                                      && s.ProjectId == projectId
                                      && s.BaselineId == endpointId, cancellationToken);
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

    /// <summary>One input's full diff, built when somebody actually opens it.</summary>
    [HttpGet("{endpointId:guid}/tests/{sessionId:guid}/samples/{sampleId:guid}/diff")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Diff(
        Guid projectId, Guid endpointId, Guid sessionId, Guid sampleId, CancellationToken cancellationToken)
    {
        var owns = await db.CaptureSamples
            .AnyAsync(s => s.Id == sampleId
                           && s.CaptureSessionId == sessionId
                           && s.Session!.ProjectId == projectId
                           && s.Session!.BaselineId == endpointId, cancellationToken);
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

    /// <summary>
    /// A decision about some of the last test's rows.
    ///
    /// The capability is checked here rather than on the attribute because the three decisions are
    /// not the same decision. Approving writes into the baseline and is what future tests compare
    /// against, so it needs <see cref="Capability.ApproveBaseline"/>; marking something reviewed or
    /// rejected changes nothing outside the session and needs only the right to record.
    ///
    /// One attribute could not say that, and the one it had said the wrong thing: the whole
    /// endpoint required RecordBaseline, which the role called Reviewer does not have — so the one
    /// role named after this job was the one role that could not do it, while a test designer,
    /// who is deliberately denied ApproveBaseline, could.
    /// </summary>
    [HttpPost("{endpointId:guid}/tests/{sessionId:guid}/review")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Review(
        Guid projectId, Guid endpointId, Guid sessionId, [FromBody] ReviewSamplesCommand command,
        CancellationToken cancellationToken)
    {
        var approving = string.Equals(
            command.Status, nameof(SampleStatus.Approved), StringComparison.OrdinalIgnoreCase);

        if (approving ? !me.Can(Capability.ApproveBaseline)
                      : !me.Can(Capability.RecordBaseline) && !me.Can(Capability.ApproveBaseline))
        {
            return Forbid();
        }

        var session = await db.CaptureSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId
                                      && s.ProjectId == projectId
                                      && s.BaselineId == endpointId, cancellationToken);
        if (session is null) return NotFound();

        // Approving is the decision the separation of duties is about; marking something reviewed
        // or rejected is not — those two do not bless anything, and blocking them would stop the
        // person who found the problem from saying so.
        if (approving
            && await separation.RefusalAsync(session.StartedByUserId, cancellationToken) is { } refusal)
        {
            return ValidationProblem(localizer[refusal].Value);
        }

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

    // ---- approving a version ------------------------------------------------------------------

    /// <summary>
    /// Everything in this project waiting on a decision, in one list.
    ///
    /// This stays a page of its own while the per-endpoint review folded into the endpoint,
    /// because it answers a different question. «What has changed about this endpoint» is asked by
    /// somebody looking at the endpoint; «is anything waiting on me» is asked by somebody who does
    /// not yet know which endpoint to look at, and sending them to twelve pages to find out is how
    /// a review queue stops being read.
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

    [HttpPost("{endpointId:guid}/versions/{versionId:guid}/approve")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ApproveBaseline)]
    public async Task<IActionResult> Approve(
        Guid projectId, Guid endpointId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await db.BaselineVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.BaselineId == endpointId, cancellationToken);
        if (version is null) return NotFound();

        // Nobody approves their own recording while somebody else could. The check is here rather
        // than in the service because the refusal has to be said in the reader's language.
        if (await separation.RefusalAsync(version.CreatedByUserId, cancellationToken) is { } refusal)
        {
            TempData.Error(localizer[refusal]);
            return Redirect($"/projects/{projectId}/endpoints/{endpointId}");
        }

        await baselines.ApproveAsync(version, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.approved", projectId, nameof(BaselineVersion), versionId,
            $"v{version.Number}"), cancellationToken);

        TempData.Success(localizer["baseline.approved", version.Number]);
        return Redirect($"/projects/{projectId}/endpoints/{endpointId}");
    }

    [HttpPost("{endpointId:guid}/versions/{versionId:guid}/reject")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ApproveBaseline)]
    public async Task<IActionResult> Reject(
        Guid projectId, Guid endpointId, Guid versionId, string? reason, CancellationToken cancellationToken)
    {
        var version = await db.BaselineVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.BaselineId == endpointId, cancellationToken);
        if (version is null) return NotFound();

        await baselines.RejectAsync(version, reason, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.rejected", projectId, nameof(BaselineVersion), versionId,
            $"v{version.Number}", new Dictionary<string, string?> { ["reason"] = reason }), cancellationToken);

        TempData.Info(localizer["baseline.rejected", version.Number]);
        return Redirect($"/projects/{projectId}/endpoints/{endpointId}");
    }

    // ---- plumbing ------------------------------------------------------------------------------

    /// <summary>
    /// The stored request, read back.
    ///
    /// Called Stored rather than Request, which is what it was: a controller inherits a Request
    /// property that is the incoming HTTP request, and a private method of the same name hides it.
    /// Nothing here needed the property, so it compiled — and the next person to write
    /// Request.Headers in this file would have got a very confusing error about a string.
    /// </summary>
    private static HttpRequestDefinition? Stored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<HttpRequestDefinition>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            // A request nobody can read is not a reason to refuse the page. The detail view says
            // so where the address would be, and the Test button is the thing that declines.
            return null;
        }
    }

    /// <summary>Fills the two lists the define form offers, keeping whatever was typed.</summary>
    private async Task<EndpointFormViewModel> FormAsync(
        Guid projectId, EndpointFormViewModel model, CancellationToken cancellationToken)
    {
        model.ProjectId = projectId;

        model.Environments = await db.Environments
            .Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.SortOrder)
            .Select(e => new RequestLabEnvironment(e.Id, e.Name, e.BaseUrl, e.IsProduction))
            .ToListAsync(cancellationToken);

        model.DataSets = await db.DataSets
            .Where(d => d.ProjectId == projectId && d.ArchivedAt == null)
            .OrderBy(d => d.Name)
            .Select(d => new EndpointDataSetOption(
                d.Id,
                d.Name,
                db.DataSetVersions
                    .Where(v => v.Id == d.CurrentVersionId)
                    .Select(v => (int?)v.RowCount)
                    .FirstOrDefault() ?? 0))
            .ToListAsync(cancellationToken);

        return model;
    }

    private static IReadOnlyDictionary<string, int> Counts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, int>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                   ?? new Dictionary<string, int>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>();
        }
    }

    private void Breadcrumbs(string projectName, Guid projectId, string? endpointName)
    {
        var crumbs = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (localizer["nav.endpoints"].Value, endpointName is null ? null : $"/projects/{projectId}/endpoints"),
        };

        if (endpointName is not null) crumbs.Add((endpointName, null));
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

    /// <summary>The request, so the endpoint can be replayed rather than only remembered.</summary>
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

    /// <summary>The inputs, chosen at the same moment as the request rather than later.</summary>
    public Guid? DataSetId { get; init; }
}

public sealed record BodyCommand(string? Body);

/// <summary>What the Test button may override for one press: where to send it, and how far to go.</summary>
public sealed record TestEndpointCommand
{
    public Guid? EnvironmentId { get; init; }

    /// <summary>Stop after this many rows. The first sweep of a two-thousand-row set is usually a
    /// mistake somebody wants to find after ten, not after twenty minutes of real calls.</summary>
    public int? Limit { get; init; }
}
