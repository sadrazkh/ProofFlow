using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProofFlow.Web.Infrastructure;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// The live reference for every component in the design system.
///
/// It exists because a component defined in CSS and used nowhere is a component that will be
/// implemented slightly differently the first time somebody actually needs it — and this
/// application is about to grow a canvas, a diff viewer, a run console and a wizard, all of which
/// will reach for tabs, skeletons and badges that until now nothing has rendered.
///
/// It is also the cheapest visual regression test available: one page in the screenshot matrix
/// covers every component in both themes, both languages and three widths, so a token change that
/// breaks a badge is visible without hunting for a page that happens to use one.
///
/// Development only. Not because it leaks anything — it renders no data — but because a route
/// that exists in production is a route that has to be maintained, secured and explained.
/// </summary>
[AllowAnonymous]
[Route("design")]
// Reachable signed out, but when somebody *is* signed in the shell around the reference has to
// look like the shell around every other page — otherwise the sidebar renders a nameless
// workspace and the reference is showing a state the application never actually produces.
[ServiceFilter<WorkspaceContextFilter>]
public sealed class DesignController(IWebHostEnvironment environment) : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        if (!environment.IsDevelopment()) return NotFound();

        ViewData["Title"] = "Design system";
        ViewData["Breadcrumbs"] = new List<(string, string?)> { ("Design system", null) };
        return View();
    }
}
