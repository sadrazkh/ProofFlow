namespace ProofFlow.Application.Abstractions;

/// <summary>
/// Sends one message to one person.
///
/// <see cref="CanSend"/> is the part worth explaining. Most of this product can assume its
/// dependencies exist; a mail server cannot be assumed, because a great many installations of a
/// self-hosted testing tool will never have one. So the sender says whether it is real, and the
/// callers decide what to do about it — a page that says "a link is on its way" when nothing was
/// sent is worse than one that hands the link over and admits why.
/// </summary>
public interface IEmailSender
{
    /// <summary>Whether a mail server is configured. False means every send is a no-op.</summary>
    bool CanSend { get; }

    /// <summary>
    /// Sends, or returns why it could not.
    ///
    /// Never throws: an unreachable relay must not turn "reset my password" into a 500. The caller
    /// gets a code back and decides whether the reader needs to know.
    /// </summary>
    Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// One message. Plain text and HTML together, because a password-reset link that arrives as an
/// unclickable string in a text-only client is a support ticket.
/// </summary>
public sealed record EmailMessage
{
    public required string To { get; init; }

    public required string Subject { get; init; }

    public required string PlainText { get; init; }

    public string? Html { get; init; }
}
