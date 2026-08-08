using ProofFlow.Domain.Common;
using ProofFlow.Domain.Projects;

namespace ProofFlow.Domain.Scheduling;

/// <summary>
/// A credential a build agent can hold.
///
/// The whole reason this exists is step nineteen: a pipeline runs the suite and fails the build on
/// a regression. A pipeline cannot sign in with a cookie, so it needs something it can put in a
/// header — and that something has to be revocable, attributable, and never recoverable from the
/// database.
///
/// Only the hash is stored. Not "for security" as a slogan: a key that can be read back out of a
/// list page is a key that leaks through a screenshot, a support session, or a database backup that
/// went to the wrong bucket. The value is shown once, at creation, and never again.
/// </summary>
public class ApiKey : Entity, IWorkspaceOwned
{
    /// <summary>What every key ProofFlow issues starts with, so one can be spotted in a log.</summary>
    public const string Prefix = "pf_";

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Null for a key that covers the whole workspace.
    ///
    /// Scoping to a project is offered because a build agent for one service has no business
    /// starting runs against another, and the narrower key is the one somebody should reach for.
    /// </summary>
    public Guid? ProjectId { get; set; }

    public Project? Project { get; set; }

    public required string Name { get; set; }

    /// <summary>SHA-256 of the key, base64. The key itself is never stored.</summary>
    public required string Hash { get; set; }

    /// <summary>
    /// The first few characters, kept in the clear.
    ///
    /// Enough to tell two keys apart in a list and to match one found in a CI log against the row
    /// that should be revoked — and far too little to sign anything with.
    /// </summary>
    public required string Preview { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// When it stops working, if it ever does.
    ///
    /// Offered rather than required. A key with no expiry is a real choice for a long-lived
    /// pipeline; a key with one is the better default and the interface says so.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Updated on use, at a coarse resolution.
    ///
    /// It answers the question that makes revoking safe — "is anything still using this?" — and the
    /// answer only has to be good to the hour, so the write is skipped when it would say the same
    /// thing again.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
