using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Infrastructure.Ai;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Scenarios;
using ProofFlow.TestEngine.Http;
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
    public async Task<IActionResult> Index(
        Guid projectId, int? page, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var query = db.Scenarios.Where(s => s.ProjectId == projectId && s.ArchivedAt == null);

        var total = await query.CountAsync(cancellationToken);
        var current = Paging.Clamp(page, Paging.DefaultPageSize, total);

        // Paged, and it took an imported collection to notice: eleven thousand scenarios rendered
        // as eleven thousand rows, which is not a list anybody can use. Imports produce endpoints
        // now, so this will rarely be long again — but «rarely» is not a reason to render
        // everything, and the scenario list is the other one that never paged.
        var rows = await query
            .OrderBy(s => s.Name)
            .Skip((current - 1) * Paging.DefaultPageSize)
            .Take(Paging.DefaultPageSize)
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
            Page = new Paging
            {
                Page = current,
                PageSize = Paging.DefaultPageSize,
                Total = total,
                Path = $"/projects/{projectId}/scenarios",
            },
            CanEdit = me.Can(Capability.EditTest),
            CanRecord = me.Can(Capability.RecordBaseline),
        });
    }

    /// <summary>
    /// Turns a scenario that is one call into the endpoint it always was.
    ///
    /// Offered rather than done automatically, and offered per scenario rather than in bulk: the
    /// name might mean something to somebody, and a button that quietly rewrote eleven thousand
    /// rows is not a button anybody should press without looking. What it moves is the request —
    /// method, address, headers, body — which is everything a one-step graph actually held.
    ///
    /// The scenario is archived, not deleted. It might be referenced by a schedule, and a run from
    /// March still names it.
    /// </summary>
    [HttpPost("{scenarioId:guid}/move-to-endpoint")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.RecordBaseline)]
    public async Task<IActionResult> MoveToEndpoint(
        Guid projectId, Guid scenarioId, CancellationToken cancellationToken)
    {
        var scenario = await db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, cancellationToken);
        if (scenario is null) return NotFound();

        var nodes = await db.WorkflowNodes
            .Where(node => node.ScenarioVersionId == scenario.DraftVersionId)
            .Select(node => new { node.Key, node.PropertiesJson })
            .ToListAsync(cancellationToken);

        // One request and nothing else that sends anything. Two requests is a chain, however
        // short, and moving it would throw the second one away.
        var requests = nodes.Where(node => node.Key == "http.request").ToList();

        if (requests.Count != 1)
        {
            TempData.Error(localizer["scenario.notOneCall"]);
            return Redirect($"/projects/{projectId}/scenarios");
        }

        if (await db.Baselines.AnyAsync(
                b => b.ProjectId == projectId && b.Name == scenario.Name, cancellationToken))
        {
            TempData.Error(localizer["baseline.nameTaken", scenario.Name]);
            return Redirect($"/projects/{projectId}/scenarios");
        }

        var endpoint = new Baseline
        {
            WorkspaceId = scenario.WorkspaceId,
            ProjectId = projectId,
            EnvironmentId = scenario.EnvironmentId,
            Name = scenario.Name,
            Description = scenario.Description,
            RequestJson = RequestFrom(requests[0].PropertiesJson),
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Baselines.Add(endpoint);

        // Archived rather than deleted: a schedule may name it, and a run from March still does.
        scenario.ArchivedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "baseline.created", projectId, nameof(Baseline), endpoint.Id, endpoint.Name,
            new Dictionary<string, string?> { ["from"] = "scenario" }), cancellationToken);

        TempData.Success(localizer["scenario.moved", endpoint.Name]);
        return Redirect($"/projects/{projectId}/endpoints/{endpoint.Id}");
    }

    /// <summary>
    /// The node's properties, as the request the endpoint page reads.
    ///
    /// A node stores its request flattened — method and url as strings, headers and query as JSON
    /// inside a string — because that is what a property grid can edit. An endpoint stores an
    /// HttpRequestDefinition. This is the one place the two shapes meet.
    /// </summary>
    private static string RequestFrom(string? propertiesJson)
    {
        var properties = propertiesJson is { Length: > 0 }
            ? JsonSerializer.Deserialize<Dictionary<string, string?>>(propertiesJson) ?? []
            : [];

        var request = new HttpRequestDefinition
        {
            Method = properties.GetValueOrDefault("method") ?? "GET",
            Url = properties.GetValueOrDefault("url") ?? string.Empty,
            Headers = Entries(properties.GetValueOrDefault("headers")),
            Query = Entries(properties.GetValueOrDefault("query")),
            Body = Body(properties),
        };

        return JsonSerializer.Serialize(
            request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        static IReadOnlyList<KeyValueEntry> Entries(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];

            try
            {
                return JsonSerializer.Deserialize<List<KeyValueEntry>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (JsonException)
            {
                // A property somebody hand-edited. Losing the headers is bad; refusing to move the
                // endpoint at all because of them is worse, and the detail page shows what arrived.
                return [];
            }
        }

        static RequestBody? Body(Dictionary<string, string?> properties)
        {
            var content = properties.GetValueOrDefault("body");
            if (string.IsNullOrWhiteSpace(content)) return null;

            return new RequestBody
            {
                Kind = properties.GetValueOrDefault("bodyKind") switch
                {
                    "json" => BodyKind.Json,
                    "form" => BodyKind.FormUrlEncoded,
                    "raw" => BodyKind.Xml,
                    _ => BodyKind.Text,
                },
                Content = content,
            };
        }
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

        // A chain from the beginning: start, a request, and a check on what came back.
        //
        // It used to be a lone start node, which is a canvas with one dot on it — and the shape of
        // a thing teaches what the thing is for. A scenario is a chain, and somebody who opens a
        // new one should be looking at the smallest chain there is rather than at an empty page
        // with a palette beside it. The request has no address yet, so nothing runs until somebody
        // fills one in; that is a form to complete, not a puzzle to solve.
        await graphs.SaveAsync(scenario, new GraphDto
        {
            Nodes =
            [
                new()
                {
                    Id = "start", Key = "core.start",
                    Name = localizer["node.core.start.title"].Value, X = 80, Y = 120,
                },
                new()
                {
                    Id = "request", Key = "http.request",
                    Name = localizer["node.http.request.title"].Value, X = 360, Y = 120,
                    Properties = new Dictionary<string, string?>
                    {
                        ["method"] = "GET",
                        ["url"] = "{{environment.baseUrl}}/",
                    },
                },
                new()
                {
                    Id = "check", Key = "assert.status",
                    Name = localizer["node.assert.status.title"].Value, X = 640, Y = 120,
                    Properties = new Dictionary<string, string?> { ["expected"] = "200" },
                },
            ],
            Edges =
            [
                new() { Id = "e1", FromId = "start", FromPort = "out", ToId = "request", ToPort = "in" },
                new() { Id = "e2", FromId = "request", FromPort = "out", ToId = "check", ToPort = "in" },

                // The data edge, without which the check has nothing to look at.
                new()
                {
                    Id = "e3", FromId = "request", FromPort = "response",
                    ToId = "check", ToPort = "response",
                },
            ],
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
