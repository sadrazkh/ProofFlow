using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Where a member can go, and what it is called.
///
/// One map, read by the sidebar and by the command palette. Two lists would drift, and the way
/// they drift is that the palette keeps offering a page the sidebar stopped showing — which is
/// how someone reaches a screen their role was supposed to have lost.
/// </summary>
public static class Navigation
{
    public static IReadOnlyList<NavSection> For(ICurrentUser user, Guid? projectId)
    {
        var sections = new List<NavSection>
        {
            new("nav.overview", [
                new NavItem("demo.start", "rocket", "/start", Capability.ViewProject),
                new NavItem("nav.dashboard", "layout-dashboard", "/", Capability.ViewProject),
                new NavItem("nav.projects", "folder-open", "/projects", Capability.ViewProject),
            ]),
        };

        // The project-scoped sections only mean anything inside a project, and showing them
        // greyed-out elsewhere would be eleven dead links on the dashboard.
        if (projectId is { } id)
        {
            var basePath = $"/projects/{id}";

            // Two entries used to sit here that went nowhere: «suites», which exists in the schema
            // and has no page, and «variables», which is part of the environment it belongs to and
            // is edited there. A sidebar that 404s is worse than a shorter sidebar, and the command
            // palette reads this same map — so it offered them too.
            //
            // Four more went with the endpoint page. «Baselines» and «Captures» were the two halves
            // of one job seen from two angles, «Guided setup» was a nine-step apology for that job
            // having no page, and the per-endpoint half of the review queue now lives on the
            // endpoint. Eleven destinations under Build and Verify became seven, and none of the
            // seven is named after a mechanism.
            sections.Add(new NavSection("nav.section_build", [
                new NavItem("nav.endpoints", "target", $"{basePath}/endpoints", Capability.ViewProject),
                new NavItem("nav.scenarios", "workflow", $"{basePath}/scenarios", Capability.ViewProject),
                new NavItem("nav.datasets", "table-2", $"{basePath}/datasets", Capability.ViewProject),
                new NavItem("nav.environments", "globe", $"{basePath}/environments", Capability.ViewProject),
            ]));

            sections.Add(new NavSection("nav.section_verify", [
                new NavItem("nav.runs", "history", $"{basePath}/runs", Capability.ViewRun),
                new NavItem("nav.matrix", "layout-grid", $"{basePath}/matrix", Capability.ViewRun),
                new NavItem("approval.title", "check-check", $"{basePath}/approvals", Capability.ViewProject),
            ]));

            sections.Add(new NavSection("nav.section_operate", [
                new NavItem("nav.schedules", "calendar-clock", $"{basePath}/schedules", Capability.ViewProject),
                new NavItem("portability.import", "upload", $"{basePath}/import", Capability.ImportProject),
                new NavItem("portability.export", "download", $"{basePath}/export", Capability.ExportProject),
                new NavItem("nav.settings", "settings", $"{basePath}/settings", Capability.ManageProject),
            ]));
        }

        sections.Add(new NavSection(null, [
            new NavItem("nav.team", "users", "/team", Capability.ViewProject),
            new NavItem("runner.title", "server", "/runners", Capability.ManageRunner),
            new NavItem("nav.audit", "shield", "/activity", Capability.ViewAudit),
            new NavItem("workspaceSettings.title", "sliders-horizontal", "/settings/workspace", Capability.ManageMembers),
        ]));

        return sections
            .Select(section => section with { Items = [.. section.Items.Where(item => user.Can(item.Capability))] })
            .Where(section => section.Items.Count > 0)
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="current"/> is the page this item points at.
    ///
    /// A prefix match, but only on a segment boundary: without that check, /projects lights up
    /// while the reader is on /projects-archive, and /runs lights up on /runs-export.
    /// </summary>
    public static bool IsActive(string itemPath, string current)
    {
        if (itemPath == "/") return current is "/" or "";
        if (!current.StartsWith(itemPath, StringComparison.OrdinalIgnoreCase)) return false;
        return current.Length == itemPath.Length || current[itemPath.Length] == '/';
    }
}

public sealed record NavSection(string? TitleKey, IReadOnlyList<NavItem> Items);

public sealed record NavItem(string LabelKey, string Icon, string Path, Capability Capability, int? Badge = null);
