using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// One word about the newest run, as an SVG a README can embed.
///
/// Anonymous at the framework level with the credential checked by hand — the
/// <see cref="RunnerApiController"/> pattern. The token proves nothing about who is asking and
/// everything about what may be answered: the project's name and one word. No counts, no scenario
/// names, no timings — a badge lives in caches and screenshots, and everything on it should be
/// something the project owner already decided to publish by minting the token.
/// </summary>
[AllowAnonymous]
[Route("badge")]
public sealed class BadgeController(ProofFlowDbContext db) : Controller
{
    [HttpGet("{token}.svg")]
    public async Task<IActionResult> Svg(string token, CancellationToken cancellationToken)
    {
        // Cheap refusals before a database round trip: the token is 32 base64url-encoded bytes.
        if (token.Length is < 40 or > 50) return NotFound();

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        // No signed-in workspace on an anonymous request, so the tenant filter would hide
        // everything; the badge hash is itself the authorisation.
        var project = await db.Projects.IgnoreQueryFilters()
            .Where(candidate => candidate.BadgeHash == hash && candidate.ArchivedAt == null)
            .Select(candidate => new { candidate.Id, candidate.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (project is null) return NotFound();

        var status = await db.Runs.IgnoreQueryFilters()
            .Where(run => run.ProjectId == project.Id
                          && (run.Status == RunStatus.Passed
                              || run.Status == RunStatus.Failed
                              || run.Status == RunStatus.Errored))
            .OrderByDescending(run => run.CreatedAt)
            .Select(run => (RunStatus?)run.Status)
            .FirstOrDefaultAsync(cancellationToken);

        var (word, colour) = status switch
        {
            RunStatus.Passed => ("passing", "#2da44e"),
            RunStatus.Failed => ("failing", "#cf222e"),
            RunStatus.Errored => ("errored", "#bf8700"),
            _ => ("no runs", "#59636e"),
        };

        // Short-lived by design: the word changes when the next run finishes, and GitHub's image
        // proxy honours max-age. Five minutes stale is a badge; five hours stale is a lie.
        Response.Headers.CacheControl = "public, max-age=300";

        return Content(Draw(project.Name, word, colour), "image/svg+xml; charset=utf-8");
    }

    /// <summary>
    /// The two-box badge shape everyone recognises, drawn by hand.
    ///
    /// Hex colours rather than the design tokens, deliberately: this SVG is served to other sites'
    /// image proxies, where the application's stylesheet does not exist. Width is estimated from
    /// character count — a README badge, not a layout engine.
    /// </summary>
    private static string Draw(string name, string word, string colour)
    {
        var label = name.Length > 40 ? name[..40] + "…" : name;

        var labelWidth = 12 + Width(label);
        var wordWidth = 12 + Width(word);
        var total = labelWidth + wordWidth;

        var escaped = Escape(label);

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{total}" height="20" role="img" aria-label="{escaped}: {word}">
              <title>{escaped}: {word}</title>
              <clipPath id="r"><rect width="{total}" height="20" rx="3" fill="#fff"/></clipPath>
              <g clip-path="url(#r)">
                <rect width="{labelWidth}" height="20" fill="#55555f"/>
                <rect x="{labelWidth}" width="{wordWidth}" height="20" fill="{colour}"/>
              </g>
              <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">
                <text x="{labelWidth / 2.0}" y="14">{escaped}</text>
                <text x="{labelWidth + wordWidth / 2.0}" y="14">{word}</text>
              </g>
            </svg>
            """;
    }

    /// <summary>Roughly 7px per character at 11px Verdana — the shields.io approximation.</summary>
    private static int Width(string text) =>
        (int)Math.Ceiling(text.Sum(c => char.IsAscii(c) ? 7.0 : 11.0));

    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
