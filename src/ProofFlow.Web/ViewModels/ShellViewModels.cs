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
}

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
