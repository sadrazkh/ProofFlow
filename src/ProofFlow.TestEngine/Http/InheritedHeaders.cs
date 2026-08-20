using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProofFlow.TestEngine.Http;

/// <summary>
/// What a request carries because of the environment it runs in.
///
/// One function, called from all five places that send anything — the request lab, an endpoint's
/// compare, a sweep across inputs, a scenario run, and the agent. It is one function rather than
/// five because the precedence rule is the interesting part, and five copies of a precedence rule is
/// four chances for a step's own header to lose an argument it should win.
///
/// The rule, from least to most specific:
///
/// <list type="number">
/// <item>The environment's default headers. <c>Accept: application/json</c> set once.</item>
/// <item>The environment's authentication. More specific than a default header, because it is the
/// dedicated mechanism — somebody who has configured both has said the same thing twice, and the
/// one they configured deliberately should win.</item>
/// <item>Whatever the request already has. A step that sets its own <c>Authorization</c> is a step
/// signing in as somebody else on purpose, which is an ordinary thing for a test about permissions
/// to do, and inheritance that overrode it would make that test impossible to write.</item>
/// </list>
///
/// Default headers had the same problem authentication did: <c>DefaultHeadersJson</c> reached the
/// variable scope as <c>{{environment.headers}}</c> and was never actually applied to anything, so
/// «set once, sent everywhere» was true of the reference and not of the header.
/// </summary>
public static class InheritedHeaders
{
    public static HttpRequestDefinition Apply(
        HttpRequestDefinition request,
        IReadOnlyList<KeyValueEntry> authHeaders,
        string? defaultHeadersJson)
    {
        // A bare request inherits nothing — that is its entire meaning, and this is the one gate
        // all five senders pass through, so «without a token» is the same fact everywhere.
        if (request.Bare) return request;

        var inherited = new List<KeyValueEntry>();

        inherited.AddRange(Defaults(defaultHeadersJson));

        // Auth over defaults: same name, the deliberate one replaces the incidental one.
        foreach (var header in authHeaders)
        {
            inherited.RemoveAll(existing => Same(existing.Name, header.Name));
            inherited.Add(header);
        }

        if (inherited.Count == 0) return request;

        // And the request over everything. Only headers it does not already name are added.
        var own = request.Headers;
        var added = inherited.Where(header => !own.Any(mine => Same(mine.Name, header.Name))).ToList();

        return added.Count == 0 ? request : request with { Headers = [.. own, .. added] };
    }

    /// <summary>
    /// The environment's default headers, as an object of name to value.
    ///
    /// Unreadable JSON yields nothing rather than throwing. A malformed blob in that column must not
    /// stop a run — it shows as invalid in the editor, and a run that refused to start would make
    /// the page that fixes it the page that cannot be reached.
    /// </summary>
    public static IReadOnlyList<KeyValueEntry> Defaults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            if (JsonNode.Parse(json) is not JsonObject holder) return [];

            return
            [
                .. holder
                    .Where(pair => pair.Value is not null)
                    .Select(pair => new KeyValueEntry(pair.Key, pair.Value!.ToString()))
                    .Where(entry => entry.Name.Length > 0),
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
