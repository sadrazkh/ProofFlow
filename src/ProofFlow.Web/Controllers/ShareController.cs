using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// A run's result, for somebody without an account.
///
/// Anonymous at the framework level with the credential checked by hand — the
/// <see cref="RunnerApiController"/> pattern. What the token buys is the summary and nothing else:
/// scenario name, environment, each step's verdict and duration, and the outcome sentence.
///
/// Deliberately absent: the log, the payloads, the graph snapshot and the typed inputs. The first
/// two are what retention exists to clear; the last two are documented as unredacted, because they
/// hold what a person wrote rather than what a server answered. Everything this page renders has
/// been through the redaction scope, which is the property that makes it safe to hand out.
/// </summary>
[AllowAnonymous]
[Route("share")]
public sealed class ShareController(ProofFlowDbContext db) : Controller
{
    [HttpGet("runs/{token}")]
    public async Task<IActionResult> Run(string token, CancellationToken cancellationToken)
    {
        if (token.Length is < 40 or > 50) return NotFound();

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        // No signed-in workspace on an anonymous request, so the tenant filter would hide
        // everything; the share hash is itself the authorisation.
        var run = await db.Runs.IgnoreQueryFilters()
            .Where(candidate => candidate.ShareHash == hash)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.ScenarioId,
                candidate.EnvironmentId,
                candidate.ProjectId,
                candidate.Status,
                candidate.Outcome,
                candidate.StartedAt,
                candidate.FinishedAt,
                candidate.DurationMs,
                candidate.AssertionsPassed,
                candidate.AssertionsFailed,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (run is null) return NotFound();

        var steps = await db.NodeRuns.IgnoreQueryFilters()
            .Where(node => node.TestRunId == run.Id)
            .OrderBy(node => node.SortOrder)
            .Select(node => new SharedStep(
                node.NodeName, node.Status.ToString(), node.DurationMs, node.Iteration))
            .ToListAsync(cancellationToken);

        var scenario = await db.Scenarios.IgnoreQueryFilters()
            .Where(candidate => candidate.Id == run.ScenarioId)
            .Select(candidate => candidate.Name)
            .FirstOrDefaultAsync(cancellationToken);

        var environment = run.EnvironmentId is { } environmentId
            ? await db.Environments.IgnoreQueryFilters()
                .Where(candidate => candidate.Id == environmentId)
                .Select(candidate => candidate.Name)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var project = await db.Projects.IgnoreQueryFilters()
            .Where(candidate => candidate.Id == run.ProjectId)
            .Select(candidate => candidate.Name)
            .FirstOrDefaultAsync(cancellationToken);

        // Not indexed and not cached anywhere in between: a link somebody was handed is not a page
        // a search engine should be able to find on its own.
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers.CacheControl = "private, no-store";

        return View(new SharedRunViewModel
        {
            ProjectName = project ?? string.Empty,
            ScenarioName = scenario ?? string.Empty,
            EnvironmentName = environment,
            Status = run.Status,
            Outcome = run.Outcome,
            StartedAt = run.StartedAt,
            DurationMs = run.DurationMs,
            AssertionsPassed = run.AssertionsPassed,
            AssertionsFailed = run.AssertionsFailed,
            Steps = steps,
        });
    }
}
