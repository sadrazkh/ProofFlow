using System.ComponentModel.DataAnnotations;
using ProofFlow.Contracts.Baselines;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;

namespace ProofFlow.Web.ViewModels;

public sealed record EndpointListViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<EndpointSummary> Endpoints { get; init; }
    public required Paging Page { get; init; }
    public bool CanRecord { get; init; }

    /// <summary>
    /// Environments that can sign in by themselves — the ones quick-add can send through.
    ///
    /// Empty hides the quick-add form entirely: without a configured sign-in the form would be a
    /// slower request lab, and with no permission to record it would end in a refusal.
    /// </summary>
    public IReadOnlyList<QuickAddEnvironment> QuickAdd { get; init; } = [];
}

public sealed record QuickAddEnvironment(Guid Id, string Name);

/// <summary>
/// One endpoint's recent history: a tone per test, oldest first, and how many of them passed.
///
/// The partial turns the count into the sentence a screen reader is handed. The bars are the
/// decoration over that sentence, never the other way round — «which one is misbehaving» has to be
/// answerable without seeing colour.
/// </summary>
public sealed record SparklineView(IReadOnlyList<string> Bars, int Passing);

/// <summary>
/// One row of the list.
///
/// Method and address are here rather than only the name because the name is whatever somebody
/// typed, and after an import it is whatever the collection called the folder. «GET /products/{id}»
/// is the thing a tester recognises.
/// </summary>
public sealed record EndpointSummary(
    Guid Id,
    string Name,
    string? Description,
    string Method,
    string Url,
    string? EnvironmentName,
    string? DataSetName,
    int InputCount,
    int VersionCount,
    BaselineStatus LatestStatus,
    EndpointLastResult? Last,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The recent tests, oldest first. Empty when this endpoint has never been tested.</summary>
    public IReadOnlyList<string> Recent { get; init; } = [];
}

/// <summary>
/// How the last test went, as three numbers.
///
/// Passed is derived rather than stored: it is what is left after the ones that differed and the
/// ones that failed outright. Storing it as well would be a fourth number that can disagree with
/// the other three.
/// </summary>
public sealed record EndpointLastResult(
    int Total, int Differing, int Failed, int Unmatched, int Slow, DateTimeOffset When)
{
    public int Passed => Math.Max(0, Total - Differing - Failed - Unmatched - Slow);

    public bool Clean => Differing == 0 && Failed == 0 && Unmatched == 0 && Slow == 0 && Total > 0;
}

public sealed record EndpointDetailViewModel
{
    public required Guid ProjectId { get; init; }
    public required Baseline Endpoint { get; init; }

    /// <summary>Read out of <see cref="Baseline.RequestJson"/>, which is where the request lives.</summary>
    public required string Method { get; init; }

    public required string Url { get; init; }

    public required IReadOnlyList<BaselineVersionRow> Versions { get; init; }
    public required IReadOnlyList<RuleDto> Rules { get; init; }
    public required IReadOnlyList<RequestLabEnvironment> Environments { get; init; }
    public required IReadOnlyList<EndpointDataSetOption> DataSets { get; init; }
    public EndpointTestSummary? LastTest { get; init; }

    public bool CanRecord { get; init; }
    public bool CanApprove { get; init; }
    public bool CanRun { get; init; }

    /// <summary>The one-click negative tests this endpoint's shape allows.</summary>
    public bool OffersBareExpectation { get; init; }

    public bool OffersMissingExpectation { get; init; }

    public BaselineVersionRow? Approved =>
        Versions.FirstOrDefault(v => v.Status == BaselineStatus.Approved);

    public BaselineVersionRow? AwaitingReview =>
        Versions.FirstOrDefault(v => v.Status == BaselineStatus.PendingApproval);

    public EndpointDataSetOption? Inputs =>
        DataSets.FirstOrDefault(set => set.Id == Endpoint.DataSetId);
}

public sealed record EndpointDataSetOption(Guid Id, string Name, int RowCount);

/// <summary>
/// Defining an endpoint that cannot be sent from the request lab.
///
/// Four of the nine-step wizard's steps, on one form. The wizard asked them one at a time because
/// it was standing in for a page that did not exist; now that the page exists, four questions on
/// one screen is four questions on one screen.
/// </summary>
public sealed class EndpointFormViewModel
{
    public Guid ProjectId { get; set; }

    [Required(ErrorMessage = "error.required")]
    [MaxLength(200, ErrorMessage = "error.tooLong")]
    public string? Name { get; set; }

    [MaxLength(2000, ErrorMessage = "error.tooLong")]
    public string? Description { get; set; }

    public string Method { get; set; } = "GET";

    [Required(ErrorMessage = "error.required")]
    [MaxLength(2000, ErrorMessage = "error.tooLong")]
    public string? Url { get; set; }

    public Guid? EnvironmentId { get; set; }

    public Guid? DataSetId { get; set; }

    public IReadOnlyList<RequestLabEnvironment> Environments { get; set; } = [];
    public IReadOnlyList<EndpointDataSetOption> DataSets { get; set; } = [];

    /// <summary>The verbs the form offers. The same seven the request lab does.</summary>
    public static readonly string[] Methods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
}

/// <summary>
/// The colour of a method chip.
///
/// A second copy of the list in <c>Scripts/islands/requestTypes.ts</c>, which the request lab
/// reads. Sharing one would mean generating C# from TypeScript or the reverse for six verbs and a
/// colour each, and the cost of them drifting is that a GET chip renders grey on a list and green
/// in the lab — visible, cosmetic, and not worth a build step. Change one, change the other.
/// </summary>
public static class MethodTone
{
    public static string For(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "pass",
        "POST" => "running",
        "PUT" => "warn",
        "PATCH" => "accent",
        "DELETE" => "fail",
        _ => "idle",
    };
}

public sealed record EndpointTestSummary(
    Guid Id,
    CaptureSessionStatus Status,
    int Total,
    int Completed,
    int Differing,
    int Failed,
    int Unmatched,
    int Slow,
    DateTimeOffset When)
{
    public int Passed => Math.Max(0, Completed - Differing - Failed - Unmatched - Slow);
}

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
