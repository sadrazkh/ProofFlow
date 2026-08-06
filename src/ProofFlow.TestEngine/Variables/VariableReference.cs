using System.Text;
using System.Text.RegularExpressions;

namespace ProofFlow.TestEngine.Variables;

/// <summary>
/// One <c>{{…}}</c> reference, parsed.
///
/// The syntax is deliberately small and readable, because the person writing it is often not a
/// programmer: a scope, then a path, and array positions in square brackets.
///
/// <code>
/// {{environment.baseUrl}}
/// {{secrets.apiToken}}
/// {{vars.pageSize}}
/// {{steps.login.response.token}}
/// {{steps.categories.response.items[0].id}}
/// {{dataset.current.studyId}}
/// {{run.id}}
/// </code>
///
/// There is no expression language here, and that is the design rather than an omission. The
/// moment references can contain arithmetic or function calls, a scenario becomes code — which the
/// brief specifically rules out, and which turns every failing test into a debugging session in a
/// language nobody documented.
/// </summary>
public sealed partial record VariableReference(string Scope, IReadOnlyList<PathSegment> Path, string Raw)
{
    /// <summary>
    /// Matches a reference and captures its inside. Non-greedy so two references on one line are
    /// two matches rather than one that swallows the text between them.
    /// </summary>
    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled)]
    public static partial Regex Pattern { get; }

    public static bool TryParse(string inside, out VariableReference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(inside)) return false;

        var segments = ParsePath(inside.Trim());
        if (segments.Count == 0) return false;

        // The first segment names the scope and must be a plain name — {{[0].id}} has no meaning.
        if (segments[0] is not PropertySegment first) return false;

        reference = new VariableReference(first.Name, segments.Skip(1).ToList(), $"{{{{{inside.Trim()}}}}}");
        return true;
    }

    /// <summary>Every reference in a string, in the order they appear.</summary>
    public static IReadOnlyList<VariableReference> FindAll(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var found = new List<VariableReference>();
        foreach (Match match in Pattern.Matches(text))
        {
            if (TryParse(match.Groups[1].Value, out var reference)) found.Add(reference);
        }
        return found;
    }

    /// <summary>
    /// Splits <c>items[0].id</c> into property and index steps.
    ///
    /// Hand-written rather than a regex because the interesting inputs are the malformed ones —
    /// <c>items[</c>, <c>items[a]</c>, <c>items[]</c> — and a regex either accepts them silently or
    /// becomes unreadable. Anything that does not parse yields an empty list, and the caller shows
    /// the reference back to the person unresolved rather than guessing.
    /// </summary>
    private static List<PathSegment> ParsePath(string input)
    {
        var segments = new List<PathSegment>();
        var current = new StringBuilder();

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];

            switch (ch)
            {
                case '.':
                    if (current.Length > 0) { segments.Add(new PropertySegment(current.ToString())); current.Clear(); }
                    break;

                case '[':
                    if (current.Length > 0) { segments.Add(new PropertySegment(current.ToString())); current.Clear(); }

                    var close = input.IndexOf(']', i);
                    if (close < 0) return [];

                    var inside = input[(i + 1)..close].Trim();
                    if (inside.Length == 0) return [];

                    if (int.TryParse(inside, out var index))
                    {
                        // Negative positions count from the end, so {{…items[-1].id}} is "the last
                        // one" — which is what people mean far more often than "position minus one".
                        segments.Add(new IndexSegment(index));
                    }
                    else
                    {
                        // A quoted key, for property names that contain a dot or a space.
                        segments.Add(new PropertySegment(inside.Trim('\'', '"')));
                    }

                    i = close;
                    break;

                case ']':
                    return [];

                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0) segments.Add(new PropertySegment(current.ToString()));
        return segments;
    }

    public override string ToString() => Raw;
}

public abstract record PathSegment;

public sealed record PropertySegment(string Name) : PathSegment
{
    public override string ToString() => Name;
}

public sealed record IndexSegment(int Index) : PathSegment
{
    public override string ToString() => $"[{Index}]";
}
