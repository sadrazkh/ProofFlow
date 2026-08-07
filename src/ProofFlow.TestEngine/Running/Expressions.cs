using System.Globalization;
using System.Text.Json.Nodes;

namespace ProofFlow.TestEngine.Running;

/// <summary>
/// The small expression language: a value, a comparison, or a value piped through a verb.
///
/// Deliberately not a scripting engine, and this is the security decision of the phase. ProofFlow
/// runs other people's tests on a machine somebody else owns; a node that evaluated arbitrary code
/// would be a way to run arbitrary code there, wearing the disguise of a test step.
///
/// What it does support is what conditions in a test are actually made of:
///
///   {{steps.login.response.statusCode}} == 200
///   {{steps.list.response.body.items}} | count > 0
///   {{vars.token}} is not empty
///
/// Everything is compared as text unless both sides parse as numbers, which is the rule people
/// expect and the one that makes "200" == 200 true — the opposite of the diff engine's rule, and
/// deliberately so: a condition is written by a person and a comparison is made against a recorded
/// answer.
/// </summary>
public static class Expressions
{
    private static readonly string[] Operators =
        ["==", "!=", ">=", "<=", ">", "<", " contains ", " starts with ", " ends with "];

    /// <summary>
    /// Evaluates an expression to a value.
    ///
    /// A bare value comes back as itself, so this doubles as the "work something out" node — which
    /// is why it returns a node rather than a boolean.
    /// </summary>
    public static JsonNode? Evaluate(string expression)
    {
        var text = expression.Trim();
        if (text.Length == 0) return null;

        foreach (var candidate in Operators)
        {
            var at = IndexOfOperator(text, candidate);
            if (at < 0) continue;

            var left = Pipe(text[..at].Trim());
            var right = Pipe(text[(at + candidate.Length)..].Trim());

            return JsonValue.Create(Compare(left, right, candidate.Trim()));
        }

        if (text.EndsWith("is not empty", StringComparison.OrdinalIgnoreCase))
            return JsonValue.Create(!IsEmpty(Pipe(text[..^"is not empty".Length].Trim())));

        if (text.EndsWith("is empty", StringComparison.OrdinalIgnoreCase))
            return JsonValue.Create(IsEmpty(Pipe(text[..^"is empty".Length].Trim())));

        return Pipe(text);
    }

    /// <summary>Whether an expression is true. What every branching node asks.</summary>
    public static bool IsTrue(string expression)
    {
        var value = Evaluate(expression);

        return value switch
        {
            null => false,
            JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
            JsonValue scalar when scalar.TryGetValue<double>(out var number) => number != 0,
            JsonArray array => array.Count > 0,
            _ => !IsEmpty(value),
        };
    }

    /// <summary>
    /// Applies the verbs after a pipe. Two of them, and both count things a test asks about.
    /// </summary>
    private static JsonNode? Pipe(string text)
    {
        var parts = text.Split('|', StringSplitOptions.TrimEntries);
        var value = Literal(parts[0]);

        foreach (var verb in parts.Skip(1))
        {
            value = verb.ToLowerInvariant() switch
            {
                "count" or "length" => JsonValue.Create(Length(value)),
                "lower" => JsonValue.Create(value?.ToString().ToLowerInvariant()),
                "upper" => JsonValue.Create(value?.ToString().ToUpperInvariant()),
                "trim" => JsonValue.Create(value?.ToString().Trim()),
                _ => value,
            };
        }

        return value;
    }

    private static int Length(JsonNode? value) => value switch
    {
        JsonArray array => array.Count,
        JsonObject obj => obj.Count,
        null => 0,
        _ => value.ToString().Length,
    };

    /// <summary>
    /// Reads one side of a comparison.
    ///
    /// By the time this runs the references are already substituted, so what arrives is text — a
    /// quoted string, a number, a boolean, a JSON document, or a bare word.
    /// </summary>
    private static JsonNode? Literal(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return null;

        if ((value.StartsWith('"') && value.EndsWith('"') && value.Length > 1)
            || (value.StartsWith('\'') && value.EndsWith('\'') && value.Length > 1))
        {
            return JsonValue.Create(value[1..^1]);
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)) return null;

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);

        if (value.StartsWith('{') || value.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(value);
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON after all. It is a string that happens to start with a brace.
            }
        }

        return JsonValue.Create(value);
    }

    private static bool Compare(JsonNode? left, JsonNode? right, string op)
    {
        var leftText = left?.ToString() ?? string.Empty;
        var rightText = right?.ToString() ?? string.Empty;

        // Both sides, always parsed: with `&&` the second call is skipped when the first fails and
        // `b` is then unassigned on every path that reads it.
        var leftIsNumber = double.TryParse(leftText, NumberStyles.Any, CultureInfo.InvariantCulture, out var a);
        var rightIsNumber = double.TryParse(rightText, NumberStyles.Any, CultureInfo.InvariantCulture, out var b);
        var numeric = leftIsNumber && rightIsNumber;

        return op switch
        {
            "==" => numeric ? a.Equals(b) : leftText == rightText,
            "!=" => numeric ? !a.Equals(b) : leftText != rightText,
            ">" => numeric && a > b,
            "<" => numeric && a < b,
            ">=" => numeric && a >= b,
            "<=" => numeric && a <= b,
            "contains" => leftText.Contains(rightText, StringComparison.Ordinal),
            "starts with" => leftText.StartsWith(rightText, StringComparison.Ordinal),
            "ends with" => leftText.EndsWith(rightText, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        _ => string.IsNullOrWhiteSpace(value.ToString()),
    };

    /// <summary>
    /// Finds an operator outside quotes.
    ///
    /// A naive search finds the <c>==</c> inside <c>"a == b"</c> and splits a string in half.
    /// </summary>
    private static int IndexOfOperator(string text, string op)
    {
        var quoted = false;
        var quote = '\0';

        for (var index = 0; index <= text.Length - op.Length; index++)
        {
            var character = text[index];

            if (character is '"' or '\'')
            {
                if (!quoted) { quoted = true; quote = character; }
                else if (character == quote) quoted = false;
                continue;
            }

            if (quoted) continue;

            if (string.CompareOrdinal(text, index, op, 0, op.Length) == 0)
            {
                // ">=" must win over ">", so the longer operators are tried first by the caller —
                // but "<" inside "<=" would still match here, and this guards the pair.
                if (op is ">" or "<" && index + 1 < text.Length && text[index + 1] == '=') continue;
                return index;
            }
        }

        return -1;
    }
}
