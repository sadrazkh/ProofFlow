using ProofFlow.Contracts.Baselines;
using ProofFlow.Domain.Baselines;

namespace ProofFlow.Web.ViewModels;

public sealed record BaselineListViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<BaselineSummary> Baselines { get; init; }
    public bool CanRecord { get; init; }
}

public sealed record BaselineSummary(
    Guid Id,
    string Name,
    string? Description,
    string? EnvironmentName,
    int VersionCount,
    BaselineStatus LatestStatus,
    DateTimeOffset UpdatedAt);

public sealed record BaselineDetailViewModel
{
    public required Guid ProjectId { get; init; }
    public required Baseline Baseline { get; init; }
    public required IReadOnlyList<BaselineVersionRow> Versions { get; init; }
    public required IReadOnlyList<RuleDto> Rules { get; init; }
    public required IReadOnlyList<RequestLabEnvironment> Environments { get; init; }
    public bool CanRecord { get; init; }
    public bool CanApprove { get; init; }
    public bool CanRun { get; init; }

    public BaselineVersionRow? Approved =>
        Versions.FirstOrDefault(v => v.Status == BaselineStatus.Approved);

    public BaselineVersionRow? AwaitingReview =>
        Versions.FirstOrDefault(v => v.Status == BaselineStatus.PendingApproval);
}

public sealed record BaselineVersionRow(
    Guid Id,
    int Number,
    BaselineStatus Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    int StatusCode,
    int BodyLength,
    string? RejectionReason);

/// <summary>
/// The badge tone for each state.
///
/// One place, because the six states appear in a list, on a timeline and in a heading, and three
/// copies of this mapping is three chances for "approved" to be green in two of them.
/// </summary>
public static class BaselineStatusTone
{
    public static string For(BaselineStatus status) => status switch
    {
        BaselineStatus.Approved => "badge-pass",
        BaselineStatus.PendingApproval => "badge-warn",
        BaselineStatus.Rejected => "badge-fail",
        BaselineStatus.Draft => "badge-idle",
        BaselineStatus.Superseded => "badge-idle",
        _ => "badge-idle",
    };

    public static string Icon(BaselineStatus status) => status switch
    {
        BaselineStatus.Approved => "circle-check",
        BaselineStatus.PendingApproval => "clock",
        BaselineStatus.Rejected => "circle-slash",
        BaselineStatus.Superseded => "history",
        BaselineStatus.Archived => "ban",
        _ => "circle-dot",
    };
}
