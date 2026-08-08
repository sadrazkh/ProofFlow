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
using ProofFlow.Domain.Runners;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// Enrolling and choosing a runner, the way a person actually does it.
///
/// The agent's own conversation has its own tests; this is the other half — the four clicks between
/// "we have an API behind a firewall" and "a run went out from inside that network". It is exercised
/// through real HTTP because the interesting failures are all at the seams: what a page hands over
/// once and never again, what a role may reach, and whether a setting on a form actually reaches the
/// run that was supposed to obey it.
/// </summary>
public sealed class RunnerPageTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task The_enrollment_code_is_shown_once_and_never_stored_in_the_clear()
    {
        var (client, _, workspaceId) = await SignedInAsync();

        var created = await client.PostAsync("/runners", await FormAsync(client, "/runners",
            new Dictionary<string, string> { ["name"] = "Office network" }));

        created.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // The page it redirects to is the only place the code exists.
        var page = await client.GetStringAsync("/runners");
        var code = Regex.Match(page, @"runner-code-value[^>]*>\s*([A-Z0-9\- ]{16,})\s*<");

        code.Success.Should().BeTrue("the page that issues a code should show it");

        var value = code.Groups[1].Value.Trim();

        value.Should().MatchRegex("^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}(-[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}){3}$",
            "four groups of four, from an alphabet with no characters people misread aloud");

        // Read once. Coming back gives the list without it, because there is nothing left to give.
        var again = await client.GetStringAsync("/runners");
        again.Should().NotContain(value);

        using var scope = app.Services.CreateScope();
        var runner = await Db(scope.ServiceProvider).Runners.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.WorkspaceId == workspaceId);

        // A hash, and nothing that could produce the code again.
        runner.EnrollmentHash.Should().NotBeNullOrWhiteSpace();
        runner.EnrollmentHash.Should().NotContain(value);
        runner.EnrolledAt.Should().BeNull();
    }

    [Fact]
    public async Task Somebody_who_cannot_manage_runners_cannot_reach_the_page()
    {
        var (_, _, workspaceId) = await SignedInAsync();

        // A test designer builds and runs scenarios all day and never enrols a machine. Enrolling
        // one mints a credential that can claim this workspace's work from anywhere.
        var designer = await JoinAsync(workspaceId, WorkspaceRole.TestDesigner);

        var response = await designer.GetAsync("/runners");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Choosing_a_runner_on_an_environment_is_saved_against_it()
    {
        var (client, projectId, workspaceId) = await SignedInAsync();

        var runnerId = await RunnerAsync(workspaceId, "Office network");

        Guid environmentId;
        using (var scope = app.Services.CreateScope())
        {
            environmentId = await Db(scope.ServiceProvider).Environments.IgnoreQueryFilters()
                .Where(candidate => candidate.ProjectId == projectId)
                .Select(candidate => candidate.Id)
                .FirstAsync();
        }

        var page = $"/projects/{projectId}/environments";

        var saved = await client.PostAsync($"{page}/{environmentId}", await FormAsync(client, page,
            new Dictionary<string, string>
            {
                ["Name"] = "Staging",
                ["BaseUrl"] = "https://staging.internal",
                ["Kind"] = nameof(EnvironmentKind.Staging),
                ["TimeoutSeconds"] = "30",
                ["MaxRedirects"] = "5",
                ["MaxResponseKilobytes"] = "4096",
                ["RunnerId"] = runnerId.ToString(),
            }));

        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = app.Services.CreateScope())
        {
            var environment = await Db(scope.ServiceProvider).Environments.IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == environmentId);

            // The whole point of the page. Everything after this — the run copying it at queue
            // time, the agent claiming it, the package it is handed — has its own tests; what was
            // missing was any way for a person to make the choice at all.
            environment.RunnerId.Should().Be(runnerId);
        }

        // And it comes back selected, rather than the form quietly reverting to "this server" the
        // next time anybody opens it.
        var html = await client.GetStringAsync(page);

        html.Should().MatchRegex($"""value="{runnerId}"[^>]*selected""");
    }

    [Fact]
    public async Task A_revoked_runner_still_appears_on_the_environment_that_uses_it()
    {
        var (client, projectId, workspaceId) = await SignedInAsync();

        var runnerId = await RunnerAsync(workspaceId, "Retired host");

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);

            var environment = await db.Environments.IgnoreQueryFilters()
                .FirstAsync(candidate => candidate.ProjectId == projectId);

            environment.RunnerId = runnerId;

            var runner = await db.Runners.IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == runnerId);

            runner.RevokedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
        }

        var html = await client.GetStringAsync($"/projects/{projectId}/environments");

        // Dropping it from the list would clear the setting the next time anybody saved an
        // unrelated field on this form, and requests would quietly start going out from here.
        html.Should().Contain("Retired host");
        html.Should().Contain(runnerId.ToString());
    }

    // ---- scaffolding ----------------------------------------------------------------------------

    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    /// <summary>A runner created directly, for the tests that are about what happens next.</summary>
    private async Task<Guid> RunnerAsync(Guid workspaceId, string name)
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var runner = new Runner
        {
            WorkspaceId = workspaceId,
            Name = name,
            EnrollmentHash = "not-a-real-hash",
            EnrollmentExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };

        db.Runners.Add(runner);
        await db.SaveChangesAsync();

        return runner.Id;
    }

    private async Task<(HttpClient Client, Guid ProjectId, Guid WorkspaceId)> SignedInAsync()
    {
        var email = $"runner-{Guid.CreateVersion7():N}@proofflow.test";
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
                Name = "Runner workspace",
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
                Name = "Runner project",
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

    private async Task<HttpClient> JoinAsync(Guid workspaceId, WorkspaceRole role)
    {
        var email = $"member-{Guid.CreateVersion7():N}@proofflow.test";

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

            var user = new ProofFlowUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Colleague",
                LastWorkspaceId = workspaceId,
            };

            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

            var db = Db(scope.ServiceProvider);
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspaceId,
                UserId = user.Id,
                Role = role,
                JoinedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        var client = app.CreateClient(NoRedirect);
        await SignInAsync(client, email);

        return client;
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
