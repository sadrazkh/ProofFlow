using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Baselines;

/// <summary>
/// What correct looks like for one input.
///
/// A <see cref="BaselineVersion"/> answers "what should this endpoint return"; this answers "what
/// should it return for study 12345", two thousand times over. They are different questions and
/// merging them would mean either two thousand baselines, each with its own name and rules and
/// approval chain, or one baseline that cannot say anything per-row.
///
/// So: one baseline, one set of rules, one request — and a row here per data-set key. Approving a
/// captured sample writes one of these. A regression run reads them.
/// </summary>
public class BaselineSample : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid BaselineId { get; set; }

    public Baseline? Baseline { get; set; }

    /// <summary>The data-set row this is the answer for.</summary>
    public required string Key { get; set; }

    /// <summary>The approved response, secrets already removed.</summary>
    public required string Body { get; set; }

    public string? ContentType { get; set; }

    public int StatusCode { get; set; }

    /// <summary>SHA-256 under the rules in force when it was approved — the cheap comparison.</summary>
    public string? NormalizedHash { get; set; }

    /// <summary>
    /// The version of the data set the approved response came from.
    ///
    /// Kept so a run can notice that the input changed under the answer: if the row for this key
    /// now says something different, the approved response is about a question nobody is asking
    /// any more, and the sample is stale rather than wrong.
    /// </summary>
    public Guid? DataSetVersionId { get; set; }

    /// <summary>The capture sample this was approved from, so the trail runs both ways.</summary>
    public Guid? ApprovedFromSampleId { get; set; }

    public Guid ApprovedByUserId { get; set; }

    public DateTimeOffset ApprovedAt { get; set; }
}
