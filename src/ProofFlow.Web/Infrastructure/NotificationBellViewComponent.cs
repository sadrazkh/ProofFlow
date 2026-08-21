using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// The bell: the newest few notifications, and how many the reader has not seen.
///
/// A view component rather than filter state because it belongs to the shell, not to any one
/// controller — every authenticated page carries it, and the query is one bounded read per
/// request. «Seen» is the reader's own timestamp; the rows themselves are workspace-shared,
/// because a failure is news to the team and not to one person.
/// </summary>
public sealed class NotificationBellViewComponent(ProofFlowDbContext db, ICurrentUser me) : ViewComponent
{
    public const int Shown = 10;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!me.IsAuthenticated || me.WorkspaceId is null)
        {
            return View(new NotificationBellViewModel([], 0));
        }

        var seenAt = await db.Users
            .Where(user => user.Id == me.UserId)
            .Select(user => user.NotificationsSeenAt)
            .FirstOrDefaultAsync();

        var rows = await db.Notifications
            .OrderByDescending(n => n.CreatedAt)
            .Take(Shown)
            .Select(n => new NotificationRow(n.Kind, n.ArgsJson, n.LinkPath, n.CreatedAt))
            .ToListAsync();

        // Counted over what the menu shows rather than the whole table: «9+» tells the same story
        // as «214» and does not need a second unbounded count on every page load.
        var unseen = seenAt is { } marker
            ? rows.Count(row => row.CreatedAt > marker)
            : rows.Count;

        return View(new NotificationBellViewModel(rows, unseen));
    }
}

public sealed record NotificationBellViewModel(IReadOnlyList<NotificationRow> Rows, int Unseen);

public sealed record NotificationRow(string Kind, string? ArgsJson, string? LinkPath, DateTimeOffset CreatedAt)
{
    /// <summary>The sentence's arguments, decoded — empty on anything malformed, never a throw.</summary>
    public string[] Args
    {
        get
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(ArgsJson ?? "[]") ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }
    }
}
