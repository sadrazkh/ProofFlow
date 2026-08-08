using ProofFlow.Contracts.Portability;

namespace ProofFlow.Web.ViewModels;

/// <summary>
/// What the export page shows before anybody downloads anything.
///
/// The counts are here so somebody can tell at a glance whether they are exporting the project they
/// meant to, and <see cref="Secrets"/> so the sentence about secrets is not an abstract promise but
/// a list of the specific names that will be missing on the far side.
/// </summary>
public sealed record ExportViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string FileName { get; init; }
    public required IReadOnlyList<ImportCount> Counts { get; init; }
    public IReadOnlyList<string> Secrets { get; init; } = [];
}

/// <summary>Step one: where the file is coming from.</summary>
public sealed record ImportStartViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }

    /// <summary>The four readers, in the order somebody is likely to want them.</summary>
    public static readonly string[] Sources = ["proofflow", "openapi", "postman", "curl"];

    public string Source { get; init; } = "proofflow";
}

/// <summary>
/// Step two: what would happen.
///
/// The whole reason this is a separate step. An import is not undoable, and a page that says "4
/// scenarios, 2 environments, 3 already here and left alone" is one somebody can check against what
/// they expected before it is too late to.
/// </summary>
public sealed record ImportPreviewViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string Ticket { get; init; }
    public required string Source { get; init; }
    public string? FileName { get; init; }

    public required ImportPreview Preview { get; init; }

    /// <summary>What the reader chose: into this project, or as a new one.</summary>
    public bool AsNewProject { get; init; }

    /// <summary>Resource keys for what the reader should know was left behind.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The gallery.</summary>
public sealed record TemplateGalleryViewModel
{
    public required Guid ProjectId { get; init; }
    public required IReadOnlyList<TemplateCardViewModel> Templates { get; init; }
    public bool CanCreate { get; init; }
}

public sealed record TemplateCardViewModel
{
    public required string Key { get; init; }
    public required string Icon { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required int Steps { get; init; }

    /// <summary>True when it points at something that has to be chosen before it will run.</summary>
    public bool NeedsChoosing { get; init; }

    /// <summary>
    /// The drawing, built from the graph rather than stored beside it.
    ///
    /// Inline SVG, so it inherits the theme and cannot drift from the scenario it describes.
    /// </summary>
    public required string Sketch { get; init; }
}
