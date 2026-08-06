using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Workspaces;

/// <summary>
/// The outermost container: a team, and everything it owns.
///
/// Every project, environment, secret, baseline and run hangs off exactly one workspace, and the
/// database enforces that with a global query filter rather than a remembered <c>Where</c> clause.
/// </summary>
public class Workspace : Entity
{
    public required string Name { get; set; }

    /// <summary>URL-safe, unique, and stable — it appears in links people paste to each other.</summary>
    public required string Slug { get; set; }

    public Guid CreatedByUserId { get; set; }

    public ICollection<WorkspaceMember> Members { get; set; } = [];
}
