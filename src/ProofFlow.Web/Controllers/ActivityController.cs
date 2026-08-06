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

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);

        // One row more than a page is asked for, purely to answer "is there a next page" without
        // a second COUNT over a table that only grows.
        var rows = await db.AuditEvents
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
        });
    }
}
