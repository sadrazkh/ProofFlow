using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Common;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;

namespace ProofFlow.Domain.Capture;

/// <summary>
/// One sweep across a data set: send the request for every row, keep what came back.
///
/// This is the mode section 4 of the brief calls capture. It is deliberately not the same act as
/// approving: capturing two thousand responses records what the API does today, which is a fact,
/// and says nothing about whether today is correct. The review queue is where that second question
/// gets asked, one sample at a time or in bulk, by somebody who chose to.
///
/// Sessions are kept after review. "What did the sweep on the seventh look like" is the question
/// asked the moment a regression is disputed.
/// </summary>
public class CaptureSession : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>The baseline whose request is being swept, and whose rules the samples compare under.</summary>
    public Guid BaselineId { get; set; }

    public Baseline? Baseline { get; set; }

    /// <summary>
    /// The exact version of the data that was used.
    ///
    /// The version, never the set: a report that says "ran against the customers set" is worthless
    /// six weeks and four edits later.
    /// </summary>
    public Guid DataSetVersionId { get; set; }

    public DataSetVersion? DataSetVersion { get; set; }

    public Guid? EnvironmentId { get; set; }

    public ProjectEnvironment? Environment { get; set; }

    public CaptureMode Mode { get; set; } = CaptureMode.Capture;

    public CaptureSessionStatus Status { get; set; } = CaptureSessionStatus.Running;

    public int TotalRows { get; set; }

    public int Completed { get; set; }

    /// <summary>Samples whose response differed from the approved one. The number people look at.</summary>
    public int Differing { get; set; }

    /// <summary>Samples where the request itself never completed — a timeout, a refusal, a 500.</summary>
    public int Failed { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Why it stopped early, when it did. Shown rather than inferred from the counts.</summary>
    public string? StoppedReason { get; set; }

    public Guid StartedByUserId { get; set; }

    public ICollection<CaptureSample> Samples { get; set; } = [];
}

/// <summary>
/// What a sweep was for.
///
/// The same machinery answers two questions and the answers mean opposite things, so the session
/// records which one was asked. A capture that finds a difference has found nothing — there was
/// nothing to differ from. A regression run that finds one has found the thing it exists for.
/// </summary>
public enum CaptureMode
{
    /// <summary>First pass: record what the API returns, for review.</summary>
    Capture = 1,

    /// <summary>Later passes: compare against what was approved.</summary>
    Regression = 2,
}

public enum CaptureSessionStatus
{
    Running = 1,
    Completed = 2,

    /// <summary>Stopped by a person. Whatever was captured before that is kept.</summary>
    Cancelled = 3,

    /// <summary>Stopped by something going wrong that was not one row's fault.</summary>
    Failed = 4,
}

/// <summary>
/// One row's response, and what somebody decided about it.
///
/// The body is stored even when it matches, because "it matched" is a claim and the evidence for
/// it should outlive the run that made it — for a while, at least. Retention is a setting; the
/// brief asks for one.
/// </summary>
public class CaptureSample : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid CaptureSessionId { get; set; }

    public CaptureSession? Session { get; set; }

    /// <summary>The row's key, copied so a sample can be found without joining the data set.</summary>
    public required string Key { get; set; }

    public int Ordinal { get; set; }

    public SampleStatus Status { get; set; } = SampleStatus.Captured;

    /// <summary>The URL as actually sent, with every reference resolved. Redacted.</summary>
    public string? ResolvedUrl { get; set; }

    public int StatusCode { get; set; }

    public string? ContentType { get; set; }

    /// <summary>The response, secrets already removed. Null when the request never completed.</summary>
    public string? Body { get; set; }

    /// <summary>SHA-256 of the body under the baseline's rules — the cheap "did it move?" test.</summary>
    public string? NormalizedHash { get; set; }

    public double DurationMs { get; set; }

    /// <summary>
    /// True when this sample differs from the approved baseline for the same key.
    ///
    /// Stored rather than recomputed: the rules that were in force at capture time are the rules
    /// this answer was given under, and they change.
    /// </summary>
    public bool Differs { get; set; }

    /// <summary>Counts per diff category as JSON, so the queue can show a shape without a re-diff.</summary>
    public string? DiffSummaryJson { get; set; }

    /// <summary>Why the request failed, in words a reader can act on.</summary>
    public string? FailureMessage { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ReviewNote { get; set; }
}

/// <summary>
/// Where one sample stands.
///
/// Six states because the alternatives collapse distinctions that matter. "Not looked at" is not
/// "looked at and left alone"; "the request failed" is not "the response was wrong"; and a sample
/// approved against a baseline that has since moved is neither approved nor rejected — it is
/// stale, and saying so is the only way somebody knows to look again.
/// </summary>
public enum SampleStatus
{
    /// <summary>Recorded. Nobody has looked at it.</summary>
    Captured = 1,

    /// <summary>Somebody looked and did not decide. Kept apart from Captured so a queue of two
    /// thousand can be worked through without losing the place.</summary>
    Reviewed = 2,

    /// <summary>Accepted as correct. This is what the baseline for this key becomes.</summary>
    Approved = 3,

    /// <summary>Wrong. In a regression run this is the finding the whole product exists for.</summary>
    Rejected = 4,

    /// <summary>Was approved, but the baseline or the data moved underneath it.</summary>
    Outdated = 5,

    /// <summary>The request never completed, so there is no response to judge.</summary>
    Failed = 6,
}
