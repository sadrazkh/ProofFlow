using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Contracts.Portability;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Portability;
using ProofFlow.Infrastructure.Portability.Importers;
using ProofFlow.Infrastructure.Scenarios;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// A project out of the building, and other people's files into it.
///
/// Import is three pages rather than one button, and that is the whole design. An import is not
/// undoable: a page that says "4 scenarios, 2 environments, 3 already here and left alone" before
/// anything happens is one somebody can check against what they expected. A single button that does
/// it and then tells you is not.
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class PortabilityController(
    ProofFlowDbContext db,
    BundleExporter exporter,
    BundleImporter importer,
    ScenarioGraphService graphs,
    ImportScratch scratch,
    ICurrentUser me,
    IAuditLog audit,
    IStringLocalizer localizer) : Controller
{
    /// <summary>
    /// How big a pasted or uploaded file may be.
    ///
    /// Sixteen megabytes was a guess, and a real Postman export of a real API came in at thirty —
    /// which is a description of a system rather than a database dump, and exactly the thing this
    /// page is for. Kestrel's own default stops at 30 MB too, so the endpoint lifts it explicitly;
    /// a limit that is enforced in two places at two numbers is a limit nobody can reason about.
    /// </summary>
    public const long MaxUploadBytes = 128L * 1024 * 1024;

    // ---- out ------------------------------------------------------------------------------------

    [HttpGet("export")]
    [Authorize(Policy = Policies.ExportProject)]
    public async Task<IActionResult> Export(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        var bundle = await exporter.ExportAsync(projectId, cancellationToken);

        Breadcrumbs(project.Name, projectId, localizer["portability.export"].Value);
        ViewData["Title"] = localizer["portability.export"].Value;

        return View(new ExportViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            FileName = FileNameFor(project.Slug),
            Counts = Counts(bundle),
            Secrets = [.. bundle.SecretsToSupply.Select(secret => secret.Name)],
        });
    }

    /// <summary>
    /// The file itself.
    ///
    /// A GET rather than a POST, so the address is one somebody can put in a script — this is the
    /// same document a CI job would want, and making it reachable only by pressing a button would
    /// be an odd place to draw the line.
    /// </summary>
    [HttpGet("export/download")]
    [Authorize(Policy = Policies.ExportProject)]
    public async Task<IActionResult> Download(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        var bundle = await exporter.ExportAsync(projectId, cancellationToken);

        await audit.RecordAsync(
            new AuditEntry("project.exported", projectId, "Project", projectId, project.Name),
            cancellationToken);

        return File(
            Encoding.UTF8.GetBytes(BundleJson.Write(bundle)),
            "application/json",
            FileNameFor(project.Slug));
    }

    // ---- in -------------------------------------------------------------------------------------

    [HttpGet("import")]
    [Authorize(Policy = Policies.ImportProject)]
    public async Task<IActionResult> Import(
        Guid projectId, string? source, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        Breadcrumbs(project.Name, projectId, localizer["portability.import"].Value);
        ViewData["Title"] = localizer["portability.import"].Value;

        return View(new ImportStartViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Source = ImportStartViewModel.Sources.Contains(source) ? source! : "proofflow",
        });
    }

    /// <summary>
    /// Takes the bytes and nothing else.
    ///
    /// Split out from the preview so the browser can show how much of a thirty-megabyte file has
    /// gone up. A form post gives no progress at all: the page sits on a spinner for as long as the
    /// upload takes, which on a slow connection is a minute of no information — and «is it working
    /// or has it hung» is the only question somebody has during it.
    ///
    /// It parses nothing. What comes back is a ticket and a size, and the preview reads the same
    /// bytes out of the scratch store a moment later.
    /// </summary>
    [HttpPost("import/upload")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ImportProject)]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> Upload(
        Guid projectId, [FromForm] string source, [FromForm] string? pasted,
        IFormFile? file, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        var (text, fileName, refusal) = await ReadAsync(file, pasted, cancellationToken);

        if (refusal is not null) return BadRequest(new { problem = localizer[refusal].Value });

        var ticket = scratch.Hold(me.UserId ?? Guid.Empty, new HeldImport(text!, source, fileName));

        return Json(new
        {
            ticket,
            fileName,
            bytes = Encoding.UTF8.GetByteCount(text!),
        });
    }

    /// <summary>Step two: read the file, say what it would do, change nothing.</summary>
    [HttpPost("import/preview")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ImportProject)]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> Preview(
        Guid projectId, [FromForm] string source, [FromForm] string? pasted,
        IFormFile? file, [FromForm] bool asNewProject, [FromForm] string? ticket,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        string? text;
        string? fileName;
        string? refusal;

        // Already uploaded, when the browser could run the two steps: the bytes are in the scratch
        // store and nothing goes over the wire twice. Falling back to reading the form keeps the
        // page working with no script at all, which is the only reason the fallback exists.
        if (scratch.Take(me.UserId ?? Guid.Empty, ticket) is { } held)
        {
            (text, fileName, refusal) = (held.Text, held.FileName, null);
            source = held.Source;
        }
        else
        {
            (text, fileName, refusal) = await ReadAsync(file, pasted, cancellationToken);
        }

        if (refusal is not null)
        {
            TempData.Error(localizer[refusal]);
            return RedirectToAction(nameof(Import), new { projectId, source });
        }

        var (bundle, reading) = ToBundle(text!, source);

        if (reading is not null)
        {
            TempData.Error(localizer[reading]);
            return RedirectToAction(nameof(Import), new { projectId, source });
        }

        var preview = await importer.PreviewAsync(
            bundle!, asNewProject ? null : projectId, cancellationToken);

        Breadcrumbs(project.Name, projectId, localizer["portability.import"].Value);
        ViewData["Title"] = localizer["portability.import"].Value;

        return View(new ImportPreviewViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Ticket = scratch.Hold(me.UserId ?? Guid.Empty, new HeldImport(text!, source, fileName)),
            Source = source,
            FileName = fileName,
            Preview = preview,
            AsNewProject = asNewProject,
            Notes = Notes(text!, source),

            // So the page can offer to bring them, and say how many, rather than listing names and
            // leaving somebody to type each value in by hand.
            CredentialsInFile = Credentials(text!, source)?.Count ?? 0,
        });
    }

    /// <summary>Step three, and the only one that writes.</summary>
    [HttpPost("import/apply")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ImportProject)]
    public async Task<IActionResult> Apply(
        Guid projectId, [FromForm] string ticket, [FromForm] bool asNewProject,
        [FromForm] bool bringCredentials, CancellationToken cancellationToken)
    {
        var held = scratch.Take(me.UserId ?? Guid.Empty, ticket);

        if (held is null)
        {
            // Twenty minutes, a restart, or a tab left open overnight. Asking for the file again is
            // better than importing from something nobody previewed.
            TempData.Error(localizer["import.expired"]);
            return RedirectToAction(nameof(Import), new { projectId });
        }

        var (bundle, refusal) = ToBundle(held.Text, held.Source);

        if (refusal is not null)
        {
            TempData.Error(localizer[refusal]);
            return RedirectToAction(nameof(Import), new { projectId });
        }

        // Read again here rather than carried from the preview, so the values live for the length
        // of one request and are never held anywhere between the two pages.
        var credentials = bringCredentials ? Credentials(held.Text, held.Source) : null;

        var result = await importer.ApplyAsync(
            bundle!, asNewProject ? null : projectId, credentials, cancellationToken);

        scratch.Release(me.UserId ?? Guid.Empty, ticket);

        await audit.RecordAsync(
            new AuditEntry("project.imported", result.ProjectId, "Project", result.ProjectId,
                result.ProjectName,
                new Dictionary<string, string?>
                {
                    ["source"] = held.Source,
                    ["added"] = result.Counts.Sum(count => count.Adding).ToString(),
                    ["skipped"] = result.Skipped.Count.ToString(),

                    // Worth a line in the log on its own: somebody chose to bring credentials in
                    // from a file, and «who decided that, and when» is the question afterwards.
                    ["credentials"] = bringCredentials ? "brought" : null,
                }),
            cancellationToken);

        TempData.Success(localizer["import.done", result.Counts.Sum(count => count.Adding)]);

        return Redirect($"/projects/{result.ProjectId}");
    }

    // ---- templates ------------------------------------------------------------------------------

    [HttpGet("templates")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Templates(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();

        Breadcrumbs(project.Name, projectId, localizer["template.title"].Value);
        ViewData["Title"] = localizer["template.title"].Value;

        return View(new TemplateGalleryViewModel
        {
            ProjectId = projectId,
            CanCreate = me.Can(Capability.CreateTest),
            Templates =
            [
                .. TemplateCatalogue.All.Select(template => new TemplateCardViewModel
                {
                    Key = template.Key,
                    Icon = template.Icon,
                    Tags = template.Tags,
                    Steps = template.Steps,
                    NeedsChoosing = template.NeedsChoosing,
                    Sketch = GraphSketch.Draw(template.Graph),
                }),
            ],
        });
    }

    /// <summary>
    /// Turns a template into a scenario and opens it on the canvas.
    ///
    /// Straight to the canvas rather than back to a list: the point of starting from a template is
    /// to change the address in it, and the canvas is where that happens.
    /// </summary>
    [HttpPost("templates/{key}/use")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CreateTest)]
    public async Task<IActionResult> Use(
        Guid projectId, string key, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null) return NotFound();
        if (TemplateCatalogue.Find(key) is not { } template) return NotFound();

        var scenario = new TestScenario
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = projectId,
            Name = localizer[template.TitleKey].Value,
            Description = localizer[template.SummaryKey].Value,

            // The first environment, so it is runnable rather than asking a question before it has
            // shown anybody anything.
            EnvironmentId = await db.Environments
                .Where(environment => environment.ProjectId == projectId)
                .OrderBy(environment => environment.SortOrder)
                .Select(environment => (Guid?)environment.Id)
                .FirstOrDefaultAsync(cancellationToken),

            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Scenarios.Add(scenario);
        await db.SaveChangesAsync(cancellationToken);

        await graphs.SaveAsync(scenario, template.Graph, cancellationToken);

        await audit.RecordAsync(
            new AuditEntry("scenario.created", projectId, nameof(TestScenario), scenario.Id,
                scenario.Name, new Dictionary<string, string?> { ["template"] = key }),
            cancellationToken);

        return Redirect($"/projects/{projectId}/scenarios/{scenario.Id}");
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private async Task<(string? Text, string? FileName, string? Refusal)> ReadAsync(
        IFormFile? file, string? pasted, CancellationToken cancellationToken)
    {
        if (file is { Length: > 0 })
        {
            if (file.Length > MaxUploadBytes) return (null, null, "import.tooLarge");

            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);

            return (await reader.ReadToEndAsync(cancellationToken), file.FileName, null);
        }

        if (!string.IsNullOrWhiteSpace(pasted))
        {
            return pasted.Length > MaxUploadBytes
                ? (null, null, "import.tooLarge")
                : (pasted, null, null);
        }

        return (null, null, "import.empty");
    }

    /// <summary>
    /// Everything becomes a bundle, whatever it arrived as.
    ///
    /// One write path. Three separate ones would be three places for the collision rules to
    /// disagree and three things to keep in step with the domain.
    /// </summary>
    /// <summary>
    /// The credential values the file carries, for the one path that asks for them.
    ///
    /// A ProofFlow bundle never has any — an export carries names and nothing else, deliberately —
    /// so this only ever answers for the three foreign formats.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? Credentials(string text, string source) =>
        source switch
        {
            "openapi" => OpenApiImporter.Read(text).SecretValues,
            "postman" => PostmanImporter.Read(text).SecretValues,
            "curl" => CurlImporter.Read(text).SecretValues,
            _ => null,
        };

    private static (Bundle? Bundle, string? Refusal) ToBundle(string text, string source)
    {
        if (source == "proofflow") return BundleJson.Read(text);

        var imported = source switch
        {
            "openapi" => OpenApiImporter.Read(text),
            "postman" => PostmanImporter.Read(text),
            "curl" => CurlImporter.Read(text),
            _ => Imported.Refused("import.unknownSource"),
        };

        return imported.Refusal is { } refusal
            ? (null, refusal)
            : (ImportedBundle.From(imported), null);
    }

    /// <summary>What the reader should know was left behind. Empty for this product's own files.</summary>
    private static IReadOnlyList<string> Notes(string text, string source) => source switch
    {
        "openapi" => OpenApiImporter.Read(text).Notes,
        "postman" => PostmanImporter.Read(text).Notes,
        "curl" => CurlImporter.Read(text).Notes,
        _ => [],
    };

    private static IReadOnlyList<ImportCount> Counts(Bundle bundle) =>
    [
        new("environment", bundle.Environments.Count, 0),
        new("scenario", bundle.Scenarios.Count, 0),
        new("baseline", bundle.Baselines.Count, 0),
        new("dataset", bundle.DataSets.Count, 0),
        new("schedule", bundle.Schedules.Count, 0),
    ];

    /// <summary>
    /// A name somebody can commit without renaming it.
    ///
    /// No timestamp in it, deliberately: a file called <c>catalog-api.proofflow.json</c> replaces
    /// the last one in a repository, which is the point. A date in the name makes every export a
    /// new file and the history impossible to read.
    /// </summary>
    private static string FileNameFor(string slug) =>
        $"{Slug.From(slug, "project")}.proofflow.json";

    private void Breadcrumbs(string projectName, Guid projectId, string here) =>
        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["nav.projects"].Value, "/projects"),
            (projectName, $"/projects/{projectId}"),
            (here, null),
        };
}
