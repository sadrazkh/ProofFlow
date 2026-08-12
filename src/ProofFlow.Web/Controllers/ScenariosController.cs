using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Infrastructure.Ai;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Scenarios;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Scenarios and the canvas that draws them.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/scenarios")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed partial class ScenariosController(
    ProofFlowDbContext db,
    ScenarioGraphService graphs,
    ICurrentUser me,
    IAuditLog audit,
    ScenarioAuthor author,
    IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var rows = await db.Scenarios
            .Where(s => s.ProjectId == projectId && s.ArchivedAt == null)
            .OrderBy(s => s.Name)
            .Select(s => new ScenarioSummary(
                s.Id,
                s.Name,
                s.Description,
                db.WorkflowNodes.Count(n => n.ScenarioVersionId == s.DraftVersionId),
                s.PublishedVersionId != null,
                db.ScenarioVersions
                    .Where(v => v.Id == s.DraftVersionId)
                    .Select(v => (bool?)v.IsValid)
                    .FirstOrDefault(),
                s.UpdatedAt))
            .ToListAsync(cancellationToken);

        Breadcrumbs(project.Name, projectId, null);
        ViewData["Title"] = localizer["nav.scenarios"].Value;

        return View(new ScenarioListViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Scenarios = rows,
            CanEdit = me.Can(Capability.EditTest),
        });
    }

    [HttpGet("new")]
    [Authorize(Policy = Policies.CreateTest)]
    public async Task<IActionResult> New(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var name = localizer["scenario.untitled"].Value;
        var unique = name;
        var suffix = 2;

        while (await db.Scenarios.AnyAsync(s => s.ProjectId == projectId && s.Name == unique, cancellationToken))
        {
            unique = $"{name} {suffix++}";
        }

        var scenario = new TestScenario
        {
            WorkspaceId = me.WorkspaceId!.Value,
            ProjectId = projectId,
            Name = unique,
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Scenarios.Add(scenario);
        await db.SaveChangesAsync(cancellationToken);

        // A start node from the beginning. An empty canvas with no way to begin is a puzzle, and
        // the validator would immediately complain about a graph nobody has drawn yet.
        await graphs.SaveAsync(scenario, new GraphDto
        {
            Nodes =
            [
                new()
                {
                    Id = "start", Key = "core.start",
                    Name = localizer["node.core.start.title"].Value, X = 80, Y = 80,
                },
            ],
            Edges = [],
        }, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "scenario.created", projectId, nameof(TestScenario), scenario.Id, scenario.Name),
            cancellationToken);

        return Redirect($"/projects/{projectId}/scenarios/{scenario.Id}");
    }

    [HttpGet("{scenarioId:guid}")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Canvas(
        Guid projectId, Guid scenarioId, CancellationToken cancellationToken)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, cancellationToken);
        if (scenario is null) return NotFound();

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);

        Breadcrumbs(project.Name, projectId, scenario.Name);
        ViewData["Title"] = scenario.Name;

        // The canvas is the page: the shell's chrome would take a third of the height it needs.
        ViewData["PageClass"] = "page-canvas";

        return View(new ScenarioCanvasViewModel
        {
            ProjectId = projectId,
            Scenario = scenario,
            CanEdit = me.Can(Capability.EditTest),
            CanRun = me.Can(Capability.RunTest),
            Inputs = ScenarioInputs.Read(scenario.InputsJson),

            // The button only exists where somebody has put a key in. A feature that asks for a
            // key when pressed is a feature that gets pressed once.
            CanDraw = me.Can(Capability.EditTest)
                      && await db.Workspaces.AnyAsync(
                          w => w.Id == me.WorkspaceId && w.AiKeyCipher != null, cancellationToken),
            Environments = await db.Environments
                .Where(e => e.ProjectId == projectId)
                .OrderBy(e => e.SortOrder)
                .Select(e => new ScenarioEnvironment(e.Id, e.Name, e.IsProduction))
                .ToListAsync(cancellationToken),
        });
    }

    /// <summary>Every node type this version knows. Fetched once when the canvas opens.</summary>
    [HttpGet("catalogue")]
    [Authorize(Policy = Policies.ViewProject)]
    public IActionResult Catalogue() => Json(ScenarioGraphService.Catalogue());

    [HttpGet("{scenarioId:guid}/graph")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Graph(
        Guid projectId, Guid scenarioId, CancellationToken cancellationToken)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, cancellationToken);
        if (scenario is null) return NotFound();

        return Json(scenario.DraftVersionId is { } id
            ? await graphs.LoadAsync(id, cancellationToken)
            : new GraphDto { Nodes = [], Edges = [] });
    }

    [HttpPost("{scenarioId:guid}/graph")]
    [Authorize(Policy = Policies.EditTest)]
    public async Task<IActionResult> SaveGraph(
        Guid projectId, Guid scenarioId, [FromBody] GraphDto graph, CancellationToken cancellationToken)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, cancellationToken);
        if (scenario is null) return NotFound();

        var result = await graphs.SaveAsync(scenario, graph, cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "scenario.saved", projectId, nameof(ScenarioVersion), result.VersionId, scenario.Name,
            new Dictionary<string, string?>
            {
                ["nodes"] = graph.Nodes.Count.ToString(),
                ["valid"] = result.IsValid ? "true" : "false",
            }), cancellationToken);

        return Json(result);
    }

    /// <summary>
    /// Checks a graph without saving it.
    ///
    /// Its own endpoint because the canvas asks while somebody is still drawing, and a check that
    /// wrote to the database on every keystroke would fill the history with noise.
    /// </summary>
    [HttpPost("validate")]
    [Authorize(Policy = Policies.ViewProject)]
    public IActionResult Validate([FromBody] GraphDto graph) =>
        Json(graphs.Validate(graph));

    [HttpPost("{scenarioId:guid}/publish")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditTest)]
    public async Task<IActionResult> Publish(
        Guid projectId, Guid scenarioId, CancellationToken cancellationToken)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, cancellationToken);
        if (scenario is null) return NotFound();

        try
        {
            var version = await graphs.PublishAsync(scenario, cancellationToken);

            await audit.RecordAsync(new AuditEntry(
                "scenario.published", projectId, nameof(ScenarioVersion), version.Id,
                $"{scenario.Name} v{version.Number}"), cancellationToken);

            TempData.Success(localizer["scenario.published", version.Number]);
        }
        catch (InvalidOperationException)
        {
            TempData.Error(localizer["scenario.cannotPublish"]);
        }

        return Redirect($"/projects/{projectId}/scenarios/{scenarioId}");
    }


    /// <summary>
    /// Saves what this scenario has to be told.
    ///
    /// A row whose name is blank is a row somebody cleared, which is how an input is removed — there
    /// is no second button for it and no confirmation, because nothing that has run is lost: a run
    /// keeps its own copy of what it was given.
    /// </summary>

    /// <summary>
    /// Asks the workspace's model to draw a scenario, and hands back a graph the canvas can show.
    ///
    /// Nothing is saved. What comes back arrives as unsaved changes on the canvas, so the person who
    /// asked reads it, moves it and decides — exactly as if they had dragged it out of the palette.
    /// A draft nobody looked at is not a test.
    /// </summary>
    [HttpPost("{scenarioId:guid}/draw")]
    [Authorize(Policy = Policies.EditTest)]
    public async Task<IActionResult> Draw(
        Guid projectId, Guid scenarioId, [FromBody] DrawRequest request,
        CancellationToken cancellationToken)
    {
        if (me.WorkspaceId is not { } workspaceId) return Forbid();

        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, cancellationToken);

        if (scenario is null) return NotFound();

        var workspace = await db.Workspaces
            .FirstOrDefaultAsync(candidate => candidate.Id == workspaceId, cancellationToken);

        if (workspace is null) return NotFound();

        var drawn = await author.DrawAsync(workspace, request.Request ?? string.Empty, cancellationToken);

        if (!drawn.Ok) return ValidationProblem(localizer[drawn.Problem!].Value);

        await audit.RecordAsync(
            new AuditEntry("scenario.drawn", projectId, nameof(TestScenario), scenarioId, scenario.Name),
            cancellationToken);

        return Json(drawn.Graph);
    }

    [HttpPost("{scenarioId:guid}/inputs")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditTest)]
    public async Task<IActionResult> Inputs(
        Guid projectId, Guid scenarioId, [FromForm] List<ScenarioInputDto> inputs,
        CancellationToken cancellationToken)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, cancellationToken);

        if (scenario is null) return NotFound();

        // Trimmed, named, and de-duplicated by name. Two inputs called the same thing would make
        // {{inputs.orderId}} mean whichever the dictionary happened to keep.
        var kept = inputs
            .Where(input => !string.IsNullOrWhiteSpace(input.Name))
            .Select(input => input with
            {
                Name = input.Name.Trim(),
                Label = string.IsNullOrWhiteSpace(input.Label) ? null : input.Label.Trim(),
                Default = string.IsNullOrWhiteSpace(input.Default) ? null : input.Default,
            })
            .GroupBy(input => input.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var bad = kept.FirstOrDefault(input => !ValidInputName().IsMatch(input.Name));

        if (bad is not null)
        {
            TempData.Error(localizer["input.name.shape", bad.Name]);
            return RedirectToAction(nameof(Canvas), new { projectId, scenarioId });
        }

        scenario.InputsJson = kept.Count == 0 ? null : ScenarioInputs.Write(kept);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEntry("scenario.inputsChanged", projectId, nameof(TestScenario), scenarioId,
                scenario.Name),
            cancellationToken);

        return RedirectToAction(nameof(Canvas), new { projectId, scenarioId });
    }

    /// <summary>The shape a reference can actually use: {{inputs.orderId}} and nothing exotic.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial System.Text.RegularExpressions.Regex ValidInputName();

    [HttpPost("{scenarioId:guid}/rename")]
    [Authorize(Policy = Policies.EditTest)]
    public async Task<IActionResult> Rename(
        Guid projectId, Guid scenarioId, [FromBody] RenameScenarioCommand command,
        CancellationToken cancellationToken)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, cancellationToken);
        if (scenario is null) return NotFound();

        if (string.IsNullOrWhiteSpace(command.Name))
            return ValidationProblem(localizer["error.required"].Value);

        var name = command.Name.Trim();

        if (await db.Scenarios.AnyAsync(
                s => s.ProjectId == projectId && s.Name == name && s.Id != scenarioId, cancellationToken))
        {
            return ValidationProblem(localizer["scenario.nameTaken", name].Value);
        }

        scenario.Name = name;
        scenario.Description = command.Description;
        await db.SaveChangesAsync(cancellationToken);

        return Json(new { name = scenario.Name });
    }

    private void Breadcrumbs(string projectName, Guid projectId, string? scenarioName)
    {
        var crumbs = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (localizer["nav.scenarios"].Value, scenarioName is null ? null : $"/projects/{projectId}/scenarios"),
        };

        if (scenarioName is not null) crumbs.Add((scenarioName, null));
        ViewData["Breadcrumbs"] = crumbs;
    }
}

/// <summary>What somebody typed into the box before pressing the button.</summary>
public sealed record DrawRequest
{
    public string? Request { get; init; }
}

public sealed record RenameScenarioCommand(string? Name, string? Description);
