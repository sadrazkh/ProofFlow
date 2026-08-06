using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Projects;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

[Authorize]
[Route("projects")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class ProjectsController(
    ProofFlowDbContext db,
    ICurrentUser me,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(bool archived = false, CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .Where(p => archived || p.ArchivedAt == null)
            .OrderBy(p => p.ArchivedAt != null)
            .ThenByDescending(p => p.UpdatedAt)
            .Select(p => new ProjectCardViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Description = p.Description,
                Accent = p.Accent,
                IsArchived = p.ArchivedAt != null,
            })
            .ToListAsync(cancellationToken);

        ViewData["Title"] = localizer["project.title"].Value;
        ViewData["Breadcrumbs"] = Crumbs((localizer["project.title"].Value, null));

        return View(new ProjectListViewModel
        {
            Projects = projects,
            ShowArchived = archived,
            CanCreate = me.Can(Capability.ManageProject),
        });
    }

    [HttpGet("new")]
    [Authorize(Policy = Policies.ManageProject)]
    public IActionResult Create()
    {
        ViewData["Title"] = localizer["project.newTitle"].Value;
        ViewData["Breadcrumbs"] = Crumbs(
            (localizer["project.title"].Value, "/projects"),
            (localizer["project.newTitle"].Value, null));

        return View(new ProjectFormViewModel());
    }

    [HttpPost("new")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Create(ProjectFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ProjectFormViewModel.Accents.Contains(model.Accent)) model.Accent = "indigo";
        if (!ModelState.IsValid) return View(model);

        var taken = await db.Projects.Select(p => p.Slug).ToListAsync(cancellationToken);

        var project = new Project
        {
            WorkspaceId = me.WorkspaceId!.Value,
            Name = model.Name.Trim(),
            Slug = Slug.Unique(Slug.From(model.Name, "project"), taken),
            Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(),
            Accent = model.Accent,
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "project.created", project.Id, nameof(Project), project.Id, project.Name), cancellationToken);

        TempData.Success(localizer["project.created", project.Name]);
        return Redirect($"/projects/{project.Id}");
    }

    [HttpGet("{projectId:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Details(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        ViewData["Title"] = project.Name;
        ViewData["Breadcrumbs"] = Crumbs(
            (localizer["project.title"].Value, "/projects"),
            (project.Name, null));

        return View(new ProjectCardViewModel
        {
            Id = project.Id,
            Name = project.Name,
            Slug = project.Slug,
            Description = project.Description,
            Accent = project.Accent,
            IsArchived = project.IsArchived,
        });
    }

    [HttpGet("{projectId:guid}/settings")]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Settings(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        ViewData["Title"] = localizer["nav.settings"].Value;
        ViewData["Breadcrumbs"] = Crumbs(
            (localizer["project.title"].Value, "/projects"),
            (project.Name, $"/projects/{project.Id}"),
            (localizer["nav.settings"].Value, null));

        return View(new ProjectFormViewModel
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            Accent = project.Accent,
        });
    }

    [HttpPost("{projectId:guid}/settings")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Settings(Guid projectId, ProjectFormViewModel model, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        if (!ProjectFormViewModel.Accents.Contains(model.Accent)) model.Accent = project.Accent;
        if (!ModelState.IsValid) { model.Id = projectId; return View(model); }

        project.Name = model.Name.Trim();
        project.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        project.Accent = model.Accent;

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(
            "project.updated", project.Id, nameof(Project), project.Id, project.Name), cancellationToken);

        TempData.Success(localizer["project.updated"]);
        return Redirect($"/projects/{project.Id}/settings");
    }

    [HttpPost("{projectId:guid}/archive")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Archive(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        // Archived, not deleted. A project holds the only record of what an API used to return;
        // making that one click away from gone is not a kindness.
        project.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "project.archived", project.Id, nameof(Project), project.Id, project.Name), cancellationToken);

        TempData.Success(localizer["project.archived", project.Name]);
        return Redirect("/projects");
    }

    [HttpPost("{projectId:guid}/restore")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageProject)]
    public async Task<IActionResult> Restore(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        project.ArchivedAt = null;
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "project.restored", project.Id, nameof(Project), project.Id, project.Name), cancellationToken);

        TempData.Success(localizer["project.restored", project.Name]);
        return Redirect($"/projects/{project.Id}");
    }

    private static List<(string Label, string? Href)> Crumbs(params (string Label, string? Href)[] items) => [.. items];
}
