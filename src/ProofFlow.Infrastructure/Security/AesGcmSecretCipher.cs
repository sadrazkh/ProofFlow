using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProofFlow.Application.Abstractions;

namespace ProofFlow.Infrastructure.Security;

/// <summary>
/// AES-256-GCM over secret values, with a fresh nonce per value.
///
/// GCM rather than CBC because it authenticates as well as encrypts: without a tag, a ciphertext
/// can be altered in ways that decrypt to something else, and a bearer token that decrypts to a
/// different string is a worse failure than one that fails to decrypt at all.
///
/// The nonce is random and never reused. GCM's failure mode on nonce reuse is not degraded
/// security, it is catastrophic — two messages under one nonce leak the XOR of their plaintexts
/// and the authentication key — so it is generated per call and never derived from anything.
/// </summary>
public sealed class AesGcmSecretCipher : ISecretCipher
{
    private const int NonceBytes = 12;   // 96 bits, the size GCM is defined for.
    private const int TagBytes = 16;     // 128 bits, the full tag. Truncating it weakens the MAC.

    private readonly byte[] _key;

    public AesGcmSecretCipher(IConfiguration configuration, ILogger<AesGcmSecretCipher> logger)
    {
        var configured = configuration["ProofFlow:MasterKey"];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = DeriveKey(configured);
            return;
        }

        // No configured key. Refusing to start would make a first run impossible, so one is
        // generated and written beside the data — but it has to be *the same key* on the next
        // start, which rules out deriving it from anything that includes fresh randomness per call.
        // A key that changes on restart does not fail loudly; it turns every stored secret into an
        // authentication error somewhere else, days later.
        var path = configuration["ProofFlow:MasterKeyPath"]
                   ?? Path.Combine(AppContext.BaseDirectory, "keys", "master.key");

        _key = LoadOrCreateKey(path, logger);
    }

    public int CurrentKeyVersion => 1;

    public SealedSecret Seal(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[bytes.Length];
        var tag = new byte[TagBytes];

        using var gcm = new AesGcm(_key, TagBytes);
        gcm.Encrypt(nonce, bytes, ciphertext, tag);

        return new SealedSecret(
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            CurrentKeyVersion);
    }

    public string Open(SealedSecret sealedSecret)
    {
        ArgumentNullException.ThrowIfNull(sealedSecret);

        if (sealedSecret.KeyVersion != CurrentKeyVersion)
            throw new InvalidOperationException(
                $"This value was sealed with key version {sealedSecret.KeyVersion}; this installation " +
                $"holds version {CurrentKeyVersion}.");

        var ciphertext = Convert.FromBase64String(sealedSecret.Ciphertext);
        var nonce = Convert.FromBase64String(sealedSecret.Nonce);
        var tag = Convert.FromBase64String(sealedSecret.Tag);
        var plaintext = new byte[ciphertext.Length];

        using var gcm = new AesGcm(_key, TagBytes);
        try
        {
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            // Deliberately does not say which part failed. The distinction between "wrong key" and
            // "altered ciphertext" is useful to an attacker probing the store and useless to an
            // operator, who needs the same answer either way.
            throw new InvalidOperationException(
                "A stored secret could not be decrypted. Either the master key changed, or the row was altered.", ex);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// SHA-256 of the configured material, so any length of passphrase produces a 256-bit key.
    ///
    /// Not a password KDF, and that is a considered choice: this input is machine-generated
    /// configuration, not a human-chosen password, so the cost of Argon2 or PBKDF2 buys nothing
    /// against an attacker who already has the configuration file.
    /// </summary>
    private static byte[] DeriveKey(string material) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(material));

    /// <summary>
    /// Reads the generated key, or writes one on first run.
    ///
    /// The warning is deliberately blunt. This file *is* every secret in the installation: back it
    /// up with the database or lose both together, and an operator who learns that after storing a
    /// production token has learned it too late.
    /// </summary>
    private static byte[] LoadOrCreateKey(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
                return Convert.FromBase64String(File.ReadAllText(path).Trim());

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(path, Convert.ToBase64String(key));

            // Owner-only. On Windows this is a no-op and the directory's inherited ACL applies,
            // which is stated rather than silently relied upon.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            logger.LogWarning(
                "ProofFlow:MasterKey was not set, so a key was generated at {Path}. That file is the " +
                "only thing that can decrypt stored secrets — back it up with the database, or set " +
                "ProofFlow:MasterKey and restart before storing anything you cannot re-issue.", path);

            return key;
        }
        catch (Exception ex)
        {
            // Refusing to start is right here. Falling back to an in-memory key would encrypt this
            // session's secrets with something that disappears on restart.
            throw new InvalidOperationException(
                $"No master key: {path} could not be read or created, and ProofFlow:MasterKey is not set. " +
                "Set ProofFlow:MasterKey, or make that path writable.", ex);
        }
    }
}
