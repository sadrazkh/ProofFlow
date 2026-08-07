using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;

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

        foreach (var project in DemoProjects)
        {
            db.Projects.Add(new Project
            {
                WorkspaceId = workspace.Id,
                Name = project.Name,
                Slug = Slug.From(project.Name, "project"),
                Description = persian ? project.Persian : project.English,
                Accent = project.Accent,
                CreatedByUserId = user.Id,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        user.LastWorkspaceId = workspace.Id;
        await users.UpdateAsync(user);

        logger.LogInformation("Seeded the demo workspace and signed-in account {Email}.", DemoEmail);
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
