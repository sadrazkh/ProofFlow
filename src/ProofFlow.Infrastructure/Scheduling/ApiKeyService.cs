using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Scheduling;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Scheduling;

/// <summary>
/// Issuing and checking the keys a build agent holds.
///
/// Three decisions, all about the same thing: a key that can be read back is a key that leaks.
///
/// Only a hash is stored, so a database backup in the wrong bucket contains nothing usable. The
/// value is returned exactly once, from <see cref="IssueAsync"/>, and there is no code path that
/// can produce it again. And the hash is plain SHA-256 rather than a password hash — deliberately,
/// because this is a 256-bit random value and not a human's password: there is nothing to brute
/// force, and a slow hash on every CI request would be a denial of service somebody built for
/// themselves.
/// </summary>
public sealed class ApiKeyService(ProofFlowDbContext db, IClock clock, ICurrentUser me)
{
    /// <summary>How many random bytes a key carries. 256 bits, so guessing is not a strategy.</summary>
    public const int Bytes = 32;

    /// <summary>How long the readable part is. Enough to tell keys apart, far too little to use.</summary>
    public const int PreviewLength = 8;

    /// <summary>
    /// Makes a key and returns it in the clear, once.
    ///
    /// The caller has to show it to somebody immediately, because nothing — not this service, not
    /// the database, not a support engineer — can produce it again.
    /// </summary>
    public async Task<(ApiKey Key, string Secret)> IssueAsync(
        Guid workspaceId, Guid? projectId, string name, DateTimeOffset? expiresAt,
        CancellationToken cancellation = default)
    {
        // Base64url: a key travels in an HTTP header and through a CI variable, and neither is a
        // good place for '+', '/' or '='.
        var secret = ApiKey.Prefix + Base64Url(RandomNumberGenerator.GetBytes(Bytes));

        var key = new ApiKey
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Name = name.Trim(),
            Hash = Fingerprint(secret),
            Preview = secret[..(ApiKey.Prefix.Length + PreviewLength)],
            CreatedByUserId = me.UserId,
            ExpiresAt = expiresAt,
        };

        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellation);

        return (key, secret);
    }

    /// <summary>
    /// Finds the key behind a presented secret, or nothing.
    ///
    /// Reads across workspaces on purpose: the caller has presented a credential and nothing else,
    /// so there is no tenant to narrow by yet — the key is what says which workspace this is.
    /// </summary>
    public async Task<ApiKey?> FindAsync(string? presented, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(presented)) return null;
        if (!presented.StartsWith(ApiKey.Prefix, StringComparison.Ordinal)) return null;

        var hash = Fingerprint(presented.Trim());

        var key = await db.ApiKeys
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Hash == hash, cancellation);

        if (key is null || !key.IsUsable(clock.UtcNow)) return null;

        // Written at most once an hour. It answers the one question that makes revoking safe — "is
        // anything still using this?" — and that answer does not need to be accurate to the second,
        // whereas a write on every CI request would be.
        if (key.LastUsedAt is null || clock.UtcNow - key.LastUsedAt > TimeSpan.FromHours(1))
        {
            key.LastUsedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellation);
        }

        return key;
    }

    public async Task<bool> RevokeAsync(
        Guid workspaceId, Guid keyId, CancellationToken cancellation = default)
    {
        var key = await db.ApiKeys
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == keyId && candidate.WorkspaceId == workspaceId, cancellation);

        if (key is null || key.RevokedAt is not null) return false;

        // Revoked, not deleted. The audit trail says which key started which run, and a deleted row
        // turns that into a dangling id.
        key.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellation);

        return true;
    }

    internal static string Fingerprint(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
