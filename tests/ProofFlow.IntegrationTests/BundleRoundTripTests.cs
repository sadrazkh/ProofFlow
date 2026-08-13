using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Portability;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Domain.Scheduling;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Common;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Security;
using ProofFlow.Infrastructure.Portability;
using ProofFlow.Infrastructure.Portability.Importers;
using ProofFlow.Infrastructure.Scenarios;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// A project written to a file and read back is the same project.
///
/// This is the test the whole format exists for, and the one that would catch almost any mistake in
/// it: export, import into an empty project, export again, and compare. A field the exporter forgets
/// is a field missing from the second document. A field the importer drops is the same. A property
/// that comes back in a different order makes the two files differ, which is the point — a format
/// that cannot be diffed is a format nobody will keep in a repository.
/// </summary>
public sealed class BundleRoundTripTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;

    private readonly Guid _workspaceId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    private Guid _projectId;

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
    public async Task Everything_that_went_out_comes_back()
    {
        await using var context = Db();

        var first = await Exporter(context).ExportAsync(_projectId);

        var imported = await Importer(context).ApplyAsync(first, projectId: null);
        var second = await Exporter(context).ExportAsync(imported.ProjectId);

        // Four things are allowed to differ, and every one of them is a rule this format has
        // rather than a gap in it.
        //
        // The timestamp, because it is about the export rather than the project. The project's own
        // name and slug, because this import landed beside its original and a collision is numbered
        // rather than made into a second project that looks identical in every list. Whether a
        // schedule is switched on, because an imported one never is. And the secret names, because
        // the import creates no secrets — it only tells somebody which ones to make.
        //
        // The last two have tests of their own below. Everything else has to match exactly.
        Normalise(second).Should().Be(Normalise(first));

        static string Normalise(Bundle bundle) => BundleJson.Write(bundle with
        {
            ExportedAt = null,
            Project = bundle.Project with { Name = "same", Slug = "same" },
            Schedules = [.. bundle.Schedules.Select(schedule => schedule with { Enabled = false })],
            SecretsToSupply = [],
        });
    }

    [Fact]
    public async Task An_export_of_an_unchanged_project_is_the_same_file_twice()
    {
        await using var context = Db();
        var exporter = Exporter(context);

        var first = await exporter.ExportAsync(_projectId);
        var second = await exporter.ExportAsync(_projectId);

        BundleJson.Write(second with { ExportedAt = null })
            .Should().Be(BundleJson.Write(first with { ExportedAt = null }));
    }

    [Fact]
    public async Task No_secret_leaves_the_building()
    {
        await using var context = Db();

        var json = BundleJson.Write(await Exporter(context).ExportAsync(_projectId));

        // Not the value, not the ciphertext, not the nonce, not the tag. The name is here, and
        // deliberately: the far side has to know what to create.
        json.Should().NotContain("cipher-text-that-should-never-travel");
        json.Should().NotContain("nonce-value");
        json.Should().NotContain("tag-value");
        json.Should().Contain("apiToken");
        json.Should().Contain("secretsToSupply");
    }

    [Fact]
    public async Task The_graph_carries_no_database_identifiers()
    {
        await using var context = Db();

        var bundle = await Exporter(context).ExportAsync(_projectId);
        var graph = bundle.Scenarios.Should().ContainSingle().Subject.Graph;

        graph.Nodes.Select(node => node.Id).Should().BeEquivalentTo(["n1", "n2", "n3"]);
        graph.Edges.Select(edge => edge.Id).Should().OnlyContain(id => id.StartsWith('e'));

        // And nothing anywhere in the file looks like one.
        BundleJson.Write(bundle).Should().NotMatchRegex(
            "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");
    }

    [Fact]
    public async Task An_imported_scenario_is_a_scenario_that_runs()
    {
        await using var context = Db();

        var bundle = await Exporter(context).ExportAsync(_projectId);
        var imported = await Importer(context).ApplyAsync(bundle, projectId: null);

        var scenario = await context.Scenarios
            .FirstAsync(candidate => candidate.ProjectId == imported.ProjectId);

        // Saved through the same service the canvas uses, so it has a numbered version and a
        // validator verdict rather than a pile of rows nobody checked.
        var version = await context.ScenarioVersions
            .FirstAsync(candidate => candidate.ScenarioId == scenario.Id);

        version.Number.Should().Be(1);
        version.IsValid.Should().BeTrue();

        (await context.WorkflowNodes.CountAsync(node => node.ScenarioVersionId == version.Id))
            .Should().Be(3);

        // And it points at the environment it named, not at nothing.
        scenario.EnvironmentId.Should().NotBeNull();

        (await context.Environments.FirstAsync(e => e.Id == scenario.EnvironmentId))
            .Slug.Should().Be("staging");
    }

    [Fact]
    public async Task Importing_twice_adds_nothing_the_second_time()
    {
        // An import adds and never overwrites, so running one again is a no-op rather than a
        // duplicate of everything — which is the shape of the mistake somebody makes at 6pm.
        await using var context = Db();

        var bundle = await Exporter(context).ExportAsync(_projectId);
        var first = await Importer(context).ApplyAsync(bundle, projectId: null);

        var again = await Importer(context).ApplyAsync(bundle, first.ProjectId);

        again.Counts.Sum(count => count.Adding).Should().Be(0);
        again.Skipped.Should().NotBeEmpty();

        (await context.Scenarios.CountAsync(s => s.ProjectId == first.ProjectId)).Should().Be(1);
        (await context.Baselines.CountAsync(b => b.ProjectId == first.ProjectId)).Should().Be(1);
    }

    [Fact]
    public async Task The_preview_says_what_the_import_will_do_and_is_right()
    {
        await using var context = Db();

        var bundle = await Exporter(context).ExportAsync(_projectId);
        var importer = Importer(context);

        var fresh = await importer.PreviewAsync(bundle, projectId: null);

        fresh.CreatesProject.Should().BeTrue();
        fresh.Skipped.Should().BeEmpty();
        fresh.Counts.Should().Contain(count => count.Kind == "scenario" && count.Adding == 1);
        fresh.SecretsToSupply.Should().Contain("apiToken");

        // Against the project it came from, everything already exists — and the preview says so
        // before anybody presses anything, which is the whole reason it is a separate step.
        var same = await importer.PreviewAsync(bundle, _projectId);

        same.CreatesProject.Should().BeFalse();
        same.Total.Should().Be(0);
        same.Skipped.Should().HaveCountGreaterThan(3);
    }

    [Fact]
    public async Task An_imported_schedule_arrives_switched_off()
    {
        // A file somebody was handed must not start firing at somebody's production API at a time
        // nobody chose.
        await using var context = Db();

        var bundle = await Exporter(context).ExportAsync(_projectId);

        bundle.Schedules.Should().ContainSingle().Which.Enabled.Should().BeTrue();

        var imported = await Importer(context).ApplyAsync(bundle, projectId: null);

        var schedule = await context.RunSchedules
            .FirstAsync(candidate => candidate.ProjectId == imported.ProjectId);

        schedule.Enabled.Should().BeFalse();
        schedule.Cron.Should().Be("0 7 * * 1-5");

        // And still points at the scenario and environment it named.
        (await context.ScheduleScenarios.CountAsync(link => link.RunScheduleId == schedule.Id))
            .Should().Be(1);
        (await context.ScheduleEnvironments.CountAsync(link => link.RunScheduleId == schedule.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_baseline_arrives_approved_with_its_rules()
    {
        await using var context = Db();

        var bundle = await Exporter(context).ExportAsync(_projectId);
        var imported = await Importer(context).ApplyAsync(bundle, projectId: null);

        var baseline = await context.Baselines
            .FirstAsync(candidate => candidate.ProjectId == imported.ProjectId);

        baseline.ApprovedVersionId.Should().NotBeNull();

        var version = await context.BaselineVersions.FirstAsync(v => v.Id == baseline.ApprovedVersionId);

        version.Status.Should().Be(BaselineStatus.Approved);
        version.Body.Should().Contain("\"ok\":true");
        version.RulesJson.Should().Contain("$.id");
        version.StatusCode.Should().Be(200);
    }

    [Fact]
    public void A_file_from_a_later_format_is_refused_rather_than_half_read()
    {
        var (bundle, refusal) = BundleJson.Read("""{"proofflow": 99, "project": {"name":"X","slug":"x"}}""");

        bundle.Should().BeNull();
        refusal.Should().Be("import.tooNew");
    }

    [Theory]
    [InlineData("", "import.empty")]
    [InlineData("not json at all", "import.notJson")]
    [InlineData("""{"hello":"world"}""", "import.notABundle")]
    [InlineData("""{"proofflow":1}""", "import.notABundle")]
    public void Anything_that_is_not_a_bundle_says_so(string json, string expected)
    {
        BundleJson.Read(json).Refusal.Should().Be(expected);
    }

    // ---- setup ---------------------------------------------------------------------------------

    private async Task SeedAsync(ProofFlowDbContext context)
    {
        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "W", Slug = "w" });

        var project = new Project
        {
            WorkspaceId = _workspaceId,
            Name = "Catalog API",
            Slug = "catalog-api",
            Description = "محصولات و دسته‌ها",
            Accent = "violet",
            CreatedByUserId = _userId,
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync();

        _projectId = project.Id;

        var local = new ProjectEnvironment
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = "Local",
            Slug = "local",
            BaseUrl = "http://localhost:9000",
            Kind = EnvironmentKind.Local,
            AllowPrivateNetwork = true,
            SortOrder = 0,
        };

        var staging = new ProjectEnvironment
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = "Staging",
            Slug = "staging",
            BaseUrl = "https://staging.example.test",
            Kind = EnvironmentKind.Staging,
            AllowedHosts = "*.example.test",
            TimeoutSeconds = 45,
            MaxRedirects = 3,
            MaxResponseKilobytes = 2048,
            SortOrder = 1,
        };

        context.Environments.AddRange(local, staging);
        await context.SaveChangesAsync();

        context.Variables.Add(new EnvironmentVariable
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            EnvironmentId = staging.Id,
            Name = "tenant",
            Value = "acme",
            Description = "Which tenant the staging data belongs to.",
        });

        // A secret, so the test can prove none of it travels.
        context.Secrets.Add(new Secret
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            EnvironmentId = staging.Id,
            Name = "apiToken",
            Description = "The bearer token staging expects.",
            Ciphertext = "cipher-text-that-should-never-travel",
            Nonce = "nonce-value",
            Tag = "tag-value",
            Preview = "abcd",
        });

        var scenario = new TestScenario
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = "Read one product",
            Description = "Signs in, reads a product, checks the status.",
            EnvironmentId = staging.Id,
            CreatedByUserId = _userId,

            // Inputs are part of what a scenario is, so they belong in the fixture the whole
            // round-trip is measured against rather than in a test of their own.
            InputsJson = ScenarioInputs.Write(
            [
                new ScenarioInputDto
                {
                    Name = "productId",
                    Label = "Product id",
                    Description = "Which one to read.",
                    Default = "5001",
                    Required = true,
                },
            ]),
        };

        context.Scenarios.Add(scenario);
        await context.SaveChangesAsync();

        await Graphs(context).SaveAsync(scenario, new GraphDto
        {
            Nodes =
            [
                Node("start", "core.start"),
                Node("call", "http.request",
                    ("method", "GET"), ("url", "{{environment.baseUrl}}/products/1")),
                Node("check", "assert.status", ("expected", "200")),
            ],
            Edges =
            [
                Edge("start", "call"),
                Edge("call", "check"),
                new GraphEdgeDto
                {
                    Id = "d1", FromId = "call", FromPort = "response", ToId = "check", ToPort = "response",
                },
            ],
        });

        var baseline = new Baseline
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            EnvironmentId = staging.Id,
            Name = "product detail",
            Description = "What one product looks like.",
            RequestJson = """{"method":"GET","url":"{{environment.baseUrl}}/products/1"}""",
            CreatedByUserId = _userId,
        };

        context.Baselines.Add(baseline);
        await context.SaveChangesAsync();

        var version = new BaselineVersion
        {
            WorkspaceId = _workspaceId,
            BaselineId = baseline.Id,
            Number = 1,
            Status = BaselineStatus.Approved,
            Body = """{"id":1,"ok":true}""",
            ContentType = "application/json",
            StatusCode = 200,
            HeadersJson = """{"content-type":"application/json"}""",
            RulesJson = """[{"path":"$.id","matcher":"Ignore"}]""",
            CreatedByUserId = _userId,
            ApprovedByUserId = _userId,
            ApprovedAt = DateTimeOffset.UtcNow,
        };

        context.BaselineVersions.Add(version);
        await context.SaveChangesAsync();

        baseline.ApprovedVersionId = version.Id;

        var set = new DataSet
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = "product ids",
            KeyColumn = "id",
            CreatedByUserId = _userId,
        };

        context.DataSets.Add(set);
        await context.SaveChangesAsync();

        var setVersion = new DataSetVersion
        {
            WorkspaceId = _workspaceId,
            DataSetId = set.Id,
            Number = 1,
            ColumnsJson = """["id","name"]""",
            RowCount = 2,
            CreatedByUserId = _userId,
        };

        context.DataSetVersions.Add(setVersion);
        await context.SaveChangesAsync();

        set.CurrentVersionId = setVersion.Id;

        context.DataSetRows.AddRange(
            new DataSetRow
            {
                WorkspaceId = _workspaceId,
                DataSetVersionId = setVersion.Id,
                Ordinal = 0,
                Key = "1",
                ValuesJson = """{"id":"1","name":"Anvil"}""",
            },
            new DataSetRow
            {
                WorkspaceId = _workspaceId,
                DataSetVersionId = setVersion.Id,
                Ordinal = 1,
                Key = "2",
                ValuesJson = """{"id":"2","name":"Rope"}""",
                Enabled = false,
            });

        var schedule = new RunSchedule
        {
            WorkspaceId = _workspaceId,
            ProjectId = _projectId,
            Name = "Weekday morning",
            Cron = "0 7 * * 1-5",
            TimeZoneId = "Asia/Tehran",
            Enabled = true,
            CreatedByUserId = _userId,
        };

        context.RunSchedules.Add(schedule);
        await context.SaveChangesAsync();

        context.ScheduleScenarios.Add(new ScheduleScenario
        {
            WorkspaceId = _workspaceId,
            RunScheduleId = schedule.Id,
            ScenarioId = scenario.Id,
        });

        context.ScheduleEnvironments.Add(new ScheduleEnvironment
        {
            WorkspaceId = _workspaceId,
            RunScheduleId = schedule.Id,
            EnvironmentId = staging.Id,
        });

        await context.SaveChangesAsync();
    }

    private static GraphNodeDto Node(
        string id, string key, params (string Name, string Value)[] properties) =>
        new()
        {
            Id = id,
            Key = key,
            Name = id,
            Properties = properties.ToDictionary(pair => pair.Name, pair => (string?)pair.Value),
        };

    private static GraphEdgeDto Edge(string from, string to) =>
        new() { Id = $"{from}-{to}", FromId = from, FromPort = "out", ToId = to, ToPort = "in" };

    private BundleExporter Exporter(ProofFlowDbContext context) =>
        new(context, Graphs(context), new SystemClock());

    private BundleImporter Importer(ProofFlowDbContext context) =>
        new(context, Graphs(context), Cipher(), User(), new SystemClock());

    private static AesGcmSecretCipher Cipher() =>
        new(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ProofFlow:MasterKey"] = Convert.ToBase64String(new byte[32]),
                })
                .Build(),
            NullLogger<AesGcmSecretCipher>.Instance);

    private ScenarioGraphService Graphs(ProofFlowDbContext context) =>
        new(context, User(), new SystemClock(), new PlainProblemText());

    private ICurrentUser User() => new FixedUser(_workspaceId, _userId);

    private ProofFlowDbContext Db()
    {
        var options = new DbContextOptionsBuilder<SqliteProofFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new SqliteProofFlowDbContext(options, new FixedWorkspaceScope(_workspaceId));
    }

    private sealed class FixedUser(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? WorkspaceId => workspaceId;
        public string DisplayName => "Tester";
        public WorkspaceRole? Role => WorkspaceRole.Owner;
        public bool IsAuthenticated => true;
        public bool Can(Capability capability) => true;
    }

    private sealed class PlainProblemText : IProblemText
    {
        public string For(ProofFlow.TestEngine.Nodes.GraphProblem problem) => problem.Code;
    }

    [Fact]
    public async Task A_collection_with_repeated_names_imports_instead_of_failing_at_the_database()
    {
        // What a real Postman export looks like and what this used to die on.
        //
        // Two requests called the same thing in the same folder is ordinary — and it produced two
        // rows with different slugs and the same name, against a unique index that is on the name.
        // The import stopped partway through on «an error occurred while saving the entity
        // changes», with some of the collection written and the rest not.
        //
        // A folder path five deep is ordinary too, and the name column holds two hundred
        // characters.
        var deep = string.Join("/", Enumerable.Repeat("A rather long folder name", 12));

        var json = $$"""
            {
              "info": {
                "name": "Repeats",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "item": [
                {
                  "name": "Orders",
                  "item": [
                    { "name": "Get", "request": { "method": "GET", "url": "https://api.example.com/a" } },
                    { "name": "Get", "request": { "method": "GET", "url": "https://api.example.com/b" } },
                    { "name": "Get", "request": { "method": "GET", "url": "https://api.example.com/c" } }
                  ]
                },
                {
                  "name": "{{deep}}",
                  "item": [
                    { "name": "One", "request": { "method": "GET", "url": "https://api.example.com/d" } },
                    { "name": "Two", "request": { "method": "GET", "url": "https://api.example.com/e" } }
                  ]
                }
              ]
            }
            """;

        var imported = PostmanImporter.Read(json);
        imported.Refusal.Should().BeNull();

        var bundle = ImportedBundle.From(imported, "Repeats");

        await using var context = Db();
        var result = await Importer(context).ApplyAsync(bundle, projectId: null);

        // Endpoints now, not scenarios — and the collision is the same one, against the
        // same unique index, on a table that had never been asked to hold two thousand rows.
        result.Counts.Single(count => count.Kind == "baseline").Adding
            .Should().Be(5, "every request arrives, however it was named");

        // And nothing became a scenario. A single call is not a chain.
        (await context.Scenarios.CountAsync(s => s.ProjectId == result.ProjectId)).Should().Be(0);

        var names = await context.Baselines
            .Where(endpoint => endpoint.ProjectId == result.ProjectId)
            .Select(endpoint => endpoint.Name)
            .ToListAsync();

        names.Should().OnlyHaveUniqueItems();
        names.Should().OnlyContain(name => name.Length <= 200, "that is what the column holds");
    }

    [Fact]
    public async Task Importing_the_same_collection_twice_adds_nothing_the_second_time()
    {
        const string json = """
            {
              "info": {
                "name": "Twice",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "item": [
                { "name": "Ping", "request": { "method": "GET", "url": "https://api.example.com/ping" } }
              ]
            }
            """;

        var bundle = ImportedBundle.From(PostmanImporter.Read(json), "Twice");

        await using var context = Db();

        var first = await Importer(context).ApplyAsync(bundle, projectId: null);
        var again = await Importer(context).ApplyAsync(bundle, first.ProjectId);

        first.Counts.Single(count => count.Kind == "baseline").Adding.Should().Be(1);
        again.Counts.Single(count => count.Kind == "baseline").Adding
            .Should().Be(0, "it is already here, and a second one would be a second unique name");
    }
    [Fact]
    public async Task Credentials_in_a_collection_stay_out_unless_somebody_asks_for_them()
    {
        // The default, and the rule the rest of this product is built on: the name crosses, the
        // value does not. A file somebody was handed holds a live token, and writing it down
        // uninvited puts it in the database, in the next export and in the first screenshot.
        const string json = """
            {
              "info": {
                "name": "Held",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "variable": [
                { "key": "baseUrl", "value": "https://api.example.com" },
                { "key": "apiToken", "value": "a-live-token-nobody-meant-to-share" }
              ],
              "item": [
                { "name": "Ping", "request": { "method": "GET", "url": "{{baseUrl}}/ping" } }
              ]
            }
            """;

        var imported = PostmanImporter.Read(json);
        var bundle = ImportedBundle.From(imported, "Held");

        await using var context = Db();
        var result = await Importer(context).ApplyAsync(bundle, projectId: null);

        var secrets = await context.Secrets
            .Where(secret => secret.ProjectId == result.ProjectId)
            .ToListAsync();

        secrets.Should().BeEmpty("nobody asked, so nothing was written");

        // And the reader is told which ones to make, by name.
        imported.SecretsToSupply.Should().Contain("apiToken");
    }

    [Fact]
    public async Task Asked_for_them_they_arrive_sealed_rather_than_typed_in_by_hand()
    {
        // The one path where the value crosses: somebody importing their own collection ticks the
        // box. What happens then is what happens when they type one into the secrets page — sealed
        // with the same cipher, four characters of preview, never handed back.
        const string json = """
            {
              "info": {
                "name": "Brought",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "auth": {
                "type": "bearer",
                "bearer": [ { "key": "token", "value": "a-live-token-nobody-meant-to-share" } ]
              },
              "variable": [ { "key": "baseUrl", "value": "https://api.example.com" } ],
              "item": [
                { "name": "Ping", "request": { "method": "GET", "url": "{{baseUrl}}/ping" } }
              ]
            }
            """;

        var imported = PostmanImporter.Read(json);

        imported.SecretValues.Should().ContainKey("bearerToken");

        var bundle = ImportedBundle.From(imported, "Brought");

        await using var context = Db();

        var result = await Importer(context)
            .ApplyAsync(bundle, projectId: null, imported.SecretValues);

        var secret = await context.Secrets
            .SingleAsync(candidate => candidate.ProjectId == result.ProjectId);

        secret.Name.Should().Be("bearerToken");
        secret.Preview.Should().Be("hare", "the last four characters, and nothing else");

        // Sealed, not stored: the value is nowhere in the row, and it comes back out only through
        // the cipher.
        secret.Ciphertext.Should().NotContain("a-live-token");

        Cipher().Open(new SealedSecret(
                secret.Ciphertext, secret.Nonce, secret.Tag, secret.KeyVersion))
            .Should().Be("a-live-token-nobody-meant-to-share");
    }
}
