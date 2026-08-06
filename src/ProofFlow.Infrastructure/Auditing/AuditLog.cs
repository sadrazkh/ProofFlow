using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Auditing;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Auditing;

/// <summary>
/// Writes the audit trail.
///
/// Two decisions worth stating. It swallows its own failures — a log write that throws must not
/// roll back the approval it was describing, because that turns a logging problem into an outage.
/// And it writes on its own <c>SaveChanges</c> rather than joining the caller's transaction, so a
/// caller that later rolls back still leaves evidence that the attempt happened.
/// </summary>
public sealed class AuditLog(
    ProofFlowDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<AuditLog> logger) : IAuditLog
{
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var workspaceId = currentUser.WorkspaceId ?? db.Scope.WorkspaceId;
        if (workspaceId is null)
        {
            // Nothing to attach it to. Say so in the application log rather than dropping it in
            // silence — an audit call from an unscoped context is a bug in the caller.
            logger.LogWarning("Audit event {Action} had no workspace scope and was not recorded.", entry.Action);
            return;
        }

        try
        {
            db.AuditEvents.Add(new AuditEvent
            {
                WorkspaceId = workspaceId.Value,
                ProjectId = entry.ProjectId,
                ActorUserId = currentUser.UserId,
                ActorDisplay = currentUser.IsAuthenticated ? currentUser.DisplayName : "system",
                Action = entry.Action,
                TargetType = entry.TargetType,
                TargetId = entry.TargetId,
                TargetLabel = Truncate(entry.TargetLabel, 300),
                DetailsJson = entry.Details is { Count: > 0 } ? JsonSerializer.Serialize(entry.Details) : null,
                OccurredAt = clock.UtcNow,
            });

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record audit event {Action}.", entry.Action);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
