using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

[Authorize(Policy = Policies.ViewAudit)]
[Route("activity")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class ActivityController(ProofFlowDbContext db, IStringLocalizer localizer) : Controller
{
    private const int PageSize = 50;

    /// <summary>
    /// The log, narrowed by who and by what kind.
    ///
    /// The second one is called <c>kind</c> rather than <c>action</c> on purpose. <c>action</c> is a
    /// reserved routing token, so a parameter of that name binds to the name of this method — the
    /// filter then silently reads "Index", matches nothing, and the page shows an empty log with a
    /// Clear button on it. Nothing throws; it just quietly answers the wrong question.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(
        int page = 1, string? actor = null, string? kind = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);

        var query = db.AuditEvents.AsQueryable();

        // Two filters and no more. An audit log is read to answer one of two questions — "what did
        // this person do" and "who did this kind of thing" — and a form with eight fields is one
        // nobody fills in.
        if (!string.IsNullOrWhiteSpace(actor))
        {
            var needle = actor.Trim();
            query = query.Where(a => a.ActorDisplay != null && a.ActorDisplay.Contains(needle));
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            // A prefix, so "baseline" finds every baseline event rather than only the exact one
            // somebody happened to pick out of a list.
            var prefix = kind.Trim();
            query = query.Where(a => a.Action.StartsWith(prefix));
        }

        // One row more than a page is asked for, purely to answer "is there a next page" without
        // a second COUNT over a table that only grows.
        var rows = await query
            .OrderByDescending(a => a.OccurredAt)
            .Skip((page - 1) * PageSize)
            .Take(PageSize + 1)
            .Select(a => new AuditRowViewModel
            {
                Actor = a.ActorDisplay,
                ActionKey = a.Action,
                TargetLabel = a.TargetLabel,
                TargetType = a.TargetType,
                OccurredAt = a.OccurredAt,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > PageSize;

        ViewData["Title"] = localizer["audit.title"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)> { (localizer["audit.title"].Value, null) };

        return View(new AuditListViewModel
        {
            Events = hasMore ? rows[..PageSize] : rows,
            Page = page,
            HasMore = hasMore,
            Actor = actor,
            Kind = kind,

            // The kinds of thing that have actually happened here, rather than a fixed list of
            // every action the code can emit — most of which this workspace has never done.
            Kinds = await db.AuditEvents
                .Select(a => a.Action)
                .Distinct()
                .OrderBy(name => name)
                .Take(60)
                .ToListAsync(cancellationToken),
        });
    }
}
