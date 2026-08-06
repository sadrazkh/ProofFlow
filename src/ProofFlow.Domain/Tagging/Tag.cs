using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Tagging;

/// <summary>
/// A free label people attach to scenarios, baselines and runs so they can find them later
/// ("smoke", "nightly", "billing").
///
/// Workspace-scoped rather than project-scoped: teams reuse the same handful of words across
/// projects, and a tag that means one thing here and another thing there is worse than no tag.
/// </summary>
public class Tag : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    /// <summary>A design-system accent name, matching <see cref="Projects.Project.Accent"/>.</summary>
    public string Accent { get; set; } = "slate";
}

/// <summary>
/// The join between a tag and whatever it is on.
///
/// Polymorphic by <see cref="TargetType"/> + <see cref="TargetId"/> rather than a join table per
/// taggable entity. There are six taggable things and there will be more; six near-identical
/// tables is a schema that punishes adding the seventh.
/// </summary>
public class TagAssignment : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid TagId { get; set; }

    public Tag? Tag { get; set; }

    public required string TargetType { get; set; }

    public Guid TargetId { get; set; }
}
