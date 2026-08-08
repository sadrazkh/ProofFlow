using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Common;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;
using ProofFlow.Infrastructure.Workspaces;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// Who may do what, and the two rules that keep a workspace usable.
///
/// The separation of author and approver is the point of having a review at all, and the last owner
/// is the person who can repair everything else. Both are enforced here rather than in a form,
/// because a form is not the only caller.
/// </summary>
public sealed class TeamAndApprovalTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;

    private readonly Guid _workspaceId = Guid.CreateVersion7();
    private readonly Guid _owner = Guid.CreateVersion7();
    private readonly Guid _designer = Guid.CreateVersion7();
    private readonly Guid _reviewer = Guid.CreateVersion7();
    private readonly Guid _viewer = Guid.CreateVersion7();

    private Guid _projectId;
    private Guid _baselineId;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        await using var context = Db();
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ---- separation of duties ------------------------------------------------------------------

    [Fact]
    public async Task Nobody_approves_their_own_recording_while_somebody_else_could()
    {
        // The owner is the interesting case, because the owner is the one person who holds both
        // RecordBaseline and ApproveBaseline and could therefore do the whole thing alone.
        await using var context = Db();

        var version = await VersionAsync(context, recordedBy: _owner);

        var refusal = await Separation(context, _owner, WorkspaceRole.Owner)
            .RefusalAsync(version.CreatedByUserId);

        refusal.Should().Be("approval.notYourOwn");
    }

    [Fact]
    public async Task Somebody_else_approves_it_perfectly_well()
    {
        await using var context = Db();

        var version = await VersionAsync(context, recordedBy: _owner);

        var refusal = await Separation(context, _reviewer, WorkspaceRole.Reviewer)
            .RefusalAsync(version.CreatedByUserId);

        refusal.Should().BeNull();
    }

    [Fact]
    public async Task A_role_that_cannot_approve_is_refused_before_the_authorship_is_even_considered()
    {
        // A designer never approves anything, their own recording least of all — the capability
        // table leaves ApproveBaseline out of that role on purpose, and this is the check that
        // notices if somebody ever puts it back.
        await using var context = Db();

        var version = await VersionAsync(context, recordedBy: _designer);

        var refusal = await Separation(context, _designer, WorkspaceRole.TestDesigner)
            .RefusalAsync(version.CreatedByUserId);

        refusal.Should().Be("approval.notAllowed");
    }

    [Fact]
    public async Task In_a_workspace_of_one_the_rule_does_not_bind()
    {
        // A workspace of one person is not a governance failure, it is a workspace of one person,
        // and a product that refuses to let them approve anything is one they cannot use.
        await using var context = Db();

        var alone = new Guid("00000000-0000-0000-0000-0000000000aa");

        context.Workspaces.Add(new Workspace { Id = alone, Name = "Alone", Slug = "alone" });
        context.Users.Add(new ProofFlowUser { Id = alone, UserName = "solo", DisplayName = "Solo" });
        context.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = alone,
            UserId = alone,
            Role = WorkspaceRole.Owner,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        await using var solo = Db(alone);

        var refusal = await new Separation(solo, new FixedUser(alone, alone, WorkspaceRole.Owner))
            .RefusalAsync(alone);

        refusal.Should().BeNull();
    }

    [Fact]
    public async Task A_machine_has_no_self_to_separate_from()
    {
        // A run that recorded a baseline has no author to compare against; the capability check is
        // the only one that applies.
        await using var context = Db();

        var refusal = await Separation(context, _reviewer, WorkspaceRole.Reviewer).RefusalAsync(null);

        refusal.Should().BeNull();
    }

    [Fact]
    public async Task Only_somebody_who_could_actually_approve_counts_as_somebody_else()
    {
        // Three viewers and one owner is a workspace where nobody else can approve, and telling the
        // owner to find a reviewer who does not exist is worse than letting them approve.
        await using var context = Db();

        foreach (var member in await context.WorkspaceMembers
                     .Where(m => m.UserId != _owner)
                     .ToListAsync())
        {
            member.Role = WorkspaceRole.Viewer;
        }

        await context.SaveChangesAsync();

        await using var alone = Db();

        var separation = Separation(alone, _owner, WorkspaceRole.Owner);

        (await separation.SomebodyElseAsync()).Should().BeFalse();
        (await separation.RefusalAsync(_owner)).Should().BeNull();
    }

    // ---- the team ------------------------------------------------------------------------------

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_or_removed()
    {
        await using var context = Db();
        var team = Team(context, _reviewer);

        (await team.ChangeRoleAsync(_workspaceId, _owner, WorkspaceRole.Viewer))
            .Should().Be("team.lastOwner");

        (await team.RemoveAsync(_workspaceId, _owner)).Should().Be("team.lastOwner");

        // And is still an owner afterwards, which is the part that matters.
        (await context.WorkspaceMembers.FirstAsync(m => m.UserId == _owner))
            .Role.Should().Be(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task A_second_owner_makes_the_first_removable()
    {
        await using var context = Db();
        var team = Team(context, _reviewer);

        (await team.ChangeRoleAsync(_workspaceId, _designer, WorkspaceRole.Owner)).Should().BeNull();
        (await team.ChangeRoleAsync(_workspaceId, _owner, WorkspaceRole.Viewer)).Should().BeNull();

        (await context.WorkspaceMembers.CountAsync(m => m.Role == WorkspaceRole.Owner)).Should().Be(1);
    }

    [Fact]
    public async Task Nobody_changes_their_own_role()
    {
        // Somebody who could promote themselves does not have a role, they have a suggestion.
        await using var context = Db();
        var team = Team(context, _reviewer);

        (await team.ChangeRoleAsync(_workspaceId, _reviewer, WorkspaceRole.Owner))
            .Should().Be("team.notYourself");

        (await team.RemoveAsync(_workspaceId, _reviewer)).Should().Be("team.notYourself");
    }

    // ---- invitations ---------------------------------------------------------------------------

    [Fact]
    public async Task An_invitation_is_found_by_its_token_and_nothing_else()
    {
        await using var context = Db();
        var team = Team(context, _owner);

        var (invitation, token) = await team.InviteAsync(
            _workspaceId, "New.Person@Example.COM", WorkspaceRole.Runner);

        // Lower-cased, because that is how it will be matched at sign-up.
        invitation.Email.Should().Be("new.person@example.com");

        // Only the hash is stored, so a database in the wrong hands is not a way in.
        invitation.Hash.Should().NotBe(token);

        (await team.FindAsync(token))!.Id.Should().Be(invitation.Id);
        (await team.FindAsync(token + "x")).Should().BeNull();
        (await team.FindAsync(invitation.Hash)).Should().BeNull();
    }

    [Fact]
    public async Task Inviting_the_same_address_twice_leaves_one_live_link()
    {
        // Two live links to one mailbox is two ways in, and only one of them is the one somebody
        // meant to send.
        await using var context = Db();
        var team = Team(context, _owner);

        var (_, first) = await team.InviteAsync(_workspaceId, "twice@example.com", WorkspaceRole.Viewer);
        var (_, second) = await team.InviteAsync(_workspaceId, "twice@example.com", WorkspaceRole.Viewer);

        (await team.FindAsync(first)).Should().BeNull();
        (await team.FindAsync(second)).Should().NotBeNull();
    }

    [Fact]
    public async Task Accepting_makes_a_member_and_closes_the_invitation()
    {
        await using var context = Db();
        var team = Team(context, _owner);

        var joiner = Guid.CreateVersion7();
        context.Users.Add(new ProofFlowUser { Id = joiner, UserName = "joiner", DisplayName = "Joiner" });
        await context.SaveChangesAsync();

        var (invitation, token) = await team.InviteAsync(
            _workspaceId, "joiner@example.com", WorkspaceRole.Runner);

        (await team.AcceptAsync(invitation, joiner)).Should().BeTrue();

        var member = await context.WorkspaceMembers.FirstAsync(m => m.UserId == joiner);
        member.Role.Should().Be(WorkspaceRole.Runner);
        member.JoinedAt.Should().NotBeNull();

        // Used up: the same link cannot be walked through twice.
        (await team.FindAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task An_expired_or_revoked_invitation_is_not_usable()
    {
        await using var context = Db();
        var team = Team(context, _owner);

        var (revoked, revokedToken) = await team.InviteAsync(
            _workspaceId, "gone@example.com", WorkspaceRole.Viewer);

        await team.RevokeInvitationAsync(_workspaceId, revoked.Id);
        (await team.FindAsync(revokedToken)).Should().BeNull();

        var (lapsed, lapsedToken) = await team.InviteAsync(
            _workspaceId, "old@example.com", WorkspaceRole.Viewer);

        lapsed.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
        await context.SaveChangesAsync();

        (await team.FindAsync(lapsedToken)).Should().BeNull();
    }

    [Fact]
    public async Task Somebody_already_in_the_workspace_cannot_be_invited_again()
    {
        await using var context = Db();

        var invite = async () => await Team(context, _owner)
            .InviteAsync(_workspaceId, "designer@example.com", WorkspaceRole.Viewer);

        await invite.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Ownership_is_transferred_rather_than_invited()
    {
        await using var context = Db();

        var invite = async () => await Team(context, _owner)
            .InviteAsync(_workspaceId, "stranger@example.com", WorkspaceRole.Owner);

        await invite.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- the inbox -----------------------------------------------------------------------------

    [Fact]
    public async Task The_inbox_says_who_recorded_each_thing_and_whether_it_was_you()
    {
        await using var context = Db();

        await VersionAsync(context, recordedBy: _designer);
        await VersionAsync(context, recordedBy: _reviewer);

        var inbox = await new ApprovalInbox(
                context, Separation(context, _designer, WorkspaceRole.TestDesigner),
                new FixedUser(_workspaceId, _designer, WorkspaceRole.TestDesigner))
            .ReadAsync(_projectId);

        inbox.Versions.Should().HaveCount(2);
        inbox.Versions.Count(version => version.ByYou).Should().Be(1);
        inbox.SomebodyElseCanApprove.Should().BeTrue();

        inbox.Versions.Select(version => version.RecordedBy)
            .Should().Contain("Designer").And.Contain("Reviewer");
    }

    // ---- setup ---------------------------------------------------------------------------------

    private async Task<BaselineVersion> VersionAsync(ProofFlowDbContext context, Guid recordedBy)
    {
        var number = await context.BaselineVersions.CountAsync(v => v.BaselineId == _baselineId) + 1;

        var version = new BaselineVersion
        {
            WorkspaceId = _workspaceId,
            BaselineId = _baselineId,
            Number = number,
            Body = """{"ok":true}""",
            Status = BaselineStatus.PendingApproval,
            CreatedByUserId = recordedBy,
        };

        context.BaselineVersions.Add(version);
        await context.SaveChangesAsync();

        return version;
    }

    private Separation Separation(ProofFlowDbContext context, Guid userId, WorkspaceRole role) =>
        new(context, new FixedUser(_workspaceId, userId, role));

    private TeamService Team(ProofFlowDbContext context, Guid userId) =>
        new(context, new SystemClock(), new FixedUser(_workspaceId, userId, WorkspaceRole.Owner));

    private ProofFlowDbContext Db(Guid? workspaceId = null)
    {
        var options = new DbContextOptionsBuilder<SqliteProofFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new SqliteProofFlowDbContext(
            options, new FixedWorkspaceScope(workspaceId ?? _workspaceId));
    }

    private async Task SeedAsync(ProofFlowDbContext context)
    {
        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Team", Slug = "team" });

        foreach (var (id, name, email, role) in new[]
                 {
                     (_owner, "Owner", "owner@example.com", WorkspaceRole.Owner),
                     (_designer, "Designer", "designer@example.com", WorkspaceRole.TestDesigner),
                     (_reviewer, "Reviewer", "reviewer@example.com", WorkspaceRole.Reviewer),
                     (_viewer, "Viewer", "viewer@example.com", WorkspaceRole.Viewer),
                 })
        {
            context.Users.Add(new ProofFlowUser
            {
                Id = id,
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = name,
            });

            context.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = _workspaceId,
                UserId = id,
                Role = role,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        var project = new Project { WorkspaceId = _workspaceId, Name = "Catalog", Slug = "catalog" };
        context.Projects.Add(project);
        await context.SaveChangesAsync();

        _projectId = project.Id;

        var baseline = new Baseline
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = "product detail",
            CreatedByUserId = _designer,
        };

        context.Baselines.Add(baseline);
        await context.SaveChangesAsync();

        _baselineId = baseline.Id;
    }

    private sealed class FixedUser(Guid workspaceId, Guid userId, WorkspaceRole role) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? WorkspaceId => workspaceId;
        public string DisplayName => role.ToString();
        public WorkspaceRole? Role => role;
        public bool IsAuthenticated => true;
        public bool Can(Capability capability) => RoleCapabilities.Allows(role, capability);
    }
}
