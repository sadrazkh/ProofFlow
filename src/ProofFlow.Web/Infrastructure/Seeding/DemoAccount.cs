using Microsoft.Extensions.Options;

namespace ProofFlow.Web.Infrastructure.Seeding;

/// <summary>
/// The account a fresh copy of this comes up with, and whether it is safe to say so.
///
/// One place, because two of them would disagree: the seeder decides what the password is and the
/// sign-in page prints it, and a page printing a password the seeder did not use is worse than a
/// page printing nothing.
///
/// «Safe to say so» means Development and nothing else. A known password is a convenience while
/// somebody is pressing F5 on their own machine and a back door everywhere else, so the printing,
/// the default, and the whole account are all held behind the same switch — and the switch is the
/// environment, which cannot be forgotten the way a setting can.
/// </summary>
public sealed class DemoAccount(IConfiguration configuration, IHostEnvironment host)
{
    /// <summary>What a development copy uses when nobody has chosen one.</summary>
    public const string DevelopmentPassword = "ProofFlow!Demo2026";

    public const string Email = DemoDataSeeder.DemoEmail;

    /// <summary>Whether a demo account is meant to exist at all.</summary>
    public bool Seeds => configuration.GetValue("Demo:Seed", false);

    /// <summary>
    /// The password the seeder will use, or null when it will refuse.
    ///
    /// Outside Development an empty setting stays empty: the seeder logs why it made nothing and
    /// the first account is the one somebody signs up for.
    /// </summary>
    public string? Password
    {
        get
        {
            var chosen = configuration["Demo:Password"];

            if (!string.IsNullOrWhiteSpace(chosen)) return chosen;

            return host.IsDevelopment() ? DevelopmentPassword : null;
        }
    }

    /// <summary>
    /// Whether the sign-in page may print it.
    ///
    /// Development only, and only when a password was not chosen deliberately: somebody who set one
    /// in user-secrets knows it, and printing it on a page is then all cost and no help.
    /// </summary>
    public bool Show =>
        host.IsDevelopment()
        && Seeds
        && string.IsNullOrWhiteSpace(configuration["Demo:Password"]);

    /// <summary>The colleagues, who share the same password. Shown so the roles can be tried.</summary>
    public static readonly (string Email, string Role)[] Colleagues =
    [
        ("reviewer@proofflow.local", "Reviewer"),
        ("designer@proofflow.local", "TestDesigner"),
        ("runner@proofflow.local", "Runner"),
        ("viewer@proofflow.local", "Viewer"),
    ];
}
