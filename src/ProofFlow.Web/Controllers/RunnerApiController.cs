using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Runners;
using ProofFlow.Domain.Runners;
using ProofFlow.Infrastructure.Runners;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The three calls an agent makes, and the only surface it can reach.
///
/// Anonymous at the framework level and authenticated by hand, which is deliberate: an agent holds
/// a runner token, not a session and not an API key, and folding it into either of those schemes
/// would mean a runner token could be used somewhere a runner has no business being. Everything
/// here checks the token itself and acts as that runner and nothing else.
///
/// The whole conversation is outbound-only. Nothing in this product ever connects to an agent —
/// there is nothing to connect to, which is the point of having one at all.
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("api/v1/runners")]
public sealed class RunnerApiController(
    RunnerService runners,
    RunnerJobs jobs,
    BackgroundWorkspace workspace,
    IAuditLog audit,
    ILogger<RunnerApiController> logger) : ControllerBase
{
    /// <summary>The header an agent presents its token in.</summary>
    public const string TokenHeader = "X-ProofFlow-Runner";

    /// <summary>
    /// Redeems an enrollment code.
    ///
    /// The one call that does not need a token, because it is how a token is obtained. Rate-limited
    /// with the same policy as sign-in: a code is short, and short things get guessed at.
    /// </summary>
    [HttpPost("enroll")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> Enroll(
        [FromBody] EnrollRequest request, CancellationToken cancellationToken)
    {
        var enrolled = await runners.EnrollAsync(
            request.Code, request.Hostname, request.Version, cancellationToken);

        if (enrolled is null)
        {
            // One answer for "no such code", "already used" and "expired". Three answers would let
            // somebody with a list of guesses learn which ones were once real.
            logger.LogWarning("A runner enrollment was refused.");
            return Unauthorized(new { error = "That code is not usable." });
        }

        // Recorded as the workspace the code belonged to, which is the only thing that identifies
        // this caller — there is no user here.
        workspace.ActFor(enrolled.Runner.WorkspaceId);

        await audit.RecordAsync(
            new AuditEntry("runner.enrolled", null, nameof(Runner), enrolled.Runner.Id,
                enrolled.Runner.Name),
            cancellationToken);

        return Ok(new EnrollResponse
        {
            RunnerId = enrolled.Runner.Id,
            Name = enrolled.Runner.Name,
            Token = enrolled.Token,
            SigningKey = enrolled.SigningKey,
        });
    }

    /// <summary>
    /// Asks for work.
    ///
    /// 204 when there is none, which is the answer most of the time — an agent polls once a minute
    /// and a workspace does not queue a run a minute. A body that said "nothing" would be a body
    /// somebody has to parse to learn nothing.
    /// </summary>
    [HttpPost("jobs/claim")]
    public async Task<IActionResult> Claim(CancellationToken cancellationToken)
    {
        if (await AuthenticateAsync(cancellationToken) is not { } runner) return Unauthorized();

        var job = await jobs.ClaimAsync(runner, cancellationToken);

        return job is null ? NoContent() : Ok(job);
    }

    /// <summary>Reports what happened.</summary>
    [HttpPost("jobs/result")]
    public async Task<IActionResult> Result(
        [FromBody] JobResult result, CancellationToken cancellationToken)
    {
        if (await AuthenticateAsync(cancellationToken) is not { } runner) return Unauthorized();

        // Not found rather than forbidden when the run is not this runner's. Telling an agent that
        // a run exists but belongs to somebody else is telling it something it has no use for.
        return await jobs.ReportAsync(runner, result, cancellationToken) ? Ok() : NotFound();
    }

    /// <summary>
    /// Reads the token, finds the runner, and puts the request in that runner's workspace.
    ///
    /// The workspace comes from the token rather than from anything the agent sent. An agent that
    /// could name its own workspace would be an agent that could ask for another tenant's work.
    /// </summary>
    private async Task<Runner?> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var token = Request.Headers[TokenHeader].ToString();

        var runner = await runners.AuthenticateAsync(token, cancellationToken);

        if (runner is not null) workspace.ActFor(runner.WorkspaceId);

        return runner;
    }
}
