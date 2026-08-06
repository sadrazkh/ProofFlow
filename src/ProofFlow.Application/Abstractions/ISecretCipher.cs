namespace ProofFlow.Application.Abstractions;

/// <summary>
/// Encrypts and decrypts secret values.
///
/// A port rather than a static helper so a deployment can put the master key somewhere better than
/// configuration — a KMS, a vault — without any caller changing.
/// </summary>
public interface ISecretCipher
{
    SealedSecret Seal(string plaintext);

    /// <summary>
    /// Throws when the ciphertext, nonce or tag do not agree — which happens when the master key
    /// changed, or when a row was tampered with. Both are worth an exception rather than a null:
    /// a run that silently sends an empty token produces a 401 that looks like an API bug.
    /// </summary>
    string Open(SealedSecret sealedSecret);

    /// <summary>The key version new values are sealed under. Older rows record their own.</summary>
    int CurrentKeyVersion { get; }
}

public sealed record SealedSecret(string Ciphertext, string Nonce, string Tag, int KeyVersion);
