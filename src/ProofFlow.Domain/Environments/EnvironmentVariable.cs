using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Environments;

/// <summary>
/// A named value a scenario can refer to as <c>{{environment.name}}</c> or <c>{{vars.name}}</c>.
///
/// A variable with a null <see cref="EnvironmentId"/> belongs to the project and applies to every
/// environment; one with an id overrides it there. That is the whole resolution rule, and it is
/// deliberately only two levels deep — a third would make "where did this value come from?"
/// a question nobody can answer while looking at a failing run.
/// </summary>
public class EnvironmentVariable : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>Null for a project-wide default.</summary>
    public Guid? EnvironmentId { get; set; }

    public ProjectEnvironment? Environment { get; set; }

    public required string Name { get; set; }

    public string Value { get; set; } = string.Empty;

    public string? Description { get; set; }
}
