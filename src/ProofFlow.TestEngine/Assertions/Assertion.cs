using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;
using Json.Schema;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.TestEngine.Assertions;

public enum AssertionKind
{
    StatusCode = 0,
    StatusIsSuccess = 1,
    Header = 2,
    JsonField = 3,
    Schema = 4,
    ResponseTime = 5,
    BodyContains = 6,
}

/// <summary>
/// One check against a response.
///
/// Deliberately not a general expression language. The brief's reader is somebody who does not
/// write code, and every check here is a sentence they can read back: this status, this header,
/// this field, under this many milliseconds. The moment assertions become expressions, a failing
/// test becomes a debugging problem in a language nobody documented.
/// </summary>
public sealed record Assertion
{
    public required AssertionKind Kind { get; init; }

    /// <summary>Header name, JSON path, or the schema — depending on the kind.</summary>
    public string? Target { get; init; }

    public MatcherKind Matcher { get; init; } = MatcherKind.Exact;

    /// <summary>What the value is expected to be, as JSON text.</summary>
    public string? Expected { get; init; }

    public double? Number { get; init; }
    public double? Number2 { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Shown instead of the generated sentence, when somebody wrote a better one.</summary>
    public string? Description { get; init; }
}

public sealed record AssertionOutcome(
    Assertion Assertion, bool Passed, string Summary, string? Detail = null);

/// <summary>
/// Runs assertions against a response and explains the failures in a sentence.
///
/// The explanation is the deliverable. "Assert failed: $.price" is a line a developer can work
/// with and a line nobody else can; "the field price was expected to be 120 but came back as 125"
/// is the same information written for the person who has to decide whether it matters.
/// </summary>
public static class AssertionEngine
{
    public static IReadOnlyList<AssertionOutcome> Run(
        IEnumerable<Assertion> assertions, HttpExchangeResult response)
    {
        var body = ParseBody(response);
        return [.. assertions.Where(a => a.Enabled).Select(a => Check(a, response, body))];
    }

    private static AssertionOutcome Check(Assertion assertion, HttpExchangeResult response, JsonNode? body)
    {
        if (!response.Succeeded)
        {
            // No response means every assertion about one is unanswerable. Reporting them as
            // failures would bury the single real cause under a list of consequences.
            return new AssertionOutcome(assertion, false,
                "the request never completed, so this could not be checked",
                response.Failure?.Message);
        }

        return assertion.Kind switch
        {
            AssertionKind.StatusCode => CheckStatus(assertion, response),
            AssertionKind.StatusIsSuccess => new AssertionOutcome(
                assertion,
                response.StatusCode is >= 200 and < 300,
                response.StatusCode is >= 200 and < 300
                    ? $"the status was {response.StatusCode}"
                    : $"the status was {response.StatusCode}, which is not a success"),

            AssertionKind.Header => CheckHeader(assertion, response),
            AssertionKind.JsonField => CheckJsonField(assertion, body),
            AssertionKind.Schema => CheckSchema(assertion, body),
            AssertionKind.ResponseTime => CheckTime(assertion, response),

            AssertionKind.BodyContains => new AssertionOutcome(
                assertion,
                assertion.Expected is { Length: > 0 } text && response.Body.Contains(text, StringComparison.Ordinal),
                assertion.Expected is { Length: > 0 } needle
                    ? response.Body.Contains(needle, StringComparison.Ordinal)
                        ? $"the response contains «{Trim(needle)}»"
                        : $"the response does not contain «{Trim(needle)}»"
                    : "no text was given to look for"),

            _ => new AssertionOutcome(assertion, false, "this check is not one ProofFlow knows"),
        };
    }

    private static AssertionOutcome CheckStatus(Assertion assertion, HttpExchangeResult response)
    {
        if (!int.TryParse(assertion.Expected, out var wanted))
            return new AssertionOutcome(assertion, false, "no status code was given for this check");

        var passed = response.StatusCode == wanted;

        return new AssertionOutcome(assertion, passed,
            passed
                ? $"the status was {wanted}, as expected"
                : $"the status was expected to be {wanted} but came back as {response.StatusCode}"
                  + (response.ReasonPhrase is { Length: > 0 } reason ? $" ({reason})" : string.Empty));
    }

    private static AssertionOutcome CheckHeader(Assertion assertion, HttpExchangeResult response)
    {
        if (assertion.Target is not { Length: > 0 } name)
            return new AssertionOutcome(assertion, false, "no header name was given for this check");

        var header = response.ResponseHeaders
            .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));

        if (header is null)
            return new AssertionOutcome(assertion, false, $"the response has no «{name}» header");

        var complaint = Matcher.Check(
            assertion.Matcher,
            new ComparisonRule
            {
                Path = name, Kind = assertion.Matcher, Text = assertion.Expected,
                Number = assertion.Number, Number2 = assertion.Number2,
            },
            JsonValue.Create(assertion.Expected),
            JsonValue.Create(header.Value));

