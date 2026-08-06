namespace ProofFlow.Application.Abstractions;

/// <summary>
/// Records that something happened. Never throws into the caller: an audit write that fails must
/// not roll back the action it was describing, because the alternative is an operation people
/// cannot perform at all whenever the log has a problem.
/// </summary>
public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// One line for the log. <paramref name="Action"/> is a stable dotted key — the display string is
/// looked up from resources, so the log reads in the reader's language rather than the language of
/// whoever wrote the call site.
/// </summary>
public sealed record AuditEntry(
    string Action,
    Guid? ProjectId = null,
    string? TargetType = null,
    Guid? TargetId = null,
    string? TargetLabel = null,
    IReadOnlyDictionary<string, string?>? Details = null);
