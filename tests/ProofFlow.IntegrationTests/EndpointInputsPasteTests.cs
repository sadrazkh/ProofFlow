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
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// Giving an endpoint its inputs without leaving the page.
///
/// The thing worth testing here is not that a data set can be made — that has its own tests. It is
/// that one press does all three parts of it: the set, its first version with the rows that were
/// previewed, and the endpoint pointed at the result. Two of the three happening is the failure
/// mode, and it looks exactly like success from the browser.
///
/// The other two tests are about the refusals, because a refusal that half-writes is worse than no
/// feature: an empty set in the list looks like inputs and sweeps nothing.
/// </summary>
public sealed class EndpointInputsPasteTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task Pasting_rows_makes_the_set_its_first_version_and_points_the_endpoint_at_it()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, "GET /products/{id}");

        var response = await PasteAsync(client, projectId, endpointId, new
        {
            text = "id,label\n1,one\n2,two\n3,three",
            format = "Csv",
            keyColumn = "id",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The address to go back to, so the browser lands on the endpoint rather than on JSON.
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()
            .Should().Be($"/projects/{projectId}/endpoints/{endpointId}");

        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var set = await db.DataSets.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.ProjectId == projectId);

        // Named after the endpoint, because nobody pasting rows onto one endpoint wants to be
        // asked what to call them first.
        set.Name.Should().Be("GET /products/{id}");
        set.KeyColumn.Should().Be("id");

        var version = await db.DataSetVersions.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.DataSetId == set.Id);

        version.Number.Should().Be(1);
        version.RowCount.Should().Be(3, "the header line is the header, not a row");

        // The count on the version and the rows actually stored are two different facts, and a
        // version claiming three rows over two stored ones is a sweep that silently skips one.
        (await db.DataSetRows.IgnoreQueryFilters()
            .CountAsync(row => row.DataSetVersionId == version.Id)).Should().Be(3);

        set.CurrentVersionId.Should().Be(version.Id);

        // The whole point: the Test button asks the endpoint for its inputs, and until this is set
        // it refuses.
        var endpoint = await db.Baselines.IgnoreQueryFilters().SingleAsync(b => b.Id == endpointId);
        endpoint.DataSetId.Should().Be(set.Id);
    }

    [Fact]
    public async Task The_page_names_the_columns_this_endpoint_asks_every_row_for()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, "GET /products/{id} again");

        var html = WebUtility.HtmlDecode(
            await client.GetStringAsync($"/projects/{projectId}/endpoints/{endpointId}"));

        html.Should().Contain("data-island=\"endpoint-inputs-paste\"",
            "the paste box is offered beside the dropdown, not instead of it");

        // Read out of the stored request rather than guessed. Pasting a column called «productId»
        // against an address that says {{dataset.current.id}} produces a sweep where every call
        // goes to the same wrong address, and the report reads like the API being down.
        html.Should().Contain("\"needs\":[\"id\"]");
    }

    [Fact]
    public async Task A_paste_that_reads_as_no_rows_writes_nothing_at_all()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, "GET /empty");

        var (sets, versions, rows) = await CountsAsync();

        var response = await PasteAsync(client, projectId, endpointId, new
        {
            text = "   \n\n \n",
            format = (string?)null,
            keyColumn = (string?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // An empty set is the tempting thing to write here, and it is the worst outcome available:
        // it appears in the list looking like inputs, and pressing Test sweeps zero rows and
        // reports a clean result for a check that never ran.
        (await CountsAsync()).Should().Be((sets, versions, rows));

        using var scope = app.Services.CreateScope();
        var endpoint = await Db(scope.ServiceProvider).Baselines.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == endpointId);

        endpoint.DataSetId.Should().BeNull();
    }

    [Fact]
    public async Task A_name_another_set_in_the_project_already_has_is_refused()
    {
        var (client, projectId) = await SignedInAsync();
        var endpointId = await EndpointAsync(projectId, "GET /orders");

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var workspaceId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == projectId).Select(p => p.WorkspaceId).FirstAsync();

            db.DataSets.Add(new DataSet
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Name = "GET /orders",
                CreatedByUserId = Guid.CreateVersion7(),
            });
            await db.SaveChangesAsync();
        }

        // No name given, so it defaults to the endpoint's — which is exactly the collision a second
        // paste on the same endpoint produces.
        var response = await PasteAsync(client, projectId, endpointId, new
        {
            text = "7\n8\n9",
            format = "Lines",
            keyColumn = (string?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("detail").GetString()
            .Should().Contain("GET /orders", "the refusal should name the set it collided with");

        using var scope2 = app.Services.CreateScope();
        var db2 = Db(scope2.ServiceProvider);

        // Refused rather than numbered apart, and refused before anything was written.
        (await db2.DataSets.IgnoreQueryFilters().CountAsync(d => d.ProjectId == projectId))
            .Should().Be(1);

        (await db2.Baselines.IgnoreQueryFilters().SingleAsync(b => b.Id == endpointId))
            .DataSetId.Should().BeNull();
    }

    // ---- scaffolding ----------------------------------------------------------------------------

    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    private async Task<(int Sets, int Versions, int Rows)> CountsAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        return (
            await db.DataSets.IgnoreQueryFilters().CountAsync(),
            await db.DataSetVersions.IgnoreQueryFilters().CountAsync(),
            await db.DataSetRows.IgnoreQueryFilters().CountAsync());
    }

    /// <summary>An endpoint whose address refers to a column, which is the shape this feeds.</summary>
    private async Task<Guid> EndpointAsync(Guid projectId, string name)
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var workspaceId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId).Select(p => p.WorkspaceId).FirstAsync();

        var endpoint = new Baseline
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Name = name,
            CreatedByUserId = Guid.CreateVersion7(),
            RequestJson = JsonSerializer.Serialize(new
            {
                method = "GET",
                url = "{{environment.baseUrl}}/products/{{dataset.current.id}}",
                headers = Array.Empty<object>(),
            }),
        };

        db.Baselines.Add(endpoint);
        await db.SaveChangesAsync();

        return endpoint.Id;
    }

    /// <summary>
    /// The island's call, made the way the island makes it.
    ///
    /// The token travels in the header rather than in a form field, which is how every JSON write
    /// in this application is protected — and getting it from the page the reader is already on is
    /// what the browser does too.
    /// </summary>
    private static async Task<HttpResponseMessage> PasteAsync(
        HttpClient client, Guid projectId, Guid endpointId, object payload)
    {
        var page = $"/projects/{projectId}/endpoints/{endpointId}";
        var html = await client.GetStringAsync(page);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        match.Success.Should().BeTrue($"{page} should render an antiforgery token");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{page}/inputs/paste")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-Token", match.Groups[1].Value);

        return await client.SendAsync(request);
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
                var email = $"paste-{Guid.CreateVersion7():N}@proofflow.test";

                using var scope = app.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

                var user = new ProofFlowUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = "Paster",
                };

                (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

                var db = Db(scope.ServiceProvider);

                var workspace = new Workspace
                {
                    Name = "Paste workspace",
                    Slug = $"pw-{Guid.CreateVersion7():N}"[..20],
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
                    Name = $"Paste {Guid.CreateVersion7():N}"[..18],
                    Slug = $"pp-{Guid.CreateVersion7():N}"[..20],
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
