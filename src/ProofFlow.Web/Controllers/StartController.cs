using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The first minute.
///
/// Everything this product does is available from the sidebar, and that is exactly the problem for
/// somebody who has just signed in: eleven destinations, none of which is «start here». This page
/// is the shortest path from an empty workspace to a request that has been sent, and it earns its
/// place by having a button that does the setup rather than a paragraph describing it.
/// </summary>
[Authorize]
[Route("start")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class StartController(
    ProofFlowDbContext db,
    ICurrentUser me,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    /// <summary>What the sandbox project is called, and how it is found again.</summary>
    public const string SandboxName = "Sandbox";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = localizer["demo.start"].Value;

        var projects = await db.Projects
            .Where(project => project.ArchivedAt == null)
            .OrderBy(project => project.Name)
            .Select(project => new { project.Id, project.Name })
            .ToListAsync(cancellationToken);

        // The demo test, when the demo data is here. Linking to «a scenario» in general would be a
        // link to a list, and the point of this page is that every button lands somewhere useful.
        var flow = await db.Scenarios
            .Where(scenario => scenario.ArchivedAt == null)
            .OrderBy(scenario => scenario.CreatedAt)
            .Select(scenario => new { scenario.Id, scenario.ProjectId, scenario.Name })
            .FirstOrDefaultAsync(cancellationToken);

        return View(new StartViewModel
        {
            FirstProjectId = projects.FirstOrDefault()?.Id,
            FirstProjectName = projects.FirstOrDefault()?.Name,
            ProjectCount = projects.Count,
            FlowId = flow?.Id,
            FlowProjectId = flow?.ProjectId,
            FlowName = flow?.Name,
            CanCreate = me.Can(Capability.ManageProject),
        });
    }

    /// <summary>
    /// Makes a project pointed at the API this application serves, and opens it.
    ///
    /// The one thing standing between somebody and their first request is an address they trust
    /// enough to send to. There is one in the box — the fake API — and it is loopback, which the
    /// URL guard refuses by default. So the environment is created with that allowance switched on
    /// deliberately, in one place, rather than left as something to discover after a refusal.
    ///
    /// Pressing it twice opens the one that exists instead of making a second.
    /// </summary>
    [HttpPost("sandbox")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Sandbox(CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        var existing = await db.Projects
            .FirstOrDefaultAsync(project => project.Name == SandboxName, cancellationToken);

        if (existing is not null)
        {
            TempData.Success(localizer["demo.sandbox.exists", existing.Name]);
            return Redirect($"/projects/{existing.Id}/request?url={FirstRequest}");
        }

        var project = new Project
        {
            WorkspaceId = workspaceId,
            Name = SandboxName,
            Slug = Slug.From(SandboxName, "project"),
            Description = localizer["demo.sandbox.body"].Value,
            Accent = "teal",
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Projects.Add(project);

        // The address of this application, from this application: whatever it is being served on,
        // which is the only value that works whether somebody pressed F5 in Visual Studio, ran
        // dotnet run, or brought the container up on a different port.
        var baseUrl = $"{Request.Scheme}://{Request.Host}/fake";

        db.Environments.Add(new ProjectEnvironment
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Built-in API",
            Slug = "built-in",
            BaseUrl = baseUrl,
            Kind = EnvironmentKind.Development,
            IsProduction = false,
            SortOrder = 0,

            // Loopback, deliberately. The guard refuses private addresses precisely so that this
            // is a decision somebody made rather than a default nobody noticed.
            AllowPrivateNetwork = true,
        });

        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEntry("project.created", project.Id, "Project", project.Id, project.Name),
            cancellationToken);

        TempData.Success(localizer["demo.sandbox.made", project.Name]);

        // With a first address already in the box. Landing on an empty form is landing on a
        // decision — and the obvious first thing to type, /categories, is the one endpoint that
        // answers 401 until somebody has been through the authorisation panel.
        return Redirect($"/projects/{project.Id}/request?url={FirstRequest}");
    }

    /// <summary>Something that answers without a token, so the first press produces a body.</summary>
    private const string FirstRequest = "/records/1";
}
