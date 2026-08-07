using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using ProofFlow.Domain.Runs;

namespace ProofFlow.TestEngine.Running;

/// <summary>
/// Shaping values, and the two checks that need more than a matcher.
///
/// These exist so that nobody has to write code to get from what an API returned to what the next
/// step needs. Each one is a verb somebody would say out loud — sort this, keep the ones where,
/// take the second, merge these two — and each is a node on the canvas rather than a line in a
/// script box.
/// </summary>
public sealed partial class NodeExecutors
{
    // ---- lists ---------------------------------------------------------------------------------

    private static NodeOutcome FilterList(NodeContext context)
    {
        if (context.Input("list") is not JsonArray list) return NodeOutcome.Failed("That is not a list.");

        var condition = context.Property("condition");
        if (string.IsNullOrWhiteSpace(condition)) return NodeOutcome.Failed("No condition was given.");

        var kept = new JsonArray();

        foreach (var item in list)
        {
            if (Expressions.IsTrue(Bind(condition, item))) kept.Add(item?.DeepClone());
        }

        return NodeOutcome.Ok(("list", kept));
    }

    /// <summary>
    /// Puts the current item into a condition written as <c>item.active == true</c>.
    ///
    /// Substitution rather than a second variable scope: the condition is evaluated once per item,
    /// and a scope that had to be pushed and popped two thousand times would be two thousand
    /// chances to leave it set.
    /// </summary>
    private static string Bind(string condition, JsonNode? item)
    {
        var builder = new StringBuilder();
        var index = 0;

        while (index < condition.Length)
        {
            if (condition[index] == 'i' && condition.AsSpan(index).StartsWith("item")
                && (index == 0 || !char.IsLetterOrDigit(condition[index - 1])))
            {
                var end = index + 4;
                while (end < condition.Length && (condition[end] == '.' || condition[end] == '_'
                                                  || char.IsLetterOrDigit(condition[end])))
                {
                    end++;
                }

                var path = condition[(index + 4)..end].TrimStart('.');
                var value = path.Length == 0 ? item : Read(item, "$." + path);

                builder.Append(Quote(value));
                index = end;
                continue;
            }

            builder.Append(condition[index]);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>A value as the expression language would read it back.</summary>
    private static string Quote(JsonNode? value) => value switch
    {
        null => "null",
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        JsonValue scalar when scalar.TryGetValue<double>(out var number) =>
            number.ToString(CultureInfo.InvariantCulture),
        JsonArray or JsonObject => value.ToJsonString(),
        _ => $"\"{value}\"",
    };

    private static NodeOutcome SortList(NodeContext context)
    {
        if (context.Input("list") is not JsonArray list) return NodeOutcome.Failed("That is not a list.");

        var by = context.Property("by");
        if (string.IsNullOrWhiteSpace(by)) return NodeOutcome.Failed("No field to sort by was named.");

        var path = by.StartsWith('$') ? by : "$." + by.TrimStart('.');

        var ordered = list
            .Select(item => (Item: item, Key: Read(item, path)))
            .OrderBy(entry => entry.Key, SortKeys.Instance)
            .Select(entry => entry.Item?.DeepClone())
            .ToList();

        if (context.Property("direction") == "descending") ordered.Reverse();

        return NodeOutcome.Ok(("list", new JsonArray([.. ordered])));
    }

    /// <summary>
    /// Orders numbers as numbers and everything else as text.
    ///
    /// A version column of 2, 10, 11 sorted as text gives 10, 11, 2, and the person looking at the
    /// list would be right to say the sort is broken.
    /// </summary>
    private sealed class SortKeys : IComparer<JsonNode?>
    {
        public static readonly SortKeys Instance = new();

        public int Compare(JsonNode? left, JsonNode? right)
        {
            if (left is null) return right is null ? 0 : -1;
            if (right is null) return 1;

            if (left is JsonValue a && right is JsonValue b
                && a.TryGetValue<double>(out var first) && b.TryGetValue<double>(out var second))
            {
                return first.CompareTo(second);
            }

            return string.CompareOrdinal(left.ToString(), right.ToString());
        }
    }

    // ---- objects -------------------------------------------------------------------------------

    private static NodeOutcome MapFields(NodeContext context)
    {
        if (context.Input("json") is not JsonNode source) return NodeOutcome.Failed("There is nothing to map.");

        var mapping = Pairs(context.Property("mapping"));
        if (mapping.Count == 0) return NodeOutcome.Failed("No fields were mapped.");

        var mapped = new JsonObject();

        foreach (var (from, to) in mapping)
        {
            var path = from.StartsWith('$') ? from : "$." + from.TrimStart('.');
            mapped[to] = Read(source, path)?.DeepClone();
        }

        return NodeOutcome.Ok(("json", mapped));
    }

    private static NodeOutcome Merge(NodeContext context)
    {
        if (context.Input("first") is not JsonObject first || context.Input("second") is not JsonObject second)
            return NodeOutcome.Failed("Both sides have to be objects to merge.");

        var onConflict = context.Property("onConflict") ?? "second";
        var merged = first.DeepClone().AsObject();

        foreach (var (name, value) in second)
        {
            if (merged.ContainsKey(name))
            {
                if (onConflict == "fail")
                    return NodeOutcome.Failed($"Both sides have «{name}», and this step was told to stop.");

                if (onConflict == "first") continue;
            }

            merged[name] = value?.DeepClone();
        }

        return NodeOutcome.Ok(("json", merged));
    }

    // ---- the rest ------------------------------------------------------------------------------

    private static NodeOutcome ExtractCookie(NodeContext context)
    {
        var wanted = context.Property("cookie");
        if (string.IsNullOrWhiteSpace(wanted)) return NodeOutcome.Failed("No cookie was named.");

        if (context.Input("response")?["headers"] is not JsonObject headers)
            return NodeOutcome.Failed("There is no response to read.");

        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var candidate in (value?.ToString() ?? string.Empty).Split('\n'))
            {
                var pair = candidate.Split(';', 2)[0];
                var at = pair.IndexOf('=');
                if (at <= 0) continue;

                if (string.Equals(pair[..at].Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    return NodeOutcome.Ok(("value", JsonValue.Create(pair[(at + 1)..].Trim())));
            }
        }

        return NodeOutcome.Failed($"The response set no «{wanted}» cookie.");
    }

    private async Task<NodeOutcome> DatasetRow(NodeContext context)
    {
        var reference = context.Property("dataSet");

        // No data set named means the row the surrounding loop is on, which is what the node's help
        // says and what makes it useful inside a data-set loop without repeating the reference.
        if (string.IsNullOrWhiteSpace(reference))
        {
            var current = context.Resolver.Validate("{{dataset.current}}").Count == 0
                ? context.Resolver.ResolveTyped("{{dataset.current}}")
                : null;

            return current is null
                ? NodeOutcome.Failed("There is no row here — this step is not inside a data-set loop.")
                : NodeOutcome.Ok(("row", current.DeepClone()),
                                 ("key", JsonValue.Create(context.LoopKey)));
        }

        var rows = await services.DataSetRowsAsync(reference, context.Cancellation);
        if (rows.Count == 0) return NodeOutcome.Failed("That data set has no rows.");

        return NodeOutcome.Ok(("row", rows[0].DeepClone()), ("key", JsonValue.Create("1")));
    }

    /// <summary>
    /// Makes up a value.
    ///
    /// Seeded on request, and the seed is the whole point: a generated value that differs every run
    /// is a value no baseline can hold, so a scenario that needs a stable answer says so once here
    /// rather than adding an ignore rule for every field it touches.
    /// </summary>
    private static NodeOutcome Generate(NodeContext context)
    {
        var seed = context.Property("seed");
        var random = string.IsNullOrEmpty(seed)
            ? Random.Shared
            : new Random(StableHash(seed + context.Node.Id + context.Iteration));

        var words = (string[])
            ["amber", "birch", "cedar", "delta", "ember", "flint", "grove", "hazel", "indigo", "juniper"];

        var value = context.Property("kind") switch
        {
            "email" => $"{words[random.Next(words.Length)]}.{random.Next(1000, 9999)}@example.test",
            "name" => $"{Capital(words[random.Next(words.Length)])} {Capital(words[random.Next(words.Length)])}",
            "number" => random.Next(1, 1_000_000).ToString(CultureInfo.InvariantCulture),
            "date" => DateOnly.FromDateTime(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddDays(random.Next(0, 3650))).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "word" => words[random.Next(words.Length)],
            "sentence" => string.Join(' ', Enumerable.Range(0, 6)
                .Select(_ => words[random.Next(words.Length)])) + ".",
            _ => Uuid(random),
        };

        return NodeOutcome.Ok(("value", JsonValue.Create(value)));
    }

    private static string Capital(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// A UUID from the given source of randomness, so a seeded run produces the same one.
    ///
    /// <c>Guid.NewGuid</c> could not do that, and "same seed, same value" is the property the seed
    /// exists for.
    /// </summary>
    private static string Uuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes).ToString();
    }

    /// <summary>A hash that does not change between runs, which <c>string.GetHashCode</c> does.</summary>
    private static int StableHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return BitConverter.ToInt32(bytes, 0);
    }

