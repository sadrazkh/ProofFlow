using FluentAssertions;
using ProofFlow.Domain.Authorization;

namespace ProofFlow.Tests;

public class RoleCapabilityTests
{
    [Fact]
    public void An_owner_holds_every_capability()
    {
        foreach (var capability in Enum.GetValues<Capability>())
            RoleCapabilities.Allows(WorkspaceRole.Owner, capability).Should().BeTrue($"{capability}");
    }

    [Fact]
    public void A_test_designer_may_propose_a_baseline_but_not_approve_one()
    {
        // The whole point of the review workflow. If one role can do both, the approval step is a
        // formality that records a name without meaning anything.
        RoleCapabilities.Allows(WorkspaceRole.TestDesigner, Capability.RecordBaseline).Should().BeTrue();
        RoleCapabilities.Allows(WorkspaceRole.TestDesigner, Capability.ApproveBaseline).Should().BeFalse();
    }

    [Fact]
    public void A_reviewer_approves_but_does_not_author()
    {
        RoleCapabilities.Allows(WorkspaceRole.Reviewer, Capability.ApproveBaseline).Should().BeTrue();
        RoleCapabilities.Allows(WorkspaceRole.Reviewer, Capability.CreateTest).Should().BeFalse();
        RoleCapabilities.Allows(WorkspaceRole.Reviewer, Capability.EditTest).Should().BeFalse();
    }

    [Fact]
    public void A_runner_runs_and_reads_and_changes_nothing()
    {
        RoleCapabilities.Allows(WorkspaceRole.Runner, Capability.RunTest).Should().BeTrue();
        RoleCapabilities.Allows(WorkspaceRole.Runner, Capability.ViewRun).Should().BeTrue();

        foreach (var capability in new[]
                 {
                     Capability.CreateTest, Capability.EditTest, Capability.DeleteTest,
                     Capability.ManageEnvironment, Capability.ManageSecret, Capability.RecordBaseline,
                     Capability.ApproveBaseline, Capability.ManageMembers, Capability.DeleteRun,
                 })
        {
            RoleCapabilities.Allows(WorkspaceRole.Runner, capability).Should().BeFalse($"{capability}");
        }
    }

    [Fact]
    public void A_viewer_cannot_even_run()
    {
        RoleCapabilities.Allows(WorkspaceRole.Viewer, Capability.ViewProject).Should().BeTrue();
        RoleCapabilities.Allows(WorkspaceRole.Viewer, Capability.RunTest).Should().BeFalse();
    }

    [Fact]
    public void Only_owners_and_admins_may_reveal_a_secret()
    {
        var allowed = Enum.GetValues<WorkspaceRole>()
            .Where(role => RoleCapabilities.Allows(role, Capability.ViewSecret))
            .ToArray();

        allowed.Should().BeEquivalentTo([WorkspaceRole.Owner, WorkspaceRole.Admin]);
    }

    [Fact]
    public void Every_role_has_at_least_one_capability()
    {
        // A role that grants nothing is a role that silently locks someone out of the tool they
        // were just invited to.
        foreach (var role in Enum.GetValues<WorkspaceRole>())
            RoleCapabilities.For(role).Should().NotBeEmpty($"{role}");
    }
}
