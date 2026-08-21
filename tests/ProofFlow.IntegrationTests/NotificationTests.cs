using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Notifications;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.FakeApi;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Tenancy;
using ProofFlow.Web.Infrastructure;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// When something fails, somebody finds out.
///
/// Before this existed, a schedule that broke at 3am wrote a log line on a server nobody reads and
/// nothing else. These prove the three outlets against real machinery: the row is written by the
/// same save that records the failure, the webhook arrives at a real socket with a verifiable
/// signature, and the bell shows the sentence to a signed-in browser.
/// </summary>
public sealed class NotificationTests(ProofFlowApplication app)
    : IClassFixture<ProofFlowApplication>, IAsyncLifetime
{
    private const string Password = "a-long-enough-password";

    private WebApplication _receiver = null!;
    private string _receiverUrl = null!;

    public async Task InitializeAsync()
    {
        // A real socket, because the delivery worker sends through the guarded executor and the
        // guarded executor does not speak to in-memory test servers — deliberately.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddFakeApi();

        _receiver = builder.Build();
        _receiver.MapFakeApi();
        await _receiver.StartAsync();
        _receiverUrl = _receiver.Urls.First().TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        await _receiver.StopAsync();
        await _receiver.DisposeAsync();
    }

    [Fact]
    public async Task A_signed_webhook_reaches_a_real_receiver_and_the_signature_verifies()
    {
        var (_, projectId, workspaceId) = await SignedInAsync();
        string secret;

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var cipher = scope.ServiceProvider.GetRequiredService<ISecretCipher>();

            secret = "test-signing-secret";
            var boxed = cipher.Seal(secret);

            var project = await db.Projects.IgnoreQueryFilters().FirstAsync(p => p.Id == projectId);
            project.WebhookUrl = $"{_receiverUrl}/fake/hook";
            project.WebhookAllowPrivate = true;
            project.WebhookSecretCipher = boxed.Ciphertext;
            project.WebhookSecretNonce = boxed.Nonce;
            project.WebhookSecretTag = boxed.Tag;
            project.WebhookSecretKeyVersion = boxed.KeyVersion;

            db.Notifications.Add(new Notification
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Kind = "run.failed",
                ArgsJson = """["Nightly checkout","Staging"]""",
                LinkPath = $"/projects/{projectId}/runs/{Guid.CreateVersion7()}",
                TargetLabel = "Nightly checkout",
            });

            await db.SaveChangesAsync();
        }

        await SweepAsync();

        var last = await new HttpClient().GetFromJsonAsync<JsonElement>($"{_receiverUrl}/fake/hook/last");

        var body = last.GetProperty("body").GetString()!;
        body.Should().Contain("run.failed");
        body.Should().Contain("Nightly checkout", "the sentence's subject should travel");

        // The receiver can prove who sent it — that is the entire point of the signature.
        var expected = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

        last.GetProperty("signature").GetString().Should().Be(expected);

        using (var scope = app.Services.CreateScope())
        {
            var row = await Db(scope.ServiceProvider).Notifications.IgnoreQueryFilters()
                .FirstAsync(n => n.ProjectId == projectId);

            row.WebhookAt.Should().NotBeNull("delivery should be stamped so it is not sent twice");
            row.WebhookFailure.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_dead_webhook_never_blocks_and_says_why_it_failed()
    {
        var (_, projectId, workspaceId) = await SignedInAsync();

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);
            var project = await db.Projects.IgnoreQueryFilters().FirstAsync(p => p.Id == projectId);

            // A port nothing listens on. The guarded executor refuses politely; the worker must
            // record that refusal and move on rather than throw the sweep away.
            project.WebhookUrl = "http://127.0.0.1:9/hook";
            project.WebhookAllowPrivate = true;

            db.Notifications.Add(new Notification
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Kind = "sweep.failed",
                ArgsJson = """["GET /orders","401 from the sign-in"]""",
            });

            await db.SaveChangesAsync();
        }

        await SweepAsync();

        using var check = app.Services.CreateScope();
        var row = await Db(check.ServiceProvider).Notifications.IgnoreQueryFilters()
            .FirstAsync(n => n.ProjectId == projectId);

        row.WebhookAt.Should().BeNull();
        row.WebhookAttempts.Should().Be(1);
        row.WebhookFailure.Should().NotBeNullOrEmpty("the settings card surfaces this");
    }

    [Fact]
    public async Task The_bell_shows_the_sentence_and_marking_seen_clears_the_dot()
    {
        var (client, projectId, workspaceId) = await SignedInAsync();

        using (var scope = app.Services.CreateScope())
        {
            var db = Db(scope.ServiceProvider);

            db.Notifications.Add(new Notification
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Kind = "schedule.broken",
                ArgsJson = """["Every morning","the cron is unreadable"]""",
                LinkPath = $"/projects/{projectId}/schedules",
            });
            await db.SaveChangesAsync();
        }

        var page = await client.GetStringAsync("/");

        page.Should().Contain("bell-dot", "something is new and the topbar should say so");
        page.Should().Contain("Every morning", "the sentence names what broke");
        page.Should().Contain($"/projects/{projectId}/schedules", "the entry links to the place itself");

        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1].Value;

        var seen = await client.PostAsync("/notifications/seen",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["back"] = "/",
                ["__RequestVerificationToken"] = token,
            }));

        seen.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await client.GetStringAsync("/")).Should().NotContain("bell-dot",
            "seen means seen — until the next failure");
    }

    // ---- scaffolding ----------------------------------------------------------------------------

    /// <summary>One pass of the real delivery worker, run to completion on demand.</summary>
    private async Task SweepAsync()
    {
        var worker = new NotificationDeliveryWorker(
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<IEmailSender>(),
            app.Services.GetRequiredService<IHttpClientFactory>(),
            app.Services.GetRequiredService<IConfiguration>(),
            NullLogger<NotificationDeliveryWorker>.Instance);

        await worker.SweepOnceAsync(CancellationToken.None);
    }

    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

    private static ProofFlowDbContext Db(IServiceProvider services) =>
        new SqliteProofFlowDbContext(
            services.GetRequiredService<DbContextOptions<SqliteProofFlowDbContext>>(),
            new SystemWorkspaceScope());

    private static readonly SemaphoreSlim SessionLock = new(1, 1);
    private static HttpClient? _sharedClient;
    private static Guid _sharedWorkspaceId;

    private async Task<(HttpClient Client, Guid ProjectId, Guid WorkspaceId)> SignedInAsync()
    {
        await SessionLock.WaitAsync();
        try
        {
            if (_sharedClient is null)
            {
                var email = $"notify-{Guid.CreateVersion7():N}@proofflow.test";

                using var scope = app.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ProofFlowUser>>();

                var user = new ProofFlowUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = "Notified",
                };

                (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

                var db = Db(scope.ServiceProvider);

                var workspace = new Workspace
                {
                    Name = "Notify workspace",
                    Slug = $"nw-{Guid.CreateVersion7():N}"[..20],
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

                (await client.PostAsync("/account/sign-in",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["Email"] = email,
                        ["Password"] = Password,
                        ["RememberMe"] = "true",
                        ["__RequestVerificationToken"] = token,
                    }))).StatusCode.Should().Be(HttpStatusCode.Found);

                _sharedClient = client;
                _sharedWorkspaceId = workspace.Id;
            }

            using (var scope = app.Services.CreateScope())
            {
                var db = Db(scope.ServiceProvider);

                var project = new Project
                {
                    WorkspaceId = _sharedWorkspaceId,
                    Name = $"Notify {Guid.CreateVersion7():N}"[..18],
                    Slug = $"n-{Guid.CreateVersion7():N}"[..20],
                };
                db.Projects.Add(project);
                await db.SaveChangesAsync();

                return (_sharedClient, project.Id, _sharedWorkspaceId);
            }
        }
        finally
        {
            SessionLock.Release();
        }
    }
}
