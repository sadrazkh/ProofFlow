using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Puts the current workspace's name where the layout can find it.
///
/// A filter rather than a view injection so the lookup happens once per request instead of once
/// per partial that wants it, and so a view never issues a query of its own — a database call
/// inside a Razor page is a call nobody can see when they are reading the controller.
/// </summary>
public sealed class WorkspaceContextFilter(ProofFlowDbContext db, ICurrentUser currentUser) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (currentUser.WorkspaceId is { } workspaceId)
        {
            var name = await db.Workspaces
                .Where(w => w.Id == workspaceId)
                .Select(w => w.Name)
                .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

            context.HttpContext.Items["WorkspaceName"] = name;
        }

        // The project a page belongs to drives which navigation sections appear. Read from the
        // route so every project-scoped page gets it without remembering to set it.
        if (context.RouteData.Values.TryGetValue("projectId", out var raw)
            && Guid.TryParse(raw?.ToString(), out var projectId))
        {
            context.HttpContext.Items["ProjectId"] = projectId;
        }

        await next();
    }
}
