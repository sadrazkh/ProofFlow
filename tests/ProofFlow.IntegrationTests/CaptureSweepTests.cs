using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Capture;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Workspaces;
using ProofFlow.FakeApi;
using ProofFlow.Infrastructure.Capture;
using ProofFlow.Infrastructure.Common;
using ProofFlow.Infrastructure.Data;
using ProofFlow.Infrastructure.Environments;

using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Security;
using ProofFlow.Infrastructure.Tenancy;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// A sweep across a data set, against a real server and a real database.
///
/// This is the part of section 5 that cannot be tested with a fake anything: two thousand rows is
/// two thousand real calls, the per-row variable scope has to be per-row under concurrency, and
/// "approve these forty" has to write forty baseline samples and not thirty-nine.
/// </summary>
public sealed class CaptureSweepTests : IAsyncLifetime
{
    private WebApplication _server = null!;
    private ServiceProvider _http = null!;
    private SqliteConnection _connection = null!;
    private string _baseUrl = null!;

    private readonly Guid _workspaceId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private Guid _projectId;
    private Guid _environmentId;
    private Guid _baselineId;
    private Guid _versionId;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddFakeApi();

        _server = builder.Build();
        _server.MapFakeApi();
        await _server.StartAsync();
        _baseUrl = _server.Urls.First().TrimEnd('/');

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProofFlowHttpClients();
        _http = services.BuildServiceProvider();

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        await using var context = Db();
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        await _http.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_sweep_sends_one_request_per_row_and_keeps_every_response()
    {
        await using var context = Db();
        var session = await Capture(context).RunAsync(Command());

        session.Status.Should().Be(CaptureSessionStatus.Completed);
        session.TotalRows.Should().Be(6);
        session.Completed.Should().Be(6);
        session.Failed.Should().Be(0);

        var samples = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == session.Id)
            .OrderBy(s => s.Ordinal)
            .ToListAsync();

        samples.Should().HaveCount(6);

        // The failure message is in the assertion on purpose: a sweep that returns nothing useful
        // is the commonest way this breaks, and "expected 200, found 0" does not say why.
        samples.Should().OnlyContain(s => s.FailureMessage == null,
            "no row should have failed, but: {0}",
            string.Join(" | ", samples.Where(s => s.FailureMessage != null)
                .Select(s => $"{s.Key}: {s.FailureMessage}")));

