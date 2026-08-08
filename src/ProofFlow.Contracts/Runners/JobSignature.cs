using System.Security.Cryptography;
using System.Text;

namespace ProofFlow.Contracts.Runners;

/// <summary>
/// Signs a job, and checks a signature.
///
/// One implementation for both sides, and it lives in Contracts for the same reason the wire
/// formats do: the server signs with it and the agent verifies with it, and the agent cannot
/// reference Infrastructure — that is where the database is, and a runner that could open the
/// application's database would defeat the whole point of having a runner.
///
/// Having a single function also means the two sides cannot drift into disagreeing about what
/// exactly is being signed, which is the way signature schemes usually fail.
///
/// HMAC-SHA256 over the payload bytes as written. Not over a re-serialised object: two JSON
/// serialisers that differ in key order or whitespace produce two different signatures for the same
/// job, and the bug that follows is one that only appears after somebody upgrades a library.
/// </summary>
public static class JobSignature
{
    public static string Sign(string payload, string signingKey) =>
        Convert.ToBase64String(
            HMACSHA256.HashData(Convert.FromBase64String(signingKey), Encoding.UTF8.GetBytes(payload)));

    /// <summary>
    /// Whether a signature belongs to this payload.
    ///
    /// Compared in fixed time. The difference it makes here is small — an attacker who can time a
    /// remote comparison over a network has better options — but a variable-time compare in a
    /// signature check is the kind of thing that gets copied into somewhere it matters more.
    /// </summary>
    public static bool Verify(string payload, string signature, string signingKey)
    {
        if (string.IsNullOrEmpty(signature)) return false;

        byte[] presented;

        try
        {
            presented = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(
            Convert.FromBase64String(signingKey), Encoding.UTF8.GetBytes(payload));

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
