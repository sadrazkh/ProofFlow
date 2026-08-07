using ProofFlow.Domain.Capture;

namespace ProofFlow.Web.ViewModels;

public sealed record DataSetListViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<DataSetSummary> Sets { get; init; }
    public bool CanManage { get; init; }
}

public sealed record DataSetSummary(
    Guid Id,
    string Name,
    string? Description,
    string? KeyColumn,
    int VersionCount,
    int RowCount,
    DateTimeOffset UpdatedAt);

public sealed record DataSetDetailViewModel
{
    public required Guid ProjectId { get; init; }
    public required Guid DataSetId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<DataSetVersionRow> Versions { get; init; }
    public required Guid? CurrentVersionId { get; init; }
    public bool CanManage { get; init; }
}

public sealed record DataSetVersionRow(
    Guid Id, int Number, int RowCount, string? Description, DateTimeOffset CreatedAt, bool IsCurrent);

// ------------------------------------------------------------------------------------------------

public sealed record CaptureListViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<CaptureSummary> Sessions { get; init; }
    public bool CanRun { get; init; }
    public bool HasBaselines { get; init; }
    public bool HasDataSets { get; init; }
}

public sealed record CaptureSummary(
    Guid Id,
    string BaselineName,
    string DataSetName,
    int DataSetVersion,
    CaptureMode Mode,
    CaptureSessionStatus Status,
    int TotalRows,
    int Differing,
    int Failed,
    int AwaitingReview,
    DateTimeOffset StartedAt);

public sealed record CaptureDetailViewModel
{
    public required Guid ProjectId { get; init; }
    public required CaptureSession Session { get; init; }
    public required string BaselineName { get; init; }
    public required string DataSetName { get; init; }
    public required int DataSetVersion { get; init; }
    public bool CanReview { get; init; }
}

/// <summary>
/// The badge tone for each sample state.
///
/// One place, because the six states appear in the queue, in a filter bar and on a session summary,
/// and three copies of this mapping is three chances for "rejected" to be amber in one of them.
/// </summary>
public static class SampleStatusTone
{
    public static string For(SampleStatus status) => status switch
    {
        SampleStatus.Captured => "badge-running",
        SampleStatus.Reviewed => "badge-accent",
        SampleStatus.Approved => "badge-pass",
        SampleStatus.Rejected => "badge-fail",
        SampleStatus.Outdated => "badge-warn",
        SampleStatus.Failed => "badge-fail",
        _ => "badge-idle",
    };

    public static string Icon(SampleStatus status) => status switch
    {
        SampleStatus.Captured => "circle-dot",
        SampleStatus.Reviewed => "eye",
        SampleStatus.Approved => "circle-check",
        SampleStatus.Rejected => "circle-slash",
        SampleStatus.Outdated => "history",
        SampleStatus.Failed => "triangle-alert",
        _ => "circle-dot",
    };
}

public static class CaptureStatusTone
{
    public static string For(CaptureSessionStatus status) => status switch
    {
        CaptureSessionStatus.Running => "badge-running",
        CaptureSessionStatus.Completed => "badge-pass",
        CaptureSessionStatus.Cancelled => "badge-idle",
        CaptureSessionStatus.Failed => "badge-fail",
        _ => "badge-idle",
    };
}

// ------------------------------------------------------------------------------------------------

public sealed record WizardViewModel
{
    public required Guid ProjectId { get; init; }
    public required IReadOnlyList<WizardEnvironment> Environments { get; init; }
    public required IReadOnlyList<WizardBaseline> Baselines { get; init; }
    public required IReadOnlyList<WizardDataSet> DataSets { get; init; }
    public bool CanRun { get; init; }
    public bool CanManage { get; init; }
}

public sealed record WizardEnvironment(Guid Id, string Name, string? BaseUrl, bool IsProduction);

public sealed record WizardBaseline(Guid Id, string Name);

public sealed record WizardDataSet(Guid Id, string Name, Guid? CurrentVersionId, int RowCount);
