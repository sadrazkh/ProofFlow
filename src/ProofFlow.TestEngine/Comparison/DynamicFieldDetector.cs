using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProofFlow.TestEngine.Comparison;

/// <summary>
/// Finds the fields that will differ next time whatever else happens, and proposes a rule for each.
///
/// This is the difference between a baseline somebody keeps and one they abandon. Approve a
/// response with a request id and a timestamp in it and every future run fails on fields nobody
/// cares about; after the third time, people stop looking at the results.
///
/// **Nothing here is applied.** The brief is explicit and it is right: a suggestion that silently
/// became a rule is a field somebody stopped checking without deciding to. These are offered as
/// pre-filled rows with a checkbox, and an unticked one does nothing.
///
/// Confidence is carried so the interface can pre-tick the certain ones and leave the guesses to a
/// person. A GUID in a field called <c>requestId</c> is not a judgement call; a string that merely
/// looks random is.
/// </summary>
public static partial class DynamicFieldDetector
{
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled)]
    private static partial Regex Guid { get; }

    /// <summary>
    /// ISO dates, with or without a time.
    ///
    /// Date-only counts: "today's date" is one of the commonest dynamic fields there is. It stays
    /// a *possibility* rather than a certainty unless the field is named for a timestamp, because
    /// a date of birth is also a date and excluding one would stop a real check from ever running.
    /// </summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2})?(\.\d+)?(Z|[+-]\d{2}:?\d{2})?)?$",
        RegexOptions.Compiled)]
    private static partial Regex Timestamp { get; }

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.", RegexOptions.Compiled)]
    private static partial Regex Jwt { get; }

    [GeneratedRegex(@"[?&](X-Amz-Signature|Signature|sig|token|expires|Expires)=", RegexOptions.Compiled)]
    private static partial Regex SignedUrl { get; }

    /// <summary>Field names that say what they are. Matched on the segment, case-insensitively.</summary>
    private static readonly (string[] Fragments, DynamicReason Reason)[] ByName =
    [
        (["requestid", "correlationid", "traceid", "spanid", "operationid"], DynamicReason.TraceId),
        (["createdat", "updatedat", "modifiedat", "timestamp", "issuedat", "expiresat", "lastseen"],
            DynamicReason.Timestamp),
        (["token", "accesstoken", "refreshtoken", "sessionid", "jti"], DynamicReason.Token),
        (["nonce", "random", "salt", "etag"], DynamicReason.Random),
    ];

    public static IReadOnlyList<DynamicFieldSuggestion> Suggest(JsonNode? document, int limit = 200)
    {
        var found = new List<DynamicFieldSuggestion>();
        if (document is not null) Walk(JsonLocation.Root, document, found, limit, 0);

        return found;
    }

    /// <summary>
    /// Compares two captures of the same request and proposes a rule for everything that moved.
    ///
    /// Far stronger evidence than looking at one response: a field that actually differs between
    /// two runs of the same call is dynamic by demonstration rather than by its name looking
    /// suspicious. Used by capture mode, which takes several samples on purpose.
    /// </summary>
    public static IReadOnlyList<DynamicFieldSuggestion> SuggestFromPair(JsonNode? first, JsonNode? second)
    {
        var diff = SemanticDiff.Compare(first, second);
        var suggestions = new List<DynamicFieldSuggestion>();

        foreach (var finding in diff.Findings)
        {
            if (finding.Kind is not (DiffKind.Changed or DiffKind.TypeChanged)) continue;

            var reason = Classify(finding.Location.Leaf, ValueOf(finding.Actual));

            suggestions.Add(new DynamicFieldSuggestion(
                finding.Path,
                reason.Reason == DynamicReason.None ? DynamicReason.DiffersBetweenRuns : reason.Reason,
                // Demonstrated, not guessed: it genuinely differed between two runs.
                Confidence.Certain,
                Propose(reason.Reason),
                finding.Actual));
        }

        return suggestions;
    }

    private static void Walk(
        JsonLocation location, JsonNode node, List<DynamicFieldSuggestion> found, int limit, int depth)
    {
        if (found.Count >= limit || depth > SemanticDiff.MaxDepth) return;

        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is not null) Walk(location.Field(key), value, found, limit, depth + 1);
                }
                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is { } item) Walk(location.At(index), item, found, limit, depth + 1);
                }
                break;

            default:
            {
                var (reason, confidence) = Classify(location.Leaf, Matcher.AsText(node));
                if (reason == DynamicReason.None) return;

                found.Add(new DynamicFieldSuggestion(
                    location.ToString(), reason, confidence,
                    Propose(reason), SemanticDiff.Render(node)));
                break;
            }
        }
    }

    private static (DynamicReason Reason, Confidence Confidence) Classify(string fieldName, string? value)
    {
        var name = fieldName.ToLowerInvariant();

        // Shape first: a GUID is a GUID whatever the field is called, and that is the strongest
        // single signal there is.
        if (value is not null)
        {
            if (Guid.IsMatch(value)) return (DynamicReason.Guid, Confidence.Certain);
            if (Jwt.IsMatch(value)) return (DynamicReason.Token, Confidence.Certain);
            if (SignedUrl.IsMatch(value)) return (DynamicReason.ExpiringUrl, Confidence.Certain);
            if (Timestamp.IsMatch(value))
            {
                // A timestamp in a field named for one is certain; a date somewhere else may well
                // be real data — a birth date, an order date — and guessing wrong stops a real
                // check from ever running.
                return (DynamicReason.Timestamp,
                    ByName[1].Fragments.Any(name.Contains) ? Confidence.Certain : Confidence.Possible);
            }
        }

        foreach (var (fragments, reason) in ByName)
        {
            if (fragments.Any(name.Contains)) return (reason, Confidence.Likely);
        }

        return (DynamicReason.None, Confidence.Possible);
    }

    /// <summary>
    /// The rule a suggestion becomes if accepted.
    ///
    /// Ignore for things with no stable shape; type-only where the shape is worth keeping, because
    /// a request id that becomes a number is still a defect worth catching.
    /// </summary>
    private static ComparisonRule Propose(DynamicReason reason) => reason switch
    {
        DynamicReason.Guid or DynamicReason.TraceId => new ComparisonRule
        {
            Path = string.Empty, Kind = MatcherKind.TypeOnly, Note = "regenerated on every call",
        },
        DynamicReason.Timestamp => new ComparisonRule
        {
            Path = string.Empty, Kind = MatcherKind.Ignore, Note = "moves with the clock",
        },
        DynamicReason.Token or DynamicReason.ExpiringUrl => new ComparisonRule
        {
            Path = string.Empty, Kind = MatcherKind.TypeOnly, Note = "issued fresh, and not worth storing",
        },
        _ => new ComparisonRule
        {
            Path = string.Empty, Kind = MatcherKind.Ignore, Note = "differed between runs",
        },
    };

    private static string? ValueOf(string? json)
    {
        if (json is null) return null;
        return json.Length >= 2 && json[0] == '"' && json[^1] == '"' ? json[1..^1] : json;
    }
}

public enum DynamicReason
{
    None = 0,
    Guid,
    Timestamp,
    TraceId,
    Token,
    Random,
    ExpiringUrl,

    /// <summary>Proved dynamic by differing between two captures of the same request.</summary>
    DiffersBetweenRuns,
}

/// <summary>
/// How sure the detector is. Drives whether the interface pre-ticks the row — never whether the
/// rule is applied, which is always a person's decision.
/// </summary>
public enum Confidence
{
    Possible = 0,
    Likely = 1,
    Certain = 2,
}

public sealed record DynamicFieldSuggestion(
    string Path,
    DynamicReason Reason,
    Confidence Confidence,
    ComparisonRule Proposed,
    string? Sample)
{
    /// <summary>The rule with its path filled in — what accepting this suggestion stores.</summary>
    public ComparisonRule Rule => Proposed with { Path = Path };
}
