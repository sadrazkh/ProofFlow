using ProofFlow.Domain.Common;

namespace ProofFlow.Domain.Environments;

/// <summary>
/// A value that must never be readable from a list, a log, an export or a screenshot.
///
/// Only the ciphertext is stored. There is no property here that holds the plaintext, and that is
/// structural rather than a convention: a field called <c>Value</c> next to a field called
/// <c>Ciphertext</c> gets populated by someone eventually, and then it gets serialised.
///
/// <see cref="Preview"/> is the four characters shown in the interface so a person can tell two
/// tokens apart. Four is few enough to be useless to an attacker and enough to answer "is this the
/// staging key or the production one".
/// </summary>
public class Secret : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>Null for a secret that applies to every environment in the project.</summary>
    public Guid? EnvironmentId { get; set; }

    public ProjectEnvironment? Environment { get; set; }

    /// <summary>Referred to as <c>{{secrets.name}}</c>. Unique per project and environment.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>AES-256-GCM ciphertext, base64.</summary>
    public required string Ciphertext { get; set; }

    /// <summary>The nonce this value was encrypted under, base64. Never reused.</summary>
    public required string Nonce { get; set; }

    /// <summary>The authentication tag, base64. Without it the ciphertext is malleable.</summary>
    public required string Tag { get; set; }

    /// <summary>
    /// Which master key encrypted this. Recorded so a key can be rotated: rows re-encrypt
    /// gradually, and a row that has not been touched yet still says which key opens it.
    /// </summary>
    public int KeyVersion { get; set; } = 1;

    /// <summary>Last four characters of the plaintext, for telling values apart in a list.</summary>
    public string Preview { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
