using ProofFlow.Domain.Runs;

namespace ProofFlow.Web.ViewModels;

/// <summary>Everything the dashboard renders. Assembled by the controller, never queried by a view.</summary>
public sealed record DashboardViewModel
{
    public required string DisplayName { get; init; }
    public required IReadOnlyList<ProjectCardViewModel> Projects { get; init; }
    public int TotalProjects { get; init; }
    public int TotalRuns { get; init; }
    public int PassRatePercent { get; init; }
    public int FailingCount { get; init; }
    public int AwaitingApproval { get; init; }
    public bool IsEmpty => TotalProjects == 0;

    /// <summary>
    /// The four things somebody has to have done before this product has told them anything.
    ///
    /// Not a tour and not a dismissible banner: it is four facts read from the database, and it
    /// stops being shown when the last of them is true. A checklist somebody has to close is a
    /// checklist that outlives its usefulness by a year.
    /// </summary>
    public bool HasEnvironment { get; init; }
    public bool HasScenario { get; init; }
    public bool HasRun { get; init; }

    /// <summary>Where the next unfinished step goes, or null when there is nothing left to do.</summary>
    public Guid? FirstProjectId { get; init; }

    public bool ShowGettingStarted => !HasRun;

    /// <summary>The last few runs, across every project in the workspace.</summary>
    public IReadOnlyList<RecentRunRow> RecentRuns { get; init; } = [];
}

/// <summary>
/// One line of the recent-runs panel.
///
/// Carries the project as well as the scenario, because this list crosses projects and "Fetch every
/// study by id · failed" is the same sentence in four of them.
/// </summary>
public sealed record RecentRunRow(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string ScenarioName,
    string? EnvironmentName,
    RunStatus Status,
    DateTimeOffset StartedAt);

public sealed record ProjectCardViewModel
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? Description { get; init; }
    public required string Accent { get; init; }
    public bool IsArchived { get; init; }
    public int EnvironmentCount { get; init; }
    public int ScenarioCount { get; init; }
    public int BaselineCount { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}

public sealed record ProjectListViewModel
{
    public required IReadOnlyList<ProjectCardViewModel> Projects { get; init; }
    public bool ShowArchived { get; init; }
    public bool CanCreate { get; init; }
}

public sealed record AuditListViewModel
{
    public required IReadOnlyList<AuditRowViewModel> Events { get; init; }
    public int Page { get; init; }
    public bool HasMore { get; init; }

    /// <summary>What the reader narrowed by, echoed back so the form keeps its state.</summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Named <c>Kind</c> rather than <c>Action</c> because the query parameter behind it has to be:
    /// <c>action</c> is a routing token and binds to the method name instead of the query string.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// The kinds of thing that have actually happened here.
    ///
    /// Read from the log rather than listed from the code: most of what the code can emit has never
    /// happened in this workspace, and a filter offering forty options that all return nothing is a
    /// filter nobody opens twice.
    /// </summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];
}

public sealed record AuditRowViewModel
{
    public required string Actor { get; init; }
    public required string ActionKey { get; init; }
    public string? TargetLabel { get; init; }
    public string? TargetType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>The workspace's own settings. One section today, and it says what it costs.</summary>
public sealed record WorkspaceSettingsViewModel
{
    public required string Name { get; init; }

    public string? AiBaseUrl { get; init; }
    public string? AiModel { get; init; }

    /// <summary>The last four characters of the stored key, or null when there is none.</summary>
    public string? AiKeyPreview { get; init; }

    public required string DefaultBaseUrl { get; init; }
    public required string DefaultModel { get; init; }

    public bool HasKey => !string.IsNullOrWhiteSpace(AiKeyPreview);
}

/// <summary>
/// The first minute, as one page.
///
/// Nullable everywhere it points at something that may not exist yet: a workspace with no projects
/// and no scenarios is the exact case this page is for, and a model that assumed otherwise would
/// fall over on the only visit that matters.
/// </summary>
public sealed record StartViewModel
{
    public Guid? FirstProjectId { get; init; }
    public string? FirstProjectName { get; init; }
    public int ProjectCount { get; init; }

    public Guid? FlowId { get; init; }
    public Guid? FlowProjectId { get; init; }
    public string? FlowName { get; init; }

    public bool CanCreate { get; init; }

    public bool HasProject => FirstProjectId is not null;
    public bool HasFlow => FlowId is not null && FlowProjectId is not null;
}
