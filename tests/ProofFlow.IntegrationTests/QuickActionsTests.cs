using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Runs;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The small buttons: run it again, duplicate an environment, publish a badge.
///
/// Each is a few lines of controller over machinery proved elsewhere, and each is tested through
/// the real HTTP surface anyway — the few lines are exactly where «same inputs» quietly becomes
/// «no inputs», and where a copied secret quietly reuses a nonce.
/// </summary>
public sealed class QuickActionsTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task Running_it_again_keeps_the_inputs_and_says_it_was_a_rerun()
    {
        var (client, projectId) = await SignedInAsync();

        // The starter scenario the canvas gives everyone — created through the same door a person
        // uses, so it has a saved version to queue.
        var made = await client.GetAsync($"/projects/{projectId}/scenarios/new");
        made.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var scenarioId = Guid.Parse(Regex.Match(
            made.Headers.Location!.ToString(), "scenarios/([0-9a-f-]{36})$").Groups[1].Value);

        // An input, so «again» has something to forget.
        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var scenario = await db.Scenarios.IgnoreQueryFilters().FirstAsync(s => s.Id == scenarioId);
            scenario.InputsJson = """[{"name":"colour","defaultValue":"red"}]""";
            await db.SaveChangesAsync();
        }

        var token = await AntiforgeryAsync(client, $"/projects/{projectId}/runs");

        var started = await client.PostAsync($"/projects/{projectId}/runs/start",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["scenarioId"] = scenarioId.ToString(),
                ["input.colour"] = "green",
                ["__RequestVerificationToken"] = token,
            }));

        started.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var firstId = Guid.Parse(Regex.Match(
            started.Headers.Location!.ToString(), "runs/([0-9a-f-]{36})$").Groups[1].Value);

        var again = await client.PostAsync($"/projects/{projectId}/runs/{firstId}/again",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        again.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var secondId = Guid.Parse(Regex.Match(
            again.Headers.Location!.ToString(), "runs/([0-9a-f-]{36})$").Groups[1].Value);

        secondId.Should().NotBe(firstId);

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var first = await db.Runs.IgnoreQueryFilters().FirstAsync(r => r.Id == firstId);
            var second = await db.Runs.IgnoreQueryFilters().FirstAsync(r => r.Id == secondId);

            second.ScenarioId.Should().Be(first.ScenarioId);
            second.EnvironmentId.Should().Be(first.EnvironmentId);

            // The value typed for the first run, not the default and not nothing.
            second.InputsJson.Should().Contain("green");

            // Named as what it is, so the history can tell a person's press from an echo of one.
            second.Trigger.Should().Be(RunTrigger.Rerun);
        }
    }

    [Fact]
    public async Task Duplicating_an_environment_reseals_its_secrets_under_fresh_nonces()
    {
        var (client, projectId) = await SignedInAsync();

        Guid environmentId;
        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var cipher = scope.ServiceProvider.GetRequiredService<ISecretCipher>();
            var workspaceId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == projectId).Select(p => p.WorkspaceId).FirstAsync();

            var environment = new ProjectEnvironment
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Name = "Original",
                Slug = "original",
                BaseUrl = "https://api.example.test",
                AllowedHosts = "api.example.test",
                TimeoutSeconds = 45,
                AuthenticationJson =
                    """{"mode":2,"tokenUrl":"/auth/login","credentials":{"password":"{{secrets.apiPassword}}"}}""",
            };
            db.Environments.Add(environment);

            var sealedValue = cipher.Seal("the-actual-password");
            db.Secrets.Add(new Secret
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                EnvironmentId = environment.Id,
                Name = "apiPassword",
                Ciphertext = sealedValue.Ciphertext,
                Nonce = sealedValue.Nonce,
                Tag = sealedValue.Tag,
                KeyVersion = sealedValue.KeyVersion,
            });

            await db.SaveChangesAsync();
            environmentId = environment.Id;
        }

        var token = await AntiforgeryAsync(client, $"/projects/{projectId}/environments");

        var response = await client.PostAsync(
            $"/projects/{projectId}/environments/{environmentId}/duplicate",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var cipher = scope.ServiceProvider.GetRequiredService<ISecretCipher>();

            var copy = await db.Environments.IgnoreQueryFilters().FirstAsync(
                e => e.ProjectId == projectId && e.Id != environmentId);

            // The settings and the sign-in travelled; the slug did not collide.
            copy.BaseUrl.Should().Be("https://api.example.test");
            copy.TimeoutSeconds.Should().Be(45);
            copy.AllowedHosts.Should().Be("api.example.test");
            copy.AuthenticationJson.Should().Contain("{{secrets.apiPassword}}");
            copy.Slug.Should().NotBe("original");

            var original = await db.Secrets.IgnoreQueryFilters()
                .FirstAsync(s => s.EnvironmentId == environmentId);
            var copied = await db.Secrets.IgnoreQueryFilters()
                .FirstAsync(s => s.EnvironmentId == copy.Id);

            copied.Name.Should().Be("apiPassword");

            // Same value, different nonce — the entity's own invariant. A row copy would pass the
            // first assertion and violate the second.
            cipher.Open(new SealedSecret(copied.Ciphertext, copied.Nonce, copied.Tag, copied.KeyVersion))
                .Should().Be("the-actual-password");
            copied.Nonce.Should().NotBe(original.Nonce);
        }
    }

    [Fact]
    public async Task The_badge_answers_anyone_and_a_revoked_one_answers_nobody()
    {
        var (client, projectId) = await SignedInAsync();

        var token = await AntiforgeryAsync(client, $"/projects/{projectId}/settings");

        // Minting redirects to settings, where the markdown snippet carries the token — the only
        // place it ever appears.
        var minted = await client.PostAsync($"/projects/{projectId}/settings/badge",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        minted.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var settings = await client.GetStringAsync($"/projects/{projectId}/settings");
        var badge = Regex.Match(settings, "/badge/([A-Za-z0-9_-]{40,50})\\.svg");

        badge.Success.Should().BeTrue("the settings page should show the minted address once");

        // A different client with no cookies at all: the badge is for other people's proxies.
        var anonymous = app.CreateClient(NoRedirect);

        var svg = await anonymous.GetAsync($"/badge/{badge.Groups[1].Value}.svg");

        svg.StatusCode.Should().Be(HttpStatusCode.OK);
        svg.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");

        var image = await svg.Content.ReadAsStringAsync();
        image.Should().Contain("no runs", "a fresh project has no verdict to report");
        image.Should().NotContain("<script", "an SVG served to strangers must stay an image");

        var revoked = await client.PostAsync($"/projects/{projectId}/settings/badge/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        revoked.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await anonymous.GetAsync($"/badge/{badge.Groups[1].Value}.svg"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_checklist_ticks_follow_what_the_workspace_actually_contains()
    {
        var (client, projectId) = await SignedInAsync();

        // A fresh workspace would show all four steps, but this class shares one — so the
        // assertion is on the step that this test itself changes.
        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);

            // Wind the shared workspace back to «nothing connected yet».
            await db.Environments.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Runs.IgnoreQueryFilters().ExecuteDeleteAsync();
        }

        var before = await client.GetStringAsync("/");
        before.Should().Contain("getting-started", "with no run yet, the card is shown");
        before.Should().Contain("/connect", "the next step should point at the connect flow");

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var workspaceId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == projectId).Select(p => p.WorkspaceId).FirstAsync();

            db.Environments.Add(new ProjectEnvironment
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Name = "Connected",
                Slug = "connected",
                BaseUrl = "https://api.example.test",
            });
            await db.SaveChangesAsync();
        }

        // The tick is the data: no per-step state to write, so making the thing is the only way
        // to complete the step.
        var after = await client.GetStringAsync("/");
        after.Should().Contain("getting-started");
        after.Should().Contain("is-done", "the connect step should now be ticked");
    }

    [Fact]
    public async Task The_sparkline_says_in_words_what_its_bars_say_in_colour()
    {
        var (client, projectId) = await SignedInAsync();

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var workspaceId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == projectId).Select(p => p.WorkspaceId).FirstAsync();

            var endpoint = new Baseline
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Name = "GET /history",
                CreatedByUserId = Guid.CreateVersion7(),
            };
            db.Baselines.Add(endpoint);

            // A real set and version: capture sessions carry a foreign key to one, and inventing
            // an id would be a test passing over a schema that would refuse it in production.
            var set = new DataSet
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Name = $"history {Guid.CreateVersion7()}",
                KeyColumn = "key",
                CreatedByUserId = Guid.CreateVersion7(),
            };
            db.DataSets.Add(set);

            var version = new DataSetVersion
            {
                WorkspaceId = workspaceId,
                DataSetId = set.Id,
                Number = 1,
                ColumnsJson = """["key"]""",
                RowCount = 4,
                CreatedByUserId = Guid.CreateVersion7(),
            };
            db.DataSetVersions.Add(version);

            // Three tests: two clean, one with a difference.
            for (var at = 0; at < 3; at++)
            {
                db.CaptureSessions.Add(new CaptureSession
                {
                    WorkspaceId = workspaceId,
                    ProjectId = projectId,
                    BaselineId = endpoint.Id,
                    DataSetVersionId = version.Id,
                    TotalRows = 4,
                    Completed = 4,
                    Differing = at == 1 ? 1 : 0,
                    Status = CaptureSessionStatus.Completed,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-at),
                });
            }

            await db.SaveChangesAsync();
        }

        var page = await client.GetStringAsync($"/projects/{projectId}/endpoints");

        page.Should().Contain("sparkline-bar is-pass");
        page.Should().Contain("sparkline-bar is-warn", "the middle test found a difference");

        // Colour is never the only carrier. The sentence is what a screen reader is handed.
        page.Should().MatchRegex(@"aria-label=""[^""]*2[^""]*3[^""]*""",
            "the label should say two of the last three passed");
    }

    [Fact]
    public async Task A_shared_run_shows_the_verdict_to_a_stranger_and_nothing_else()
    {
        var (client, projectId) = await SignedInAsync();

        Guid runId;
        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var workspaceId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == projectId).Select(p => p.WorkspaceId).FirstAsync();

            var scenario = new TestScenario
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Name = "Checkout end to end",
                CreatedByUserId = Guid.CreateVersion7(),
            };
            db.Scenarios.Add(scenario);

            var run = new TestRun
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                ScenarioId = scenario.Id,
                Status = RunStatus.Failed,
                Outcome = "The third step expected 200 and got 500.",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                FinishedAt = DateTimeOffset.UtcNow,
                DurationMs = 1234,
                AssertionsPassed = 4,
                AssertionsFailed = 1,

                // The two things a share link must never expose, both documented as unredacted.
                DefinitionJson = """{"nodes":[{"id":"a","name":"Sign in","key":"http.request"}]}""",
                InputsJson = """{"apiPassword":"hunter2-in-the-inputs"}""",
            };
            db.Runs.Add(run);

            db.NodeRuns.Add(new NodeRun
            {
                WorkspaceId = workspaceId,
                TestRunId = run.Id,
                NodeId = "a",
                NodeName = "Sign in",
                NodeKey = "http.request",
                Status = NodeRunStatus.Passed,
                DurationMs = 42,
                SortOrder = 0,
            });

            await db.SaveChangesAsync();
            runId = run.Id;
        }

        var token = await AntiforgeryAsync(client, $"/projects/{projectId}/runs");

        var minted = await client.PostAsync($"/projects/{projectId}/runs/{runId}/share?revoke=false",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        minted.StatusCode.Should().Be(HttpStatusCode.OK);

        var url = (await minted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("url").GetString()!;

        var share = Regex.Match(url, "/share/runs/([A-Za-z0-9_-]{40,50})").Groups[1].Value;
        share.Should().NotBeEmpty("minting should hand back a usable address");

        // A client with no cookies at all — the whole point of the feature.
        var stranger = app.CreateClient(NoRedirect);
        var response = await stranger.GetAsync($"/share/runs/{share}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadAsStringAsync();

        page.Should().Contain("Checkout end to end", "the scenario is what the reader came for");
        page.Should().Contain("Sign in", "each step's name and verdict");
        page.Should().Contain("The third step expected 200 and got 500.");

        // The boundary. These are on the run row and must not travel with the link.
        page.Should().NotContain("hunter2-in-the-inputs", "typed inputs are unredacted by design");
        page.Should().NotContain("http.request", "the graph snapshot is not part of a result");

        // And it is not something a crawler should file away.
        response.Headers.GetValues("X-Robots-Tag").Should().Contain(value => value.Contains("noindex"));

        var revoked = await client.PostAsync($"/projects/{projectId}/runs/{runId}/share?revoke=true",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        revoked.StatusCode.Should().Be(HttpStatusCode.OK);

        (await stranger.GetAsync($"/share/runs/{share}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "taking the link back has to actually take it back");
    }

    // ---- scaffolding ----------------------------------------------------------------------------

    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    private static async Task<string> AntiforgeryAsync(HttpClient client, string page)
    {
        var html = await client.GetStringAsync(page);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        match.Success.Should().BeTrue($"{page} should render an antiforgery token");
        return match.Groups[1].Value;
    }

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
                var email = $"quick-{Guid.CreateVersion7():N}@proofflow.test";

                using var scope = app.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

                var user = new ProofFlowUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = "Quick",
                };

                (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

                var db = Db(scope.ServiceProvider);

                var workspace = new Workspace
                {
                    Name = "Quick workspace",
                    Slug = $"qw-{Guid.CreateVersion7():N}"[..20],
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
                    Name = $"Quick {Guid.CreateVersion7():N}"[..18],
                    Slug = $"q-{Guid.CreateVersion7():N}"[..20],
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
