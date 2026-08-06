using System.Text;
using System.Text.Json.Nodes;
using ProofFlow.TestEngine.Redaction;

namespace ProofFlow.TestEngine.Variables;

/// <summary>
/// Turns <c>{{…}}</c> into values.
///
/// Three decisions that shape everything downstream.
///
/// **An unresolved reference is an error, not an empty string.** Substituting nothing turns
/// <c>Bearer {{secrets.token}}</c> into <c>Bearer </c>, the API answers 401, and the person spends
/// an afternoon looking at the wrong system. Failing with "there is no secret called token in this
/// environment" costs one second.
///
/// **A reference that is the whole value keeps its type.** <c>{"limit": "{{vars.pageSize}}"}</c>
/// should send `10`, not `"10"` — an API that validates its request body will reject the string,
/// and the person has no way to say "but as a number" in a text box.
///
/// **Every secret that is resolved is remembered.** That is the only moment ProofFlow knows which
/// characters in the outgoing request are a credential; afterwards a token in a URL is just text.
/// </summary>
public sealed class VariableResolver(VariableScopes scopes, RedactionScope? redaction = null)
{
    public const string EnvironmentScope = "environment";
    public const string SecretsScope = "secrets";
    public const string VariablesScope = "vars";
    public const string StepsScope = "steps";
    public const string DatasetScope = "dataset";
    public const string RunScope = "run";

    /// <summary>Substitutes every reference in a string. Throws on the first one that cannot be resolved.</summary>
    public string Resolve(string? text)
    {
        var result = TryResolve(text);
        if (result.Unresolved.Count > 0) throw new VariableResolutionException(result.Unresolved);
        return result.Text;
    }

    /// <summary>
    /// Substitutes what it can and reports what it could not, so a request builder can show the
    /// live preview with the missing pieces highlighted rather than refusing to render.
    /// </summary>
    public ResolutionResult TryResolve(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new ResolutionResult(text ?? string.Empty, []);

        var unresolved = new List<UnresolvedReference>();
        var builder = new StringBuilder();
        var last = 0;

        foreach (System.Text.RegularExpressions.Match match in VariableReference.Pattern.Matches(text))
        {
            builder.Append(text, last, match.Index - last);
            last = match.Index + match.Length;

            if (!VariableReference.TryParse(match.Groups[1].Value, out var reference))
            {
                unresolved.Add(new UnresolvedReference(match.Value,
                    $"«{match.Value}» is not a reference ProofFlow understands."));
                builder.Append(match.Value);
                continue;
            }

            var lookup = Find(reference);
            if (lookup.Found)
            {
                builder.Append(ToText(lookup.Value));
            }
            else
            {
                unresolved.Add(new UnresolvedReference(reference.Raw, lookup.Explanation!));
                builder.Append(reference.Raw);
            }
        }

        builder.Append(text, last, text.Length - last);
        return new ResolutionResult(builder.ToString(), unresolved);
    }

    /// <summary>
    /// Resolves a value that may be a whole reference on its own, preserving its JSON type.
    ///
    /// Used for request-body fields and node properties, where the difference between the number
    /// 10 and the string "10" is the difference between a 200 and a 400.
    /// </summary>
    public JsonNode? ResolveTyped(string? text)
    {
        if (string.IsNullOrEmpty(text)) return JsonValue.Create(text ?? string.Empty);

        var matches = VariableReference.Pattern.Matches(text);

        if (matches.Count == 1 && matches[0].Value.Length == text.Trim().Length
            && VariableReference.TryParse(matches[0].Groups[1].Value, out var only))
        {
            var lookup = Find(only);
            if (!lookup.Found) throw new VariableResolutionException(
                [new UnresolvedReference(only.Raw, lookup.Explanation!)]);

            return lookup.Value?.DeepClone();
        }

        // Mixed text and references: the result is a string, because "id-{{vars.n}}" cannot be
        // anything else.
        return JsonValue.Create(Resolve(text));
    }

    /// <summary>Every reference in the text that cannot currently be resolved. For live validation.</summary>
    public IReadOnlyList<UnresolvedReference> Validate(string? text) => TryResolve(text).Unresolved;

