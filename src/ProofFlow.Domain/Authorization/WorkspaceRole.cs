namespace ProofFlow.Domain.Authorization;

/// <summary>
/// The six roles of the brief, ordered from most to least authority.
///
/// Numbered explicitly and never renumbered: the value is persisted, so shifting it would
/// silently promote or demote every existing member.
/// </summary>
public enum WorkspaceRole
{
    /// <summary>Everything, including deleting the workspace and transferring ownership.</summary>
    Owner = 1,

    /// <summary>Everything operational: members, environments, schedules, secrets.</summary>
    Admin = 2,

    /// <summary>Builds and runs tests, records baselines — but cannot approve their own.</summary>
    TestDesigner = 3,

    /// <summary>Approves or rejects baseline changes. Reviews, does not author.</summary>
    Reviewer = 4,

    /// <summary>Runs existing tests and reads results. Changes nothing.</summary>
    Runner = 5,

    /// <summary>Reads. The default for a new member.</summary>
    Viewer = 6,
}
