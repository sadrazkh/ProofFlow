using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The rules that make secret storage worth anything, exercised through the real stack.
///
/// A secret must never come back from a page that merely lists it; revealing one must be
/// impossible without the capability and impossible without leaving a trace; and widening what an
/// environment may reach must be visible afterwards. All three are the sort of thing that is true
/// when written and quietly stops being true three refactors later, so they are asserted against
/// real HTTP rather than against a controller's return value.
/// </summary>
public class EnvironmentAndSecretTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task A_stored_secret_is_never_readable_from_the_page_that_lists_it()
    {
        var (client, projectId, _) = await SignedInProjectAsync();
        await CreateSecretAsync(client, projectId, "apiToken", "sk-super-secret-value-1234");

        var html = await client.GetStringAsync($"/projects/{projectId}/environments");

        // The name is there, the last four characters are there, the value is not.
        html.Should().Contain("apiToken");
        html.Should().Contain("1234");
        html.Should().NotContain("sk-super-secret-value-1234");
    }

    [Fact]
    public async Task The_ciphertext_is_what_reaches_the_database()
    {
        var (client, projectId, _) = await SignedInProjectAsync();
        await CreateSecretAsync(client, projectId, "dbCheck", "plaintext-that-must-not-be-stored");

        using var scope = app.Services.CreateScope();
        var db = Db(scope.ServiceProvider);

        var secret = await db.Secrets.IgnoreQueryFilters()
            .SingleAsync(s => s.ProjectId == projectId && s.Name == "dbCheck");

        secret.Ciphertext.Should().NotContain("plaintext-that-must-not-be-stored");
        secret.Nonce.Should().NotBeEmpty();
        secret.Tag.Should().NotBeEmpty();
        secret.Preview.Should().Be("ored");

        // And it round-trips, so what happened is encryption rather than destruction.
        var cipher = scope.ServiceProvider.GetRequiredService<ISecretCipher>();
        cipher.Open(new SealedSecret(secret.Ciphertext, secret.Nonce, secret.Tag, secret.KeyVersion))
            .Should().Be("plaintext-that-must-not-be-stored");
    }

    [Fact]
    public async Task Revealing_a_secret_returns_the_value_and_records_it()
    {
        var (client, projectId, workspaceId) = await SignedInProjectAsync();
        var secretId = await CreateSecretAsync(client, projectId, "revealMe", "the-actual-value-here");

        var response = await client.PostAsync(
            $"/projects/{projectId}/environments/secrets/{secretId}/reveal",
            await TokenOnlyAsync(client, $"/projects/{projectId}/environments"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("value").GetString().Should().Be("the-actual-value-here");

        using var scope = app.Services.CreateScope();
        var entries = await Db(scope.ServiceProvider).AuditEvents.IgnoreQueryFilters()
            .Where(a => a.WorkspaceId == workspaceId && a.Action == "secret.revealed")
            .ToListAsync();

        // Written before the value was handed over, so a read is on the record even if the
        // response never arrived.
        entries.Should().ContainSingle();
        entries[0].TargetLabel.Should().Be("revealMe");
    }

    [Fact]
    public async Task A_member_without_the_capability_cannot_reveal_a_secret()
    {
        // Stored by the owner, then attempted by a colleague who builds tests. A test designer can
        // do almost everything in a project and deliberately cannot read a stored credential back.
        var (owner, projectId, workspaceId) = await SignedInProjectAsync();
        var secretId = await CreateSecretAsync(owner, projectId, "gated", "value-behind-a-gate");

        var designer = await JoinAsync(workspaceId, WorkspaceRole.TestDesigner);

        var response = await designer.PostAsync(
            $"/projects/{projectId}/environments/secrets/{secretId}/reveal",
            await TokenOnlyAsync(designer, $"/projects/{projectId}/environments"));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);

        using var scope = app.Services.CreateScope();
        var audits = await Db(scope.ServiceProvider).AuditEvents.IgnoreQueryFilters()
            .CountAsync(a => a.Action == "secret.revealed" && a.WorkspaceId == workspaceId);

        // Refused before anything was decrypted, so there is nothing to have recorded.
        audits.Should().Be(0);
    }

    [Fact]
    public async Task Turning_on_private_network_reach_is_recorded_with_what_changed()
    {
        var (client, projectId, workspaceId) = await SignedInProjectAsync();

        Guid environmentId;
        using (var scope = app.Services.CreateScope())
        {
            environmentId = (await Db(scope.ServiceProvider).Environments
                .IgnoreQueryFilters().FirstAsync(e => e.ProjectId == projectId)).Id;
        }

        var form = await FormAsync(client, $"/projects/{projectId}/environments", new Dictionary<string, string>
        {
            ["Name"] = "Staging",
            ["Kind"] = nameof(EnvironmentKind.Staging),
            ["BaseUrl"] = "https://staging.example.com",
            ["TimeoutSeconds"] = "30",
            ["MaxRedirects"] = "5",
            ["MaxResponseKilobytes"] = "4096",
            ["AllowPrivateNetwork"] = "true",
        });

        var response = await client.PostAsync($"/projects/{projectId}/environments/{environmentId}", form);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var check = app.Services.CreateScope();
        var entry = await Db(check.ServiceProvider).AuditEvents.IgnoreQueryFilters()
            .Where(a => a.WorkspaceId == workspaceId && a.Action == "environment.updated")
            .OrderByDescending(a => a.OccurredAt)
            .FirstAsync();

        // The detail is what makes the entry useful: "something changed" does not tell a reviewer
        // that the blast radius widened.
        entry.DetailsJson.Should().NotBeNull();
        entry.DetailsJson.Should().Contain("allowPrivateNetwork");
        entry.DetailsJson.Should().Contain("true");
    }

    [Fact]
    public async Task A_request_with_an_unknown_reference_is_refused_before_it_is_sent()
    {
        var (client, projectId, _) = await SignedInProjectAsync();

        var response = await client.PostAsJsonAsync($"/projects/{projectId}/request/send", new
        {
            environmentId = (Guid?)null,
            method = "GET",
            url = "https://example.com/x?token={{secrets.doesNotExist}}",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        payload.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        payload.GetProperty("failureKind").GetString().Should().Be("Unresolved");

        // And it says which one, so the person is not left comparing their typing to a list.
        payload.GetProperty("unresolved").GetArrayLength().Should().Be(1);
        payload.GetProperty("unresolved")[0].GetProperty("reference").GetString()
            .Should().Contain("doesNotExist");
    }

    [Fact]
    public async Task The_variable_names_endpoint_hands_back_names_and_never_values()
    {
        var (client, projectId, _) = await SignedInProjectAsync();
        await CreateSecretAsync(client, projectId, "topSecret", "the-value-nobody-should-see");

        var json = await client.GetStringAsync($"/projects/{projectId}/request/variables");

        json.Should().Contain("topSecret");
        json.Should().NotContain("the-value-nobody-should-see");
    }

    // ---- plumbing -----------------------------------------------------------------------------

    /// <summary>
    /// Over https, and not incidentally.
    ///
    /// Outside development the session cookie is marked Secure, so it is never offered over plain
    /// http — correct, and the reason a test client on http://localhost signs in successfully and
    /// then arrives at the next page as an anonymous visitor. Using https here keeps the production
    /// cookie policy under test rather than switching the host to development to dodge it.
    /// </summary>
    private static readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    /// <summary>
    /// A context outside any workspace, for assertions the test never signed a request into.
    /// </summary>
    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    /// <summary>
    /// An account, a workspace and a project, then a real sign-in.
    ///
    /// The account is created through the services rather than the sign-up form, because only the
    /// very first sign-up in a database may open a workspace — everyone after that arrives by
    /// invitation. Tests that each signed up were only ever going to work one at a time, and did.
    /// </summary>
    private async Task<(HttpClient Client, Guid ProjectId, Guid WorkspaceId)> SignedInProjectAsync(
        WorkspaceRole role = WorkspaceRole.Owner)
    {
        var email = $"env-{Guid.CreateVersion7():N}@proofflow.test";
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

            var created = await users.CreateAsync(user, Password);
            created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

            var db = Db(scope.ServiceProvider);

            var workspace = new Workspace
            {
                Name = "Test workspace",
                Slug = $"ws-{Guid.CreateVersion7():N}"[..20],
                CreatedByUserId = user.Id,
            };
            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = role,
                JoinedAt = DateTimeOffset.UtcNow,
            });

            var project = new Project
            {
                WorkspaceId = workspace.Id,
                Name = "Test project",
                Slug = $"p-{Guid.CreateVersion7():N}"[..20],
            };
            db.Projects.Add(project);
            db.Environments.Add(new ProjectEnvironment
            {
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Name = "Staging",
                Slug = "staging",
                BaseUrl = "https://staging.example.com",
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

    /// <summary>Adds a second member to an existing workspace and signs them in.</summary>
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

        var response = await client.PostAsync("/account/sign-in", form);

        // A 200 means the form came back with an error rather than signing in, and every assertion
        // after that would be about an anonymous session.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "sign-in should succeed");
    }

    private async Task<Guid> CreateSecretAsync(HttpClient client, Guid projectId, string name, string value)
    {
        var form = await FormAsync(client, $"/projects/{projectId}/environments", new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Value"] = value,
        });

        var response = await client.PostAsync($"/projects/{projectId}/environments/secrets", form);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = app.Services.CreateScope();
        return (await Db(scope.ServiceProvider).Secrets.IgnoreQueryFilters()
            .SingleAsync(s => s.ProjectId == projectId && s.Name == name)).Id;
    }

    /// <summary>
    /// Builds a form with a live antiforgery token, read from a page that renders one.
    ///
    /// Never bypassed: a test that posts without it is a test that would keep passing after CSRF
    /// protection was removed.
    /// </summary>
    private static async Task<FormUrlEncodedContent> FormAsync(
        HttpClient client, string tokenPage, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await ReadTokenAsync(client, tokenPage);
        return new FormUrlEncodedContent(fields);
    }

    private static async Task<FormUrlEncodedContent> TokenOnlyAsync(HttpClient client, string tokenPage) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await ReadTokenAsync(client, tokenPage),
        });

    private static async Task<string> ReadTokenAsync(HttpClient client, string page)
    {
        var response = await client.GetAsync(page);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"{page} should render for a signed-in member rather than redirect");

        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        match.Success.Should().BeTrue($"{page} should render an antiforgery token");
        return match.Groups[1].Value;
    }
}
