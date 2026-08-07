using System.ComponentModel.DataAnnotations;
using ProofFlow.Domain.Environments;

namespace ProofFlow.Web.ViewModels;

/// <summary>
/// The environments page: the list on one side, whichever one is selected on the other.
///
/// Selection travels in the query string rather than in JavaScript, so the page is a real URL —
/// linkable, back-buttonable, and rendered whole by the server.
/// </summary>
public sealed record EnvironmentsPageViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<EnvironmentSummary> Environments { get; init; }
    public EnvironmentFormViewModel? Selected { get; init; }
    public IReadOnlyList<VariableRow> Variables { get; init; } = [];
    public IReadOnlyList<SecretRow> Secrets { get; init; } = [];
    public bool CanManage { get; init; }
    public bool CanManageSecrets { get; init; }
    public bool CanRevealSecrets { get; init; }
}

public sealed record EnvironmentSummary(
    Guid Id, string Name, string Slug, string? BaseUrl, EnvironmentKind Kind,
    bool IsProduction, bool AllowPrivateNetwork, bool AllowInvalidCertificate,
    int VariableCount, int SecretCount);

/// <summary>
/// A variable, project-wide or scoped to one environment.
///
/// <paramref name="IsInherited"/> drives the badge that says a value came from the project rather
/// than from the environment being looked at — without it, someone edits what they think is this
/// environment's value and changes every environment's.
/// </summary>
public sealed record VariableRow(Guid Id, string Name, string Value, string? Description, bool IsInherited);

/// <summary>
/// A secret, as far as a list is ever allowed to know it.
///
/// There is no value here. <paramref name="Preview"/> is the last four characters, which is enough
/// to tell the staging key from the production one and useless to anybody else.
/// </summary>
public sealed record SecretRow(
    Guid Id, string Name, string? Description, string Preview, bool IsInherited, DateTimeOffset? LastUsedAt);

public sealed class EnvironmentFormViewModel
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "error.required")]
    [MaxLength(80, ErrorMessage = "error.tooLong")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000, ErrorMessage = "error.tooLong")]
    [Url(ErrorMessage = "environment.baseUrlInvalid")]
    public string? BaseUrl { get; set; }

    public EnvironmentKind Kind { get; set; } = EnvironmentKind.Custom;

    [Range(1, 600, ErrorMessage = "environment.timeoutRange")]
    public int TimeoutSeconds { get; set; } = 30;

    [Range(0, 20, ErrorMessage = "environment.redirectRange")]
    public int MaxRedirects { get; set; } = 5;

    [Range(1, 262_144, ErrorMessage = "environment.sizeRange")]
    public int MaxResponseKilobytes { get; set; } = 4096;

    [MaxLength(4000, ErrorMessage = "error.tooLong")]
    public string? AllowedHosts { get; set; }

    public bool AllowPrivateNetwork { get; set; }

    public bool AllowInvalidCertificate { get; set; }

    public bool IsProduction { get; set; }

    [MaxLength(500, ErrorMessage = "error.tooLong")]
    public string? ProxyUrl { get; set; }

    /// <summary>
    /// The kinds, ordered the way a deployment pipeline runs rather than alphabetically — the list
    /// reads as a progression, which is how people think about where a change is.
    /// </summary>
    public static readonly EnvironmentKind[] Kinds =
    [
        EnvironmentKind.Local, EnvironmentKind.Development, EnvironmentKind.QA,
        EnvironmentKind.Staging, EnvironmentKind.Production, EnvironmentKind.Custom,
    ];
}

public sealed class VariableFormViewModel
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "error.required")]
    [MaxLength(120, ErrorMessage = "error.tooLong")]
    [RegularExpression(@"^[A-Za-z_][A-Za-z0-9_]*$", ErrorMessage = "variable.nameShape")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(8000, ErrorMessage = "error.tooLong")]
    public string Value { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "error.tooLong")]
    public string? Description { get; set; }

    /// <summary>Null means the variable belongs to the project and applies to every environment.</summary>
    public Guid? EnvironmentId { get; set; }
}

public sealed class SecretFormViewModel
{
    [Required(ErrorMessage = "error.required")]
    [MaxLength(120, ErrorMessage = "error.tooLong")]
    [RegularExpression(@"^[A-Za-z_][A-Za-z0-9_]*$", ErrorMessage = "variable.nameShape")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "error.required")]
    [MaxLength(8000, ErrorMessage = "error.tooLong")]
    public string Value { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "error.tooLong")]
    public string? Description { get; set; }

    public Guid? EnvironmentId { get; set; }
}
