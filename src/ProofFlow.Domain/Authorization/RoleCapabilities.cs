namespace ProofFlow.Domain.Authorization;

/// <summary>
/// Which role holds which capability. One table, consulted by the policy handler and by the
/// navigation that decides what to render.
///
/// Written as an explicit set per role rather than as "Admin inherits Designer plus…". Inheritance
/// reads well until someone needs a Reviewer who may approve but may not author, and then the
/// chain has to be broken anyway. Being able to read one line and know exactly what a Reviewer can
/// do is worth the repetition.
/// </summary>
public static class RoleCapabilities
{
    private static readonly Capability[] ReadOnly =
    [
        Capability.ViewProject,
        Capability.ViewRun,
    ];

    private static readonly Capability[] Runner =
    [
        Capability.ViewProject,
        Capability.ViewRun,
        Capability.RunTest,
        Capability.CancelRun,
    ];

    private static readonly Capability[] Reviewer =
    [
        Capability.ViewProject,
        Capability.ViewRun,
        Capability.ViewAudit,
        Capability.RunTest,
        Capability.CancelRun,
        Capability.ApproveBaseline,
    ];

    private static readonly Capability[] TestDesigner =
    [
        Capability.ViewProject,
        Capability.ViewRun,
        Capability.CreateTest,
        Capability.EditTest,
        Capability.DeleteTest,
        Capability.ManageDataSet,
        Capability.RunTest,
        Capability.CancelRun,
        Capability.RecordBaseline,
        Capability.ExportProject,
        // Deliberately absent: ApproveBaseline. A designer who can approve their own change turns
        // the review workflow into a formality.
    ];

    private static readonly Capability[] Admin =
    [
        Capability.ViewProject,
        Capability.ViewRun,
        Capability.ViewAudit,
        Capability.ViewSecret,
        Capability.CreateTest,
        Capability.EditTest,
        Capability.DeleteTest,
        Capability.ManageDataSet,
        Capability.RunTest,
        Capability.CancelRun,
        Capability.DeleteRun,
        Capability.RecordBaseline,
        Capability.ApproveBaseline,
        Capability.ManageEnvironment,
        Capability.ManageSecret,
        Capability.ManageSchedule,
        Capability.ManageProject,
        Capability.ManageMembers,
        Capability.ManageRunner,
        Capability.ExportProject,
        Capability.ImportProject,
    ];

    private static readonly Dictionary<WorkspaceRole, HashSet<Capability>> Map = new()
    {
        [WorkspaceRole.Owner] = [.. Enum.GetValues<Capability>()],
        [WorkspaceRole.Admin] = [.. Admin],
        [WorkspaceRole.TestDesigner] = [.. TestDesigner],
        [WorkspaceRole.Reviewer] = [.. Reviewer],
        [WorkspaceRole.Runner] = [.. Runner],
        [WorkspaceRole.Viewer] = [.. ReadOnly],
    };

    public static bool Allows(WorkspaceRole role, Capability capability) =>
        Map.TryGetValue(role, out var set) && set.Contains(capability);

    public static IReadOnlyCollection<Capability> For(WorkspaceRole role) =>
        Map.TryGetValue(role, out var set) ? set : [];
}
