using System.Globalization;
using System.Text;

namespace ProofFlow.Application.Common;

/// <summary>
/// Turns a name people typed into something safe for a URL and an exported filename.
///
/// The interesting case here is not English. A workspace called «سامانه سفارش‌ها» must still get a
/// usable slug, and stripping non-ASCII would leave an empty string — so the transliteration
/// fallback is a deterministic hash suffix rather than a refusal, and the caller is expected to
/// show the user what they got.
/// </summary>
public static class Slug
{
    private const int MaxLength = 60;

    public static string From(string input, string fallbackPrefix = "item")
    {
        if (string.IsNullOrWhiteSpace(input))
            return $"{fallbackPrefix}-{Suffix(input ?? string.Empty)}";

        // Decompose accents (é → e + combining mark) so the mark can be dropped rather than the letter.
        var normalised = input.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        var lastWasSeparator = false;

        foreach (var ch in normalised)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxLength)
            slug = slug[..MaxLength].TrimEnd('-');

        // Nothing survived — the name was written in a script with no ASCII in it at all. A stable
        // hash of the original keeps the slug reproducible, so re-importing the same export does
        // not produce a second project.
        return slug.Length == 0 ? $"{fallbackPrefix}-{Suffix(input)}" : slug;
    }

    /// <summary>Appends -2, -3, … until the slug is not in <paramref name="taken"/>.</summary>
    public static string Unique(string candidate, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(candidate))
            return candidate;

        for (var n = 2; n < 10_000; n++)
        {
            var next = $"{candidate}-{n}";
            if (!used.Contains(next))
                return next;
        }

        return $"{candidate}-{Suffix(candidate)}";
    }

    private static string Suffix(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes)[..8];
    }
}
