using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Finding the things a person made, rather than the pages they live on.
///
/// The command palette used to list destinations and nothing else, so typing «orders» found
/// nothing at all while an endpoint called <c>GET /orders</c> sat three pages into a table. A
/// destination list is a map of the building; this is the index.
///
/// Everything here goes through the ordinary DbSets, which means the global tenant filter decides
/// what exists. Nothing in this file calls <c>IgnoreQueryFilters</c>, and that is the whole
/// security story: a search box is the one control people type other people's words into, and a
/// hand-written «WorkspaceId ==» somewhere in a chain of four queries is the line somebody
/// eventually forgets.
///
/// Matching is a plain <c>Contains</c> so the same code translates on SQLite and on PostgreSQL. No
/// full-text index, no raw SQL, no trigram extension — four scans over one workspace's names cost
/// nothing, and the day they do, the answer is an index rather than a second search engine to keep
/// in step with the first.
///
/// One thing it does not do, and it should be said rather than discovered: the match is
/// case-sensitive, so an endpoint named «GET /Orders» is not found by typing «orders». Folding both
/// sides with <c>lower()</c> translates on both providers and is the fix, and it is left for the
/// moment somebody actually asks — it changes what every one of these queries means, and that is
/// worth doing on purpose rather than in passing.
/// </summary>
[Authorize]
[Route("search")]
public sealed class SearchController(ProofFlowDbContext db, ICurrentUser me) : Controller
{
    /// <summary>How many rows one kind of thing may contribute. Five is a glance.</summary>
    private const int PerGroup = 5;

    /// <summary>The whole answer's ceiling, however the groups divide it up.</summary>
    private const int Overall = 20;

    /// <summary>
    /// Longer than any name anybody has, and short enough that a pasted document cannot become a
    /// pattern the database has to drag across every row.
    /// </summary>
    private const int LongestTerm = 200;

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, Guid? project, CancellationToken cancellationToken)
    {
        var term = (q ?? string.Empty).Trim();

        // Nothing typed, nothing asked — and nothing read. The palette fires this on every
        // keystroke including the backspace that empties the box, and «everything you own, ordered
        // by name» is not an answer to a blank question.
        if (term.Length == 0) return Json(new SearchAnswer([]));
        if (term.Length > LongestTerm) term = term[..LongestTerm];

        var groups = new List<SearchGroup>();
        var left = Overall;

        // The same capability the sidebar entry is gated on, checked for the same reason: a
        // palette that offers a row a role cannot open is a 403 with extra steps, and a search
        // result is a stronger claim than a menu item — it says «this exists».
        var canView = me.Can(Capability.ViewProject);

        if (canView && left > 0)
        {
            var endpoints = db.Baselines.Where(b => b.ArchivedAt == null);
            if (project is { } id) endpoints = endpoints.Where(b => b.ProjectId == id);

            left -= Add(groups, "nav.endpoints", "target", await endpoints
                // The address is inside the stored request document rather than in a column of its
                // own, so the filter is on the document. That casts a slightly wider net — a header
                // value can match — which is the right way round for a search box.
                .Where(b => b.Name.Contains(term)
                            || (b.RequestJson != null && b.RequestJson.Contains(term)))
                .OrderBy(b => b.Name)
                .Take(Math.Min(PerGroup, left))
                .Select(b => new Found(b.Name, b.ProjectId, $"/projects/{b.ProjectId}/endpoints/{b.Id}"))
                .ToListAsync(cancellationToken));
        }

        if (canView && left > 0)
        {
            var scenarios = db.Scenarios.Where(s => s.ArchivedAt == null);
            if (project is { } id) scenarios = scenarios.Where(s => s.ProjectId == id);

            left -= Add(groups, "nav.scenarios", "workflow", await scenarios
                .Where(s => s.Name.Contains(term))
                .OrderBy(s => s.Name)
                .Take(Math.Min(PerGroup, left))
                .Select(s => new Found(s.Name, s.ProjectId, $"/projects/{s.ProjectId}/scenarios/{s.Id}"))
                .ToListAsync(cancellationToken));
        }

        if (canView && left > 0)
        {
            var environments = db.Environments.AsQueryable();
            if (project is { } id) environments = environments.Where(e => e.ProjectId == id);

            left -= Add(groups, "nav.environments", "globe", await environments
                .Where(e => e.Name.Contains(term))
                .OrderBy(e => e.Name)
                .Take(Math.Min(PerGroup, left))
                // An environment has no page of its own; the list opens with one selected. Linking
                // to the bare list would make every environment result the same link.
                .Select(e => new Found(
                    e.Name, e.ProjectId, $"/projects/{e.ProjectId}/environments?selected={e.Id}"))
                .ToListAsync(cancellationToken));
        }

        if (canView && left > 0)
        {
            var dataSets = db.DataSets.Where(d => d.ArchivedAt == null);
            if (project is { } id) dataSets = dataSets.Where(d => d.ProjectId == id);

            left -= Add(groups, "nav.datasets", "table-2", await dataSets
                .Where(d => d.Name.Contains(term))
                .OrderBy(d => d.Name)
                .Take(Math.Min(PerGroup, left))
                .Select(d => new Found(d.Name, d.ProjectId, $"/projects/{d.ProjectId}/datasets/{d.Id}"))
                .ToListAsync(cancellationToken));
        }

        // Which project a thing is in, but only when the search spanned more than one. Inside a
        // project it is the same word under every row, and a subtitle that never varies is noise.
        var names = project is null ? await ProjectNamesAsync(groups, cancellationToken) : [];

        return Json(new SearchAnswer([
            .. groups.Select(group => group with
            {
                Items = [.. group.Items.Select(item => item with
                {
                    Subtitle = names.GetValueOrDefault(item.ProjectId),
                })],
            }),
        ]));
    }

    private static int Add(List<SearchGroup> groups, string labelKey, string icon, List<Found> rows)
    {
        if (rows.Count == 0) return 0;

        groups.Add(new SearchGroup(labelKey, [
            .. rows.Select(row => new SearchItem(row.Name, null, row.Path, icon) { ProjectId = row.Project }),
        ]));

        return rows.Count;
    }

    private async Task<Dictionary<Guid, string>> ProjectNamesAsync(
        List<SearchGroup> groups, CancellationToken cancellationToken)
    {
        var wanted = groups
            .SelectMany(group => group.Items)
            .Select(item => item.ProjectId)
            .Distinct()
            .ToList();

        if (wanted.Count == 0) return [];

        return await db.Projects
            .Where(p => wanted.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
    }

    private sealed record Found(string Name, Guid Project, string Path);
}

/// <summary>
/// What the browser is handed.
///
/// Group labels are keys rather than sentences. The catalogue is already in every page for the
/// islands, so the browser localises «Endpoints» itself — and a server that formatted them would
/// be deciding the language of a list the palette may still be holding after somebody switches it.
/// </summary>
public sealed record SearchAnswer(IReadOnlyList<SearchGroup> Groups);

public sealed record SearchGroup(string LabelKey, IReadOnlyList<SearchItem> Items);

public sealed record SearchItem(string Title, string? Subtitle, string Path, string Icon)
{
    /// <summary>Which project this came from. Carried to look the name up, never sent.</summary>
    [JsonIgnore]
    public Guid ProjectId { get; init; }
}
