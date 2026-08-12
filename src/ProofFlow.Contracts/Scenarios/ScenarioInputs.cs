using System.Text.Json;

namespace ProofFlow.Contracts.Scenarios;

/// <summary>
/// Reading a scenario's inputs, and settling what a run was actually given.
///
/// One place, because four callers need the same answer and would each get it slightly wrong: the
/// page that draws the form, the controller that starts a run from it, the API a build agent posts
/// to, and the packer that sends a job to an agent. A default applied in three of those and
/// forgotten in the fourth is a run that fails only from a pipeline.
/// </summary>
public static class ScenarioInputs
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The definitions a scenario carries, or nothing at all.</summary>
    public static IReadOnlyList<ScenarioInputDto> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ScenarioInputDto>>(json, Json)?
                .Where(input => !string.IsNullOrWhiteSpace(input.Name))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            // Unreadable definitions mean no form and no substitution, which surfaces as an
            // unresolved reference naming the input — better than a page that will not render.
            return [];
        }
    }

    public static string Write(IEnumerable<ScenarioInputDto> inputs) =>
        JsonSerializer.Serialize(inputs, Json);

    /// <summary>The values a run should use: what was supplied, with defaults where it was not.</summary>
    public static Dictionary<string, string> Settle(
        IReadOnlyList<ScenarioInputDto> defined, IReadOnlyDictionary<string, string?>? supplied)
    {
        var settled = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in defined)
        {
            var given = supplied is not null && supplied.TryGetValue(input.Name, out var value)
                ? value
                : null;

            // An empty box is not an answer. Somebody who clears a field and presses Run means the
            // default, not the empty string — and a required input with an empty box is refused
            // below rather than sent as nothing.
            settled[input.Name] = string.IsNullOrWhiteSpace(given)
                ? input.Default ?? string.Empty
                : given;
        }

        return settled;
    }

    /// <summary>
    /// The required inputs still without a value, by name.
    ///
    /// Checked before a run is queued. A scenario that starts and dies on its first step because
    /// nobody filled a box is a red run in the history that means nothing and has to be explained.
    /// </summary>
    public static IReadOnlyList<string> Missing(
        IReadOnlyList<ScenarioInputDto> defined, IReadOnlyDictionary<string, string> settled) =>
        [.. defined
            .Where(input => input.Required)
            .Where(input => !settled.TryGetValue(input.Name, out var value)
                            || string.IsNullOrWhiteSpace(value))
            .Select(input => input.Name)];

    /// <summary>What a run recorded it was given. Same shape going in and coming out.</summary>
    public static Dictionary<string, string> ReadValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string WriteValues(IReadOnlyDictionary<string, string> values) =>
        JsonSerializer.Serialize(values, Json);
}
