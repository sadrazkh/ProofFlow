using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ProofFlow.Web.Controllers;

[AllowAnonymous]
[Route("error")]
public sealed class ErrorController(ILogger<ErrorController> logger) : Controller
{
    /// <summary>
    /// The themed page behind every failing status code.
    ///
    /// Section 23 of the brief forbids showing a raw stack trace to an ordinary reader, and this is
    /// where that rule is enforced: the exception is logged with its detail, and the page says what
    /// happened, whether anything was saved, and what to do next.
    /// </summary>
    [Route("{code:int}")]
    public IActionResult Status(int code)
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (feature?.Error is { } error)
        {
            logger.LogError(error, "Unhandled failure at {Path}.", feature.Path);
        }

        Response.StatusCode = code is >= 400 and < 600 ? code : 500;

        ViewData["Code"] = Response.StatusCode;
        return View("Error");
    }
}
