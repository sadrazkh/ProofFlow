using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Common;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.Tests;

/// <summary>
/// The tenant boundary, exercised against a real SQLite database rather than the in-memory
/// provider — global query filters are translated to SQL, and the in-memory provider does not
/// translate anything, so it would pass whether or not the filter works.
/// </summary>
public sealed class WorkspaceIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public WorkspaceIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var context = Context(new SystemWorkspaceScope());
        context.Database.EnsureCreated();
    }

    /// <summary>
    /// Every workspace-owned entity is actually filtered.
    ///
    /// The filters are applied by reflecting over the model, which is the right mechanism and also
    /// a silent one: an entity that implements the interface but is never added as a
    /// <c>DbSet</c> — or one added under a base type — simply is not there, and nothing says so.
    /// The consequence is a table readable across tenants, discovered by a customer.
    /// </summary>
    [Fact]
    public void Every_workspace_owned_entity_is_behind_a_filter()
    {
        using var context = Context(new SystemWorkspaceScope());

        var owned = typeof(Project).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                        && typeof(IWorkspaceOwned).IsAssignableFrom(t))
            .ToArray();

        owned.Should().NotBeEmpty();

        var unfiltered = owned
            .Where(type => context.Model.FindEntityType(type)?.GetDeclaredQueryFilters().Any() != true)
            .Select(type => type.Name)
            .OrderBy(name => name)
            .ToArray();

        unfiltered.Should().BeEmpty(
            "these entities hold customer data and are not behind the tenant filter: {0}",
            string.Join(", ", unfiltered));
    }

    [Fact]
    public async Task A_workspace_sees_only_its_own_rows()
    {
        var (first, second) = await SeedTwoWorkspacesAsync();

        await using var scoped = Context(new FixedWorkspaceScope(first));
        var names = await scoped.Projects.Select(p => p.Name).ToListAsync();

        names.Should().ContainSingle().Which.Should().Be("First");
        _ = second;
    }

    [Fact]
    public async Task The_system_scope_spans_workspaces()
    {
        await SeedTwoWorkspacesAsync();

        // This is the assertion that protects background work. A scheduler running under a request
        // scope reads nothing, does nothing, and finishes successfully — the most expensive kind
        // of failure, because every signal says it worked.
        await using var system = Context(new SystemWorkspaceScope());
        var names = await system.Projects.Select(p => p.Name).ToListAsync();

        names.Should().BeEquivalentTo(["First", "Second"]);
    }

    [Fact]
    public async Task No_workspace_and_no_system_flag_yields_nothing()
    {
        await SeedTwoWorkspacesAsync();

        await using var unscoped = Context(new NoScope());
        var projects = await unscoped.Projects.ToListAsync();

        // Not an exception — an empty result. Stated as a test so the behaviour is a decision
        // rather than a surprise, and so `.Value` never creeps back into the filter expression.
        projects.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_row_takes_the_workspace_from_the_scope()
    {
        var workspaceId = Guid.CreateVersion7();

        await using (var system = Context(new SystemWorkspaceScope()))
        {
            system.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Only", Slug = "only" });
            await system.SaveChangesAsync();
        }

        await using (var scoped = Context(new FixedWorkspaceScope(workspaceId)))
        {
            // WorkspaceId deliberately not set by the caller.
            scoped.Projects.Add(new Project { Name = "Inferred", Slug = "inferred" });
            await scoped.SaveChangesAsync();
        }

        await using var check = Context(new SystemWorkspaceScope());
        var project = await check.Projects.SingleAsync(p => p.Slug == "inferred");
        project.WorkspaceId.Should().Be(workspaceId);
    }

    [Fact]
    public async Task A_row_cannot_be_moved_to_another_workspace()
    {
        var (first, second) = await SeedTwoWorkspacesAsync();

        await using var scoped = Context(new FixedWorkspaceScope(first));
        var project = await scoped.Projects.SingleAsync();
        project.WorkspaceId = second;

        var save = async () => await scoped.SaveChangesAsync();

        await save.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*do not move between tenants*");
    }

    [Fact]
    public async Task Timestamps_are_stamped_in_UTC()
    {
        var workspaceId = Guid.CreateVersion7();

        await using var system = Context(new SystemWorkspaceScope());
        system.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Clock", Slug = "clock" });
        await system.SaveChangesAsync();

        var stored = await system.Workspaces.SingleAsync(w => w.Id == workspaceId);

        // Npgsql maps DateTimeOffset to timestamptz and rejects a non-zero offset outright, so a
        // machine in any time zone but UTC would throw on insert while SQLite happily accepted it.
        stored.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        stored.UpdatedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    private async Task<(Guid First, Guid Second)> SeedTwoWorkspacesAsync()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await using var system = Context(new SystemWorkspaceScope());

        system.Workspaces.AddRange(
            new Workspace { Id = first, Name = "First workspace", Slug = "first" },
            new Workspace { Id = second, Name = "Second workspace", Slug = "second" });

        system.Projects.AddRange(
            new Project { WorkspaceId = first, Name = "First", Slug = "first" },
            new Project { WorkspaceId = second, Name = "Second", Slug = "second" });

        await system.SaveChangesAsync();
        return (first, second);
    }

    private SqliteProofFlowDbContext Context(IWorkspaceScope scope)
    {
        var options = new DbContextOptionsBuilder<SqliteProofFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new SqliteProofFlowDbContext(options, scope);
    }

    private sealed class NoScope : IWorkspaceScope
    {
        public Guid? WorkspaceId => null;
        public bool IsSystem => false;
    }

    public void Dispose() => _connection.Dispose();
}
