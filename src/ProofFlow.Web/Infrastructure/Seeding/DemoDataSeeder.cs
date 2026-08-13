using ProofFlow.Domain.Scenarios;
using ProofFlow.Infrastructure.Scenarios;
using ProofFlow.Contracts.Scenarios;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Workspaces;
using ProofFlow.TestEngine.Http;

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
    ISecretCipher cipher,
    ScenarioGraphService graphs,
    DemoAccount account,
    IConfiguration configuration,
    IClock clock,
    ILogger<DemoDataSeeder> logger)
{
    public const string DemoEmail = "demo@proofflow.local";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!account.Seeds) return;

        if (await db.Users.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Demo seeding skipped: this database already has accounts.");
            return;
        }

        var password = account.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            // Outside development there is no default. A well-known password that ships in the
            // source is the same as no password, and the first demo instance left on a public
            // address proves it. In development there is one, and the sign-in page prints it,
            // because the cost of that is a machine somebody is sitting at.
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

        // Demo content is data, not interface text: a project description is a column, so it is
        // written once and read by everybody whatever language they chose.
        //
        // Which makes English the right default rather than an Anglocentric one. The names beside
        // these descriptions are «Catalog API» and «Orders API» — names of systems, untranslated
        // by the same rule — and Persian prose beside an English name reads as broken in both
        // languages. English beside English reads as sample data, which is what it is. The setting
        // is still there for a demo being shown to a Persian-speaking room.
        var persian = (configuration["Demo:Culture"] ?? "en")
            .StartsWith("fa", StringComparison.OrdinalIgnoreCase);

        (Project Project, ProjectEnvironment Local, ProjectEnvironment Staging)? first = null;

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

            var (local, staging) = Environments(workspace.Id, project);
            first ??= (project, local, staging);
        }

        await ColleaguesAsync(workspace.Id, password, persian);

        await db.SaveChangesAsync(cancellationToken);

        if (first is { } start)
        {
            await FlowAsync(
                workspace.Id, user.Id, start.Project, start.Local, start.Staging, cancellationToken);
        }

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
    private (ProjectEnvironment Local, ProjectEnvironment Staging) Environments(
        Guid workspaceId, Project project)
    {
        var fake = configuration["Demo:BaseUrl"] ?? "http://localhost:5290/fake";

        var local = new ProjectEnvironment
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Local",
            Slug = "local",
            BaseUrl = fake,
            Kind = EnvironmentKind.Local,
            AllowPrivateNetwork = true,
            SortOrder = 0,
        };

        db.Environments.Add(local);

        var staging = new ProjectEnvironment
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Staging",
            Slug = "staging",
            BaseUrl = fake,
            Kind = EnvironmentKind.Staging,
            AllowPrivateNetwork = true,
            SortOrder = 1,
        };

        db.Environments.Add(staging);

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

        return (local, staging);
    }


    /// <summary>
    /// One complete test, drawn and ready to run.
    ///
    /// The demo used to hand somebody three empty projects and a canvas, which shows what the
    /// product is made of and not what it is for. This is the shape of a real test: sign in, keep
    /// the token, read a list, take an id out of it, read that one thing, and check the answer.
    ///
    /// It uses every part deliberately — a secret for the password, an input for the page to read,
    /// a variable for the size of it, a step reading an earlier step's response — so that opening it
    /// answers "how do I refer to that" for each of them by example rather than by documentation.
    /// </summary>
    private async Task FlowAsync(
        Guid workspaceId, Guid userId, Project project,
        ProjectEnvironment local, ProjectEnvironment staging,
        CancellationToken cancellation)
    {
        // The fake API's own login takes these, so the flow signs in for real rather than
        // pretending to. The password is a secret because that is what a password is, and the
        // scenario refers to it by name — which is the thing worth showing.
        var sealedPassword = cipher.Seal("demo-password");

        // One per environment, under the same name.
        //
        // That is what a secret is for — the step says {{secrets.apiPassword}} and each environment
        // answers with its own — and it is also the thing that was missing. Only Local had one, so
        // running the demo scenario against Staging failed at the first step, every step after it
        // failed for want of a token, and a matrix comparing the two had no step that reached the
        // server on both sides. Three of four cells red is a demonstration of a broken setup.
        foreach (var environment in new[] { local, staging })
        {
            db.Secrets.Add(new Secret
            {
                WorkspaceId = workspaceId,
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
                Name = "apiPassword",
                Description = "The demo API password. Written in a step as {{secrets.apiPassword}}.",
                Ciphertext = sealedPassword.Ciphertext,
                Nonce = sealedPassword.Nonce,
                Tag = sealedPassword.Tag,
                KeyVersion = sealedPassword.KeyVersion,
                Preview = "word",
                CreatedByUserId = userId,
            });
        }

        db.Variables.Add(new EnvironmentVariable
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            EnvironmentId = null,
            Name = "pageSize",
            Value = "5",
            Description = "How many rows a page holds. The same for every run, so a variable.",
        });

        var scenario = new TestScenario
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Add a product, read it back, and clear up",
            Description =
                "The whole shape of a test: sign in, keep the token, add something, read the list, "
                + "take an id out of it, read that one, and put the API back the way it was. "
                + "Change anything and press Run.",
            EnvironmentId = local.Id,
            CreatedByUserId = userId,

            // Answered per run, and by a build agent posting to the API. The default is what makes
            // pressing Run without thinking about it work.
            InputsJson = ScenarioInputs.Write(
            [
                new ScenarioInputDto
                {
                    Name = "productName",
                    Label = "Product name",
                    Description = "What the product this run adds is called.",
                    Default = "Demo widget",
                    Required = true,
                },
                new ScenarioInputDto
                {
                    Name = "page",
                    Label = "Page",
                    Description = "Which page of products to read.",
                    Default = "1",
                    Required = true,
                },
            ]),
        };

        db.Scenarios.Add(scenario);
        await db.SaveChangesAsync(cancellation);

        await graphs.SaveAsync(scenario, DemoGraph(), cancellation);
        await db.SaveChangesAsync(cancellation);

        logger.LogInformation("Seeded a complete scenario: {Name}.", scenario.Name);

        await EndpointAsync(workspaceId, userId, project, local, cancellation);
    }

    /// <summary>
    /// One endpoint beside the one chain.
    ///
    /// The product does two things and a workspace that showed only one of them taught half of it.
    /// A chain — sign in, keep the token, read a list, take an id out of it — is what the canvas is
    /// for; a single call kept and compared is what most people arrive wanting. Both are on the
    /// first screen somebody sees now.
    ///
    /// No approved answer, deliberately. Recording one here would mean the seeder deciding what
    /// correct looks like, and the whole point of the page is the moment somebody sends it once,
    /// reads what came back, and agrees. The endpoint page says so where the answer would be.
    /// </summary>
    private async Task EndpointAsync(
        Guid workspaceId, Guid userId, Project project, ProjectEnvironment local,
        CancellationToken cancellation)
    {
        var request = new HttpRequestDefinition
        {
            Method = "GET",
            Url = "{{environment.baseUrl}}/records/1",
        };

        db.Baselines.Add(new Baseline
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            EnvironmentId = local.Id,
            Name = "One record",
            Description =
                "A single call, kept. Press Test to send it again and find out whether the answer "
                + "moved. Give it a set of inputs and the same button sweeps every row.",
            RequestJson = JsonSerializer.Serialize(
                request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            CreatedByUserId = userId,
        });

        await db.SaveChangesAsync(cancellation);

        logger.LogInformation("Seeded an endpoint: One record.");
    }

    /// <summary>
    /// The graph, laid out left to right and wrapped, so it opens readable rather than as a hairline.
    ///
    /// Four steps to a row rather than twelve in one: fitted to a canvas with the palette and the
    /// inspector open, a single row of twelve opens at a fifth of full size, which is a diagram
    /// nobody can read without panning first.
    ///
    /// Written out rather than generated: this is the one scenario somebody reads before they trust
    /// the product, and every property in it is there to answer a question they are about to have.
    /// </summary>
    private static GraphDto DemoGraph() => new()
    {
        Nodes =
        [
            Node("n1", "core.start", "Start", 60, 140),

            Node("n2", "http.request", "Sign in", 360, 140, new()
            {
                ["method"] = "POST",
                ["url"] = "{{environment.baseUrl}}/auth/login",
                ["bodyKind"] = "json",
                ["body"] = """{"username":"demo","password":"{{secrets.apiPassword}}"}""",
            }),

            Node("n3", "assert.status", "It let us in", 660, 140, new() { ["expected"] = "200" }),

            // Adds its own row rather than assuming one is there. A demo that depends on data
            // somebody else left behind is a demo that fails the second time it is run.
            Node("n4", "http.request", "Add a product", 960, 140, new()
            {
                ["method"] = "POST",
                ["url"] = "{{environment.baseUrl}}/products",
                ["bodyKind"] = "json",
                ["body"] = """{"name":"{{inputs.productName}}","categoryId":11,"price":49.9}""",
                ["headers"] = Bearer,
            }),

            Node("n5", "assert.status", "It was created", 60, 380, new() { ["expected"] = "201" }),

            Node("n6", "http.request", "Read a page of products", 360, 380, new()
            {
                ["method"] = "GET",
                ["url"] = "{{environment.baseUrl}}/products?page={{inputs.page}}&pageSize={{vars.pageSize}}",
                ["bodyKind"] = "none",
                ["headers"] = Bearer,
            }),

            Node("n7", "assert.status", "The page came back", 660, 380, new() { ["expected"] = "200" }),

            Node("n8", "http.request", "Read the first one", 960, 380, new()
            {
                ["method"] = "GET",
                ["url"] = "{{environment.baseUrl}}/products/{{steps.Read a page of products.response.body.items[0].id}}",
                ["bodyKind"] = "none",
                ["headers"] = Bearer,
            }),

            Node("n9", "assert.status", "And so did it", 60, 620, new() { ["expected"] = "200" }),

            // The step that makes this runnable twice. It deletes what this run added, by the id the
            // API gave back — not by the one the list happened to start with.
            Node("n10", "http.request", "Take it away again", 360, 620, new()
            {
                ["method"] = "DELETE",
                ["url"] = "{{environment.baseUrl}}/products/{{steps.Add a product.response.body.id}}",
                ["bodyKind"] = "none",
                ["headers"] = Bearer,
            }),

            Node("n11", "assert.status", "It is gone", 660, 620, new() { ["expected"] = "204" }),

            Node("n12", "core.end", "Done", 960, 620),
        ],

        Edges =
        [
            Edge("n1", "out", "n2", "in"),
            Edge("n2", "out", "n3", "in"),
            Edge("n2", "response", "n3", "response"),
            Edge("n3", "out", "n4", "in"),
            Edge("n4", "out", "n5", "in"),
            Edge("n4", "response", "n5", "response"),
            Edge("n5", "out", "n6", "in"),
            Edge("n6", "out", "n7", "in"),
            Edge("n6", "response", "n7", "response"),
            Edge("n7", "out", "n8", "in"),
            Edge("n8", "out", "n9", "in"),
            Edge("n8", "response", "n9", "response"),
            Edge("n9", "out", "n10", "in"),
            Edge("n10", "out", "n11", "in"),
            Edge("n10", "response", "n11", "response"),
            Edge("n11", "out", "n12", "in"),
        ],
    };

    /// <summary>
    /// The header every step after the sign-in carries.
    ///
    /// Written once because it is the same sentence four times, and four copies of a reference is
    /// four places to fix when somebody renames the step it points at.
    /// </summary>
    private const string Bearer =
        """[{"name":"Authorization","value":"Bearer {{steps.Sign in.response.body.accessToken}}"}]""";

    private static GraphNodeDto Node(
        string id, string key, string name, int x, int y,
        Dictionary<string, string?>? properties = null) => new()
    {
        Id = id,
        Key = key,
        Name = name,
        X = x,
        Y = y,
        Properties = properties ?? [],
    };

    // The id is the edge, spelled out. The save gives it a real one; this only has to be unique
    // within the graph so the validator can tell two connections apart.
    private static GraphEdgeDto Edge(string from, string fromPort, string to, string toPort) =>
        new()
        {
            Id = $"{from}:{fromPort}->{to}:{toPort}",
            FromId = from,
            FromPort = fromPort,
            ToId = to,
            ToPort = toPort,
        };

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
