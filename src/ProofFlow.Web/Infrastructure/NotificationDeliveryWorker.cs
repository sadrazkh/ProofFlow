using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Notifications;
using ProofFlow.Domain.Projects;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Delivers what the bell already knows: email and webhook, off the request path.
///
/// A sweeper in the <c>RetentionWorker</c> shape rather than a send inside the failing request,
/// because the failing request is a run that just went wrong — the worst possible moment to also
/// wait on somebody's SMTP relay. The row is the outbox; this stamps it.
///
/// It lives in Web rather than Infrastructure because composing a sentence needs the localizer,
/// and the catalogue is the web application's.
/// </summary>
public sealed class NotificationDeliveryWorker(
    IServiceScopeFactory scopes,
    IEmailSender mail,
    IHttpClientFactory http,
    IConfiguration configuration,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Sweep = TimeSpan.FromSeconds(30);

    /// <summary>Per sweep, so one noisy night cannot hold the loop open for an hour.</summary>
    public const int MaxPerSweep = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The same courtesy the other workers extend: let the application start first.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A notification delivery sweep did not finish.");
            }

            try { await Task.Delay(Sweep, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken cancellation)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProofFlowDbContext>();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer>();
        var cipher = scope.ServiceProvider.GetRequiredService<ISecretCipher>();

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-7);

        // Only rows something is actually owed on. The join is the filter: a project with neither
        // channel never has its rows fetched, so «nothing configured» costs nothing per sweep.
        var owed = await db.Notifications.IgnoreQueryFilters()
            .Join(db.Projects.IgnoreQueryFilters(),
                n => n.ProjectId, p => p.Id,
                (n, p) => new { Row = n, Project = p })
            .Where(x => x.Row.CreatedAt > cutoff
                        && ((x.Project.NotifyByEmail && x.Row.EmailedAt == null)
                            || (x.Project.WebhookUrl != null
                                && x.Row.WebhookAt == null
                                && x.Row.WebhookAttempts < Notification.MaxWebhookAttempts)))
            .OrderBy(x => x.Row.CreatedAt)
            .Take(MaxPerSweep)
            .ToListAsync(cancellation);

        if (owed.Count == 0) return;

        var publicUrl = (configuration["App:PublicUrl"] ?? string.Empty).TrimEnd('/');
        var recipients = new Dictionary<Guid, IReadOnlyList<string>>();

        foreach (var item in owed)
        {
            cancellation.ThrowIfCancellationRequested();

            // Per row, isolated: one refusing relay or one dead webhook must not dam the queue.
            try
            {
                await DeliverAsync(db, localizer, cipher, item.Row, item.Project,
                    publicUrl, recipients, now, cancellation);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification {Id} could not be delivered.", item.Row.Id);
            }
        }

        await db.SaveChangesAsync(cancellation);
    }

    private async Task DeliverAsync(
        ProofFlowDbContext db, IStringLocalizer localizer, ISecretCipher cipher,
        Notification row, Project project, string publicUrl,
        Dictionary<Guid, IReadOnlyList<string>> recipients, DateTimeOffset now,
        CancellationToken cancellation)
    {
        var args = Args(row.ArgsJson);
        var sentence = localizer[$"notify.{row.Kind}",
            args.Length > 0 ? args[0] : "", args.Length > 1 ? args[1] : ""].Value;

        var link = publicUrl.Length > 0 && row.LinkPath is { } path ? publicUrl + path : null;

        if (project.NotifyByEmail && row.EmailedAt is null && mail.CanSend)
        {
            if (!recipients.TryGetValue(row.WorkspaceId, out var to))
            {
                to = await db.WorkspaceMembers.IgnoreQueryFilters()
                    .Where(member => member.WorkspaceId == row.WorkspaceId && member.JoinedAt != null)
                    .Join(db.Users, member => member.UserId, user => user.Id, (member, user) => user.Email)
                    .Where(email => email != null)
                    .Select(email => email!)
                    .ToListAsync(cancellation);

                recipients[row.WorkspaceId] = to;
            }

            foreach (var address in to)
            {
                await mail.SendAsync(new EmailMessage
                {
                    To = address,
                    Subject = localizer["mail.failed.subject", project.Name].Value,
                    PlainText = localizer["mail.failed.body", sentence, link ?? row.LinkPath ?? ""].Value,
                }, cancellation);
            }

            row.EmailedAt = now;
        }

        if (project.WebhookUrl is { Length: > 0 } hook
            && row.WebhookAt is null
            && row.WebhookAttempts < Notification.MaxWebhookAttempts
            && Due(row, now))
        {
            await PostAsync(cipher, row, project, hook, sentence, link, now, cancellation);
        }
    }

    /// <summary>Linear backoff — a minute more per failed attempt. Enough space for a restarting
    /// receiver; not so much that the fifth try lands tomorrow.</summary>
    private static bool Due(Notification row, DateTimeOffset now) =>
        row.WebhookAttempts == 0 || row.UpdatedAt <= now.AddMinutes(-row.WebhookAttempts);

    private async Task PostAsync(
        ISecretCipher cipher, Notification row, Project project, string hook,
        string sentence, string? link, DateTimeOffset now, CancellationToken cancellation)
    {
        _ = hook;

        var delivery = await WebhookSender.SendAsync(
            http, cipher, project,
            WebhookSender.Payload(row.Kind, project.Name, row.TargetLabel, sentence, link, row.CreatedAt),
            cancellation);

        row.WebhookAttempts++;

        if (delivery.Ok)
        {
            row.WebhookAt = now;
            row.WebhookFailure = null;
        }
        else
        {
            row.WebhookFailure = delivery.Detail;
        }
    }

    private static string[] Args(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
