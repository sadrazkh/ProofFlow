using ProofFlow.Domain.Scenarios;

namespace ProofFlow.Web.ViewModels;

public sealed record ScenarioListViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<ScenarioSummary> Scenarios { get; init; }
    public bool CanEdit { get; init; }
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
    DateTimeOffset UpdatedAt);

public sealed record ScenarioCanvasViewModel
{
    public required Guid ProjectId { get; init; }
    public required TestScenario Scenario { get; init; }
    public required IReadOnlyList<ScenarioEnvironment> Environments { get; init; }
    public bool CanEdit { get; init; }
    public bool CanRun { get; init; }
}

public sealed record ScenarioEnvironment(Guid Id, string Name, bool IsProduction);
