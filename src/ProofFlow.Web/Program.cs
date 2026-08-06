using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.Infrastructure.Localization;

var builder = WebApplication.CreateBuilder(args);

// stdout is the operational log. The Windows EventLog provider, added by default on this platform,
// throws under a non-administrator account while trying to register its source — which turns a
// harmless warning into a fatal startup failure on a developer machine.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

// ---- persistence, clock, audit -----------------------------------------------------------------
// The scope and the current user come first: AddProofFlowInfrastructure builds a DbContext that
// takes IWorkspaceScope in its constructor, and the tenant filter is not something to default.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IWorkspaceScope, HttpWorkspaceScope>();
builder.Services.AddProofFlowInfrastructure(builder.Configuration);

// ---- identity -----------------------------------------------------------------------------------
// AddIdentityCore rather than AddIdentity: authorisation here is by workspace membership, so
// Identity's role tables would sit empty and invite a second, disagreeing source of truth.
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies(options =>
    {
        options.ApplicationCookie?.Configure(cookie =>
        {
            cookie.Cookie.Name = "proofflow.auth";
            cookie.LoginPath = "/account/sign-in";
            cookie.LogoutPath = "/account/sign-out";
            cookie.AccessDeniedPath = "/account/denied";
            cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
            cookie.SlidingExpiration = true;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            // Production terminates TLS in front of this process, so the session cookie must never
            // be offered over plain HTTP. Development runs on localhost and keeps the lenient rule.
            cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            // An unauthenticated API call should get 401, not a redirect to an HTML sign-in page
            // that the caller will then try to parse as JSON.
            cookie.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });
    });

builder.Services.AddIdentityCore<ProofFlowUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        // Length beats composition rules: they push people towards Passw0rd! and away from a
        // passphrase, which is both easier to remember and far harder to guess.
        options.Lockout.MaxFailedAccessAttempts = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ProofFlowDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddCapabilityAuthorization();

// ---- localisation (fa/en, RTL/LTR) --------------------------------------------------------------
builder.Services.AddSingleton<JsonTranslations>();
builder.Services.AddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>();
builder.Services.AddSingleton<IStringLocalizer>(sp =>
    new JsonStringLocalizer(sp.GetRequiredService<JsonTranslations>()));

var supportedCultures = new[] { new CultureInfo("fa"), new CultureInfo("en") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("fa");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    // The explicit choice wins over the browser's guess, which is right for anyone who works in
    // one language and reads documentation in another.
    options.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider());
});

builder.Services.AddControllersWithViews()
    .AddViewLocalization()
    // Pointed at the shared catalogue. The framework default looks for a resource file per model,
    // which nobody creates, so every validation message falls back to English on a Persian form.
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(string.Empty, string.Empty));

builder.Services.AddSignalR();
builder.Services.AddSingleton<ViteManifest>();
builder.Services.AddScoped<WorkspaceContextFilter>();
builder.Services.AddScoped<ProofFlow.Web.Infrastructure.Seeding.DemoDataSeeder>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "proofflow.csrf";
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// Credential stuffing is a volume game, so sign-in is metered by address rather than by account —
// metering by account hands an attacker a way to lock a victim out of their own tool.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static Func<HttpContext, RateLimitPartition<string>> PerAddress(int permitsPerMinute) => context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = permitsPerMinute,
                QueueLimit = 0,
            });

    options.AddPolicy("auth", PerAddress(12));
});

// Auth cookies and antiforgery tokens are encrypted with the Data Protection keyring. Left in its
// default location it lives inside the container, so every image rebuild signs everyone out
// mid-session with antiforgery failures on the way out.
var keyRing = builder.Configuration["ProofFlow:DataProtectionKeysPath"]
              ?? Path.Combine(AppContext.BaseDirectory, "keys");
try
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(Directory.CreateDirectory(keyRing))
        .SetApplicationName("ProofFlow");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"⚠ Data Protection keys stay in their default location ({ex.Message}).");
}

var app = builder.Build();

// ---- schema and demo data on boot --------------------------------------------------------------
// Startup failures must exit rather than throw into the void: a process that stays alive with a
// broken database answers every request with 502 while the restart policy never fires, because
// from the outside it is still running.
try
{
    using var scope = app.Services.CreateScope();
    var settings = scope.ServiceProvider.GetRequiredService<DatabaseSettings>();
    var db = scope.ServiceProvider.GetRequiredService<ProofFlowDbContext>();

    var autoMigrate = app.Configuration.GetValue("Database:AutoMigrate", settings.IsSqlite);
    if (autoMigrate)
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        // Off by default for PostgreSQL: with more than one instance behind a load balancer, two
        // of them applying DDL at once is worse than a refusal to start.
        var pending = await db.Database.GetPendingMigrationsAsync();
        if (pending.Any())
            throw new InvalidOperationException(
                $"The database is {pending.Count()} migration(s) behind. Apply them with " +
                "`dotnet ef database update`, or set Database:AutoMigrate=true for a single-instance install.");
    }

    await scope.ServiceProvider.GetRequiredService<ProofFlow.Web.Infrastructure.Seeding.DemoDataSeeder>()
        .SeedAsync();
}
catch (Exception ex)
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    log.LogCritical(ex, "ProofFlow could not start.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("✗ ProofFlow could not start — " + ex.Message);
    return 1;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error/500");
    app.UseHsts();
}

// A bare 404 is a blank page with a status code. Re-executing into the themed page keeps the real
// status on the response while giving the reader something to act on.
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        return Task.CompletedTask;
    });
    await next();
});

app.UseRequestLocalization(app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>().Value);

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapControllers();
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");

await app.RunAsync();
return 0;

/// <summary>Named so the integration tests can build a host from this exact configuration.</summary>
public partial class Program;
