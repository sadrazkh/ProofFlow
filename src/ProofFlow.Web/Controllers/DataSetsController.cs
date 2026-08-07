using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Capture;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Data;
using ProofFlow.Infrastructure.Data;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The inputs a scenario runs against: typing them, pasting them, and freezing them into versions.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/datasets")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class DataSetsController(
    ProofFlowDbContext db,
    DataSetService sets,
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

        var rows = await db.DataSets
            .Where(d => d.ProjectId == projectId && d.ArchivedAt == null)
            .OrderBy(d => d.Name)
            .Select(d => new DataSetSummary(
                d.Id,
                d.Name,
                d.Description,
                d.KeyColumn,
                db.DataSetVersions.Count(v => v.DataSetId == d.Id),
                db.DataSetVersions
                    .Where(v => v.Id == d.CurrentVersionId)
                    .Select(v => v.RowCount)
                    .FirstOrDefault(),
                d.UpdatedAt))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, null);
        ViewData["Title"] = localizer["nav.datasets"].Value;

        return View(new DataSetListViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Sets = rows,
            CanManage = me.Can(Capability.ManageDataSet),
        });
    }

    /// <summary>The editor with nothing in it. A separate address so it is linkable and refreshable.</summary>
    [HttpGet("new")]
    [Authorize(Policy = Policies.ManageDataSet)]
    public async Task<IActionResult> New(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        Breadcrumbs(project.Name, projectId, localizer["dataset.new"].Value);
        ViewData["Title"] = localizer["dataset.new"].Value;

        return View("Details", new DataSetDetailViewModel
        {
            ProjectId = projectId,
            DataSetId = Guid.Empty,
            Name = localizer["dataset.new"].Value,
            Versions = [],
            CurrentVersionId = null,
            CanManage = true,
        });
    }

    [HttpGet("{dataSetId:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Details(
        Guid projectId, Guid dataSetId, CancellationToken cancellationToken)
    {
        var set = await db.DataSets
            .FirstOrDefaultAsync(d => d.Id == dataSetId && d.ProjectId == projectId, cancellationToken);
        if (set is null) return NotFound();

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);

        var versions = await db.DataSetVersions
            .Where(v => v.DataSetId == dataSetId)
            .OrderByDescending(v => v.Number)
            .Select(v => new DataSetVersionRow(
                v.Id, v.Number, v.RowCount, v.Description, v.CreatedAt, v.Id == set.CurrentVersionId))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, set.Name);
        ViewData["Title"] = set.Name;

        return View(new DataSetDetailViewModel
        {
            ProjectId = projectId,
            DataSetId = dataSetId,
            Name = set.Name,
            Description = set.Description,
            Versions = versions,
            CurrentVersionId = set.CurrentVersionId,
            CanManage = me.Can(Capability.ManageDataSet),
        });
    }

    /// <summary>The rows of one version, for the editor to load.</summary>
    [HttpGet("{dataSetId:guid}/versions/{versionId:guid}/rows")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Rows(
        Guid projectId, Guid dataSetId, Guid versionId, CancellationToken cancellationToken)
    {
        var owns = await db.DataSetVersions
            .AnyAsync(v => v.Id == versionId
                           && v.DataSetId == dataSetId
                           && v.DataSet!.ProjectId == projectId, cancellationToken);

        return owns ? Json(await sets.ReadAsync(versionId, cancellationToken)) : NotFound();
    }

    /// <summary>
    /// Reads whatever was pasted, without storing anything.
    ///
    /// Deliberately a preview and not an import: the parser guesses, and a guess about somebody's
    /// data has to be shown to them before it becomes rows.
    /// </summary>
    [HttpPost("parse")]
    [Authorize(Policy = Policies.ViewProject)]
    public IActionResult Parse([FromBody] PasteCommand command) =>
        Json(DataSetService.Parse(command.Text, command.Format));

    [HttpPost("")]
    [Authorize(Policy = Policies.ManageDataSet)]
    public async Task<IActionResult> Create(
        Guid projectId, [FromBody] CreateDataSetCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return ValidationProblem(localizer["error.required"].Value);

        var name = command.Name.Trim();

        if (await db.DataSets.AnyAsync(d => d.ProjectId == projectId && d.Name == name, cancellationToken))
            return ValidationProblem(localizer["dataset.nameTaken", name].Value);

        var set = new DataSet
        {
            WorkspaceId = me.WorkspaceId!.Value,
            ProjectId = projectId,
            Name = name,
            Description = command.Description,
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.DataSets.Add(set);
        await db.SaveChangesAsync(cancellationToken);

        var version = await sets.SaveVersionAsync(set, command.Draft ?? new DataSetDraft(), cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "dataset.created", projectId, nameof(DataSet), set.Id, set.Name), cancellationToken);

        return Json(new
        {
            dataSetId = set.Id,
            versionId = version.Id,
            url = $"/projects/{projectId}/datasets/{set.Id}",
        });
    }

    /// <summary>Freezes the editor's rows as the next version. Never edits the last one.</summary>
    [HttpPost("{dataSetId:guid}/versions")]
    [Authorize(Policy = Policies.ManageDataSet)]
    public async Task<IActionResult> SaveVersion(
        Guid projectId, Guid dataSetId, [FromBody] DataSetDraft draft, CancellationToken cancellationToken)
    {
        var set = await db.DataSets
            .FirstOrDefaultAsync(d => d.Id == dataSetId && d.ProjectId == projectId, cancellationToken);
        if (set is null) return NotFound();

        var version = await sets.SaveVersionAsync(set, draft, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "dataset.versionSaved", projectId, nameof(DataSetVersion), version.Id,
            $"{set.Name} v{version.Number}",
            new Dictionary<string, string?> { ["rows"] = version.RowCount.ToString() }), cancellationToken);

        return Json(new { versionId = version.Id, number = version.Number, rows = version.RowCount });
    }

    private void Breadcrumbs(string projectName, Guid projectId, string? setName)
    {
        var crumbs = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (localizer["nav.datasets"].Value, setName is null ? null : $"/projects/{projectId}/datasets"),
        };

        if (setName is not null) crumbs.Add((setName, null));
        ViewData["Breadcrumbs"] = crumbs;
    }
}

public sealed record PasteCommand(string? Text, string? Format);

public sealed record CreateDataSetCommand
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public DataSetDraft? Draft { get; init; }
}
