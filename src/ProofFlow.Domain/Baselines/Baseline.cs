using ProofFlow.Domain.Common;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;

namespace ProofFlow.Domain.Baselines;

/// <summary>
/// What "correct" looked like for one request, over time.
///
/// The baseline itself is the named, durable thing — "the product detail response" — and it holds
/// a series of versions. Splitting them is what makes the history real: a version is never edited
/// once approved, and accepting a change creates the next one rather than overwriting the last.
/// A baseline that can be edited in place cannot answer "what did this look like in March", which
/// is the question people come to it with.
/// </summary>
public class Baseline : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>
    /// The environment this baseline was captured against, if it is tied to one.
    ///
    /// Null means the baseline is shared across environments — the right choice when a response
    /// should be identical everywhere, and the wrong one the moment staging returns different
    /// data. Both are legitimate and the brief asks for both.
    /// </summary>
    public Guid? EnvironmentId { get; set; }

    public ProjectEnvironment? Environment { get; set; }

    /// <summary>
    /// The inputs this is checked against, if there are any.
    ///
    /// Null means «run it once», which is the honest state for an endpoint that takes no
    /// parameters and a perfectly ordinary way to use one. When it is set, pressing Test sweeps
    /// the request across that set's current version instead of sending it a single time.
    ///
    /// Stored here rather than chosen at the moment of testing because it is a property of the
    /// endpoint and not of the press: somebody who picked «two thousand study identifiers» in
    /// March should not have to remember that choice in June, and a Test button that opens a
    /// dialog asking which inputs to use is a Test button nobody presses casually.
    /// </summary>
    public Guid? DataSetId { get; set; }

    public DataSet? DataSet { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The request this baseline is of, as JSON. Kept so a replay is reproducible.</summary>
    public string? RequestJson { get; set; }

    /// <summary>The version currently in force. Null while the first one is still a draft.</summary>
    public Guid? ApprovedVersionId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public ICollection<BaselineVersion> Versions { get; set; } = [];
}

/// <summary>
/// One approved — or proposed — snapshot.
///
/// Everything needed to explain a comparison months later is here rather than looked up: the rules
/// that were in force, the environment, the request. A version that points at rules which have
/// since been edited cannot say what it actually compared, and a baseline that cannot say that is
/// a baseline nobody can defend in a review.
/// </summary>
public class BaselineVersion : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid BaselineId { get; set; }

    public Baseline? Baseline { get; set; }

    /// <summary>1, 2, 3… within the baseline. Shown to people; never reused.</summary>
    public int Number { get; set; }

    public BaselineStatus Status { get; set; } = BaselineStatus.Draft;

    /// <summary>The response exactly as it arrived, secrets already redacted.</summary>
    public required string Body { get; set; }

    public string? ContentType { get; set; }

    public int StatusCode { get; set; }

    /// <summary>Response headers worth comparing, as JSON. Not all of them — Date moves.</summary>
    public string? HeadersJson { get; set; }

    /// <summary>
    /// The comparison rules as they stood when this version was approved, as JSON.
    ///
    /// A copy, deliberately, not a reference. Rules change; a version has to keep saying what it
    /// meant at the time or the history it records is fiction.
    /// </summary>
    public string? RulesJson { get; set; }

    /// <summary>
    /// SHA-256 of the body after the rules were applied.
    ///
    /// Lets a run answer "did anything change?" without a full comparison, which matters when a
    /// suite replays two thousand samples. A mismatch is what triggers the real diff.
    /// </summary>
    public string? NormalizedHash { get; set; }

    public string? Description { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Why it was rejected, for the person who has to act on that.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>The version this one replaced, so the chain can be walked backwards.</summary>
    public Guid? SupersededVersionId { get; set; }
}

/// <summary>
/// Where a version is in its life.
///
/// Numbered explicitly and never renumbered: the value is persisted, and shifting it would
/// silently re-approve or un-approve every existing row.
/// </summary>
public enum BaselineStatus
{
    /// <summary>Captured, not yet proposed. Nothing compares against it.</summary>
    Draft = 1,

    /// <summary>Proposed, waiting for somebody who did not write it.</summary>
    PendingApproval = 2,

    /// <summary>In force. This is what runs compare against.</summary>
    Approved = 3,

    /// <summary>Was approved; a later version replaced it. Kept, because history is the point.</summary>
    Superseded = 4,

    /// <summary>Proposed and turned down, with a reason.</summary>
    Rejected = 5,

    /// <summary>Put away deliberately. Not deleted — a baseline is the only record of what an
    /// API used to return.</summary>
    Archived = 6,
}

/// <summary>
/// A stored comparison rule: a path, a matcher, and why.
///
/// Lives on the baseline rather than on the version, because it is the thing people edit. A
/// version keeps its own frozen copy in <see cref="BaselineVersion.RulesJson"/>.
/// </summary>
public class BaselineRule : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid BaselineId { get; set; }

    public Baseline? Baseline { get; set; }

    public required string Path { get; set; }

    /// <summary>The matcher, stored by name so a renumbering of the engine's enum cannot
    /// silently change what a stored rule means.</summary>
    public required string Matcher { get; set; }

    public string? Text { get; set; }

    public double? Number { get; set; }

    public double? Number2 { get; set; }

    /// <summary>Why this rule exists. A rule without one is a rule nobody dares remove.</summary>
    public string? Note { get; set; }

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>True when the rule came from a suggestion a person accepted, rather than one they
    /// wrote — worth knowing when reviewing what is being ignored and why.</summary>
    public bool FromSuggestion { get; set; }
}
