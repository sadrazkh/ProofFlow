using System.Text.RegularExpressions;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.TestEngine.Redaction;

/// <summary>
/// Keeps secret values out of everything a person or a file can see.
///
/// Two layers, because either alone leaks.
///
/// The registered values are the ones ProofFlow put into the request — it knows them exactly, so
/// they are replaced wherever they appear, including inside a URL or a JSON body the user built by
/// concatenation.
///
/// The patterns catch what came *back*: a token minted by the API under test, a session cookie, an
/// Authorization header echoed into an error body. ProofFlow never saw those values, so it cannot
/// match them literally, and a run report that quietly contains a working bearer token is a
/// credential in a file people forward to each other.
/// </summary>
public static class Redactor
{
    public const string Mask = "«redacted»";

    /// <summary>
    /// Headers whose value is never worth showing. Matched case-insensitively and whole.
    /// </summary>
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie",
        "x-api-key", "api-key", "apikey", "x-auth-token", "x-access-token",
        "x-csrf-token", "x-xsrf-token", "x-amz-security-token", "x-goog-api-key",
        "private-token", "x-secret", "x-signature", "x-hub-signature", "x-hub-signature-256",
    };

    /// <summary>
    /// Shapes that are a credential wherever they appear.
    ///
    /// Kept narrow on purpose. A pattern broad enough to catch every token also catches product
    /// identifiers and order numbers, and a diff where half the values read «redacted» is a diff
    /// nobody can review — which ends with someone turning redaction off entirely.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        // JWT: three base64url segments. Unmistakable, and always sensitive.
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
            RegexOptions.Compiled)),

        // A JSON field that names itself.
        ("json-secret", new Regex(
            """
            ("(?:[a-zA-Z_]*(?:password|passwd|secret|token|apiKey|api_key|accessKey|access_key|privateKey|private_key|clientSecret|client_secret|refreshToken|refresh_token|authorization)[a-zA-Z_]*)"\s*:\s*)"[^"]*"
            """,
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)),

        // Provider-issued keys with a recognisable prefix.
        ("provider-key", new Regex(
            @"\b(?:sk-[A-Za-z0-9]{16,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,})\b",
            RegexOptions.Compiled)),

        // A bearer token in free text, e.g. echoed inside an error message.
        ("bearer", new Regex(@"\b[Bb]earer\s+[A-Za-z0-9._~+/=-]{12,}", RegexOptions.Compiled)),
    ];

    /// <summary>
    /// Values this run put into requests. Replaced literally wherever they occur.
    ///
    /// Held per <see cref="RedactionScope"/> rather than statically: a static set would outlive the
    /// run, grow without bound, and mean one project's secret masked another project's ordinary
    /// data — which reads as corruption.
    /// </summary>
    public static string Redact(string? text, IReadOnlyCollection<string>? knownValues = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = text;

        if (knownValues is not null)
        {
            // Longest first, so a value that contains another does not leave the shorter one's
            // characters exposed after the longer one has been replaced.
            foreach (var value in knownValues.Where(v => v.Length >= 4).OrderByDescending(v => v.Length))
                result = result.Replace(value, Mask, StringComparison.Ordinal);
        }

        foreach (var (name, pattern) in Patterns)
        {
            result = name == "json-secret"
                ? pattern.Replace(result, $"$1\"{Mask}\"")
                : pattern.Replace(result, Mask);
        }

        return result;
    }

    public static IReadOnlyList<KeyValueEntry> RedactHeaders(
        IReadOnlyList<KeyValueEntry> headers, IReadOnlyCollection<string>? knownValues = null) =>
        [
            .. headers.Select(header => SensitiveHeaders.Contains(header.Name)
                ? header with { Value = Mask }
                : header with { Value = Redact(header.Value, knownValues) }),
        ];

    /// <summary>True when this header's value should never be shown, whatever it contains.</summary>
    public static bool IsSensitiveHeader(string name) => SensitiveHeaders.Contains(name);
}

/// <summary>
/// The secret values in play for one run, gathered as they are resolved.
///
/// Every place a run's output is written — logs, node results, stored payloads, exports — passes
/// through this. Collecting them as the variable resolver hands them out is what makes literal
/// replacement possible at all: after that point ProofFlow no longer knows which characters in a
/// URL were a token.
/// </summary>
public sealed class RedactionScope
{
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    public void Remember(string? value)
    {
        // Below four characters, replacing every occurrence would mangle ordinary text: a secret
        // whose value is "1" would turn every number in the response into «redacted».
        if (!string.IsNullOrEmpty(value) && value.Length >= 4) _values.Add(value);
    }

    public IReadOnlyCollection<string> Values => _values;

    public string Apply(string? text) => Redactor.Redact(text, _values);

    public IReadOnlyList<KeyValueEntry> Apply(IReadOnlyList<KeyValueEntry> headers) =>
        Redactor.RedactHeaders(headers, _values);
}
