using System.Net;
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
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// Deciding about several waiting versions at once, and seeing what each one changes first.
///
/// Both halves are about the same failure mode. The inbox used to offer Approve and a link away,
/// so the only two things a reader could do were approve blind or lose their place; the fix is
/// worthless if the diff it shows is not the real one, and worse than worthless if approving four
/// quietly approves three. So: the comparison is asserted against real bodies rather than against
/// «a diff came back», and the partial press is asserted on the database rather than on the
/// sentence it produced.
/// </summary>
public sealed class BulkApprovalTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    private const string Password = "a-long-enough-password";

    // ---- approving several ----------------------------------------------------------------------

    [Fact]
    public async Task Approving_several_approves_the_ones_it_may_and_says_which_it_did_not()
    {
        var (client, projectId) = await ReviewerAsync();

        var mine = await PendingAsync(projectId, "GET /mine", _reviewerId);
        var theirs = new[]
        {
            await PendingAsync(projectId, "GET /one", _ownerId),
            await PendingAsync(projectId, "GET /two", _ownerId),
            await PendingAsync(projectId, "GET /three", _ownerId),
        };

        var response = await client.PostAsync($"/projects/{projectId}/approvals/approve",
            await FormAsync(client, $"/projects/{projectId}/approvals",
                [.. theirs.Append(mine).Select(id => id.ToString())]));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().EndWith($"/projects/{projectId}/approvals");

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);

            foreach (var id in theirs)
            {
                (await db.BaselineVersions.IgnoreQueryFilters().FirstAsync(v => v.Id == id))
                    .Status.Should().Be(BaselineStatus.Approved,
                        "three legitimate approvals must not be thrown away to punish a fourth");
            }

            // The reader's own, untouched. An all-or-nothing refusal and a silent success are the
            // two ways this could have gone wrong, and they look the same from here.
            (await db.BaselineVersions.IgnoreQueryFilters().FirstAsync(v => v.Id == mine))
                .Status.Should().Be(BaselineStatus.PendingApproval);

            // One audit entry per version, the same action the single press writes — a single
            // «approved 3» row could not answer "who approved this one".
            var recorded = await db.AuditEvents.IgnoreQueryFilters()
                .Where(entry => entry.Action == "baseline.approved" && entry.ProjectId == projectId)
                .Select(entry => entry.TargetId)
                .ToListAsync();

            recorded.Should().BeEquivalentTo(theirs.Select(id => (Guid?)id));
        }

        // And it says so, in two sentences carrying one number each.
        var page = await client.GetStringAsync($"/projects/{projectId}/approvals");
        var toasts = Regex.Matches(page, @"data-toast=""([^""]*)""")
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();

        toasts.Should().ContainSingle(text => text.StartsWith("3 approved"));
        toasts.Should().ContainSingle(text => text.StartsWith("1 left for somebody else"));
    }

    [Fact]
    public async Task Ticking_nothing_says_so_rather_than_reporting_a_silent_success()
    {
        var (client, projectId) = await ReviewerAsync();

        var response = await client.PostAsync($"/projects/{projectId}/approvals/approve",
            await FormAsync(client, $"/projects/{projectId}/approvals", []));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var page = await client.GetStringAsync($"/projects/{projectId}/approvals");
        page.Should().Contain("Nothing was ticked");
    }

    [Fact]
    public async Task The_inbox_offers_a_box_only_on_the_rows_the_reader_may_actually_approve()
    {
        var (client, projectId) = await ReviewerAsync();

        await PendingAsync(projectId, "GET /theirs", _ownerId);
        await PendingAsync(projectId, "GET /mine", _reviewerId);

        var page = await client.GetStringAsync($"/projects/{projectId}/approvals");

        // One box, not two. The row this reader recorded is shown — knowing something is waiting on
        // a colleague is the reason to go and ask them — but it is not offered for ticking.
        Regex.Matches(page, @"data-bulk-item=""versions""").Should().HaveCount(1);
        Regex.Matches(page, @"data-bulk-all=""versions""").Should().HaveCount(1);
        page.Should().Contain("Waiting on somebody else");

        // Disabled at first paint, because at first paint nothing is ticked.
        page.Should().MatchRegex(@"data-bulk-action=""versions""\s+disabled");

        // And both rows carry the expander, because reading what changed is not an approval.
        Regex.Matches(page, @"data-island=""version-diff""").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_role_that_cannot_approve_cannot_approve_several_either()
    {
        var (_, projectId) = await ReviewerAsync();
        var designer = await DesignerAsync();

        var waiting = await PendingAsync(projectId, "GET /guarded", _ownerId);

        var response = await designer.PostAsync($"/projects/{projectId}/approvals/approve",
            await FormAsync(designer, $"/projects/{projectId}/approvals", [waiting.ToString()]));

        // Denied at the policy, before any of it runs. A designer is deliberately left out of
        // ApproveBaseline, and a bulk door into the same decision would be a way round the table.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/account/denied");

        using var scope = app.Services.CreateScope();
        (await Db(scope.ServiceProvider).BaselineVersions.IgnoreQueryFilters()
                .FirstAsync(version => version.Id == waiting))
            .Status.Should().Be(BaselineStatus.PendingApproval);
    }

    // ---- what a proposal changes -----------------------------------------------------------------

    [Fact]
    public async Task The_diff_is_the_real_difference_between_what_is_approved_and_what_is_proposed()
    {
        var (client, projectId) = await ReviewerAsync();

        var endpointId = await EndpointAsync(projectId, "GET /products/1");
        await VersionAsync(endpointId, 1, BaselineStatus.Approved, _ownerId,
            """{"name":"hat","price":10}""", 200);
        var proposed = await VersionAsync(endpointId, 2, BaselineStatus.PendingApproval, _ownerId,
            """{"name":"hat","price":12}""", 200);

        var answer = await client.GetFromJsonishAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/versions/{proposed}/diff");

        answer.GetProperty("first").GetBoolean().Should().BeFalse();

        var diff = answer.GetProperty("diff");
        diff.GetProperty("matches").GetBoolean().Should().BeFalse();
        diff.GetProperty("baselineVersion").GetString().Should().Be("v1");

        var rows = diff.GetProperty("rows").EnumerateArray().ToArray();
        var price = rows.Single(row => row.GetProperty("path").GetString() == "$.price");

        price.GetProperty("kind").GetString().Should().Be("Changed");
        price.GetProperty("expected").GetString().Should().Be("10");
        price.GetProperty("actual").GetString().Should().Be("12");

        // The field that did not move is still there: a diff showing only what changed gives no
        // sense of what did not.
        rows.Should().Contain(row => row.GetProperty("path").GetString() == "$.name");
    }

    [Fact]
    public async Task A_status_code_that_moved_is_a_difference_the_body_alone_cannot_show()
    {
        var (client, projectId) = await ReviewerAsync();

        var endpointId = await EndpointAsync(projectId, "GET /products/2");
        await VersionAsync(endpointId, 1, BaselineStatus.Approved, _ownerId, """{"ok":true}""", 200);
        var proposed = await VersionAsync(endpointId, 2, BaselineStatus.PendingApproval, _ownerId,
            """{"ok":true}""", 500);

        var answer = await client.GetFromJsonishAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/versions/{proposed}/diff");

        var diff = answer.GetProperty("diff");

        diff.GetProperty("matches").GetBoolean().Should().BeFalse(
            "identical bodies with a different status line are not an identical answer");

        var status = diff.GetProperty("rows").EnumerateArray().ToArray()
            .Single(row => row.GetProperty("path").GetString() == "status");

        status.GetProperty("expected").GetString().Should().Be("200");
        status.GetProperty("actual").GetString().Should().Be("500");
    }

    [Fact]
    public async Task A_version_with_nothing_approved_before_it_says_it_is_the_first_answer()
    {
        var (client, projectId) = await ReviewerAsync();

        var endpointId = await EndpointAsync(projectId, "GET /brand-new");
        var proposed = await VersionAsync(endpointId, 1, BaselineStatus.PendingApproval, _ownerId,
            """{"ok":true}""", 200);

        var answer = await client.GetFromJsonishAsync(
            $"/projects/{projectId}/endpoints/{endpointId}/versions/{proposed}/diff");

        // Not an empty diff. "Nothing changed" and "there was nothing to change from" look the
        // same on screen and mean opposite things, and only one of them is true here.
        answer.GetProperty("first").GetBoolean().Should().BeTrue();
        answer.GetProperty("diff").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_version_in_another_project_is_not_reachable_through_this_ones_address()
    {
        var (client, projectId) = await ReviewerAsync();
        var (_, otherProjectId) = await ReviewerAsync();

        var elsewhere = await EndpointAsync(otherProjectId, "GET /somebody-elses");
        var version = await VersionAsync(elsewhere, 1, BaselineStatus.PendingApproval, _ownerId,
            """{"ok":true}""", 200);

        var response = await client.GetAsync(
            $"/projects/{projectId}/endpoints/{elsewhere}/versions/{version}/diff");

        // The workspace filter alone would have served this happily: both projects belong to the
        // same workspace, and the address is the only thing that says which one was asked for.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- scaffolding -----------------------------------------------------------------------------

    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    private async Task<Guid> EndpointAsync(Guid projectId, string name)
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var endpoint = new Baseline
        {
            WorkspaceId = _workspaceId,
            ProjectId = projectId,
            Name = name,
            CreatedByUserId = _ownerId,
        };

        db.Baselines.Add(endpoint);
        await db.SaveChangesAsync();

        return endpoint.Id;
    }

    private async Task<Guid> VersionAsync(
        Guid endpointId, int number, BaselineStatus status, Guid author, string body, int statusCode)
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var version = new BaselineVersion
        {
            WorkspaceId = _workspaceId,
            BaselineId = endpointId,
            Number = number,
            Body = body,
            StatusCode = statusCode,
            Status = status,
            CreatedByUserId = author,
        };

        db.BaselineVersions.Add(version);
        await db.SaveChangesAsync();

        return version.Id;
    }

    /// <summary>One endpoint of its own with one version waiting, recorded by whoever is named.</summary>
    private async Task<Guid> PendingAsync(Guid projectId, string name, Guid author) =>
        await VersionAsync(await EndpointAsync(projectId, name), 1,
            BaselineStatus.PendingApproval, author, """{"ok":true}""", 200);

    private static async Task<FormUrlEncodedContent> FormAsync(
        HttpClient client, string tokenPage, string[] versionIds)
    {
        var html = await client.GetStringAsync(tokenPage);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        match.Success.Should().BeTrue($"{tokenPage} should render an antiforgery token");

        // A repeated field rather than a list, because that is what a table of checkboxes posts.
        var fields = versionIds.Select(id => new KeyValuePair<string, string>("versionIds", id))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", match.Groups[1].Value));

        return new FormUrlEncodedContent(fields);
    }

    // One session per person for the whole class: the sign-in endpoint is rate limited, and a test
    // suite that trips it fails somewhere far away from the cause. Each test gets its own project.
    private static readonly SemaphoreSlim SessionLock = new(1, 1);

    private static HttpClient? _reviewerClient;
    private static HttpClient? _designerClient;
    private static Guid _workspaceId;
    private static Guid _reviewerId;
    private static Guid _ownerId;

    private async Task<(HttpClient Client, Guid ProjectId)> ReviewerAsync()
    {
        await SessionLock.WaitAsync();
        try
        {
            await WorkspaceAsync();
            return (_reviewerClient!, await ProjectAsync());
        }
        finally
        {
            SessionLock.Release();
        }
    }

    private async Task<HttpClient> DesignerAsync()
    {
        await SessionLock.WaitAsync();
        try
        {
            await WorkspaceAsync();
            return _designerClient!;
        }
        finally
        {
            SessionLock.Release();
        }
    }

    /// <summary>
    /// Three people in one workspace, because the separation rule needs all three to mean anything.
    ///
    /// The reader is a reviewer; the owner is the somebody-else whose existence makes «you recorded
    /// this one» binding rather than moot; the designer is the role the capability table leaves
    /// ApproveBaseline out of on purpose.
    /// </summary>
    private async Task WorkspaceAsync()
    {
        if (_reviewerClient is not null) return;

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();
        var db = Db(scope.ServiceProvider);

        var workspace = new Workspace
        {
            Name = "Approval workspace",
            Slug = $"aw-{Guid.CreateVersion7():N}"[..20],
        };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        _workspaceId = workspace.Id;

        async Task<(Guid Id, string Email)> MemberAsync(string who, WorkspaceRole role)
        {
            var email = $"{who}-{Guid.CreateVersion7():N}@proofflow.test";

            var user = new ProofFlowUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = who,
                LastWorkspaceId = workspace.Id,
            };

            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = role,
                JoinedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            return (user.Id, email);
        }

        var (ownerId, _) = await MemberAsync("owner", WorkspaceRole.Owner);
        var (reviewerId, reviewerEmail) = await MemberAsync("reviewer", WorkspaceRole.Reviewer);
        var (_, designerEmail) = await MemberAsync("designer", WorkspaceRole.TestDesigner);

        _ownerId = ownerId;
        _reviewerId = reviewerId;

        _reviewerClient = await SignInAsync(reviewerEmail);
        _designerClient = await SignInAsync(designerEmail);
    }

    private async Task<Guid> ProjectAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var project = new Project
        {
            WorkspaceId = _workspaceId,
            Name = $"Approvals {Guid.CreateVersion7():N}"[..20],
            Slug = $"a-{Guid.CreateVersion7():N}"[..20],
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        return project.Id;
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = app.CreateClient(NoRedirect);

        // The application's default language is Persian. Asked for in English because the sentences
        // this class asserts on are the English ones, and a browser asking for a language it can
        // read is how anybody gets them.
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

        signedIn.StatusCode.Should().Be(HttpStatusCode.Found, $"{email} should be able to sign in");

        return client;
    }
}

internal static class JsonishClient
{
    /// <summary>Reads a JSON body as a document, failing loudly on anything that is not one.</summary>
    public static async Task<JsonElement> GetFromJsonishAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{url} should answer");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
