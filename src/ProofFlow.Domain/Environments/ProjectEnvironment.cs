using ProofFlow.Domain.Common;
using ProofFlow.Domain.Projects;

namespace ProofFlow.Domain.Environments;

/// <summary>
/// One place the system under test is running: local, staging, production, or anything a team
/// calls its own.
///
/// Named <c>ProjectEnvironment</c> rather than <c>Environment</c> because the shorter name collides
/// with <see cref="System.Environment"/>, and a domain type that needs a namespace qualifier at
/// every call site is a type people work around.
/// </summary>
public class ProjectEnvironment : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public required string Name { get; set; }

    /// <summary>Stable within the project. Referenced by exports and by CI invocations.</summary>
    public required string Slug { get; set; }

    /// <summary>
    /// What <c>{{environment.baseUrl}}</c> resolves to. Stored without a trailing slash so joining
    /// a path never produces a double one.
    /// </summary>
    public string? BaseUrl { get; set; }

    public EnvironmentKind Kind { get; set; } = EnvironmentKind.Custom;

    /// <summary>Headers sent with every request made against this environment, as a JSON object.</summary>
    public string? DefaultHeadersJson { get; set; }

    /// <summary>
    /// Authentication applied to every request unless a step overrides it, as JSON. Any secret
    /// value inside is a <c>{{secrets.name}}</c> reference, never a literal.
    /// </summary>
    public string? AuthenticationJson { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRedirects { get; set; } = 5;

    /// <summary>Cap on a response body ProofFlow will read, in kilobytes.</summary>
    public int MaxResponseKilobytes { get; set; } = 4096;

    /// <summary>
    /// Hosts this environment is permitted to reach, one per line. Empty means "only the host of
    /// the base URL" — the safe reading, because an environment that may call anywhere is a
    /// server-side request forgery waiting for someone to paste a URL into a step.
    /// </summary>
    public string? AllowedHosts { get; set; }

    /// <summary>
    /// Whether requests may reach loopback and private ranges.
    ///
    /// Off by default and audited when turned on. On a hosted installation this is what separates
    /// "test my API" from "read the cloud metadata endpoint and hand me the credentials".
    /// </summary>
    public bool AllowPrivateNetwork { get; set; }

    /// <summary>An outbound proxy, if this environment is only reachable through one.</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>
    /// Whether to accept a certificate that does not validate.
    ///
    /// Exists because staging environments genuinely run with self-signed certificates, and a tool
    /// that cannot reach them is a tool nobody uses. Per environment, never global, and shown in
    /// the interface as a warning rather than a checkbox.
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>
    /// Marks an environment where a failed test is a real incident. Used to require confirmation
    /// before a run, and to refuse destructive cleanup steps without an explicit opt-in.
    /// </summary>
    public bool IsProduction { get; set; }

    /// <summary>
    /// The runner that reaches this environment, or null when the server can reach it itself.
    ///
    /// It belongs to the environment rather than to a scenario or a schedule because that is where
    /// the fact lives: a staging API inside somebody's network is unreachable from here whoever is
    /// asking and whatever they are running. Everything else follows from it.
    /// </summary>
    public Guid? RunnerId { get; set; }

    public int SortOrder { get; set; }

    public ICollection<EnvironmentVariable> Variables { get; set; } = [];
}

/// <summary>
/// What kind of place this is. Presentation and defaults only — the runner treats them alike.
/// </summary>
public enum EnvironmentKind
{
    Custom = 0,
    Local = 1,
    Development = 2,
    QA = 3,
    Staging = 4,
    Production = 5,
}
