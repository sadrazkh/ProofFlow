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
    ///
    /// Slate by default, which is no colour at all. It used to be indigo — the same hue the
    /// application uses for «this is the action» — so a workspace of twelve projects nobody had
    /// coloured showed twelve indigo marks, and the one place indigo meant something was drowned
    /// out by eleven places where it meant nothing.
    /// </summary>
    public string Accent { get; set; } = DefaultAccent;

    /// <summary>The colour a project is given when nobody chose one.</summary>
    public const string DefaultAccent = "slate";

    /// <summary>
    /// How many days of response bodies and log lines to keep. Zero means keep them for ever.
    ///
    /// The setting is about payloads, not about history. A run's verdict, its timings and its
    /// assertion results stay for ever — they are what a trend is made of, and they are small. What
    /// goes is the bulk: the bodies the steps produced, the log lines, and the artefacts. Those are
    /// what make a testing tool's database grow without limit, and they are also the part most
    /// likely to hold somebody's personal data six months after anybody needed it.
    ///
    /// Thirty days by default, which is long enough to investigate last month's failure and short
    /// enough that nobody discovers a hundred gigabytes of stale response bodies.
    /// </summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    public const int DefaultRetentionDays = 30;

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public bool IsArchived => ArchivedAt is not null;

    /// <summary>
    /// SHA-256 of the status-badge token, when one has been issued. Null means no badge.
    ///
    /// The token itself is shown once and never stored — the <see cref="Domain.Scheduling.ApiKey"/>
    /// rule, for the same reason: a value that lets an anonymous request read this project's
    /// verdict must not be readable out of the database. Plain SHA-256 rather than a slow hash
    /// because the token is 256 random bits, not a password.
    /// </summary>
    public string? BadgeHash { get; set; }

    /// <summary>The first characters of the badge token — enough to recognise, never enough to use.</summary>
    public string? BadgePreview { get; set; }
}
