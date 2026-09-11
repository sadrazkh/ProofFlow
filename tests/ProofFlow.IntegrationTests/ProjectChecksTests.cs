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
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// Checking an endpoint without holding the page open.
///
/// The sweep itself is proved in <see cref="CaptureSweepTests"/> against a real API. What is proved
/// here is the part that only exists once a check is queued: that the press comes back before the
/// work starts, that a background worker with no request picks it up and finds the right
/// workspace's endpoint, and that the page can read where it got to afterwards.
///
/// The endpoint points at an address the URL guard refuses, so every row fails the same way every
/// time. A check that ends in failure exercises the whole path and needs no second server.
/// </summary>
public sealed class ProjectChecksTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task Pressing_check_comes_back_before_the_work_starts()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, rows: 3);

        var started = await client.PostAsJsonAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/test",
            new { environmentId = (string?)null, limit = (int?)null });

        started.StatusCode.Should().Be(HttpStatusCode.OK);

        var answer = await started.Content.ReadFromJsonAsync<JsonElement>();

        // The assertion that matters. This used to be the finished sweep, which meant the browser
        // held the connection for its whole length.
        answer.GetProperty("status").GetString().Should().Be("Queued");
        answer.GetProperty("totalRows").GetInt32().Should().Be(3);
        answer.GetProperty("completed").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_queued_check_is_finished_by_the_worker_and_readable_afterwards()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, rows: 3);

        var started = await client.PostAsJsonAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/test",
            new { environmentId = (string?)null, limit = (int?)null });

        var sessionId = (await started.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sessionId").GetString()!;

        var settled = await SettledAsync(client, projectId, endpointId, sessionId);

        // Not a pass, and it is not supposed to be: the address is refused. What this proves is
        // that a worker with no HTTP request found this workspace's endpoint at all — an empty
        // tenant scope would have found nothing and left the session queued forever.
        settled.GetProperty("status").GetString().Should().Be("Completed");
        settled.GetProperty("completed").GetInt32().Should().Be(3);
        settled.GetProperty("failed").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Somebody_elses_check_is_not_readable()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, rows: 1);

        var started = await client.PostAsJsonAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/test",
            new { environmentId = (string?)null, limit = (int?)null });

        var sessionId = (await started.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sessionId").GetString()!;

        var stranger = app.CreateClient(NoRedirect);
        var refused = await stranger.GetAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/tests/{sessionId}/state");

        // Signed out, so the shell redirects to sign-in rather than answering with counters.
        refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Stopping_a_check_that_has_not_started_keeps_it_from_running()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, rows: 2);

        // Queued by hand rather than through the button, so the worker is not racing this test for
        // it: what is being proved is the refusal, not how quickly the press lands.
        Guid sessionId;
        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var endpoint = await db.Baselines.IgnoreQueryFilters().FirstAsync(b => b.Id == endpointId);
            var version = await db.DataSetVersions.IgnoreQueryFilters()
                .FirstAsync(v => v.DataSetId == endpoint.DataSetId);

            var session = new CaptureSession
            {
                WorkspaceId = endpoint.WorkspaceId,
                ProjectId = projectId,
                BaselineId = endpointId,
                DataSetVersionId = version.Id,
                Mode = CaptureMode.Regression,
                Status = CaptureSessionStatus.Queued,
                TotalRows = 2,
                StartedAt = DateTimeOffset.UtcNow,
            };

            db.CaptureSessions.Add(session);
            await db.SaveChangesAsync();
            sessionId = session.Id;
        }

        var stopped = await client.PostAsJsonAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/tests/{sessionId}/cancel", new { });

        stopped.StatusCode.Should().Be(HttpStatusCode.OK);
        (await stopped.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stopped").GetBoolean().Should().BeTrue();

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var session = await db.CaptureSessions.IgnoreQueryFilters().FirstAsync(s => s.Id == sessionId);

            session.Status.Should().Be(CaptureSessionStatus.Cancelled);
            session.FinishedAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Stopping_a_check_that_is_already_over_says_so_rather_than_pretending()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, rows: 1);

        var started = await client.PostAsJsonAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/test",
            new { environmentId = (string?)null, limit = (int?)null });

        var sessionId = (await started.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sessionId").GetString()!;

        await SettledAsync(client, projectId, endpointId, sessionId);

        var late = await client.PostAsJsonAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/tests/{sessionId}/cancel", new { });

        var answer = await late.Content.ReadFromJsonAsync<JsonElement>();

        answer.GetProperty("stopped").GetBoolean().Should().BeFalse();
        answer.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Checking_everything_starts_one_check_for_every_endpoint()
    {
        var (client, projectId) = await SignedInAsync();

        await EndpointAsync(projectId, rows: 2);

        // The one that used to be unreachable. An endpoint with no data set refused the Test
        // button outright, so «check everything» would have skipped most of a real project.
        await EndpointAsync(projectId, rows: 0);

        var token = await AntiforgeryAsync(client, $"/projects/{projectId}/endpoints");

        var started = await client.PostAsync($"/projects/{projectId}/checks",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        started.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var batchId = Guid.Parse(Regex.Match(
            started.Headers.Location!.ToString(), "checks/([0-9a-f-]{36})$").Groups[1].Value);

        var state = await SettledBatchAsync(client, projectId, batchId);

        state.GetProperty("total").GetInt32().Should().Be(2);

        var rows = state.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);

        // Both were actually carried out. The address is refused by the URL guard, so the honest
        // outcome for each is a failed row — but a row that never left «Queued» would mean the
        // batch queued work nothing ever picked up.
        rows.Should().OnlyContain(row => row.GetProperty("status").GetString() == "Completed");
        rows.Sum(row => row.GetProperty("failed").GetInt32()).Should().Be(3);
    }

    [Fact]
    public async Task Checking_everything_in_an_empty_project_refuses_and_writes_nothing()
    {
        var (client, projectId) = await SignedInAsync();

        var token = await AntiforgeryAsync(client, $"/projects/{projectId}/endpoints");

        var started = await client.PostAsync($"/projects/{projectId}/checks",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        // Back to the list with the refusal, rather than an empty batch page that looks like
        // something ran.
        started.StatusCode.Should().Be(HttpStatusCode.Redirect);
        started.Headers.Location!.ToString().Should().EndWith("/endpoints");

        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        (await db.CheckBatches.IgnoreQueryFilters().CountAsync(b => b.ProjectId == projectId))
            .Should().Be(0);
        (await db.CaptureSessions.IgnoreQueryFilters().CountAsync(s => s.ProjectId == projectId))
            .Should().Be(0);
    }

    // ---- scaffolding ----------------------------------------------------------------------------

    /// <summary>Polls a batch the way its page does, and gives up rather than hanging.</summary>
    private static async Task<JsonElement> SettledBatchAsync(
        HttpClient client, Guid projectId, Guid batchId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (true)
        {
            var state = await client.GetFromJsonAsync<JsonElement>(
                $"/projects/{projectId}/checks/{batchId}/state");

            if (state.GetProperty("settled").GetBoolean()) return state;

            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "a two-endpoint batch should finish");
            await Task.Delay(100);
        }
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client, string page)
    {
        var html = await client.GetStringAsync(page);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        match.Success.Should().BeTrue($"{page} should render an antiforgery token");
        return match.Groups[1].Value;
    }

    /// <summary>Polls the state endpoint the way the page does, and gives up rather than hanging.</summary>
    private static async Task<JsonElement> SettledAsync(
        HttpClient client, Guid projectId, Guid endpointId, string sessionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (true)
        {
            var state = await client.GetFromJsonAsync<JsonElement>(
                $"/projects/{projectId}/endpoints/{endpointId}/tests/{sessionId}/state");

            var status = state.GetProperty("status").GetString();
            if (status is not ("Queued" or "Running")) return state;

            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the worker should finish a 3-row check");
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// An endpoint pointed at an address the guard will not send to.
    ///
    /// <paramref name="rows"/> of zero makes one with no inputs at all — the shape quick-add and
    /// the request lab produce, and the one a project is mostly made of.
    /// </summary>
    private async Task<Guid> EndpointAsync(Guid projectId, int rows)
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var project = await db.Projects.IgnoreQueryFilters().FirstAsync(p => p.Id == projectId);

        var environment = new ProjectEnvironment
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = projectId,
            Name = "Nowhere",
            Slug = $"n-{Guid.NewGuid():N}"[..12],

            // Loopback, with the allowance deliberately not given. Every row comes back refused by
            // the URL guard, which is a failure this test can rely on to the millisecond.
            BaseUrl = "http://127.0.0.1:9",
            Kind = EnvironmentKind.Development,
        };
        db.Environments.Add(environment);

        if (rows == 0)
        {
            var once = new Baseline
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = projectId,
                EnvironmentId = environment.Id,
                Name = $"GET /one {Guid.NewGuid():N}"[..18],
                RequestJson = """{"method":"GET","url":"/one"}""",
            };

            db.Baselines.Add(once);
            await db.SaveChangesAsync();
            return once.Id;
        }

        var set = new DataSet
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = projectId,
            Name = $"Ids {Guid.NewGuid():N}"[..12],
        };
        db.DataSets.Add(set);
        await db.SaveChangesAsync();

        var version = new DataSetVersion
        {
            WorkspaceId = project.WorkspaceId,
            DataSetId = set.Id,
            Number = 1,
            RowCount = rows,
        };
        db.DataSetVersions.Add(version);
        await db.SaveChangesAsync();

        for (var i = 0; i < rows; i++)
        {
            db.DataSetRows.Add(new DataSetRow
            {
                WorkspaceId = project.WorkspaceId,
                DataSetVersionId = version.Id,
                Ordinal = i,
                Key = (i + 1).ToString(),
                ValuesJson = $$"""{"id":"{{i + 1}}"}""",
            });
        }

        set.CurrentVersionId = version.Id;

        var endpoint = new Baseline
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = projectId,
            EnvironmentId = environment.Id,
            DataSetId = set.Id,
            Name = $"GET /things {Guid.NewGuid():N}"[..18],
            RequestJson = """{"method":"GET","url":"/things/{{dataset.current.id}}"}""",
        };
        db.Baselines.Add(endpoint);

        await db.SaveChangesAsync();
        return endpoint.Id;
    }

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
                var email = $"checks-{Guid.CreateVersion7():N}@proofflow.test";

                using var scope = app.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

                var user = new ProofFlowUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = "Checks",
                };

                (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

                var db = Db(scope.ServiceProvider);

                var workspace = new Workspace
                {
                    Name = "Checks workspace",
                    Slug = $"cw-{Guid.NewGuid():N}"[..20],
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
                    Name = $"Checks {Guid.NewGuid():N}"[..18],
                    Slug = $"c-{Guid.NewGuid():N}"[..20],
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
