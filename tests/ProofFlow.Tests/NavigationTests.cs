using FluentAssertions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Web.Infrastructure;

namespace ProofFlow.Tests;

public class NavigationTests
{
    [Theory]
    [InlineData("/projects", "/projects", true)]
    [InlineData("/projects", "/projects/abc", true)]
    [InlineData("/projects", "/projects-archive", false)]   // the boundary check earns its keep
    [InlineData("/projects", "/", false)]
    [InlineData("/", "/", true)]
    [InlineData("/", "/projects", false)]
    [InlineData("/activity", "/activity?page=2", false)]     // callers pass Path, not PathAndQuery
    public void Active_only_matches_on_a_segment_boundary(string item, string current, bool expected) =>
        Navigation.IsActive(item, current).Should().Be(expected);

    [Fact]
    public void A_viewer_is_not_offered_project_settings()
    {
        var sections = Navigation.For(new FakeUser(WorkspaceRole.Viewer), Guid.CreateVersion7());
        var paths = sections.SelectMany(s => s.Items).Select(i => i.Path).ToArray();

        paths.Should().NotContain(path => path.EndsWith("/settings", StringComparison.Ordinal));
    }

    [Fact]
    public void A_viewer_is_not_offered_the_activity_log()
    {
        var sections = Navigation.For(new FakeUser(WorkspaceRole.Viewer), null);
        sections.SelectMany(s => s.Items).Select(i => i.Path).Should().NotContain("/activity");
    }

    [Fact]
    public void Project_sections_only_appear_inside_a_project()
    {
        var outside = Navigation.For(new FakeUser(WorkspaceRole.Owner), null)
            .SelectMany(s => s.Items).Select(i => i.Path).ToArray();

        // Eleven dead links on the dashboard is worse than a shorter sidebar.
        outside.Should().NotContain(path => path.StartsWith("/projects/", StringComparison.Ordinal));

        var inside = Navigation.For(new FakeUser(WorkspaceRole.Owner), Guid.CreateVersion7())
            .SelectMany(s => s.Items).Select(i => i.Path).ToArray();

        inside.Should().Contain(path => path.EndsWith("/environments", StringComparison.Ordinal));
    }

    [Fact]
    public void A_section_left_with_no_items_is_dropped()
    {
        var sections = Navigation.For(new FakeUser(WorkspaceRole.Viewer), Guid.CreateVersion7());

        sections.Should().OnlyContain(section => section.Items.Count > 0);
    }

    private sealed class FakeUser(WorkspaceRole role) : ICurrentUser
    {
        public Guid? UserId => Guid.Empty;
        public string DisplayName => "test";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId => Guid.Empty;
        public WorkspaceRole? Role => role;
        public bool Can(Capability capability) => RoleCapabilities.Allows(role, capability);
    }
}
