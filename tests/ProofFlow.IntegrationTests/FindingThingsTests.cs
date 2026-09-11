using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Runs;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// Finding your own things: the search behind the command palette, and the filter on the list.
///
/// Both are tested through the real HTTP surface because both are exactly the kind of feature that
/// works in a unit test and leaks in production. A search box is the one control people type other
/// people's words into, so «does it stay inside the workspace» is not a detail — it is the feature.
/// And a filter that is dropped by the pager is a filter that appears to work: page one is right,
/// page two quietly shows everything, and nobody notices until they act on it.
/// </summary>
public sealed class FindingThingsTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task Search_finds_an_endpoint_by_its_name_and_by_its_address()
    {
        var (client, projectId) = await SignedInAsync();
        var token = $"ordersearch{Guid.CreateVersion7():N}"[..24];

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);

            Endpoint(db, await WorkspaceOfAsync(db, projectId), projectId, $"GET /{token}");

            // Named something else entirely; only its address carries the word. This is the case
            // the palette was built for — after an import the name is whatever the collection
            // called the folder, and the path is the part anybody remembers.
            Endpoint(db, await WorkspaceOfAsync(db, projectId), projectId, "Folder 14 · request 3",
                $$"""{"method":"GET","url":"https://api.test/{{token}}/archive"}""");

            await db.SaveChangesAsync();
        }

        var answer = await SearchAsync(client, token);

        var endpoints = Group(answer, "nav.endpoints");

        endpoints.Should().HaveCount(2, "both the name and the address should match");
        endpoints.Select(item => item.GetProperty("title").GetString())
            .Should().Contain($"GET /{token}").And.Contain("Folder 14 · request 3");

        foreach (var item in endpoints)
        {
            item.GetProperty("path").GetString()
                .Should().StartWith($"/projects/{projectId}/endpoints/");
            item.GetProperty("icon").GetString().Should().Be("target");
        }
    }

    [Fact]
    public async Task Search_does_not_care_how_anybody_capitalised_it()
    {
        var (client, projectId) = await SignedInAsync();
        var token = $"Invoices{Guid.CreateVersion7():N}"[..20];

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            Endpoint(db, await WorkspaceOfAsync(db, projectId), projectId, $"GET /{token}");
            await db.SaveChangesAsync();
        }

        // The single likeliest thing anybody types. A search box that answers «invoices» with
        // nothing because the endpoint is called «Invoices» has failed at the one job it has.
        //
        // Worth knowing what this does and does not prove. These tests run on SQLite, where EF
        // renders Contains as LIKE and LIKE folds ASCII on its own — so this passes with or without
        // the lower() in the query. PostgreSQL renders the same expression as strpos, which does
        // not fold, and that is the provider the lower() is there for. So: this guards against
        // somebody making the filter case-sensitive on *both* providers, and it cannot catch a
        // regression that only shows in production. Said out loud rather than left as a green tick
        // that looks like more than it is.
        var lower = Group(await SearchAsync(client, token.ToLowerInvariant()), "nav.endpoints");
        lower.Should().ContainSingle();

        var upper = Group(await SearchAsync(client, token.ToUpperInvariant()), "nav.endpoints");
        upper.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_never_reaches_into_another_workspace()
    {
        var (client, projectId) = await SignedInAsync();
        var token = $"tenantcheck{Guid.CreateVersion7():N}"[..24];

        Guid strangerProjectId;

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);

            Endpoint(db, await WorkspaceOfAsync(db, projectId), projectId, $"GET /{token}/mine");

            // A whole other tenant, with an endpoint whose name is the word being searched for.
            // Written through the system scope, which is the only way this row can exist at all —
            // and the point of the test is that the request cannot see what the seed just made.
            var stranger = new Workspace
            {
                Name = "Somebody else",
                Slug = $"se-{Guid.CreateVersion7():N}"[..20],
                CreatedByUserId = Guid.CreateVersion7(),
            };
            db.Workspaces.Add(stranger);

            var strangerProject = new Project
            {
                WorkspaceId = stranger.Id,
                Name = "Theirs",
                Slug = $"t-{Guid.CreateVersion7():N}"[..20],
            };
            db.Projects.Add(strangerProject);

            Endpoint(db, stranger.Id, strangerProject.Id, $"GET /{token}/theirs");

            await db.SaveChangesAsync();
            strangerProjectId = strangerProject.Id;
        }

        var answer = await SearchAsync(client, token);
        var endpoints = Group(answer, "nav.endpoints");

        endpoints.Select(item => item.GetProperty("title").GetString())
            .Should().ContainSingle().Which.Should().Be($"GET /{token}/mine");

        var raw = answer.GetRawText();
        raw.Should().NotContain("theirs", "the tenant filter is what decides this, not a WHERE nobody wrote");
        raw.Should().NotContain(strangerProjectId.ToString());
    }

    [Fact]
    public async Task Search_narrowed_to_one_project_leaves_the_rest_of_the_workspace_alone()
    {
        var (client, projectId) = await SignedInAsync();
        var token = $"twoprojects{Guid.CreateVersion7():N}"[..24];

        Guid otherProjectId;

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var workspaceId = await WorkspaceOfAsync(db, projectId);

            Endpoint(db, workspaceId, projectId, $"GET /{token}/here");

            var elsewhere = new Project
            {
                WorkspaceId = workspaceId,
                Name = "The other one",
                Slug = $"o-{Guid.CreateVersion7():N}"[..20],
            };
            db.Projects.Add(elsewhere);

            Endpoint(db, workspaceId, elsewhere.Id, $"GET /{token}/elsewhere");

            await db.SaveChangesAsync();
            otherProjectId = elsewhere.Id;
        }

        var across = Group(await SearchAsync(client, token), "nav.endpoints");
        across.Should().HaveCount(2, "with no project named, the search spans the workspace");

        // Which project each one is in — the only thing that tells two identically named endpoints
        // apart when the search is workspace-wide.
        across.Select(item => item.GetProperty("subtitle").GetString())
            .Should().Contain("The other one");

        var inside = Group(await SearchAsync(client, token, projectId), "nav.endpoints");

        inside.Select(item => item.GetProperty("title").GetString())
            .Should().ContainSingle().Which.Should().Be($"GET /{token}/here");

        inside.Single().TryGetProperty("subtitle", out var subtitle);
        (subtitle.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined).Should().BeTrue(
            "inside one project, that project's name under every row is noise");

        across.Should().Contain(item =>
            item.GetProperty("path").GetString()!.Contains(otherProjectId.ToString()));
    }

    [Fact]
    public async Task A_blank_query_is_answered_without_touching_the_database()
    {
        var (client, _) = await SignedInAsync();

        foreach (var blank in new[] { string.Empty, "%20%20" })
        {
            var response = await client.GetAsync($"/search?q={blank}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var answer = await response.Content.ReadFromJsonAsync<JsonElement>();
            answer.GetProperty("groups").GetArrayLength().Should().Be(0);
        }
    }

    /// <summary>
    /// The filter narrows the list, and page two is still narrowed.
    ///
    /// The second half is the one worth a test. A pager that builds «?page=2» and nothing else is
    /// not obviously broken — it is broken on the page nobody looks at twice.
    /// </summary>
    [Fact]
    public async Task The_endpoint_filter_narrows_the_page_and_survives_the_pager()
    {
        var (client, projectId) = await SignedInAsync();
        var token = $"paged{Guid.CreateVersion7():N}"[..18];

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var workspaceId = await WorkspaceOfAsync(db, projectId);

            // More than one page of matches, so the filter has somewhere to be dropped. Numbers are
            // padded because the list is ordered by name and «10» sorts before «2».
            for (var at = 1; at <= 30; at++)
            {
                Endpoint(db, workspaceId, projectId, $"GET /{token}/{at:00}");
            }

            Endpoint(db, workspaceId, projectId, "GET /somethingelseentirely");

            await db.SaveChangesAsync();
        }

        var first = await client.GetStringAsync($"/projects/{projectId}/endpoints?q={token}");

        first.Should().Contain($"/{token}/01");
        first.Should().Contain($"/{token}/25");
        first.Should().NotContain($"/{token}/26", "twenty-five rows is a page");
        first.Should().NotContain("somethingelseentirely", "that is what narrowing means");

        // The pager's own links carry it. Read out of the markup rather than constructed, because
        // the thing under test is what the page offers, not what the test can build.
        var next = Regex.Match(first, @"href=""(/projects/[0-9a-f-]+/endpoints\?page=2[^""]*)""");
        next.Success.Should().BeTrue("a filtered list of thirty should still page");

        var link = WebUtility.HtmlDecode(next.Groups[1].Value);
        link.Should().Contain($"q={token}", "page two of a narrowed list is not the whole list");

        var second = await client.GetStringAsync(link);

        second.Should().Contain($"/{token}/26");
        second.Should().Contain($"/{token}/30");
        second.Should().NotContain($"/{token}/01");
        second.Should().NotContain("somethingelseentirely");
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_says_so_rather_than_offering_a_first_endpoint()
    {
        var (client, projectId) = await SignedInAsync();

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            Endpoint(db, await WorkspaceOfAsync(db, projectId), projectId, "GET /present-and-correct");
            await db.SaveChangesAsync();
        }

        var page = await client.GetStringAsync(
            $"/projects/{projectId}/endpoints?q=nothinglikethisexistsanywhere");

        // Asserted on the markup rather than on the sentences: every page carries the whole
        // translation catalogue inline for the islands, so «does the page contain this string» is
        // answered yes by the catalogue whatever the page actually shows.

        // The word the reader typed, echoed back inside the heading of the empty state. Matched
        // rather than compared, so the quotation marks around it are free to change.
        page.Should().MatchRegex("empty-title\">[^<]*nothinglikethisexistsanywhere[^<]*</h2>");

        // And kept in the box, so it can be edited rather than retyped.
        page.Should().Contain("value=\"nothinglikethisexistsanywhere\"");

        // Not the «record your first endpoint» state, whose one distinguishing link is the connect
        // flow. They have endpoints; being told otherwise is how somebody records a second copy of
        // one they already had.
        page.Should().NotContain($"/projects/{projectId}/connect");
    }

    /// <summary>
    /// The run history, narrowed by the scenario's name and by the verdict.
    ///
    /// A run has no name of its own, so «find the checkout ones» has to reach through to the
    /// scenario — which is the part that would quietly match nothing if the subquery were wrong.
    /// </summary>
    [Fact]
    public async Task The_run_filter_narrows_by_scenario_and_by_verdict()
    {
        var (client, projectId) = await SignedInAsync();
        var token = $"{Guid.CreateVersion7():N}"[..10];

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var workspaceId = await WorkspaceOfAsync(db, projectId);

            Run(db, workspaceId, projectId, $"Checkout {token}", RunStatus.Passed);
            Run(db, workspaceId, projectId, $"Signup {token}", RunStatus.Failed);

            await db.SaveChangesAsync();
        }

        var byName = await client.GetStringAsync($"/projects/{projectId}/runs?q=Checkout+{token}");
        byName.Should().Contain($"Checkout {token}");
        byName.Should().NotContain($"Signup {token}");

        var byVerdict = await client.GetStringAsync($"/projects/{projectId}/runs?status=Failed");
        byVerdict.Should().Contain($"Signup {token}");
        byVerdict.Should().NotContain($"Checkout {token}");

        // Both at once, and they disagree — which is the case that has to come back empty rather
        // than falling back to one of them.
        var neither = await client.GetStringAsync(
            $"/projects/{projectId}/runs?q=Checkout+{token}&status=Failed");

        neither.Should().NotContain($"/projects/{projectId}/runs/",
            "no run link at all, because no run satisfies both");
        neither.Should().MatchRegex($"empty-title\">[^<]*Checkout {token}[^<]*</h2>",
            "the empty state should say which word found nothing");

        // A verdict nobody sends is read as «any» rather than as «none».
        var nonsense = await client.GetStringAsync($"/projects/{projectId}/runs?status=Banana");
        nonsense.Should().Contain($"Checkout {token}").And.Contain($"Signup {token}");
    }

    // ---- scaffolding ----------------------------------------------------------------------------

    private static async Task<JsonElement> SearchAsync(HttpClient client, string term, Guid? project = null)
    {
        var address = $"/search?q={Uri.EscapeDataString(term)}"
                      + (project is { } id ? $"&project={id}" : string.Empty);

        var response = await client.GetAsync(address);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>One group's items, or an empty list when that kind of thing matched nothing.</summary>
    private static IReadOnlyList<JsonElement> Group(JsonElement answer, string labelKey) =>
    [
        .. answer.GetProperty("groups").EnumerateArray()
            .Where(group => group.GetProperty("labelKey").GetString() == labelKey)
            .SelectMany(group => group.GetProperty("items").EnumerateArray()),
    ];

    private static void Endpoint(
        ProofFlowDbContext db, Guid workspaceId, Guid projectId, string name, string? requestJson = null) =>
        db.Baselines.Add(new Baseline
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Name = name,
            RequestJson = requestJson ?? $$"""{"method":"GET","url":"https://api.test{{name}}"}""",
            CreatedByUserId = Guid.CreateVersion7(),
        });

    /// <summary>A scenario and one finished run of it — the pair the history is a list of.</summary>
    private static void Run(
        ProofFlowDbContext db, Guid workspaceId, Guid projectId, string name, RunStatus status)
    {
        var scenario = new TestScenario
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Name = name,
            CreatedByUserId = Guid.CreateVersion7(),
        };
        db.Scenarios.Add(scenario);

        db.Runs.Add(new TestRun
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            ScenarioId = scenario.Id,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FinishedAt = DateTimeOffset.UtcNow,
            DurationMs = 120,
            AssertionsPassed = status == RunStatus.Passed ? 3 : 2,
            AssertionsFailed = status == RunStatus.Passed ? 0 : 1,
        });
    }

    private static Task<Guid> WorkspaceOfAsync(ProofFlowDbContext db, Guid projectId) =>
        db.Projects.IgnoreQueryFilters().Where(p => p.Id == projectId).Select(p => p.WorkspaceId).FirstAsync();

    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    // One session for the class — the sign-in endpoint is rate limited, and the tests must live
    // with the same limits people do. Each test gets its own project.
    private static readonly SemaphoreSlim SessionLock = new(1, 1);
    private static HttpClient? _sharedClient;
    private static Guid _sharedWorkspaceId;

    private async Task<(HttpClient Client, Guid ProjectId)> SignedInAsync()
    {
        await SessionLock.WaitAsync();
        try
        {
            if (_sharedClient is null)
            {
                var email = $"finding-{Guid.CreateVersion7():N}@proofflow.test";

                using var scope = app.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

                var user = new ProofFlowUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = "Finder",
                };

                (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

                var db = Db(scope.ServiceProvider);

                var workspace = new Workspace
                {
                    Name = "Finding workspace",
                    Slug = $"fw-{Guid.CreateVersion7():N}"[..20],
                    CreatedByUserId = user.Id,
                };
                db.Workspaces.Add(workspace);
                db.WorkspaceMembers.Add(new WorkspaceMember
                {
                    WorkspaceId = workspace.Id,
                    UserId = user.Id,
                    Role = WorkspaceRole.Owner,
                    JoinedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();

                user.LastWorkspaceId = workspace.Id;
                await users.UpdateAsync(user);

                var client = app.CreateClient(NoRedirect);

                // The application answers in Persian by default, which is right for the product
                // and unreadable as a test assertion. Asking for English is what a browser set to
                // English does, so this exercises the negotiation rather than bypassing it.
                client.DefaultRequestHeaders.Add("Accept-Language", "en");

                var page = await client.GetStringAsync("/account/sign-in");
                var token = Regex.Match(page,
                    "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

                var signedIn = await client.PostAsync("/account/sign-in",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["Email"] = email,
                        ["Password"] = Password,
                        ["RememberMe"] = "true",
                        ["__RequestVerificationToken"] = token,
                    }));

                signedIn.StatusCode.Should().Be(HttpStatusCode.Found, "sign-in should succeed");

                _sharedClient = client;
                _sharedWorkspaceId = workspace.Id;
            }

            using (var scope = app.Services.CreateScope())
            {
                var db = Db(scope.ServiceProvider);

                var project = new Project
                {
                    WorkspaceId = _sharedWorkspaceId,
                    Name = $"Find {Guid.CreateVersion7():N}"[..18],
                    Slug = $"f-{Guid.CreateVersion7():N}"[..20],
                };
                db.Projects.Add(project);
                await db.SaveChangesAsync();

                return (_sharedClient, project.Id);
            }
        }
        finally
        {
            SessionLock.Release();
        }
    }
}
