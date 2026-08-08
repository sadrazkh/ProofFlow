using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProofFlow.Application.Abstractions;

namespace ProofFlow.Infrastructure.Mail;

/// <summary>
/// How to reach a mail server, if there is one.
///
/// Nothing here has a default that would accidentally work. An unconfigured install has no
/// <see cref="Host"/>, <see cref="SmtpEmailSender.CanSend"/> is false, and the product says so
/// rather than pretending to have sent something.
/// </summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    /// <summary>The relay. Empty means there is no mail server and nothing will be sent.</summary>
    public string? Host { get; set; }

    /// <summary>
    /// 587 by default, which is the submission port and the one that speaks STARTTLS.
    ///
    /// Implicit TLS on 465 is not supported: the framework's client negotiates STARTTLS only, and
    /// pointing it at 465 produces a timeout rather than an error worth reading. Every relay worth
    /// using — SES, SendGrid, Mailgun, Microsoft 365, Postfix — accepts 587.
    /// </summary>
    public int Port { get; set; } = 587;

    public bool UseTls { get; set; } = true;

    public string? UserName { get; set; }

    /// <summary>
    /// Kept out of appsettings and read from the environment or a secret store in production. It is
    /// never rendered, never logged, and never exported.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Who the message is from. Falls back to <see cref="UserName"/> when it looks like an address.</summary>
    public string? From { get; set; }

    public string? FromName { get; set; }

    /// <summary>Seconds to wait on the relay before giving up. A slow relay must not hold a request open.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Sends mail through an SMTP relay, or says it cannot.
///
/// Failure is a returned code rather than an exception, because every caller here is in the middle
/// of doing something more important than sending mail — resetting a password, inviting a colleague
/// — and none of them should fail because a relay is down.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public bool CanSend => !string.IsNullOrWhiteSpace(_options.Host) && Sender() is not null;

    public async Task<string?> SendAsync(
        EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (!CanSend)
        {
            // Not an error. An install without a mail server is a supported install, and the caller
            // has already been told by CanSend what to do instead.
            return "mail.notConfigured";
        }

        try
        {
            using var client = new SmtpClient(_options.Host, _options.Port)
            {
                EnableSsl = _options.UseTls,
                Timeout = _options.TimeoutSeconds * 1000,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            if (!string.IsNullOrWhiteSpace(_options.UserName))
            {
                client.Credentials = new NetworkCredential(_options.UserName, _options.Password);
            }

            using var mail = new MailMessage
            {
                From = new MailAddress(Sender()!, _options.FromName ?? "ProofFlow"),
                Subject = message.Subject,
                Body = message.PlainText,
                IsBodyHtml = false,
            };

            mail.To.Add(message.To);

            if (message.Html is { Length: > 0 } html)
            {
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    html, null, "text/html"));
            }

            await client.SendMailAsync(mail, cancellationToken);

            // The address is not logged. A log line naming who asked for a password reset is a list
            // of accounts worth attacking, sitting in a file with looser permissions than the
            // database it describes.
            logger.LogInformation("Sent one message through {Host}.", _options.Host);
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The mail server at {Host} refused a message.", _options.Host);
            return "mail.failed";
        }
    }

    private string? Sender()
    {
        if (!string.IsNullOrWhiteSpace(_options.From)) return _options.From;

        // A relay username is usually an address, and making people configure it twice is a way of
        // making them configure it wrong once.
        return _options.UserName?.Contains('@') == true ? _options.UserName : null;
    }
}
