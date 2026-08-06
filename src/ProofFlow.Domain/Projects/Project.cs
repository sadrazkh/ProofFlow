using ProofFlow.Domain.Common;
using ProofFlow.Domain.Workspaces;

namespace ProofFlow.Domain.Projects;

/// <summary>
/// One system under test, with its environments, suites, data sets and baselines.
///
/// A project is the unit people think in ("the orders API"), the unit permissions and exports are
/// scoped to, and the unit a baseline belongs to. Everything below it is meaningless without it.
/// </summary>
public class Project : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public required string Name { get; set; }

    /// <summary>Unique within the workspace. Appears in URLs and in exported files.</summary>
    public required string Slug { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// One of a small fixed set of accent names (not a hex value), so the palette stays under the
    /// design system's control and cannot be given a colour that fails contrast in dark mode.
    /// </summary>
    public string Accent { get; set; } = "indigo";

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public bool IsArchived => ArchivedAt is not null;
}