    // ---- the two remaining checks --------------------------------------------------------------

    private static NodeOutcome AssertSchema(NodeContext context)
    {
        var text = context.Property("schema");
        if (string.IsNullOrWhiteSpace(text))
            return Verdict(context, new("No schema was given.", false, context.Flag("soft")));

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(text);
        }
        catch (JsonException ex)
        {
            return Verdict(context, new($"That schema could not be read: {ex.Message}",
                false, context.Flag("soft")));
        }

        var response = context.Input("response");
        var body = response?["body"] ?? response;

        var result = schema.Evaluate(body.Deserialize<JsonElement>(), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (result.IsValid)
        {
            return Verdict(context, new("It matches the schema.", true, context.Flag("soft"),
                null, null, "schema"));
        }

        // The first few complaints, named by where they are. All of them would be a paragraph
        // nobody reads, and the location is the part somebody acts on.
        var complaints = (result.Details ?? [])
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error =>
                $"{detail.InstanceLocation}: {error.Value}"))
            .Distinct()
            .Take(5)
            .ToArray();

        return Verdict(context, new(
            complaints.Length == 0
                ? "It does not match the schema."
                : $"It does not match the schema — {string.Join("; ", complaints)}",
            false, context.Flag("soft"), null, null, "schema"));
    }

    private static NodeOutcome AssertListContains(NodeContext context)
    {
        var wanted = context.Property("value") ?? string.Empty;

        if (context.Input("list") is not JsonArray list)
            return Verdict(context, new("That is not a list.", false, context.Flag("soft")));

        var path = context.Property("path");
        var found = false;

        foreach (var item in list)
        {
            var value = string.IsNullOrWhiteSpace(path)
                ? item
                : Read(item, path.StartsWith('$') ? path : "$." + path.TrimStart('.'));

            var text = value is JsonValue scalar && scalar.TryGetValue<string>(out var str)
                ? str
                : value?.ToJsonString();

            if (text == wanted || value?.ToString() == wanted)
            {
                found = true;
                break;
            }
        }

        return Verdict(context, new(
            found
                ? $"The list holds «{wanted}»."
                : $"None of the {list.Count} items is «{wanted}».",
            found, context.Flag("soft"), wanted, null, path));
    }

    // ---- keeping things ------------------------------------------------------------------------

    private static NodeOutcome Attach(NodeContext context)
    {
        var name = context.Property("name");
        if (string.IsNullOrWhiteSpace(name)) return NodeOutcome.Failed("The attachment has no name.");

        var value = context.Input("value");
        var content = value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            ? text
            : value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? string.Empty;

        // Redaction defaults to on, and this is the node where that matters most: an attachment
        // outlives the run and ends up in a report somebody forwards.
        context.Attach(name, content, context.Property("redact") is not "false");

        return NodeOutcome.Ok();
    }

    private async Task<NodeOutcome> CaptureBaseline(NodeContext context)
    {
        var reference = context.Property("baseline");
        if (string.IsNullOrWhiteSpace(reference)) return NodeOutcome.Failed("No baseline was named.");

        var response = context.Input("response");
        var body = response?["text"]?.ToString();
        if (body is null) return NodeOutcome.Failed("There is no answer to record.");

        var approve = context.Flag("approve");

        var answer = new CapturedAnswer(
            body,
            response?["headers"]?["Content-Type"]?.ToString(),
            response?["statusCode"]?.GetValue<int>() ?? 0,
            response?["durationMs"]?.GetValue<double>() ?? 0,
            response?["url"]?.ToString());

        await services.CaptureBaselineAsync(reference, context.LoopKey, answer, approve, context.Cancellation);

        context.Log(RunEventLevel.Info,
            approve
                ? "Recorded, and approved as the answer to compare against."
                : "Recorded, and waiting for somebody to approve it.",
            null);

        return NodeOutcome.Ok();
    }

    /// <summary>Key/value properties as the editor stores them: name on the left, value on the right.</summary>
    private static IReadOnlyList<(string Name, string Value)> Pairs(string? json) => ReadPairs(json);
}
