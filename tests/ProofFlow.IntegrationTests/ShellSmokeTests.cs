using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The application starts, migrates, and answers. Cheap to run and the first thing to break when
/// dependency registration or a migration goes wrong.
/// </summary>
public class ShellSmokeTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    /// <summary>
    /// Every page in the navigation, fetched.
    ///
    /// This did not exist, and its absence is exactly the shape of «it doesn't run»: three URLs
    /// were covered — health, the dashboard redirect and sign-in — so a controller that threw on
    /// any other page failed no test at all, and the first person to find out was whoever opened
    /// it. Six hundred tests can be green while a page nobody wrote a test for is a stack trace.
    ///
    /// The paths are listed rather than read out of <c>Navigation</c>, and that is a real
    /// compromise: a page added to the sidebar is not covered until somebody adds it here. Reading
    /// the map would need a signed-in <c>ICurrentUser</c> to filter by capability, which is most
    /// of an integration test in itself. Listing them is worse in one way and correct in another —
    /// this also covers the four pages the sidebar does not link to at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryPage))]
    public async Task Every_page_in_the_navigation_renders(string template)
    {
        var (client, projectId) = await SignedInAsync();
        var path = string.Format(template, projectId);

        var response = await client.GetAsync(path);

        // The body is read into the message on purpose: a developer-exception page carries the
        // stack trace, and «expected OK, found 500» without it is a second debugging session.
        var body = response.IsSuccessStatusCode
            ? string.Empty
            : (await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "{0} should render, but it said {1}: {2}",
            path, (int)response.StatusCode, Excerpt(body));
    }

    public static TheoryData<string> EveryPage()
    {
        var data = new TheoryData<string>();

        // Workspace-wide, and the two that live outside a project.
        // /design is deliberately absent: it is mapped only in Development and the test host is
        // not, so asking for it here would assert a 404 into existence.
        foreach (var path in new[]
                 {
                     "/", "/start", "/projects", "/projects/new", "/team", "/runners", "/activity",
                     "/settings/workspace",
                 })
        {
            data.Add(path);
        }

        // Inside a project. The id is substituted by the test, because it is made per run.
        foreach (var path in new[]
                 {
                     "/projects/{0}", "/projects/{0}/endpoints", "/projects/{0}/endpoints/new",
                     "/projects/{0}/scenarios", "/projects/{0}/datasets", "/projects/{0}/datasets/new",
                     "/projects/{0}/environments", "/projects/{0}/request", "/projects/{0}/runs",
                     "/projects/{0}/matrix", "/projects/{0}/approvals", "/projects/{0}/schedules",
                     "/projects/{0}/templates", "/projects/{0}/import", "/projects/{0}/export",
                     "/projects/{0}/settings",
                 })
        {
            data.Add(path);
        }

        return data;
    }

    private static string Excerpt(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(no body)";

        // The exception line out of a developer-exception page, which is the part worth reading.
        var match = Regex.Match(body, @"<title>([^<]{0,300})</title>");
        var text = match.Success ? match.Groups[1].Value : body;

        return Regex.Replace(text, @"\s+", " ").Trim()[..Math.Min(300, text.Length)];
    }

    [Fact]
    public async Task Health_answers_without_a_session()
    {
        var client = app.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ok");
    }

    [Fact]
    public async Task The_dashboard_sends_an_anonymous_visitor_to_sign_in()
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/account/sign-in");
    }

    [Fact]
    public async Task The_sign_in_page_renders_in_Persian_by_default()
    {
        var client = app.CreateClient();

        var html = await client.GetStringAsync("/account/sign-in");

        html.Should().Contain("dir=\"rtl\"");
        html.Should().Contain("lang=\"fa\"");
        html.Should().Contain("ورود");
    }

    [Fact]
    public async Task The_sign_in_page_renders_in_English_when_asked()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Accept-Language", "en");

        var html = await client.GetStringAsync("/account/sign-in");

        html.Should().Contain("dir=\"ltr\"");
        html.Should().Contain("Sign in");
    }

    [Fact]
    public async Task An_unknown_path_renders_the_themed_404_and_keeps_its_status()
    {
        var client = app.CreateClient();

        var response = await client.GetAsync("/no-such-page");
        var html = await response.Content.ReadAsStringAsync();

        // Both halves matter. A themed page that answers 200 tells every crawler and every
        // monitoring check that a missing page is fine.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        html.Should().Contain("404");
    }

    [Fact]
    public async Task Security_headers_are_on_every_response()
    {
        var client = app.CreateClient();

        var response = await client.GetAsync("/account/sign-in");

        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task The_language_switch_sets_a_cookie_and_returns_to_the_page()
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/settings/language?culture=en&returnUrl=%2Faccount%2Fsign-in");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/account/sign-in");
        response.Headers.GetValues("Set-Cookie").Should().Contain(value => value.Contains(".AspNetCore.Culture"));
    }

    [Fact]
    public async Task The_language_switch_refuses_a_culture_the_app_does_not_have()
    {
        var client = app.CreateClient();

        var response = await client.GetAsync("/settings/language?culture=de");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// One sign-in for the whole class, made on first use.
    ///
    /// Twenty-five pages meant twenty-five sign-ins, and the application rate limits that endpoint
    /// — so every case after the first few came back 429 and the suite reported «/team does not
    /// render» about a page that renders perfectly. Two other scripts in this repository had
    /// already fallen into exactly this and say so in their comments; this is the third.
    ///
    /// The lock matters because xUnit runs a theory's cases in parallel: without it the first
    /// several cases each start their own sign-in and trip the limiter anyway.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (HttpClient Client, Guid ProjectId)? _session;

    private async Task<(HttpClient Client, Guid ProjectId)> SignedInAsync()
    {
        if (_session is { } ready) return ready;

        await Gate.WaitAsync();

        try
        {
            _session ??= await CreateSessionAsync();
            return _session.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>An owner, a workspace and a project with one environment — enough for every page
    /// in the list above to have something to render rather than an empty state that hides a bug.</summary>
    private async Task<(HttpClient Client, Guid ProjectId)> CreateSessionAsync()
    {
        const string password = "a-long-enough-password";
        var email = $"smoke-{Guid.CreateVersion7():N}@proofflow.test";
        Guid projectId;

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

            var user = new ProofFlowUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Smoke",
            };

            (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();

            var db = new SqliteProofFlowDbContext(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
                new SystemWorkspaceScope());

            var workspace = new Workspace
            {
                Name = "Smoke workspace",
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
                Name = "Smoke project",
                Slug = $"p-{Guid.CreateVersion7():N}"[..20],
            };
            db.Projects.Add(project);

            db.Environments.Add(new ProjectEnvironment
            {
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Name = "Local",
                Slug = "local",
                BaseUrl = "https://local.internal",
            });

            await db.SaveChangesAsync();

            user.LastWorkspaceId = workspace.Id;
            await users.UpdateAsync(user);

            projectId = project.Id;
        }

        // Redirects followed, because the walk is about «does this render», and a page that sends
        // the reader to a real one has rendered. Sign-in is asserted on its own terms below.
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var page = await client.GetStringAsync("/account/sign-in");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        token.Success.Should().BeTrue("the sign-in page should render an antiforgery token");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["RememberMe"] = "true",
            ["__RequestVerificationToken"] = token.Groups[1].Value,
        });

        var landed = await client.PostAsync("/account/sign-in", form);

        landed.StatusCode.Should().Be(HttpStatusCode.OK, "sign-in should land somewhere");
        landed.RequestMessage!.RequestUri!.AbsolutePath
            .Should().NotContain("sign-in", "landing back on the form means it did not succeed");

        return (client, projectId);
    }

    [Fact]
    public async Task The_language_switch_will_not_bounce_to_another_site()
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // An open redirect on a link that legitimately comes from us is a phishing tool.
        var response = await client.GetAsync("/settings/language?culture=en&returnUrl=https%3A%2F%2Fexample.com");

        response.Headers.Location!.OriginalString.Should().Be("/");
    }
}
