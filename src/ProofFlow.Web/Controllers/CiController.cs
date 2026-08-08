using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;
using ProofFlow.Web.Infrastructure;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The door a build agent comes through.
///
/// Deliberately small and deliberately separate from the pages. Three things a pipeline needs:
/// start a run, ask whether it has finished, and fetch a report a build system understands. Nothing
/// else — a key found in a CI log should not be able to edit a test, approve a baseline or read a
/// secret, and the surface it can reach is the clearest statement of that.
///
/// No anti-forgery token: there is no cookie and no browser, so there is no cross-site request to
/// forge. The credential is the header, which a third-party site cannot cause to be sent.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.Scheme)]
public sealed class CiController(
    ProofFlowDbContext db,
    RunService runs,
    MatrixService matrix,
    IRunQueue queue,
    JUnitReport reports,
    IAuditLog audit) : ControllerBase
{
    /// <summary>
    /// Starts a scenario, or several across several environments, and returns immediately.
    ///
    /// Returns 202 rather than 200. The run has been accepted, not carried out — and a pipeline
    /// that treated an immediate 200 as "the tests passed" would be a pipeline that never goes red.
    /// </summary>
    [HttpPost("projects/{projectId:guid}/runs")]
    public async Task<IActionResult> Start(
        Guid projectId, [FromBody] StartRunRequest request, CancellationToken cancellationToken)
    {
        if (Scoped() is { } scoped && scoped != projectId)
        {
            return Forbid(ApiKeyAuthenticationHandler.Scheme);
        }

        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        var scenarios = await ScenariosAsync(projectId, request, cancellationToken);
        if (scenarios.Count == 0) return BadRequest(new { error = "No scenario matched." });

        var environments = await EnvironmentsAsync(projectId, request, cancellationToken);
        if (environments.Count == 0) return BadRequest(new { error = "No environment matched." });

        try
        {
            // One scenario in one environment is a run; anything else is a batch. Both are offered
            // through one endpoint because a pipeline should not have to know which shape it asked
            // for before it asks.
            if (scenarios.Count == 1 && environments.Count == 1)
            {
                var run = await runs.QueueAsync(
                    scenarios[0], environments[0], RunTrigger.Api, cancellation: cancellationToken);

                await queue.EnqueueAsync(new QueuedRun(run.Id, run.WorkspaceId), cancellationToken);
                await RecordAsync(projectId, "run", run.Id, cancellationToken);

                return Accepted(new
                {
                    runId = run.Id,
                    status = run.Status.ToString(),
                    url = $"/projects/{projectId}/runs/{run.Id}",
                    report = $"/api/v1/runs/{run.Id}/junit",
                });
            }

            var batch = await matrix.QueueAsync(
                projectId, scenarios, environments, request.Name, cancellationToken);

            foreach (var run in await db.Runs
                         .Where(run => run.BatchId == batch.Id)
                         .ToListAsync(cancellationToken))
            {
                run.Trigger = RunTrigger.Api;
            }

            batch.Trigger = RunTrigger.Api;
            await db.SaveChangesAsync(cancellationToken);
            await RecordAsync(projectId, "batch", batch.Id, cancellationToken);

            return Accepted(new
            {
                batchId = batch.Id,
                total = batch.Total,
                url = $"/projects/{projectId}/matrix/{batch.Id}",
                report = $"/api/v1/batches/{batch.Id}/junit",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Where a run is, in the shape a polling loop wants.
    ///
    /// <c>finished</c> is a boolean rather than something to infer from the status word: a pipeline
    /// script should not have to hold a list of which statuses are terminal, and a list held in two
    /// places drifts.
    /// </summary>
    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> Run(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);
        if (run is null) return NotFound();

        if (Scoped() is { } scoped && scoped != run.ProjectId)
        {
            return Forbid(ApiKeyAuthenticationHandler.Scheme);
        }

        return Ok(new
        {
            runId = run.Id,
            status = run.Status.ToString(),
            finished = run.Status is not (RunStatus.Queued or RunStatus.Running),
            passed = run.Status == RunStatus.Passed,
            outcome = run.Outcome,
            steps = run.StepsRun,
            assertionsPassed = run.AssertionsPassed,
            assertionsFailed = run.AssertionsFailed,
            durationMs = run.DurationMs,
        });
    }

    [HttpGet("batches/{batchId:guid}")]
    public async Task<IActionResult> Batch(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.RunBatches
            .FirstOrDefaultAsync(candidate => candidate.Id == batchId, cancellationToken);

        if (batch is null) return NotFound();

        if (Scoped() is { } scoped && scoped != batch.ProjectId)
        {
            return Forbid(ApiKeyAuthenticationHandler.Scheme);
        }

        var grid = await matrix.ReadAsync(batchId, cancellationToken);
        if (grid is null) return NotFound();

        return Ok(new
        {
            batchId = grid.BatchId,
            state = grid.State,
            finished = grid.Done >= grid.Total,
            passed = grid.State == "Passed",
            total = grid.Total,
            done = grid.Done,
        });
    }

    /// <summary>
    /// The run as JUnit XML, which is what makes a build go red.
    ///
    /// Served as <c>application/xml</c> with a filename, so <c>curl -O</c> lands something a CI
    /// step can point its test-report collector at without renaming it.
    /// </summary>
    [HttpGet("runs/{runId:guid}/junit")]
    public async Task<IActionResult> RunReport(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);
        if (run is null) return NotFound();

        if (Scoped() is { } scoped && scoped != run.ProjectId)
        {
            return Forbid(ApiKeyAuthenticationHandler.Scheme);
        }

        var document = await reports.ForRunAsync(runId, cancellationToken);
        return document is null ? NotFound() : Xml(document, $"proofflow-run-{runId}.xml");
    }

    [HttpGet("batches/{batchId:guid}/junit")]
    public async Task<IActionResult> BatchReport(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.RunBatches
            .FirstOrDefaultAsync(candidate => candidate.Id == batchId, cancellationToken);

        if (batch is null) return NotFound();

        if (Scoped() is { } scoped && scoped != batch.ProjectId)
        {
            return Forbid(ApiKeyAuthenticationHandler.Scheme);
        }

        var document = await reports.ForBatchAsync(batchId, cancellationToken);
        return document is null ? NotFound() : Xml(document, $"proofflow-batch-{batchId}.xml");
    }

    private FileContentResult Xml(System.Xml.Linq.XDocument document, string filename)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(document.Declaration + Environment.NewLine + document);
        return File(bytes, "application/xml; charset=utf-8", filename);
    }

    /// <summary>
    /// Which scenarios to run: named, listed by id, or all of them.
    ///
    /// Names as well as ids because a pipeline is written by hand and checked into a repository,
    /// and a YAML file full of GUIDs is a file nobody can review.
    /// </summary>
    private async Task<List<Guid>> ScenariosAsync(
        Guid projectId, StartRunRequest request, CancellationToken cancellationToken)
    {
        var query = db.Scenarios
            .Where(scenario => scenario.ProjectId == projectId && scenario.ArchivedAt == null);

        if (request.ScenarioIds is { Length: > 0 } ids)
        {
            query = query.Where(scenario => ids.Contains(scenario.Id));
        }
        else if (request.Scenarios is { Length: > 0 } names)
        {
            query = query.Where(scenario => names.Contains(scenario.Name));
        }

        return await query.Select(scenario => scenario.Id).ToListAsync(cancellationToken);
    }

    private async Task<List<Guid>> EnvironmentsAsync(
        Guid projectId, StartRunRequest request, CancellationToken cancellationToken)
    {
        var query = db.Environments.Where(environment => environment.ProjectId == projectId);

        if (request.EnvironmentIds is { Length: > 0 } ids)
        {
            query = query.Where(environment => ids.Contains(environment.Id));
        }
        else if (request.Environments is { Length: > 0 } names)
        {
            query = query.Where(environment => names.Contains(environment.Name));
        }
        else
        {
            // No environment named means the one that is not production. A pipeline that forgot to
            // say should not send traffic at production by default.
            query = query.Where(environment => !environment.IsProduction);
        }

        return await query
            .OrderBy(environment => environment.SortOrder)
            .Select(environment => environment.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The project a key is limited to, when it is limited to one.</summary>
    private Guid? Scoped() =>
        Guid.TryParse(User.FindFirstValue(ApiKeyAuthenticationHandler.ProjectClaim), out var id)
            ? id
            : null;

    private Task RecordAsync(Guid projectId, string kind, Guid id, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEntry($"ci.{kind}.started", projectId, kind, id,
                User.FindFirstValue(ClaimTypes.Name)),
            cancellationToken);
}

/// <summary>
/// What a pipeline asks for.
///
/// Everything optional. The commonest call is an empty body — "run this project's tests where you
/// normally would" — and a body that has to name every scenario is a body that breaks when somebody
/// adds one.
/// </summary>
public sealed record StartRunRequest
{
    public Guid[]? ScenarioIds { get; init; }

    /// <summary>Scenario names, for a pipeline file a person has to be able to read.</summary>
    public string[]? Scenarios { get; init; }

    public Guid[]? EnvironmentIds { get; init; }

    public string[]? Environments { get; init; }

    /// <summary>What to call the batch — a commit sha or a branch name earns its place here.</summary>
    public string? Name { get; init; }
}
