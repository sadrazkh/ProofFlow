using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Auditing;

/// <summary>
/// One thing that happened, who did it, and to what.
///
/// Append-only: nothing in the application updates or deletes a row here. The brief requires every
/// important change to be recorded, and a log its own application can rewrite records nothing.
///
/// <see cref="ActorDisplay"/> is denormalised on purpose. Deleting a user must not turn their
/// history into a column of empty GUIDs — the point of the log is to still be readable afterwards.
/// </summary>
public class AuditEvent : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid? ProjectId { get; set; }

    public Guid? ActorUserId { get; set; }

    /// <summary>Name or email as it was at the time. Never joined back to the user table.</summary>
    public string ActorDisplay { get; set; } = "system";

    /// <summary>A stable dotted key, e.g. <c>baseline.approved</c>. Localised for display.</summary>
    public required string Action { get; set; }

    /// <summary>The kind of thing acted on, e.g. <c>Baseline</c>. Not a CLR type name.</summary>
    public string? TargetType { get; set; }

    public Guid? TargetId { get; set; }

    /// <summary>Short human-readable subject, e.g. the baseline's name.</summary>
    public string? TargetLabel { get; set; }

    /// <summary>
    /// Extra detail as a JSON object. Written through the redactor, so a secret's value cannot
    /// reach it even when the change being logged is to a secret.
    /// </summary>
    public string? DetailsJson { get; set; }

    public string? IpAddress { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
