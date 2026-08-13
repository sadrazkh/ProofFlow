using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Scenarios;

namespace ProofFlow.Web.ViewModels;

public sealed record ScenarioListViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<ScenarioSummary> Scenarios { get; init; }
    public required Paging Page { get; init; }
    public bool CanEdit { get; init; }

    /// <summary>Whether the reader may make an endpoint, which is what «move this» does.</summary>
    public bool CanRecord { get; init; }
}

/// <summary>
/// <paramref name="IsValid"/> is nullable on purpose: null means nobody has saved a graph yet, and
/// showing that as "invalid" would tell somebody their brand-new scenario is broken.
/// </summary>
public sealed record ScenarioSummary(
    Guid Id,
    string Name,
    string? Description,
    int NodeCount,
    bool IsPublished,
    bool? IsValid,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// A chain of one, which is not a chain.
    ///
    /// Start plus one request, or start plus a request plus a check — the two shapes an import
    /// used to produce. They are endpoints wearing a canvas, and this is what lets the list say so
    /// and offer to move them rather than leaving eleven thousand of them where chains live.
    /// </summary>
    public bool IsReallyAnEndpoint => NodeCount is > 0 and <= 3;
}

public sealed record ScenarioCanvasViewModel
{
    public required Guid ProjectId { get; init; }
    public required TestScenario Scenario { get; init; }
    public required IReadOnlyList<ScenarioEnvironment> Environments { get; init; }
    public bool CanEdit { get; init; }
    public bool CanRun { get; init; }

    /// <summary>What this test has to be told before it runs. Empty for most scenarios.</summary>
    public IReadOnlyList<ScenarioInputDto> Inputs { get; init; } = [];

    /// <summary>True when this workspace has a model key, which is the only time the button shows.</summary>
    public bool CanDraw { get; init; }
}

public sealed record ScenarioEnvironment(Guid Id, string Name, bool IsProduction);

/// <summary>
/// What a form asks before starting a set of scenarios, and what it has already been told.
///
/// Shared by the matrix and by a schedule, because they ask the same question in the same shape and
/// two copies of it would drift — one growing a description under the box and the other not.
/// </summary>
public sealed record RunInputsViewModel(
    IReadOnlyList<ProofFlow.Contracts.Scenarios.ScenarioInputDto> Inputs,
    IReadOnlyDictionary<string, string> Answered,
    string Title,
    string IdPrefix);