        return new AssertionOutcome(assertion, complaint is null,
            complaint is null
                ? $"the «{name}» header is as expected"
                : $"the «{name}» header: {complaint}");
    }

    private static AssertionOutcome CheckJsonField(Assertion assertion, JsonNode? body)
    {
        if (assertion.Target is not { Length: > 0 } path)
            return new AssertionOutcome(assertion, false, "no field was given for this check");

        if (body is null)
            return new AssertionOutcome(assertion, false, "the response was not JSON, so it has no fields");

        // JsonPath.Net here rather than the diff's own pattern matcher: the question is genuinely
        // "find me the nodes at this expression", which is what a JSON Path library is for.
        if (!JsonPath.TryParse(path, out var jsonPath))
            return new AssertionOutcome(assertion, false, $"«{path}» is not a valid JSON path");

        var matches = jsonPath.Evaluate(body).Matches;

        if (matches is null || matches.Count == 0)
        {
            var wanted = assertion.Matcher is MatcherKind.NotExists;
            return new AssertionOutcome(assertion, wanted,
                wanted ? $"there is no {path}, as expected" : $"the response has nothing at {path}");
        }

        if (matches.Count > 1)
        {
            // Several matches and one expectation is ambiguous. Saying so beats silently checking
            // the first one and reporting a pass that was never asked for.
            return new AssertionOutcome(assertion, false,
                $"{path} matched {matches.Count} places; narrow it to one");
        }

        var actual = matches[0].Value;
        var expected = Parse(assertion.Expected);

        var complaint = Matcher.Check(
            assertion.Matcher,
            new ComparisonRule
            {
                Path = path, Kind = assertion.Matcher, Text = assertion.Expected,
                Number = assertion.Number, Number2 = assertion.Number2,
            },
            expected, actual);

        if (assertion.Matcher == MatcherKind.Exact)
        {
            var same = Matcher.Equal(expected, actual);
            return new AssertionOutcome(assertion, same,
                same
                    ? $"{Name(path)} is {Matcher.Describe(actual)}, as expected"
                    : $"{Name(path)} was expected to be {Matcher.Describe(expected)} "
                      + $"but came back as {Matcher.Describe(actual)}");
        }

        return new AssertionOutcome(assertion, complaint is null,
            complaint is null ? $"{Name(path)} is as expected" : $"{Name(path)}: {complaint}");
    }

    private static AssertionOutcome CheckSchema(Assertion assertion, JsonNode? body)
    {
        if (assertion.Expected is not { Length: > 0 } text)
            return new AssertionOutcome(assertion, false, "no schema was given for this check");

        if (body is null)
            return new AssertionOutcome(assertion, false, "the response was not JSON, so no schema applies");

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(text);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new AssertionOutcome(assertion, false, "the schema itself is not valid JSON Schema", ex.Message);
        }

        var evaluation = schema.Evaluate(
            System.Text.Json.JsonSerializer.SerializeToElement(body),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        if (evaluation.IsValid)
            return new AssertionOutcome(assertion, true, "the response matches the schema");

        // The first few, named by where they are. A wall of every nested violation is unreadable,
        // and the first one is almost always the cause of the rest.
        var problems = Flatten(evaluation)
            .Where(node => node.Errors is { Count: > 0 })
            .SelectMany(node => node.Errors!.Select(error =>
                $"{node.InstanceLocation}: {error.Value}"))
            .Take(5)
            .ToList();

        return new AssertionOutcome(assertion, false,
            problems.Count == 0
                ? "the response does not match the schema"
                : $"the response does not match the schema — {problems[0]}",
            problems.Count > 1 ? string.Join("\n", problems) : null);
    }

    private static AssertionOutcome CheckTime(Assertion assertion, HttpExchangeResult response)
    {
        var limit = assertion.Number ?? 0;
        var actual = response.Duration.TotalMilliseconds;
        var passed = actual <= limit;

        return new AssertionOutcome(assertion, passed,
            passed
                ? $"the response took {actual:0}ms, within {limit:0}ms"
                : $"the response took {actual:0}ms, over the {limit:0}ms limit");
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        // Details is null rather than empty for a leaf in this library's shape.
        foreach (var child in results.Details ?? [])
        {
            foreach (var descendant in Flatten(child)) yield return descendant;
        }
    }

    private static JsonNode? ParseBody(HttpExchangeResult response)
    {
        if (!response.Succeeded || string.IsNullOrWhiteSpace(response.Body)) return null;

        try
        {
            return JsonNode.Parse(response.Body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads an expected value as JSON, falling back to treating it as text.
    ///
    /// Somebody typing <c>Electronics</c> into a box means the string; somebody typing <c>120</c>
    /// means the number. Requiring quotes around one and not the other is a distinction the reader
    /// this product is for should not have to know about.
    /// </summary>
    private static JsonNode? Parse(string? text)
    {
        if (text is null) return null;

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>The field's own name, for a sentence — "$.items[0].price" reads as "price".</summary>
    private static string Name(string path)
    {
        var cut = path.LastIndexOfAny(['.', '[']);
        var leaf = cut < 0 ? path : path[(cut + 1)..].TrimEnd(']').Trim('\'', '"');
        return leaf.Length == 0 ? path : $"the field {leaf}";
    }

    private static string Trim(string value) => value.Length <= 60 ? value : value[..60] + "…";
}
