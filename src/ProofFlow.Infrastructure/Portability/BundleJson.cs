using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofFlow.Contracts.Portability;

namespace ProofFlow.Infrastructure.Portability;

/// <summary>
/// How a bundle is written to a file and read back.
///
/// Indented, camel-cased, and with unicode left alone. All three are for the same reason: this file
/// is meant to sit in a repository and be read by people. A minified export is a single line that
/// every change rewrites entirely; escaped unicode turns a Persian environment name into
/// <c>محیط</c>, which is correct JSON and unreadable to the person whose
/// language it is.
/// </summary>
public static class BundleJson
{
    /// <summary>How big a file this will accept. A bundle is a description, not a database dump.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Relaxed rather than the default: the default escapes every non-ASCII character, and this
        // file is read by people who write Persian.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(Bundle bundle) => JsonSerializer.Serialize(bundle, Options);

    /// <summary>
    /// Reads a bundle, or says why it could not.
    ///
    /// A code rather than a sentence, the same as everywhere else that crosses out of a service.
    /// </summary>
    public static (Bundle? Bundle, string? Refusal) Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, "import.empty");

        // Parsed before it is deserialised, so the two failures can be told apart. "This is not
        // JSON" and "this is JSON, but it is not one of these" send somebody to different places:
        // the first to whatever produced the file, the second to whether they picked the right one.
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (null, "import.notJson");
        }

        try
        {
            var bundle = JsonSerializer.Deserialize<Bundle>(json, Options);

            if (bundle is null) return (null, "import.notABundle");

            // Read the version before anything else. A file from a later format has fields this
            // code does not know about, and importing three quarters of a project is worse than
            // importing none of it.
            if (bundle.ProofFlow <= 0) return (null, "import.notABundle");
            if (bundle.ProofFlow > Bundle.CurrentVersion) return (null, "import.tooNew");

            if (string.IsNullOrWhiteSpace(bundle.Project?.Name)) return (null, "import.notABundle");

            return (bundle, null);
        }
        catch (JsonException)
        {
            // Valid JSON that will not become a bundle — a missing required field, most often
            // because somebody exported from a different tool and hoped.
            return (null, "import.notABundle");
        }
    }
}
