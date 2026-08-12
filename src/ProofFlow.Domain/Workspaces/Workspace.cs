using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Workspaces;

/// <summary>
/// The outermost container: a team, and everything it owns.
///
/// Every project, environment, secret, baseline and run hangs off exactly one workspace, and the
/// database enforces that with a global query filter rather than a remembered <c>Where</c> clause.
/// </summary>
public class Workspace : Entity
{
    public required string Name { get; set; }

    /// <summary>URL-safe, unique, and stable — it appears in links people paste to each other.</summary>
    public required string Slug { get; set; }

    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// Which model writes a scenario when somebody asks for one, and where to ask it.
    ///
    /// Per workspace rather than per installation because the key is somebody's money. A team that
    /// wants this pays for it and knows what it costs; a team that does not never sees the button.
    /// </summary>
    public string? AiBaseUrl { get; set; }

    public string? AiModel { get; set; }

    /// <summary>
    /// The key, sealed with the same cipher as every other secret here.
    ///
    /// It is a credential to somebody's account with a balance on it, so it is stored the way the
    /// credentials in tests are stored — encrypted at rest, never returned to a page, and shown as
    /// a four-character preview so two keys can be told apart.
    /// </summary>
    public string? AiKeyCipher { get; set; }

    public string? AiKeyNonce { get; set; }

    public string? AiKeyTag { get; set; }

    public int AiKeyVersion { get; set; } = 1;

    /// <summary>The last four characters, so somebody can see which key is in place.</summary>
    public string? AiKeyPreview { get; set; }

    public ICollection<WorkspaceMember> Members { get; set; } = [];
}