    private ScopeLookup Find(VariableReference reference)
    {
        var root = reference.Scope switch
        {
            EnvironmentScope => scopes.Environment,
            SecretsScope => scopes.Secrets,
            VariablesScope => scopes.Variables,
            StepsScope => scopes.Steps,
            DatasetScope => scopes.Dataset,
            RunScope => scopes.Run,
            _ => null,
        };

        if (root is null)
        {
            return ScopeLookup.Missing(
                $"«{reference.Scope}» is not something ProofFlow knows about. Use one of: " +
                $"{EnvironmentScope}, {SecretsScope}, {VariablesScope}, {StepsScope}, {DatasetScope}, {RunScope}.");
        }

        // Typed as JsonNode? rather than inferred: walking a path moves through objects, arrays and
        // leaf values alike, and the narrower inferred type refuses at the first array.
        JsonNode? node = root;
        var walked = reference.Scope;

        foreach (var segment in reference.Path)
        {
            switch (segment)
            {
                case PropertySegment property:
                    if (node is not JsonObject obj || !obj.TryGetPropertyValue(property.Name, out node))
                        return ScopeLookup.Missing(Explain(reference, walked, property.Name, node));

                    walked += $".{property.Name}";
                    break;

                case IndexSegment index:
                    if (node is not JsonArray array)
                        return ScopeLookup.Missing($"«{walked}» is not a list, so {segment} means nothing here.");

                    // A negative position counts from the end: [-1] is the last item.
                    var position = index.Index < 0 ? array.Count + index.Index : index.Index;

                    if (position < 0 || position >= array.Count)
                        return ScopeLookup.Missing(
                            $"«{walked}» has {array.Count} item(s), so there is no {segment}.");

                    node = array[position];
                    walked += segment.ToString();
                    break;
            }
        }

        // Remembered here, at the only moment the engine knows this text is a credential. After
        // this it is indistinguishable from any other characters in the request.
        if (reference.Scope == SecretsScope) redaction?.Remember(ToText(node));

        return ScopeLookup.Hit(node);
    }

    /// <summary>
    /// Says what is missing and, when it can, what was available instead.
    ///
    /// A typo in a variable name is the single most common thing that goes wrong here, and
    /// "environment has: baseUrl, apiVersion" turns a stuck person into an unstuck one.
    /// </summary>
    private static string Explain(VariableReference reference, string walked, string wanted, JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var available = obj.Select(p => p.Key).Take(12).ToList();
            var list = available.Count > 0 ? string.Join(", ", available) : "nothing";
            return $"«{walked}» has no «{wanted}». It has: {list}.";
        }

        return node is null
            ? $"«{walked}» has no value, so «{wanted}» cannot be read from it."
            : $"«{walked}» is a value, not an object, so it has no «{wanted}». (In {reference.Raw}.)";
    }

    /// <summary>
    /// A node as text for substitution into a string.
    ///
    /// A JSON string becomes its contents rather than its quoted form: <c>Bearer "abc"</c> is not
    /// what anyone meant by <c>Bearer {{secrets.token}}</c>. Objects and arrays keep their JSON,
    /// because there is nothing else they could sensibly become.
    /// </summary>
    private static string ToText(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    private readonly record struct ScopeLookup(bool Found, JsonNode? Value, string? Explanation)
    {
        public static ScopeLookup Hit(JsonNode? value) => new(true, value, null);
        public static ScopeLookup Missing(string explanation) => new(false, null, explanation);
    }
}

/// <summary>
/// What a scenario can refer to, as JSON so the whole thing is one uniform navigation problem.
///
/// <c>steps</c> grows as the run proceeds: each completed node writes its result under its own
/// name, which is what makes <c>{{steps.login.response.token}}</c> work at all.
/// </summary>
public sealed class VariableScopes
{
    public JsonObject Environment { get; init; } = [];
    public JsonObject Secrets { get; init; } = [];
    public JsonObject Variables { get; init; } = [];
    public JsonObject Steps { get; init; } = [];
    public JsonObject Dataset { get; init; } = [];
    public JsonObject Run { get; init; } = [];

    /// <summary>Records a completed step's output so later steps can read it.</summary>
    public void PublishStep(string stepName, JsonNode? result) => Steps[stepName] = result?.DeepClone();
}

public sealed record ResolutionResult(string Text, IReadOnlyList<UnresolvedReference> Unresolved)
{
    public bool IsComplete => Unresolved.Count == 0;
}

public sealed record UnresolvedReference(string Reference, string Explanation);

/// <summary>
/// Thrown when a step cannot be built because something it refers to is not there.
///
/// Carries every failure rather than the first: a request with three missing variables should tell
/// the person all three, not send them round the loop three times.
/// </summary>
public sealed class VariableResolutionException(IReadOnlyList<UnresolvedReference> unresolved)
    : Exception(BuildMessage(unresolved))
{
    public IReadOnlyList<UnresolvedReference> Unresolved { get; } = unresolved;

    private static string BuildMessage(IReadOnlyList<UnresolvedReference> unresolved) =>
        unresolved.Count == 1
            ? unresolved[0].Explanation
            : "Some references could not be resolved:\n" +
              string.Join("\n", unresolved.Select(u => $"  • {u.Reference} — {u.Explanation}"));
}