        samples.Should().OnlyContain(s => s.Status == SampleStatus.Captured);
        samples.Should().OnlyContain(s => s.Body != null && s.StatusCode == 200);
    }

    [Fact]
    public async Task Each_row_gets_its_own_dataset_values_under_concurrency()
    {
        // The whole point of a data set. Four requests are in flight at once and each one must
        // carry its own row: a scope shared across them would let rows read each other's values,
        // and the resulting samples would all be answers to whichever row won the race.
        await using var context = Db();
        var session = await Capture(context).RunAsync(Command());

        var samples = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == session.Id)
            .OrderBy(s => s.Ordinal)
            .ToListAsync();

        foreach (var sample in samples)
        {
            sample.ResolvedUrl.Should().EndWith($"/fake/records/{sample.Key}");

            var body = JsonDocument.Parse(sample.Body!);
            body.RootElement.GetProperty("id").GetInt32().Should().Be(int.Parse(sample.Key));
        }
    }

    [Fact]
    public async Task Approving_writes_one_baseline_answer_per_input()
    {
        await using var context = Db();
        var capture = Capture(context);
        var session = await capture.RunAsync(Command());

        var ids = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == session.Id)
            .Select(s => s.Id)
            .ToListAsync();

        var reviewed = await capture.ReviewAsync(session.Id,
            new ReviewSamplesCommand { SampleIds = ids, Status = "Approved" });

        reviewed.Should().Be(6);

        var approved = await context.BaselineSamples
            .Where(s => s.BaselineId == _baselineId)
            .ToListAsync();

        approved.Should().HaveCount(6);
        approved.Should().OnlyContain(s => s.NormalizedHash != null);
        approved.Select(s => s.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_regression_over_inputs_nobody_has_approved_is_not_a_pass()
    {
        await using var context = Db();

        // The first test of a new set of inputs. Nothing has been approved, so every row was
        // compared against nothing — and «compared against nothing» is not «matched».
        var session = await Capture(context).RunAsync(Command("Regression"));

        session.Completed.Should().Be(6);
        session.Differing.Should().Be(0);
        session.Failed.Should().Be(0);

        // This is the whole assertion. It was zero, and the page above it derives «passed» as
        // completed minus the other three — so six unchecked rows rendered as «all 6 passed»,
        // which is a green result for a test that verified nothing.
        session.Unmatched.Should().Be(6);
    }

    [Fact]
    public async Task A_second_sweep_against_approved_answers_finds_nothing()
    {
        await using var context = Db();
        var capture = Capture(context);

        var first = await capture.RunAsync(Command());
        var ids = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == first.Id).Select(s => s.Id).ToListAsync();
        await capture.ReviewAsync(first.Id, new ReviewSamplesCommand { SampleIds = ids, Status = "Approved" });

        var second = await capture.RunAsync(Command("Regression"));

        // /fake/records/{id} is stable, so a regression run over the same rows has to be silent.
        // If this ever reports a difference, the comparison is finding noise.
        second.Differing.Should().Be(0);
        second.Failed.Should().Be(0);
    }

    [Fact]
    public async Task A_changed_response_is_reported_as_differing()
    {
        await using var context = Db();
        var capture = Capture(context);

        var first = await capture.RunAsync(Command());
        var ids = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == first.Id).Select(s => s.Id).ToListAsync();
        await capture.ReviewAsync(first.Id, new ReviewSamplesCommand { SampleIds = ids, Status = "Approved" });

        // One approved answer edited underneath, which is what a real regression looks like from
        // the comparison's side.
        var tampered = await context.BaselineSamples.FirstAsync(s => s.Key == "3");

        // Edited as a document rather than by string replacement: the response is compact JSON and
        // a search for "\"id\": 3" quietly matches nothing, which makes the test pass by comparing
        // an unchanged body against itself.
        var document = System.Text.Json.Nodes.JsonNode.Parse(tampered.Body)!;
        document["name"] = "Something else entirely";
        tampered.Body = document.ToJsonString();
        tampered.NormalizedHash = "not-the-same";
        await context.SaveChangesAsync();

        var second = await capture.RunAsync(Command("Regression"));

        second.Differing.Should().Be(1);

        var differing = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == second.Id && s.Differs)
            .SingleAsync();

        differing.Key.Should().Be("3");
        differing.DiffSummaryJson.Should().NotBeNull();
    }

    [Fact]
    public async Task A_row_that_fails_does_not_fail_the_sweep()
    {
        await using var context = Db();

        // One row is not a number at all, which the endpoint answers with a 400 and an error body.
        // That is a legitimate response and not a transport failure — the sweep has to finish, keep
        // it, and let the reviewer see that this row's answer is an error.
        var version = await AddVersionAsync(context, ["1", "2", "no-such-id"]);

        var session = await Capture(context).RunAsync(
            new StartCaptureCommand { BaselineId = _baselineId, DataSetVersionId = version });

        session.Status.Should().Be(CaptureSessionStatus.Completed);
        session.Completed.Should().Be(3);

        var samples = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == session.Id).ToListAsync();

        samples.Should().HaveCount(3);
        samples.Count(s => s.StatusCode == 200).Should().Be(2);
    }

    [Fact]
    public async Task A_limit_stops_the_sweep_early()
    {
        // The first sweep of a two-thousand-row set is usually a mistake somebody wants to find
        // after ten rows, not after twenty minutes of real calls to a real API.
        await using var context = Db();
        var session = await Capture(context).RunAsync(Command(limit: 2));

        session.TotalRows.Should().Be(2);
        session.Completed.Should().Be(2);
        (await context.CaptureSamples.CountAsync(s => s.CaptureSessionId == session.Id))
            .Should().Be(2);
    }

    [Fact]
    public async Task A_failed_sample_cannot_be_approved_into_the_baseline()
    {
        await using var context = Db();
        var capture = Capture(context);

        var version = await AddVersionAsync(context, ["1", "no-such-id"]);
        var session = await capture.RunAsync(
            new StartCaptureCommand { BaselineId = _baselineId, DataSetVersionId = version });

        var samples = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == session.Id).ToListAsync();

        // Force the failed state the way a timeout would, then try to bless it. A null body written
        // into the baseline would make every later comparison for that key meaningless.
        var broken = samples.Single(s => s.Key == "no-such-id");
        broken.Status = SampleStatus.Failed;
        broken.Body = null;
        await context.SaveChangesAsync();

        await capture.ReviewAsync(session.Id, new ReviewSamplesCommand
        {
            SampleIds = [.. samples.Select(s => s.Id)],
            Status = "Approved",
        });

        var approved = await context.BaselineSamples.Where(s => s.BaselineId == _baselineId).ToListAsync();

        approved.Should().ContainSingle().Which.Key.Should().Be("1");
        (await context.CaptureSamples.FirstAsync(s => s.Id == broken.Id))
            .Status.Should().Be(SampleStatus.Failed);
    }

    [Fact]
    public async Task Approving_the_same_key_twice_replaces_rather_than_duplicates()
    {
        await using var context = Db();
        var capture = Capture(context);

        var first = await capture.RunAsync(Command(limit: 2));
        var firstIds = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == first.Id).Select(s => s.Id).ToListAsync();
        await capture.ReviewAsync(first.Id, new ReviewSamplesCommand { SampleIds = firstIds, Status = "Approved" });

        var second = await capture.RunAsync(Command(limit: 2));
        var secondIds = await context.CaptureSamples
            .Where(s => s.CaptureSessionId == second.Id).Select(s => s.Id).ToListAsync();
        await capture.ReviewAsync(second.Id, new ReviewSamplesCommand { SampleIds = secondIds, Status = "Approved" });

        // One approved answer per input, or a regression run would have to choose between two.
        (await context.BaselineSamples.CountAsync(s => s.BaselineId == _baselineId)).Should().Be(2);
    }

    // ---- setup ---------------------------------------------------------------------------------

    private StartCaptureCommand Command(string mode = "Capture", int? limit = null) => new()
    {
        BaselineId = _baselineId,
        DataSetVersionId = _versionId,
        EnvironmentId = _environmentId,
        Mode = mode,
        Limit = limit,
    };

    private CaptureService Capture(ProofFlowDbContext context) => new(
        context,
        new EnvironmentContextBuilder(
            context,
            new AesGcmSecretCipher(Configuration(), NullLogger<AesGcmSecretCipher>.Instance),
            NullLogger<EnvironmentContextBuilder>.Instance),
        new GuardedHttpExecutor(_http.GetRequiredService<IHttpClientFactory>(),
            NullLogger<GuardedHttpExecutor>.Instance),
        new FixedUser(_workspaceId, _userId),
        new SystemClock());

    private static Microsoft.Extensions.Configuration.IConfiguration Configuration() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProofFlow:MasterKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

    private ProofFlowDbContext Db()
    {
        var options = new DbContextOptionsBuilder<SqliteProofFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new SqliteProofFlowDbContext(options, new FixedWorkspaceScope(_workspaceId));
    }

    private async Task SeedAsync(ProofFlowDbContext context)
    {
        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Sweep", Slug = "sweep" });

        var project = new Project { WorkspaceId = _workspaceId, Name = "Catalog", Slug = "catalog" };
        context.Projects.Add(project);

        var environment = new ProjectEnvironment
        {
            WorkspaceId = _workspaceId,
            ProjectId = project.Id,
            Name = "Local",
            Slug = "local",
            BaseUrl = _baseUrl,
            // The fake API listens on loopback, which the guard refuses unless it is told not to.
            AllowPrivateNetwork = true,
        };
        context.Environments.Add(environment);

        var baseline = new Baseline
        {
            WorkspaceId = _workspaceId,
            ProjectId = project.Id,
            EnvironmentId = environment.Id,
            Name = "product detail",
            CreatedByUserId = _userId,
            RequestJson = JsonSerializer.Serialize(new
            {
                method = "GET",
                url = "{{environment.baseUrl}}/fake/records/{{dataset.current.id}}",
            }),
        };
        context.Baselines.Add(baseline);

        await context.SaveChangesAsync();

        _projectId = project.Id;
        _environmentId = environment.Id;
        _baselineId = baseline.Id;
        _versionId = await AddVersionAsync(context, ["1", "2", "3", "4", "5", "6"]);
    }

    private async Task<Guid> AddVersionAsync(ProofFlowDbContext context, string[] ids)
    {
        var set = await context.DataSets.FirstOrDefaultAsync(d => d.ProjectId == _projectId);

        if (set is null)
        {
            set = new DataSet
            {
                WorkspaceId = _workspaceId,
                ProjectId = _projectId,
                Name = "products",
                KeyColumn = "id",
                CreatedByUserId = _userId,
            };
            context.DataSets.Add(set);
        }

        var number = await context.DataSetVersions.CountAsync(v => v.DataSetId == set.Id) + 1;

        var version = new DataSetVersion
        {
            WorkspaceId = _workspaceId,
            DataSetId = set.Id,
            Number = number,
            ColumnsJson = """["id"]""",
            RowCount = ids.Length,
            CreatedByUserId = _userId,
        };
        context.DataSetVersions.Add(version);

        for (var index = 0; index < ids.Length; index++)
        {
            context.DataSetRows.Add(new DataSetRow
            {
                WorkspaceId = _workspaceId,
                DataSetVersionId = version.Id,
                Ordinal = index,
                Key = ids[index],
                ValuesJson = JsonSerializer.Serialize(new { id = ids[index] }),
            });
        }

        await context.SaveChangesAsync();
        return version.Id;
    }

    private sealed class FixedUser(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? WorkspaceId => workspaceId;
        public string DisplayName => "Sweeper";
        public string? Email => "sweeper@example.test";
        public WorkspaceRole? Role => WorkspaceRole.Owner;
        public bool IsAuthenticated => true;
        public bool Can(Capability capability) => true;
    }
}
