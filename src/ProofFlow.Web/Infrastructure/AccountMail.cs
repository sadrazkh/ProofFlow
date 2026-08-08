using System.Net;
using System.Text;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// The two messages this product sends.
///
/// Composed here rather than in Infrastructure because writing a message is a matter of language and
/// URLs, and the sender should know about neither. Both bodies come from the resource catalogue like
/// every other string, so a Persian reader gets a Persian email.
/// </summary>
public sealed class AccountMail(
    IEmailSender email, IStringLocalizer localizer, PublicLinks links)
{
    /// <summary>Whether there is anywhere to send. The callers change what they say, not what they do.</summary>
    public bool CanSend => email.CanSend;

    /// <summary>
    /// A link to choose a new password.
    ///
    /// Written in the language of the request, which is right here and only here: the person asking
    /// for the reset is the person who will read it.
    /// </summary>
    public Task<string?> PasswordResetAsync(
        string to, string link, CancellationToken cancellationToken = default) =>
        email.SendAsync(
            Compose(
                to,
                localizer["mail.reset.subject"].Value,
                localizer["mail.reset.body"].Value,
                localizer["mail.reset.action"].Value,
                link,
                localizer["mail.reset.ignore"].Value),
            cancellationToken);

    /// <summary>
    /// A link into a workspace.
    ///
    /// Written in the language of whoever sent it, which is a guess — the recipient has no account
    /// yet, so there is nothing better to guess from. The link works in any language.
    /// </summary>
    public Task<string?> InvitationAsync(
        string to, string workspace, string inviter, string link,
        CancellationToken cancellationToken = default) =>
        email.SendAsync(
            Compose(
                to,
                localizer["mail.invite.subject", workspace].Value,
                localizer["mail.invite.body", inviter, workspace].Value,
                localizer["mail.invite.action"].Value,
                link,
                localizer["mail.invite.ignore"].Value),
            cancellationToken);

    public string ResetLink(Guid userId, string token) =>
        links.Absolute($"/account/reset?u={userId}&t={Uri.EscapeDataString(token)}");

    public string JoinLink(string token) =>
        links.Absolute($"/team/join?token={Uri.EscapeDataString(token)}");

    /// <summary>
    /// One shape for both messages: a sentence, a link, and a line saying what to do if it was not
    /// you. Plain text carries the URL in full, because a client that strips HTML must still leave
    /// something somebody can copy.
    /// </summary>
    private static EmailMessage Compose(
        string to, string subject, string body, string action, string link, string ignore)
    {
        var plain = new StringBuilder()
            .AppendLine(body)
            .AppendLine()
            .AppendLine(link)
            .AppendLine()
            .AppendLine(ignore)
            .ToString();

        // Hand-written and deliberately plain. Every value that reaches it is encoded; none of it is
        // user-supplied HTML, and it is not going to become a template engine.
        var html = $"""
            <div style="font-family: system-ui, sans-serif; font-size: 15px; line-height: 1.7;">
              <p>{WebUtility.HtmlEncode(body)}</p>
              <p><a href="{WebUtility.HtmlEncode(link)}">{WebUtility.HtmlEncode(action)}</a></p>
              <p style="color: #6b7280; font-size: 13px;">{WebUtility.HtmlEncode(ignore)}</p>
              <p style="color: #6b7280; font-size: 13px; word-break: break-all;">{WebUtility.HtmlEncode(link)}</p>
            </div>
            """;

        return new EmailMessage
        {
            To = to,
            Subject = subject,
            PlainText = plain,
            Html = html,
        };
    }
}
