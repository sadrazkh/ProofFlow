using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Workspaces;

namespace ProofFlow.Web.Infrastructure.Seeding;

/// <summary>
/// The demo workspace: a real account, real projects, and later a real scenario that really runs.
///
/// Gated behind <c>Demo:Seed</c> and off unless it is switched on. A seeded account with a known
/// password is a back door, and one that appears by default in production is a back door nobody
/// decided to open.
///
/// Nothing here fabricates data that only looks real. No invented response hashes, no plausible
/// run history — those pass every test and then fail the first time something compares them
/// against an actual response. What the demo contains, it can produce.
/// </summary>
public sealed class DemoDataSeeder(
    ProofFlowDbContext db,
    UserManager<ProofFlowUser> users,
    IConfiguration configuration,
    IClock clock,
    ILogger<DemoDataSeeder> logger)
{
    public const string DemoEmail = "demo@proofflow.local";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Demo:Seed", false)) return;

        if (await db.Users.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Demo seeding skipped: this database already has accounts.");
            return;
        }

        var password = configuration["Demo:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            // No default. A well-known password that ships in the source is the same as no
            // password, and the first demo instance left on a public address proves it.
            logger.LogWarning(
                "Demo:Seed is on but Demo:Password is empty, so no demo account was created. " +
                "Set Demo:Password (user-secrets or environment) to seed one.");
            return;
        }

        var user = new ProofFlowUser
        {
            Id = Guid.CreateVersion7(),
            UserName = DemoEmail,
            Email = DemoEmail,
            EmailConfirmed = true,
            DisplayName = "Demo",
            CreatedAt = clock.UtcNow,
        };

        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            logger.LogWarning("The demo account was not created: {Errors}.",
                string.Join("; ", created.Errors.Select(e => e.Description)));
            return;
        }

        var workspace = new Workspace
        {
            Name = "Demo workspace",
            Slug = "demo",
            CreatedByUserId = user.Id,
        };

        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            JoinedAt = clock.UtcNow,
        });

        // Demo content is data, not interface text, so it cannot come from the translation
        // catalogue and switch with the reader — a project description is a column. It is seeded
        // in one language, and which one is a setting rather than an assumption, because English
        // paragraphs sitting inside a Persian panel is exactly the failure the whole localisation
        // effort exists to prevent.
        var persian = (configuration["Demo:Culture"] ?? "fa")
            .StartsWith("fa", StringComparison.OrdinalIgnoreCase);

        foreach (var demo in DemoProjects)
        {
            var project = new Project
            {
                WorkspaceId = workspace.Id,
                Name = demo.Name,
                Slug = Slug.From(demo.Name, "project"),
                Description = persian ? demo.Persian : demo.English,
                Accent = demo.Accent,
                CreatedByUserId = user.Id,
            };

            db.Projects.Add(project);
            Environments(workspace.Id, project);
        }

        await ColleaguesAsync(workspace.Id, password, persian);

        await db.SaveChangesAsync(cancellationToken);

        user.LastWorkspaceId = workspace.Id;
        await users.UpdateAsync(user);

        logger.LogInformation(
            "Seeded the demo workspace, the account {Email}, and {Count} colleagues who share the "
            + "same demo password.",
            DemoEmail, Colleagues.Length);
    }

    /// <summary>
    /// The rest of the team, and one invitation nobody has taken up.
    ///
    /// Without them there is nothing of this to see. The separation of author and approver only
    /// binds when there is somebody else to approve, so a workspace of one demonstrates the
    /// exception rather than the rule; and a team page with a single row shows neither a role
    /// change nor a removal.
    ///
    /// They share <c>Demo:Password</c> rather than getting one each. A demo workspace is already
    /// gated behind a switch and a password somebody had to choose, and adding four more secrets to
    /// that arrangement makes it harder to reason about rather than safer.
    /// </summary>
    private async Task ColleaguesAsync(Guid workspaceId, string password, bool persian)
    {
        foreach (var colleague in Colleagues)
        {
            var account = new ProofFlowUser
            {
                Id = Guid.CreateVersion7(),
                UserName = colleague.Email,
                Email = colleague.Email,
                EmailConfirmed = true,
                DisplayName = persian ? colleague.Persian : colleague.English,
                CreatedAt = clock.UtcNow,
            };

            var made = await users.CreateAsync(account, password);

            if (!made.Succeeded)
            {
                logger.LogWarning("The demo colleague {Email} was not created: {Errors}.",
                    colleague.Email, string.Join("; ", made.Errors.Select(e => e.Description)));
                continue;
            }

            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspaceId,
                UserId = account.Id,
                Role = colleague.Role,
                InvitedAt = clock.UtcNow,
                JoinedAt = clock.UtcNow,
            });
        }

        // One invitation left open, so the page has something to withdraw. Its token is generated
        // and thrown away rather than kept: only the hash is stored, here as everywhere, and a
        // demo that quietly held on to the plain text would be demonstrating the opposite of the
        // rule it is meant to show.
        db.WorkspaceInvitations.Add(new WorkspaceInvitation
        {
            WorkspaceId = workspaceId,
            Email = "newcomer@proofflow.local",
            Role = WorkspaceRole.Viewer,
            Hash = TeamService.Fingerprint(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TeamService.TokenBytes))),
            ExpiresAt = clock.UtcNow + WorkspaceInvitation.Lifetime,
        });
    }

    /// <summary>
    /// Four roles, chosen so every rule in this part of the product has something to act on: a
    /// reviewer who may approve, a designer who may record but not approve, somebody who may only
    /// press Run, and somebody who may only read.
    /// </summary>
    private static readonly DemoColleague[] Colleagues =
    [
        new("reviewer@proofflow.local", WorkspaceRole.Reviewer, "Rosa Klein", "رؤیا بهرامی"),
        new("designer@proofflow.local", WorkspaceRole.TestDesigner, "Ada Okonkwo", "کیان رستمی"),
        new("runner@proofflow.local", WorkspaceRole.Runner, "Tomas Vega", "سارا نوری"),
        new("viewer@proofflow.local", WorkspaceRole.Viewer, "Mei Lin", "بهرام کاویانی"),
    ];

    private sealed record DemoColleague(
        string Email, WorkspaceRole Role, string English, string Persian);

    /// <summary>
    /// Three environments per project, because comparing them is what the product is for.
    ///
    /// At least one has to answer or nothing in the demo can run: almost every scenario's first
    /// step reads <c>{{environment.baseUrl}}</c>, and a workspace where the first test somebody
    /// presses Run on fails with "environment has no value" teaches them the product is broken.
    ///
    /// Local and Staging both point at this application's own fake API. That is not laziness — it
    /// is what makes a comparison between them show something rather than two columns of failures,
    /// and it is exactly the shape of a blue/green pair. Production is a placeholder that does not
    /// resolve, and that is deliberate too: a demo workspace must not have a working route to
    /// anything anybody could mistake for real, and a column that cannot be reached is a column
    /// that demonstrates the guard.
    ///
    /// Both reachable ones are on loopback, which the URL guard refuses by default — so they say so
    /// explicitly, which is the clearest place anybody will see that setting demonstrated.
    /// </summary>
    private void Environments(Guid workspaceId, Project project)
    {
        var fake = configuration["Demo:BaseUrl"] ?? "http://localhost:5290/fake";

        db.Environments.Add(new ProjectEnvironment
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Local",
            Slug = "local",
            BaseUrl = fake,
            Kind = EnvironmentKind.Local,
            AllowPrivateNetwork = true,
            SortOrder = 0,
        });

        db.Environments.Add(new ProjectEnvironment
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Staging",
            Slug = "staging",
            BaseUrl = fake,
            Kind = EnvironmentKind.Staging,
            AllowPrivateNetwork = true,
            SortOrder = 1,
        });

        db.Environments.Add(new ProjectEnvironment
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Production",
            Slug = "production",
            BaseUrl = "https://production.example.test",
            Kind = EnvironmentKind.Production,
            IsProduction = true,
            SortOrder = 2,
        });
    }

    /// <summary>
    /// The names stay in English — they are the names of systems, and a team testing an API called
    /// "Catalog API" calls it that in either language. Only the prose is translated.
    /// </summary>
    private static readonly DemoProject[] DemoProjects =
    [
        new("Catalog API", "violet",
            English: "Products, categories and the dynamic fields a category defines.",
            Persian: "محصولات، دسته‌ها، و فیلدهای پویایی که هر دسته تعریف می‌کند."),
        new("Orders API", "teal",
            English: "Checkout, payment callbacks and order history.",
            Persian: "تسویه، بازگشت‌های پرداخت، و تاریخچه سفارش."),
        new("Identity API", "amber",
            English: "Sign-in, token refresh and the endpoints behind them.",
            Persian: "ورود، تازه‌سازی توکن، و Endpointهای پشت آن‌ها."),
    ];

    private sealed record DemoProject(string Name, string Accent, string English, string Persian);
}
