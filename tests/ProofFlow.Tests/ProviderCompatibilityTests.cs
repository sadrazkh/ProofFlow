using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Auditing;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.Tests;

/// <summary>
/// Queries the application actually issues, run against SQLite.
///
/// Two providers is a real cost and this is where it is paid. SQLite refuses to ORDER BY a
/// <c>DateTimeOffset</c> outright — and every list in this application is ordered by a timestamp,
/// so without the conversion in <see cref="SqliteProofFlowDbContext"/> the development and test
/// provider fails on the sign-in page, the dashboard, the project list and the activity log.
///
/// It failed exactly that way once. These are the queries that caught it.
/// </summary>
public sealed class ProviderCompatibilityTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public ProviderCompatibilityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var context = Context();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Ordering_by_an_instant_translates()
    {
        await SeedAsync();
        await using var context = Context();

        var ordered = await context.Projects
            .OrderByDescending(p => p.UpdatedAt)
            .ThenBy(p => p.CreatedAt)
            .Select(p => p.Name)
            .ToListAsync();

        ordered.Should().HaveCount(3);
    }

    [Fact]
    public async Task Ordering_by_a_nullable_instant_translates()
    {
        await SeedAsync();
        await using var context = Context();

        var ordered = await context.Projects
            .OrderBy(p => p.ArchivedAt)
            .Select(p => p.Name)
            .ToListAsync();

        ordered.Should().HaveCount(3);
    }

    [Fact]
    public async Task Instants_round_trip_and_keep_their_order()
    {
        var workspaceId = await SeedAsync();
        var now = DateTimeOffset.UtcNow;

        await using (var context = Context())
        {
            // Deliberately out of order on insert, and deliberately including a sub-millisecond
            // gap — the failure a variable-width fraction would cause is exactly this pair
            // swapping places.
            context.AuditEvents.AddRange(
                new AuditEvent { WorkspaceId = workspaceId, Action = "b", OccurredAt = now.AddTicks(5) },
                new AuditEvent { WorkspaceId = workspaceId, Action = "c", OccurredAt = now.AddDays(1) },
                new AuditEvent { WorkspaceId = workspaceId, Action = "a", OccurredAt = now });
            await context.SaveChangesAsync();
        }

        await using var check = Context();
        var actions = await check.AuditEvents
            .OrderBy(a => a.OccurredAt)
            .Select(a => a.Action)
            .ToListAsync();

        actions.Should().Equal("a", "b", "c");

        var first = await check.AuditEvents.OrderBy(a => a.OccurredAt).FirstAsync();
        first.OccurredAt.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));
        first.OccurredAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Filtering_on_an_instant_range_translates()
    {
        var workspaceId = await SeedAsync();
        var now = DateTimeOffset.UtcNow;

        await using (var context = Context())
        {
            context.AuditEvents.AddRange(
                new AuditEvent { WorkspaceId = workspaceId, Action = "old", OccurredAt = now.AddDays(-10) },
                new AuditEvent { WorkspaceId = workspaceId, Action = "new", OccurredAt = now });
            await context.SaveChangesAsync();
        }

        await using var check = Context();
        var recent = await check.AuditEvents
            .Where(a => a.OccurredAt > now.AddDays(-1))
            .Select(a => a.Action)
            .ToListAsync();

        // A string comparison over a fixed-width UTC format has to answer this the same way an
        // instant comparison would. If it did not, retention sweeps would delete the wrong rows.
        recent.Should().Equal("new");
    }

    [Fact]
    public async Task Paging_the_activity_log_translates()
    {
        var workspaceId = await SeedAsync();

        await using (var context = Context())
        {
            for (var i = 0; i < 30; i++)
            {
                context.AuditEvents.Add(new AuditEvent
                {
                    WorkspaceId = workspaceId,
                    Action = $"event.{i}",
                    OccurredAt = DateTimeOffset.UtcNow.AddMinutes(i),
                });
            }
            await context.SaveChangesAsync();
        }

        await using var check = Context();
        var page = await check.AuditEvents
            .OrderByDescending(a => a.OccurredAt)
            .Skip(10)
            .Take(10)
            .ToListAsync();

        page.Should().HaveCount(10);
    }

    private async Task<Guid> SeedAsync()
    {
        var workspaceId = Guid.CreateVersion7();

        await using var context = Context();
        context.Workspaces.Add(new Workspace { Id = workspaceId, Name = "W", Slug = "w" });
        context.Projects.AddRange(
            new Project { WorkspaceId = workspaceId, Name = "One", Slug = "one" },
            new Project { WorkspaceId = workspaceId, Name = "Two", Slug = "two" },
            new Project
            {
                WorkspaceId = workspaceId, Name = "Three", Slug = "three",
                ArchivedAt = DateTimeOffset.UtcNow,
            });
        await context.SaveChangesAsync();

        return workspaceId;
    }

    private SqliteProofFlowDbContext Context() =>
        new(new DbContextOptionsBuilder<SqliteProofFlowDbContext>().UseSqlite(_connection).Options,
            new SystemWorkspaceScope());

    public void Dispose() => _connection.Dispose();
}
