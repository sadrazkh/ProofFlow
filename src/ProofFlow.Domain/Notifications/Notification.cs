using ProofFlow.Domain.Common;
using ProofFlow.Domain.Workspaces;

namespace ProofFlow.Domain.Notifications;

/// <summary>
/// Something went wrong, or something is waiting — written once, delivered three ways.
///
/// One table feeds the bell, the email and the webhook, because they are one fact with three
/// audiences. What is stored is a kind and its arguments, not a sentence: text is composed at
/// render time in the reader's language, so the same row reads Persian in your bell and English
/// in a colleague's.
///
/// Delivery state lives on the row rather than in a second table — <see cref="EmailedAt"/> and
/// <see cref="WebhookAt"/> are the outbox, and the delivery worker sweeps rows where the project
/// wants a channel and the stamp is missing. A row is never deleted by delivery; the bell reads
/// the same rows the worker sends.
/// </summary>
public class Notification : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>A dotted key the catalogue knows: run.failed, sweep.failed, schedule.broken,
    /// approval.waiting. The sentence lives in the catalogue, not here.</summary>
    public required string Kind { get; set; }

    /// <summary>The values the sentence's placeholders take, as a JSON array of strings.</summary>
    public string? ArgsJson { get; set; }

    /// <summary>Where the bell entry goes — relative, so it works behind any public address.</summary>
    public string? LinkPath { get; set; }

    /// <summary>The audit-event vocabulary, so a notification can say what it is about.</summary>
    public string? TargetType { get; set; }

    public Guid? TargetId { get; set; }

    public string? TargetLabel { get; set; }

    /// <summary>When the email went out. Null and wanted means the worker still owes one.</summary>
    public DateTimeOffset? EmailedAt { get; set; }

    /// <summary>When the webhook was delivered. Null and wanted means owed — or given up.</summary>
    public DateTimeOffset? WebhookAt { get; set; }

    /// <summary>
    /// Delivery attempts so far. At <see cref="MaxWebhookAttempts"/> the worker stops trying and
    /// <see cref="WebhookFailure"/> says why — surfaced on the settings card rather than logged
    /// into a void.
    /// </summary>
    public int WebhookAttempts { get; set; }

    public string? WebhookFailure { get; set; }

    public const int MaxWebhookAttempts = 5;
}
