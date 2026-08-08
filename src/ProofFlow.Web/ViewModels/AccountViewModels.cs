using System.ComponentModel.DataAnnotations;

namespace ProofFlow.Web.ViewModels;

/// <summary>
/// Validation messages are resource keys, resolved through the shared catalogue by the data
/// annotations localizer. Written as keys rather than English prose so a Persian form cannot end
/// up with an English error under a Persian label.
/// </summary>
public sealed class SignInViewModel
{
    [Required(ErrorMessage = "error.required")]
    [EmailAddress(ErrorMessage = "error.invalidEmail")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "error.required")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = true;

    public string? ReturnUrl { get; set; }
}

public sealed class SignUpViewModel
{
    [Required(ErrorMessage = "error.required")]
    [MaxLength(200, ErrorMessage = "error.tooLong")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "error.required")]
    [EmailAddress(ErrorMessage = "error.invalidEmail")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "error.required")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "error.required")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>
    /// Only asked for on the very first account. Every later sign-up joins an existing workspace
    /// by invitation, and asking then would create a second empty one by accident.
    /// </summary>
    [MaxLength(200, ErrorMessage = "error.tooLong")]
    public string? WorkspaceName { get; set; }

    public bool IsFirstAccount { get; set; }
}

public sealed class ProjectFormViewModel
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "error.required")]
    [MaxLength(200, ErrorMessage = "error.tooLong")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000, ErrorMessage = "error.tooLong")]
    public string? Description { get; set; }

    public string Accent { get; set; } = "indigo";

    /// <summary>The colours a project may be given. Fixed, so none of them can fail contrast.</summary>
    public static readonly string[] Accents =
        ["indigo", "violet", "teal", "amber", "rose", "sky", "emerald", "slate"];

    /// <summary>The keys a build agent can hold. Empty on the create form, which has no project yet.</summary>
    public IReadOnlyList<ApiKeyRow> Keys { get; set; } = [];

    /// <summary>
    /// A key just issued, shown once and then gone.
    ///
    /// Null on every other render, including a refresh — which is the point, and which the page has
    /// to say out loud so nobody closes the tab expecting to come back for it.
    /// </summary>
    public string? IssuedSecret { get; set; }
}
