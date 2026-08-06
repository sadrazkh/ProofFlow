using Microsoft.AspNetCore.Identity;

namespace ProofFlow.Infrastructure.Identity;

/// <summary>
/// The account someone signs in with.
///
/// It lives in Infrastructure, not Domain, because <see cref="IdentityUser{TKey}"/> is a storage
/// concern that arrives with its own tables and its own opinions. The domain refers to people by
/// a bare user id, which is all it needs and all it should be coupled to.
/// </summary>
public class ProofFlowUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }

    /// <summary>
    /// UI language: <c>fa</c> or <c>en</c>. Null means "follow the browser", which is the honest
    /// default — guessing from an IP address gets it wrong for exactly the people who notice.
    /// </summary>
    public string? PreferredCulture { get; set; }

    /// <summary><c>light</c>, <c>dark</c> or <c>system</c>.</summary>
    public string ThemeChoice { get; set; } = "system";

    /// <summary>The workspace this account lands in after sign-in.</summary>
    public Guid? LastWorkspaceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
}
