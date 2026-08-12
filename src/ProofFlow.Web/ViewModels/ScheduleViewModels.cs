using ProofFlow.Contracts.Runs;

namespace ProofFlow.Web.ViewModels;

/// <summary>
/// One standing instruction, as the list shows it.
///
/// The raw expression travels with the description, always. A translation of cron is a convenience
/// and the expression is the truth — and the person who has to change it needs to see what they are
/// changing.
/// </summary>
public sealed record ScheduleRow(
    Guid Id,
    string Name,
    string Cron,
    string TimeZoneId,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    Guid? LastBatchId,
    string? Problem,
    int ScenarioCount,
    int EnvironmentCount,
    int InputCount);

public sealed class ScheduleListViewModel
{
    public required Guid ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required IReadOnlyList<ScheduleRow> Schedules { get; init; }

    public required IReadOnlyList<FlakyScenarioDto> Flaky { get; init; }

    public required IReadOnlyList<MatrixChoice> Scenarios { get; init; }

    public required IReadOnlyList<MatrixChoice> Environments { get; init; }

    /// <summary>What this project's scenarios ask before they run, merged and asked once.</summary>
    public IReadOnlyList<ProofFlow.Contracts.Scenarios.ScenarioInputDto> Inputs { get; init; } = [];

    /// <summary>The expressions offered as buttons, so nobody has to remember cron's field order.</summary>
    public required IReadOnlyList<string> Presets { get; init; }

    /// <summary>The reader's own zone, offered as the default.</summary>
    public required string ViewerZone { get; init; }

    public bool CanEdit { get; init; }

    public bool CanQuarantine { get; init; }
}

/// <summary>
/// One key, as the list shows it.
///
/// The preview is the only part of the value that exists anywhere — enough to match a key found in
/// a CI log against the row to revoke, and far too little to sign anything with.
/// </summary>
public sealed record ApiKeyRow(
    Guid Id,
    string Name,
    string Preview,
    bool WholeWorkspace,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

public sealed class ProjectSettingsViewModel
{
    public required Guid ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required IReadOnlyList<ApiKeyRow> Keys { get; init; }

    /// <summary>
    /// A key just created, shown once and then gone.
    ///
    /// Null on every other render, including a refresh — which is the point, and which the page has
    /// to say out loud so nobody closes the tab expecting to come back for it.
    /// </summary>
    public string? IssuedSecret { get; init; }

    public bool CanManage { get; init; }
}
