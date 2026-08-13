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
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The endpoint page: the list that does not try to render everything, and the button that
/// refuses rather than pretending.
///
/// Both halves of this are about failures that look like successes. A list with no paging did not
/// error on an imported collection — it took eleven seconds and produced a document nobody could
/// use — and a Test button with nothing to test could very easily have reported «0 passed», which
/// is a green result for a test that never ran.
/// </summary>
public sealed class EndpointPageTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task The_list_shows_one_page_at_a_time_however_many_there_are()
    {
        var (client, projectId, _) = await SignedInAsync(endpoints: 30);

        var first = await client.GetStringAsync($"/projects/{projectId}/endpoints");

        // Twenty-five rows and a pager, not thirty rows. The count is asserted through the links
        // the page renders rather than through the view model, because the view model being right
        // while the markup renders everything is precisely the bug this replaces.
        Rows(first, projectId).Should().HaveCount(25);
        first.Should().Contain("pager-link");

        var second = await client.GetStringAsync($"/projects/{projectId}/endpoints?page=2");

        Rows(second, projectId).Should().HaveCount(5);

        // And the two pages are different rows, not the same twenty-five twice — which is what a
        // pager that renders correctly and pages nothing would produce.
        Rows(second, projectId).Should().NotIntersectWith(Rows(first, projectId));
    }

    [Fact]
    public async Task A_page_number_past_the_end_lands_on_the_last_one_rather_than_on_nothing()
    {
        var (client, projectId, _) = await SignedInAsync(endpoints: 30);

        var response = await client.GetAsync($"/projects/{projectId}/endpoints?page=99");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // A pasted link, or a page emptied by somebody else's deletion. Either way an empty table
        // with no way back is worse than the last page that still exists.
        Rows(await response.Content.ReadAsStringAsync(), projectId).Should().HaveCount(5);
    }

    [Fact]
    public async Task Testing_an_endpoint_with_no_inputs_says_so_instead_of_reporting_nothing_passed()
    {
        var (client, projectId, _) = await SignedInAsync(endpoints: 1);
        var endpointId = await FirstEndpointAsync(projectId);

        var response = await client.PostAsJsonAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/test", new { });

        // The alternative — sweeping zero rows and answering «0 of 0 passed» — is a green result
        // for a test that never ran, which is the one thing this product must never produce.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"totalRows\"");
    }

    [Fact]
    public async Task An_endpoint_cannot_be_pointed_at_another_projects_inputs()
    {
        var (client, projectId, workspaceId) = await SignedInAsync(endpoints: 1);
        var endpointId = await FirstEndpointAsync(projectId);

        var (otherProjectId, foreignDataSetId) = await SecondProjectAsync(workspaceId);

        var page = $"/projects/{projectId}/endpoints/{endpointId}";

        var response = await client.PostAsync($"{page}/inputs", await FormAsync(client, page,
            new Dictionary<string, string> { ["dataSetId"] = foreignDataSetId.ToString() }));

        // The tenant filter already stops a cross-workspace pairing. This is the same mistake one
        // level down, and it would produce an endpoint sweeping rows nobody on this project can see.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = app.Services.CreateScope();
        var stored = await Db(scope.ServiceProvider).Baselines.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == endpointId);

        stored.DataSetId.Should().BeNull();
        otherProjectId.Should().NotBe(projectId);
    }

    [Fact]
    public async Task Choosing_inputs_is_remembered_on_the_endpoint()
    {
        var (client, projectId, _) = await SignedInAsync(endpoints: 1, withInputs: true);
        var endpointId = await FirstEndpointAsync(projectId);

        Guid dataSetId;
        using (var scope = app.Services.CreateScope())
        {
            dataSetId = await Db(scope.ServiceProvider).DataSets.IgnoreQueryFilters()
                .Where(candidate => candidate.ProjectId == projectId)
                .Select(candidate => candidate.Id)
                .SingleAsync();
        }

        var page = $"/projects/{projectId}/endpoints/{endpointId}";

        var response = await client.PostAsync($"{page}/inputs", await FormAsync(client, page,
            new Dictionary<string, string> { ["dataSetId"] = dataSetId.ToString() }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = app.Services.CreateScope())
        {
            // The point of storing it: the Test button no longer has to ask, so pressing it is one
            // press rather than a dialog somebody has to answer the same way every time.
            var stored = await Db(scope.ServiceProvider).Baselines.IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == endpointId);

            stored.DataSetId.Should().Be(dataSetId);
        }

        // And it comes back chosen, rather than the form reverting to «send it once».
        var html = await client.GetStringAsync(page);
        html.Should().MatchRegex($"""value="{dataSetId}"[^>]*selected""");
    }

    [Fact]
    public async Task Saving_the_address_keeps_the_headers_the_request_lab_put_there()
    {
        var (client, projectId, _) = await SignedInAsync(endpoints: 1, withHeaders: true);
        var endpointId = await FirstEndpointAsync(projectId);

        var page = $"/projects/{projectId}/endpoints/{endpointId}";

        var response = await client.PostAsync($"{page}/request", await FormAsync(client, page,
            new Dictionary<string, string>
            {
                ["method"] = "POST",
                ["url"] = "{{environment.baseUrl}}/products",
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = app.Services.CreateScope();
        var stored = await Db(scope.ServiceProvider).Baselines.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == endpointId);

        var request = JsonDocument.Parse(stored.RequestJson!).RootElement;

        request.GetProperty("method").GetString().Should().Be("POST");
        request.GetProperty("url").GetString().Should().Be("{{environment.baseUrl}}/products");

        // The form owns two fields. Serialising only those two over the top would silently delete
        // the authorisation header somebody set up in the request lab, and the next test would
        // fail with a 401 nobody could explain.
        request.GetProperty("headers").EnumerateArray()
            .Select(header => header.GetProperty("name").GetString())
            .Should().Contain("Authorization");
    }

    // ---- scaffolding ----------------------------------------------------------------------------

    /// <summary>The endpoint links on a rendered list page, which is what «a row» means here.</summary>
    private static string[] Rows(string html, Guid projectId) =>
        [.. Regex.Matches(html, $@"/projects/{projectId}/endpoints/([0-9a-f\-]{{36}})""")
            .Select(match => match.Groups[1].Value)
            .Distinct()];

    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    private async Task<Guid> FirstEndpointAsync(Guid projectId)
    {
        using var scope = app.Services.CreateScope();

        return await Db(scope.ServiceProvider).Baselines.IgnoreQueryFilters()
            .Where(candidate => candidate.ProjectId == projectId)
            .OrderBy(candidate => candidate.Name)
            .Select(candidate => candidate.Id)
            .FirstAsync();
    }

    private async Task<(Guid ProjectId, Guid DataSetId)> SecondProjectAsync(Guid workspaceId)
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var project = new Project
        {
            WorkspaceId = workspaceId,
            Name = "Somebody else's project",
            Slug = $"o-{Guid.CreateVersion7():N}"[..20],
        };
        db.Projects.Add(project);

        var set = new DataSet
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Their rows",
        };
        db.DataSets.Add(set);

        await db.SaveChangesAsync();

        return (project.Id, set.Id);
    }

    private async Task<(HttpClient Client, Guid ProjectId, Guid WorkspaceId)> SignedInAsync(
        int endpoints, bool withInputs = false, bool withHeaders = false)
    {
        var email = $"endpoint-{Guid.CreateVersion7():N}@proofflow.test";
        Guid projectId, workspaceId;

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

            var user = new ProofFlowUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Tester",
            };

            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

            var db = Db(scope.ServiceProvider);

            var workspace = new Workspace
            {
                Name = "Endpoint workspace",
                Slug = $"ws-{Guid.CreateVersion7():N}"[..20],
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

            var project = new Project
            {
                WorkspaceId = workspace.Id,
                Name = "Endpoint project",
                Slug = $"p-{Guid.CreateVersion7():N}"[..20],
            };
            db.Projects.Add(project);

            db.Environments.Add(new ProjectEnvironment
            {
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Name = "Staging",
                Slug = "staging",
                BaseUrl = "https://staging.internal",
            });

            if (withInputs)
            {
                db.DataSets.Add(new DataSet
                {
                    WorkspaceId = workspace.Id,
                    ProjectId = project.Id,
                    Name = "Product ids",
                    KeyColumn = "id",
                    CreatedByUserId = user.Id,
                });
            }

            for (var index = 0; index < endpoints; index++)
            {
                db.Baselines.Add(new Baseline
                {
                    WorkspaceId = workspace.Id,
                    ProjectId = project.Id,
                    // Padded, so «endpoint 10» sorts after «endpoint 09» and the two pages of the
                    // paging test are the two halves anybody would expect them to be.
                    Name = $"endpoint {index:D2}",
                    CreatedByUserId = user.Id,
                    RequestJson = JsonSerializer.Serialize(new
                    {
                        method = "GET",
                        url = "{{environment.baseUrl}}/products/1",
                        headers = withHeaders
                            ? new[] { new { name = "Authorization", value = "Bearer {{secrets.token}}", enabled = true } }
                            : [],
                    }),
                });
            }

            await db.SaveChangesAsync();

            user.LastWorkspaceId = workspace.Id;
            await users.UpdateAsync(user);

            workspaceId = workspace.Id;
            projectId = project.Id;
        }

        var client = app.CreateClient(NoRedirect);
        await SignInAsync(client, email);

        return (client, projectId, workspaceId);
    }

    private static async Task SignInAsync(HttpClient client, string email)
    {
        var form = await FormAsync(client, "/account/sign-in", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["RememberMe"] = "true",
        });

        (await client.PostAsync("/account/sign-in", form)).StatusCode
            .Should().Be(HttpStatusCode.Redirect, "sign-in should succeed");
    }

    private static async Task<FormUrlEncodedContent> FormAsync(
        HttpClient client, string tokenPage, Dictionary<string, string> fields)
    {
        var response = await client.GetAsync(tokenPage);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{tokenPage} should render");

        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        match.Success.Should().BeTrue($"{tokenPage} should render an antiforgery token");

        fields["__RequestVerificationToken"] = match.Groups[1].Value;
        return new FormUrlEncodedContent(fields);
    }
}
