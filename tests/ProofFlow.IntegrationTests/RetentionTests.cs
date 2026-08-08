using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Runs;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// What an old run keeps and what it loses.
///
/// The whole design is in the split. A run's verdict, its timings and its assertion results are
/// small, are what a trend is made of, and are kept for ever. What it saw — bodies, log lines,
/// artefacts — is large, is the thing most likely to hold somebody's personal data, and goes.
///
/// A test that only checked the deletions would pass just as well if retention deleted the runs
/// themselves, so every one of these says both halves.
/// </summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;

    private readonly Guid _workspaceId = Guid.CreateVersion7();
    private readonly Frozen _clock = new(DateTimeOffset.Parse("2026-08-08T12:00:00Z"));

    private Guid _projectId;
    private Guid _scenarioId;
    private Guid _oldRunId;
    private Guid _recentRunId;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        await using var context = Db();
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task An_old_run_keeps_its_verdict_and_loses_its_payload()
    {
        await using var context = Db();

        var cleared = await Service(context).SweepAsync(await ProjectAsync(context));

        cleared.Should().Be(1);

        var run = await context.Runs.FirstAsync(candidate => candidate.Id == _oldRunId);

        // Everything anybody builds a trend from.
        run.Status.Should().Be(RunStatus.Failed);
        run.DurationMs.Should().Be(1234);
        run.StepsRun.Should().Be(3);
        run.AssertionsFailed.Should().Be(1);
        run.Outcome.Should().Be("One check did not hold.");

        // And the bulk is gone.
        run.PayloadsCleared.Should().BeTrue();

        (await context.RunEvents.CountAsync(entry => entry.TestRunId == _oldRunId)).Should().Be(0);
        (await context.RunArtifacts.CountAsync(a => a.TestRunId == _oldRunId)).Should().Be(0);

        (await context.NodeRuns.Where(node => node.TestRunId == _oldRunId).ToListAsync())
            .Should().OnlyContain(node => node.OutputJson == null);
    }

    [Fact]
    public async Task The_steps_and_their_results_are_still_there_to_read()
    {
        // A cleared run is not an empty run. Which steps ran, how long each took and which
        // assertions failed is the part somebody investigating last quarter actually needs.
        await using var context = Db();

        await Service(context).SweepAsync(await ProjectAsync(context));

        var nodes = await context.NodeRuns
            .Where(node => node.TestRunId == _oldRunId)
            .OrderBy(node => node.SortOrder)
            .ToListAsync();

        nodes.Should().HaveCount(2);
        nodes[0].NodeName.Should().Be("call");
        nodes[0].DurationMs.Should().Be(42);
        nodes[1].FailureMessage.Should().Be("Expected 200, got 500.");

        // Scoped to this run: the other one has an assertion too, and a bare count would pass
        // whether or not the right one survived.
        var checkId = nodes[1].Id;

        (await context.AssertionResults.CountAsync(result => result.NodeRunId == checkId))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_recent_run_is_left_alone()
    {
        await using var context = Db();

        await Service(context).SweepAsync(await ProjectAsync(context));

        (await context.RunEvents.CountAsync(entry => entry.TestRunId == _recentRunId))
            .Should().Be(2);

        (await context.Runs.FirstAsync(run => run.Id == _recentRunId))
            .PayloadsCleared.Should().BeFalse();
    }

    [Fact]
    public async Task Sweeping_twice_does_nothing_the_second_time()
    {
        // The flag is what stops the sweep walking the same rows every hour for ever.
        await using var context = Db();

        var project = await ProjectAsync(context);

        (await Service(context).SweepAsync(project)).Should().Be(1);
        (await Service(context).SweepAsync(project)).Should().Be(0);
    }

    [Fact]
    public async Task Zero_days_means_keep_everything()
    {
        await using var context = Db();

        var project = await ProjectAsync(context);
        project.RetentionDays = 0;
        await context.SaveChangesAsync();

        (await Service(context).SweepAsync(project)).Should().Be(0);
        (await context.RunEvents.CountAsync(entry => entry.TestRunId == _oldRunId)).Should().Be(3);
    }

    [Fact]
    public async Task A_run_that_never_finished_is_not_swept()
    {
        // A run still going has no age worth measuring, and clearing the log out from under a
        // console somebody is watching would be a strange thing to do.
        await using var context = Db();

        var running = new TestRun
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            ScenarioId = _scenarioId,
            Status = RunStatus.Running,
            Trigger = RunTrigger.Person,
            StartedAt = _clock.UtcNow.AddDays(-90),
        };

        context.Runs.Add(running);
        await context.SaveChangesAsync();

        context.RunEvents.Add(new RunEvent
        {
            WorkspaceId = _workspaceId,
            TestRunId = running.Id,
            Sequence = 1,
            Message = "still going",
            At = _clock.UtcNow.AddDays(-90),
        });

        await context.SaveChangesAsync();

        await Service(context).SweepAsync(await ProjectAsync(context));

        (await context.RunEvents.CountAsync(entry => entry.TestRunId == running.Id)).Should().Be(1);
    }

    // ---- setup ---------------------------------------------------------------------------------

    private async Task<Project> ProjectAsync(ProofFlowDbContext context) =>
        await context.Projects.FirstAsync(project => project.Id == _projectId);

    private RetentionService Service(ProofFlowDbContext context) =>
        new(context, _clock, NullLogger<RetentionService>.Instance);

    private async Task SeedAsync(ProofFlowDbContext context)
    {
        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "W", Slug = "w" });

        var project = new Project
        {
            WorkspaceId = _workspaceId,
            Name = "Catalog",
            Slug = "catalog",
            RetentionDays = 30,
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync();

        _projectId = project.Id;

        // A run points at the scenario it ran, and that is a real foreign key: the record of what
        // happened is not allowed to name something that never existed.
        var scenario = new TestScenario
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = "Read one product",
            CreatedByUserId = Guid.CreateVersion7(),
        };

        context.Scenarios.Add(scenario);
        await context.SaveChangesAsync();

        _scenarioId = scenario.Id;

        _oldRunId = await RunAsync(context, finished: _clock.UtcNow.AddDays(-40), events: 3, withBodies: true);
        _recentRunId = await RunAsync(context, finished: _clock.UtcNow.AddDays(-2), events: 2, withBodies: true);
    }

    private async Task<Guid> RunAsync(
        ProofFlowDbContext context, DateTimeOffset finished, int events, bool withBodies)
    {
        var run = new TestRun
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            ScenarioId = _scenarioId,
            Status = RunStatus.Failed,
            Trigger = RunTrigger.Person,
            StartedAt = finished.AddSeconds(-2),
            FinishedAt = finished,
            DurationMs = 1234,
            StepsRun = 3,
            StepsFailed = 1,
            AssertionsPassed = 2,
            AssertionsFailed = 1,
            Outcome = "One check did not hold.",
        };

        context.Runs.Add(run);
        await context.SaveChangesAsync();

        var call = new NodeRun
        {
            WorkspaceId = _workspaceId,
            TestRunId = run.Id,
            NodeId = "n2",
            NodeKey = "http.request",
            NodeName = "call",
            Status = NodeRunStatus.Passed,
            StartedAt = finished.AddSeconds(-2),
            FinishedAt = finished.AddSeconds(-1),
            DurationMs = 42,
            OutputJson = withBodies ? """{"body":"a customer's name and address"}""" : null,
            SortOrder = 0,
        };

        var check = new NodeRun
        {
            WorkspaceId = _workspaceId,
            TestRunId = run.Id,
            NodeId = "n3",
            NodeKey = "assert.status",
            NodeName = "status",
            Status = NodeRunStatus.Failed,
            StartedAt = finished.AddSeconds(-1),
            FinishedAt = finished,
            DurationMs = 1,
            FailureMessage = "Expected 200, got 500.",
            SortOrder = 1,
        };

        context.NodeRuns.AddRange(call, check);
        await context.SaveChangesAsync();

        context.AssertionResults.Add(new AssertionResult
        {
            WorkspaceId = _workspaceId,
            NodeRunId = check.Id,
            Description = "status is 200",
            Passed = false,
        });

        for (var at = 1; at <= events; at++)
        {
            context.RunEvents.Add(new RunEvent
            {
                WorkspaceId = _workspaceId,
                TestRunId = run.Id,
                Sequence = at,
                Message = $"line {at}",
                At = finished,
            });
        }

        context.RunArtifacts.Add(new RunArtifact
        {
            WorkspaceId = _workspaceId,
            TestRunId = run.Id,
            Name = "response.json",
            Kind = "response",
            Content = "a large body nobody needs in six months",
            SizeBytes = 38,
        });

        await context.SaveChangesAsync();

        return run.Id;
    }

    private ProofFlowDbContext Db()
    {
        var options = new DbContextOptionsBuilder<SqliteProofFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new SqliteProofFlowDbContext(options, new FixedWorkspaceScope(_workspaceId));
    }

    /// <summary>A clock that does not move, so "forty days ago" means the same thing every run.</summary>
    private sealed class Frozen(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
