using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Runners;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Runners;

/// <summary>
/// Creating runners, enrolling them, and knowing which one is calling.
///
/// The enrollment dance is two credentials and one hand-off, and each part is the way it is for a
/// reason.
///
/// An administrator creates the runner here and gets a <b>code</b>: short, typable, good for fifteen
/// minutes, single use. It is short because somebody reads it off a screen and types it into a
/// terminal on another machine, and it is safe to be short because on its own it buys nothing but
/// the right to become a runner that already exists.
///
/// The agent redeems the code and receives a <b>token</b> and a <b>signing key</b>. The token is 256
/// random bits, stored only as a hash, and is what it presents on every later call. The signing key
/// is what the server signs jobs with, so the agent can tell that the work it is about to run came
/// from this installation and was not altered on the way.
/// </summary>
public sealed class RunnerService(
    ProofFlowDbContext db, ISecretCipher cipher, IClock clock, ICurrentUser me)
{
    /// <summary>Bytes in the long-lived token. Guessing one is not a strategy.</summary>
    public const int TokenBytes = 32;

    /// <summary>Bytes in the signing key. The same size as the HMAC it feeds.</summary>
    public const int SigningKeyBytes = 32;

    /// <summary>How many characters of the token are readable, so two runners can be told apart.</summary>
    public const int PreviewLength = 8;

    /// <summary>
    /// Creates a runner and returns the code to type into the agent.
    ///
    /// The code is returned once and only here — the row keeps a hash, and nothing can produce the
    /// code again. Somebody who loses it before enrolling issues a new one.
    /// </summary>
    public async Task<(Runner Runner, string Code)> CreateAsync(
        Guid workspaceId, Guid? projectId, string name, string? description,
        CancellationToken cancellation = default)
    {
        var code = EnrollmentCode();

        var runner = new Runner
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            EnrollmentHash = Fingerprint(code),
            EnrollmentExpiresAt = clock.UtcNow + Runner.EnrollmentLifetime,
            CreatedByUserId = me.UserId,
        };

        db.Runners.Add(runner);
        await db.SaveChangesAsync(cancellation);

        return (runner, code);
    }

    /// <summary>Issues a fresh code for a runner nobody enrolled in time.</summary>
    public async Task<string?> ReissueAsync(
        Guid workspaceId, Guid runnerId, CancellationToken cancellation = default)
    {
        var runner = await db.Runners
            .FirstOrDefaultAsync(candidate => candidate.Id == runnerId
                                              && candidate.WorkspaceId == workspaceId, cancellation);

        // Not for one that is already enrolled. Handing out a second way in to a machine that
        // already has a token is a way to end up with two agents claiming to be the same runner.
        if (runner is null || runner.EnrolledAt is not null || runner.RevokedAt is not null) return null;

        var code = EnrollmentCode();

        runner.EnrollmentHash = Fingerprint(code);
        runner.EnrollmentExpiresAt = clock.UtcNow + Runner.EnrollmentLifetime;

        await db.SaveChangesAsync(cancellation);

        return code;
    }

    /// <summary>
    /// Redeems a code, once.
    ///
    /// Across workspaces, because the code is what says which workspace this is — the agent knows
    /// nothing except the address and the code somebody read to it.
    /// </summary>
    public async Task<Enrolled?> EnrollAsync(
        string? code, string? hostname, string? version, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var hash = Fingerprint(Normalise(code));

        var runner = await db.Runners
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.EnrollmentHash == hash, cancellation);

        if (runner is null) return null;
        if (runner.RevokedAt is not null) return null;
        if (runner.EnrolledAt is not null) return null;
        if (runner.EnrollmentExpiresAt < clock.UtcNow) return null;

        var token = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SigningKeyBytes));

        var kept = cipher.Seal(signingKey);

        runner.TokenHash = Fingerprint(token);
        runner.TokenPreview = token[..PreviewLength];
        runner.SigningKeyCipher = kept.Ciphertext;
        runner.SigningKeyNonce = kept.Nonce;
        runner.SigningKeyTag = kept.Tag;
        runner.SigningKeyVersion = kept.KeyVersion;
        runner.EnrolledAt = clock.UtcNow;
        runner.LastSeenAt = clock.UtcNow;
        runner.Hostname = Trim(hostname, 200);
        runner.Version = Trim(version, 50);

        // Spent. The code cannot enrol a second agent, which is what stops one leaked code from
        // becoming two machines that both believe they are this runner.
        runner.EnrollmentHash = null;
        runner.EnrollmentExpiresAt = null;

        await db.SaveChangesAsync(cancellation);

        return new Enrolled(runner, token, signingKey);
    }

    /// <summary>
    /// The runner a token belongs to, or nothing.
    ///
    /// Also stamps the poll, because every call an agent makes is evidence it is alive and there is
    /// no separate heartbeat worth building.
    /// </summary>
    public async Task<Runner?> AuthenticateAsync(
        string? token, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = Fingerprint(token.Trim());

        var runner = await db.Runners
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellation);

        if (runner is null || runner.RevokedAt is not null) return null;

        runner.LastSeenAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellation);

        return runner;
    }

    /// <summary>Reads a runner's signing key back, for signing one job.</summary>
    public string? SigningKey(Runner runner) =>
        runner.SigningKeyCipher is { } text
        && runner.SigningKeyNonce is { } nonce
        && runner.SigningKeyTag is { } tag
            ? cipher.Open(new SealedSecret(text, nonce, tag, runner.SigningKeyVersion))
            : null;

    public async Task<bool> RevokeAsync(
        Guid workspaceId, Guid runnerId, CancellationToken cancellation = default)
    {
        var runner = await db.Runners
            .FirstOrDefaultAsync(candidate => candidate.Id == runnerId
                                              && candidate.WorkspaceId == workspaceId, cancellation);

        if (runner is null || runner.RevokedAt is not null) return false;

        runner.RevokedAt = clock.UtcNow;

        // The token and the key go with it. A revoked runner whose credentials are still in the
        // row is a revocation that depends on every future query remembering to check a flag.
        runner.TokenHash = null;
        runner.SigningKeyCipher = null;
        runner.SigningKeyNonce = null;
        runner.SigningKeyTag = null;
        runner.EnrollmentHash = null;

        await db.SaveChangesAsync(cancellation);

        return true;
    }

    /// <summary>
    /// A code somebody can read aloud: four groups of four, from an alphabet with no ambiguity in
    /// it.
    ///
    /// No 0/O, no 1/I/L. Those are the characters that turn "type this code" into a support call,
    /// and the entropy lost by dropping them is nothing next to the fifteen-minute lifetime.
    /// </summary>
    private static string EnrollmentCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        var code = new StringBuilder(19);

        for (var group = 0; group < 4; group++)
        {
            if (group > 0) code.Append('-');

            for (var at = 0; at < 4; at++)
            {
                code.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
            }
        }

        return code.ToString();
    }

    /// <summary>
    /// Reads a typed code the way a person types it: any case, dashes optional.
    ///
    /// Somebody who leaves out the dashes has typed the right code, and refusing it teaches them
    /// nothing except that the product is fussy.
    /// </summary>
    internal static string Normalise(string code)
    {
        var cleaned = new string([.. code.Where(char.IsLetterOrDigit)]).ToUpperInvariant();

        return string.Join('-',
            Enumerable.Range(0, (cleaned.Length + 3) / 4)
                .Select(group => cleaned.Substring(group * 4, Math.Min(4, cleaned.Length - (group * 4)))));
    }

    internal static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Trim(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= length ? value.Trim()
        : value[..length];
}

/// <summary>
/// What an agent receives, once, when it enrols.
///
/// Both secrets are in this record and in no other place: the token exists only as a hash
/// afterwards, and the signing key only as ciphertext.
/// </summary>
public sealed record Enrolled(Runner Runner, string Token, string SigningKey);
