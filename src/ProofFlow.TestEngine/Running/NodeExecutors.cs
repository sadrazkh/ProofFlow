using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Path;
using ProofFlow.Domain.Runs;
using ProofFlow.TestEngine.Comparison;

namespace ProofFlow.TestEngine.Running;

/// <summary>
/// What each kind of node actually does.
///
/// A table from node key to a function, for the same reason the catalogue is data: seventy classes
/// would be seventy files to find, and the runner would need a factory to reach them. The container
/// and branching nodes are not here — those are the runner's own business, because they decide
/// where control goes rather than doing something.
///
/// Everything a node needs from outside arrives through <see cref="IRunServices"/>, so this file
/// has no database, no HTTP client and no clock of its own.
///
/// One instance per run, not a singleton: the auth half of this class holds the credentials a
/// scenario picked up on its way through, and two runs sharing those would be one run signing in as
/// the other.
/// </summary>
public sealed partial class NodeExecutors(IRunServices services)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public bool Handles(string key) => Table.ContainsKey(key);

    public Task<NodeOutcome> RunAsync(NodeContext context) =>
        Table.TryGetValue(context.Node.Key, out var run)
            ? run(this, context)
            : Task.FromResult(NodeOutcome.Ok());

    private static readonly Dictionary<string, Func<NodeExecutors, NodeContext, Task<NodeOutcome>>> Table =
        new(StringComparer.Ordinal)
        {
            ["core.start"] = (_, _) => Done(NodeOutcome.Ok()),
            ["core.end"] = (_, c) => Done(End(c)),
            ["core.abort"] = (_, c) => Done(NodeOutcome.Ends(NodeVerdict.Failed, c.Property("reason"))),
            ["core.comment"] = (_, _) => Done(NodeOutcome.Skipped()),
            ["core.checkpoint"] = (_, c) => Done(Checkpoint(c)),
            ["core.log"] = (_, c) => Done(Log(c)),
            ["core.delay"] = (_, c) => Delay(c),
            ["core.setVariable"] = (_, c) => Done(SetVariable(c)),
            ["core.expression"] = (_, c) => Done(Expression(c)),

            ["http.request"] = (e, c) => e.Request(c),
            ["http.graphql"] = (e, c) => e.GraphQl(c),

            ["data.extractJsonPath"] = (_, c) => Done(ExtractPath(c)),
            ["data.extractHeader"] = (_, c) => Done(ExtractHeader(c)),
            ["data.extractStatus"] = (_, c) => Done(ExtractStatus(c)),
            ["data.extractRegex"] = (_, c) => Done(ExtractRegex(c)),
            ["data.jsonParse"] = (_, c) => Done(ParseJson(c)),
            ["data.jsonStringify"] = (_, c) => Done(Stringify(c)),
            ["data.count"] = (_, c) => Done(Count(c)),
            ["data.pickIndex"] = (_, c) => Done(Pick(c)),
            ["data.template"] = (_, c) => Done(Template(c)),
            ["data.hash"] = (_, c) => Done(Hash(c)),
            ["data.base64"] = (_, c) => Done(Base64(c)),
            ["data.extractCookie"] = (_, c) => Done(ExtractCookie(c)),
            ["data.filterList"] = (_, c) => Done(FilterList(c)),
            ["data.sortList"] = (_, c) => Done(SortList(c)),
            ["data.mapFields"] = (_, c) => Done(MapFields(c)),
            ["data.merge"] = (_, c) => Done(Merge(c)),
            ["data.generate"] = (_, c) => Done(Generate(c)),
            ["data.datasetRow"] = (e, c) => e.DatasetRow(c),

            ["assert.status"] = (_, c) => Done(AssertStatus(c)),
            ["assert.header"] = (_, c) => Done(AssertHeader(c)),
            ["assert.jsonField"] = (_, c) => Done(AssertField(c)),
            ["assert.responseTime"] = (_, c) => Done(AssertTime(c)),
            ["assert.bodyContains"] = (_, c) => Done(AssertContains(c)),
            ["assert.notNull"] = (_, c) => Done(AssertNotNull(c)),
            ["assert.matchesRegex"] = (_, c) => Done(AssertRegex(c)),
            ["assert.listCount"] = (_, c) => Done(AssertListCount(c)),
            ["assert.listContains"] = (_, c) => Done(AssertListContains(c)),
            ["assert.jsonSchema"] = (_, c) => Done(AssertSchema(c)),

            ["baseline.compare"] = (e, c) => e.CompareBaseline(c),
            ["baseline.capture"] = (e, c) => e.CaptureBaseline(c),

            ["auth.basic"] = (e, c) => Done(e.Basic(c)),
            ["auth.bearer"] = (e, c) => Done(e.Bearer(c)),
            ["auth.apiKey"] = (e, c) => Done(e.ApiKey(c)),
            ["auth.setHeader"] = (e, c) => Done(e.SetHeader(c)),
            ["auth.cookieJar"] = (e, c) => Done(e.CookieJar(c)),
            ["auth.signHmac"] = (_, c) => Done(SignHmac(c)),
            ["auth.login"] = (e, c) => e.Login(c),
            ["auth.oauth2ClientCredentials"] = (e, c) => e.ClientCredentials(c),
            ["auth.oauth2Password"] = (e, c) => e.PasswordGrant(c),
            ["auth.oauth2Refresh"] = (e, c) => e.RefreshGrant(c),

            ["test.softFail"] = (_, c) => Done(SoftFail(c)),
            ["test.tag"] = (_, c) => Done(Tag(c)),
            ["test.attach"] = (_, c) => Done(Attach(c)),
        };

    private static Task<NodeOutcome> Done(NodeOutcome outcome) => Task.FromResult(outcome);

    // ---- core --------------------------------------------------------------------------------

    private static NodeOutcome End(NodeContext context) => context.Property("outcome") switch
    {
        "failed" => NodeOutcome.Ends(NodeVerdict.Failed, context.Property("reason")),
        "skipped" => NodeOutcome.Ends(NodeVerdict.Skipped),
        _ => NodeOutcome.Ends(NodeVerdict.Passed),
    };

    private static NodeOutcome Checkpoint(NodeContext context)
    {
        context.Log(RunEventLevel.Info, context.Property("name") ?? context.Node.Name, null);
        return NodeOutcome.Ok();
    }

    private static NodeOutcome Log(NodeContext context)
    {
        var level = context.Property("level") switch
        {
            "warning" => RunEventLevel.Warning,
            "error" => RunEventLevel.Error,
            _ => RunEventLevel.Info,
        };

        context.Log(level, context.Property("message") ?? string.Empty, null);
        return NodeOutcome.Ok();
    }

    private static async Task<NodeOutcome> Delay(NodeContext context)
    {
        var duration = context.Duration("duration", TimeSpan.FromSeconds(1));

        // Capped, because a scenario that waits an hour on a build agent is a scenario nobody
        // notices until the agent is gone.
        await Task.Delay(duration > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : duration,
            context.Cancellation);

        return NodeOutcome.Ok();
    }

    private static NodeOutcome SetVariable(NodeContext context)
    {
        var name = context.Property("name");
        if (string.IsNullOrWhiteSpace(name)) return NodeOutcome.Failed("This step has no name to set.");

        // Typed rather than always text: a variable set from a count and then compared with a
        // number should compare as a number.
        var value = context.Resolver.ResolveTyped(context.Raw("value"));

        context.SetVariable(name, value);
        return NodeOutcome.Ok(("value", value));
    }

    /// <summary>
    /// The small expression language: a value, optionally piped through one of a few verbs.
    ///
    /// Deliberately not a scripting engine. ProofFlow runs other people's tests, so a node that
    /// evaluated arbitrary code would be a way to run arbitrary code on whatever runs them.
    /// </summary>
    private static NodeOutcome Expression(NodeContext context)
    {
        var value = context.Property("expression") ?? string.Empty;
        return NodeOutcome.Ok(("result", Expressions.Evaluate(value)));
    }

    // ---- http --------------------------------------------------------------------------------

    private async Task<NodeOutcome> Request(NodeContext context)
    {
        var url = context.Property("url");
        if (string.IsNullOrWhiteSpace(url)) return NodeOutcome.Failed("This step has no address.");

        var (dressed, headers) = Dress(Query(url, context.Property("query")),
            ReadPairs(context.Property("headers")));

        var request = new HttpNodeRequest(
            context.Property("method") ?? "GET",
            dressed,
            headers,
            context.Property("bodyKind") is "none" or null ? null : context.Property("body"),
            context.Property("bodyKind"),
            context.Raw("timeoutSeconds") is null ? null : context.Duration("timeoutSeconds", TimeSpan.FromSeconds(30)));

        var result = await services.SendAsync(request, context.Cancellation);

        if (!result.Succeeded)
        {
            context.Log(RunEventLevel.Error, result.Failure ?? "The request did not complete.", null);
            return NodeOutcome.Failed(result.Failure ?? "The request did not complete.");
        }

        Harvest(result);

        context.Log(RunEventLevel.Info,
            $"{request.Method} {result.ResolvedUrl} → {result.StatusCode}",
            new JsonObject { ["status"] = result.StatusCode, ["ms"] = Math.Round(result.DurationMs) });

        return NodeOutcome.Ok(("response", Response(result)));
    }

    private async Task<NodeOutcome> GraphQl(NodeContext context)
    {
        var url = context.Property("url");
        if (string.IsNullOrWhiteSpace(url)) return NodeOutcome.Failed("This step has no address.");

        var payload = new JsonObject { ["query"] = context.Property("query") ?? string.Empty };

        if (context.Property("variables") is { Length: > 0 } variables)
        {
            try
            {
                payload["variables"] = JsonNode.Parse(variables);
            }
            catch (JsonException)
            {
                return NodeOutcome.Failed("The variables are not valid JSON.");
            }
        }

        var own = ReadPairs(context.Property("headers")).ToList();
        own.Add(("Content-Type", "application/json"));

        var (dressed, headers) = Dress(url, own);

        var result = await services.SendAsync(
            new HttpNodeRequest("POST", dressed, headers, payload.ToJsonString(), "json", null),
            context.Cancellation);

        if (!result.Succeeded) return NodeOutcome.Failed(result.Failure ?? "The request did not complete.");

        Harvest(result);

        // A GraphQL server answers 200 with an "errors" array, so the status is not the verdict.
        var response = Response(result);
        var errors = response["body"]?["errors"] as JsonArray;

        if (errors is { Count: > 0 })
        {
            return NodeOutcome.Failed(
                $"The query came back with {errors.Count} error(s): {errors[0]?["message"]}",
                new Dictionary<string, JsonNode?> { ["response"] = response });
        }

        return NodeOutcome.Ok(("response", response));
    }

    /// <summary>
    /// A response as the rest of the graph sees it.
    ///
    /// One shape, so <c>{{steps.login.response.body.token}}</c> means the same thing after every
    /// kind of request.
    /// </summary>
    private static JsonNode Response(HttpNodeResult result)
    {
        var headers = new JsonObject();
        foreach (var (name, value) in result.Headers) headers[name] = value;

        JsonNode? body = null;
        try
        {
            body = JsonNode.Parse(result.Body);
        }
        catch (JsonException)
        {
            // Not JSON. Kept as text rather than refused: an HTML error page from a proxy is
            // exactly the thing somebody needs to see.
        }

        return new JsonObject
        {
            ["statusCode"] = result.StatusCode,
            ["reason"] = result.ReasonPhrase,
            ["headers"] = headers,
            ["body"] = body ?? JsonValue.Create(result.Body),
            ["text"] = result.Body,
            ["durationMs"] = Math.Round(result.DurationMs, 1),
            ["url"] = result.ResolvedUrl,
        };
    }

    // ---- data --------------------------------------------------------------------------------

    private static NodeOutcome ExtractPath(NodeContext context)
    {
        var response = context.Input("response");
        var document = response?["body"] ?? response;
        var path = context.Property("path");

        if (string.IsNullOrWhiteSpace(path)) return NodeOutcome.Failed("No field was named.");

        var found = Read(document, path);

        if (found is null)
        {
            return context.Property("onMissing") switch
            {
                "null" => NodeOutcome.Ok(("value", null)),
                "default" => NodeOutcome.Ok(("value", JsonValue.Create(context.Property("default")))),
                _ => NodeOutcome.Failed($"There is no «{path}» in the response."),
            };
        }

        return NodeOutcome.Ok(("value", found.DeepClone()));
    }

    private static NodeOutcome ExtractHeader(NodeContext context)
    {
        var name = context.Property("header");
        var headers = context.Input("response")?["headers"] as JsonObject;

        if (name is null || headers is null) return NodeOutcome.Failed("No header was named.");

        var match = headers.FirstOrDefault(
            pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));

        return match.Value is null
            ? NodeOutcome.Failed($"The response has no «{name}» header.")
            : NodeOutcome.Ok(("value", match.Value.DeepClone()));
    }

    private static NodeOutcome ExtractStatus(NodeContext context) =>
        NodeOutcome.Ok(("value", context.Input("response")?["statusCode"]?.DeepClone()));

    private static NodeOutcome ExtractRegex(NodeContext context)
    {
        var text = context.Input("text")?.ToString() ?? string.Empty;
        var pattern = context.Property("pattern");

        if (string.IsNullOrWhiteSpace(pattern)) return NodeOutcome.Failed("No pattern was given.");

        try
        {
            // A bounded timeout: a pattern somebody pasted can backtrack for minutes on a long
            // body, and this runs on a shared machine.
            var match = Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
            if (!match.Success) return NodeOutcome.Failed("The pattern did not match.");

            var group = context.Number("group", 1);
            return NodeOutcome.Ok(("value", JsonValue.Create(
                group < match.Groups.Count ? match.Groups[group].Value : match.Value)));
        }
        catch (ArgumentException)
        {
            return NodeOutcome.Failed("That pattern could not be read.");
        }
        catch (RegexMatchTimeoutException)
        {
            return NodeOutcome.Failed("That pattern took too long on this text.");
        }
    }

    private static NodeOutcome ParseJson(NodeContext context)
    {
        try
        {
            return NodeOutcome.Ok(("json", JsonNode.Parse(context.Input("text")?.ToString() ?? "null")));
        }
        catch (JsonException ex)
        {
            return NodeOutcome.Failed($"That is not valid JSON: {ex.Message}");
        }
    }

    private static NodeOutcome Stringify(NodeContext context) =>
        NodeOutcome.Ok(("text", JsonValue.Create(context.Input("json")?.ToJsonString(
            new JsonSerializerOptions { WriteIndented = context.Flag("indent") }))));

    private static NodeOutcome Count(NodeContext context) =>
        NodeOutcome.Ok(("count", JsonValue.Create(
            context.Input("list") is JsonArray array ? array.Count : 0)));

    private static NodeOutcome Pick(NodeContext context)
    {
        if (context.Input("list") is not JsonArray array) return NodeOutcome.Failed("That is not a list.");

        var index = context.Number("index", 0);
        if (index < 0) index = array.Count + index;

        return index >= 0 && index < array.Count
            ? NodeOutcome.Ok(("item", array[index]?.DeepClone()))
            : NodeOutcome.Failed($"The list has {array.Count} items, so there is no position {index}.");
    }

    private static NodeOutcome Template(NodeContext context) =>
        NodeOutcome.Ok(("text", JsonValue.Create(context.Property("template") ?? string.Empty)));

    private static NodeOutcome Hash(NodeContext context)
    {
        var text = context.Input("text")?.ToString() ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(text);

        var hash = context.Property("algorithm") switch
        {
            "sha512" => System.Security.Cryptography.SHA512.HashData(bytes),
            "md5" => System.Security.Cryptography.MD5.HashData(bytes),
            _ => System.Security.Cryptography.SHA256.HashData(bytes),
        };

        return NodeOutcome.Ok(("hash", JsonValue.Create(Convert.ToHexStringLower(hash))));
    }

    private static NodeOutcome Base64(NodeContext context)
    {
        var text = context.Input("text")?.ToString() ?? string.Empty;

        if (context.Property("direction") == "decode")
        {
            try
            {
                return NodeOutcome.Ok(("text",
                    JsonValue.Create(Encoding.UTF8.GetString(Convert.FromBase64String(text)))));
            }
            catch (FormatException)
            {
                return NodeOutcome.Failed("That is not Base64.");
            }
        }

        return NodeOutcome.Ok(("text", JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)))));
    }

    // ---- assertions --------------------------------------------------------------------------

    /// <summary>
    /// Records the check and decides what the step's verdict is.
    ///
    /// A soft assertion records the failure and lets the run carry on, which is what somebody wants
    /// when one step checks fifteen fields and they would rather see all fifteen results than the
    /// first one that broke.
    /// </summary>
    private static NodeOutcome Verdict(NodeContext context, AssertionRecord record)
    {
        context.Record(record);

        // Not logged here: the runner logs every check as it is recorded, and doing it twice would
        // put every soft failure in the log two ways.
        if (record.Passed) return NodeOutcome.Ok();
        if (record.Soft) return NodeOutcome.Ok();

        return NodeOutcome.Failed(record.Description);
    }

    private static NodeOutcome AssertStatus(NodeContext context)
    {
        var actual = context.Input("response")?["statusCode"]?.GetValue<int>() ?? 0;
        var expected = context.Property("expected") ?? "200";

        var passed = expected
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => Matches(part, actual));

        return Verdict(context, new(
            passed ? $"The response came back {actual}." : $"Expected {expected}, got {actual}.",
            passed, context.Flag("soft"), expected, actual.ToString(), "status"));
    }

    /// <summary>Matches an exact code, or a family written as <c>2xx</c>.</summary>
    private static bool Matches(string expected, int actual)
    {
        if (int.TryParse(expected, out var exact)) return exact == actual;

        return expected.Length == 3
               && char.IsDigit(expected[0])
               && expected[1] is 'x' or 'X'
               && actual / 100 == expected[0] - '0';
    }

    private static NodeOutcome AssertHeader(NodeContext context)
    {
        var name = context.Property("header") ?? string.Empty;
        var headers = context.Input("response")?["headers"] as JsonObject;

        var actual = headers?.FirstOrDefault(
            pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.ToString();

        var expected = context.Property("value");
        var kind = Enum.TryParse<MatcherKind>(context.Property("matcher"), out var parsed)
            ? parsed
            : MatcherKind.Exact;

        // Check returns null when it holds and a sentence when it does not — the sentence is the
        // useful half, so there is nothing to unwrap.
        var complaint = Matcher.Check(kind,
            new ComparisonRule { Path = name, Kind = kind, Text = expected },
            expected is null ? null : JsonValue.Create(expected),
            actual is null ? null : JsonValue.Create(actual));

        return Verdict(context, new(
            complaint ?? $"«{name}» is as expected.",
            complaint is null, context.Flag("soft"), expected, actual, name));
    }

    private static NodeOutcome AssertField(NodeContext context)
    {
        var response = context.Input("response");
        var path = context.Property("path") ?? "$";
        var actual = Read(response?["body"] ?? response, path);

        var expected = context.Property("value");
        var kind = Enum.TryParse<MatcherKind>(context.Property("matcher"), out var parsed)
            ? parsed
            : MatcherKind.Exact;

        var complaint = Matcher.Check(kind,
            new ComparisonRule { Path = path, Kind = kind, Text = expected, Number = AsNumber(expected) },
            Expected(expected),
            actual);

        return Verdict(context, new(
            complaint ?? $"«{path}» is as expected.",
            complaint is null, context.Flag("soft"), expected, actual?.ToJsonString(), path));
    }

    /// <summary>
    /// The expected value as a node, so a number compares as a number.
    ///
    /// Wrapping "200" as a string would make an exact match against 200 fail, which is the one
    /// comparison every scenario makes.
    /// </summary>
    private static JsonNode? Expected(string? value)
    {
        if (value is null) return null;
        if (AsNumber(value) is { } number && value.Trim() == number.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
        {
            return JsonValue.Create(number);
        }

        if (bool.TryParse(value, out var flag)) return JsonValue.Create(flag);
        return JsonValue.Create(value);
    }

    private static double? AsNumber(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static NodeOutcome AssertTime(NodeContext context)
    {
        var actual = context.Input("response")?["durationMs"]?.GetValue<double>() ?? 0;
        var limit = context.Duration("under", TimeSpan.FromSeconds(2)).TotalMilliseconds;
        var passed = actual <= limit;

        return Verdict(context, new(
            passed
                ? $"It answered in {Math.Round(actual)}ms."
                : $"It took {Math.Round(actual)}ms, and {Math.Round(limit)}ms is the limit.",
            passed, context.Flag("soft"), $"{Math.Round(limit)}ms", $"{Math.Round(actual)}ms", "duration"));
    }

    private static NodeOutcome AssertContains(NodeContext context)
    {
        var body = context.Input("response")?["text"]?.ToString() ?? string.Empty;
        var wanted = context.Property("text") ?? string.Empty;

        var passed = body.Contains(wanted, context.Flag("ignoreCase")
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

        return Verdict(context, new(
            passed ? $"The response holds «{wanted}»." : $"The response does not hold «{wanted}».",
            passed, context.Flag("soft"), wanted, null, "body"));
    }

    private static NodeOutcome AssertNotNull(NodeContext context)
    {
        var value = context.Input("value");
        var passed = value is not null && value.GetValueKind() != JsonValueKind.Null;

        return Verdict(context, new(
            passed ? "It is there." : "It is missing or empty.",
            passed, context.Flag("soft"), null, value?.ToJsonString()));
    }

    private static NodeOutcome AssertRegex(NodeContext context)
    {
        var text = context.Input("text")?.ToString() ?? string.Empty;
        var pattern = context.Property("pattern") ?? string.Empty;

        bool passed;
        try
        {
            passed = Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            return Verdict(context, new("That pattern could not be read.", false, context.Flag("soft")));
        }
        catch (RegexMatchTimeoutException)
        {
            return Verdict(context, new("That pattern took too long on this text.", false, context.Flag("soft")));
        }

        return Verdict(context, new(
            passed ? "It matches the pattern." : "It does not match the pattern.",
            passed, context.Flag("soft"), pattern, text));
    }

    private static NodeOutcome AssertListCount(NodeContext context)
    {
        var count = context.Input("list") is JsonArray array ? array.Count : 0;
        var wanted = context.Number("count", 0);
        var upper = context.Number("upper", wanted);

        var passed = context.Property("comparison") switch
        {
            "atLeast" => count >= wanted,
            "atMost" => count <= wanted,
            "between" => count >= wanted && count <= upper,
            _ => count == wanted,
        };

        return Verdict(context, new(
            passed ? $"There are {count}." : $"There are {count}, not {wanted}.",
            passed, context.Flag("soft"), wanted.ToString(), count.ToString(), "count"));
    }

    // ---- baseline ----------------------------------------------------------------------------

    private async Task<NodeOutcome> CompareBaseline(NodeContext context)
    {
        var reference = context.Property("baseline");
        if (string.IsNullOrWhiteSpace(reference)) return NodeOutcome.Failed("No baseline was named.");

        var answer = await services.BaselineAsync(reference, context.Property("key"), context.Cancellation);

        if (answer is null)
        {
            // Nothing approved for this input yet. Not a failure — the first run of a new input is
            // meant to have nothing to compare against, and calling that a regression would make
            // every new row a false alarm.
            context.Log(RunEventLevel.Info, "There is no approved answer for this input yet.", null);
            return NodeOutcome.Ok();
        }

        var body = context.Input("response")?["text"]?.ToString() ?? string.Empty;
        var diff = SemanticDiff.CompareText(answer.Body, body, answer.Rules);

        var counts = new JsonObject();
        foreach (var (kind, count) in diff.Counts) counts[kind.ToString()] = count;

        return Verdict(context, new(
            diff.Matches
                ? "It matches what was approved."
                : $"{diff.Findings.Count} differences from what was approved.",
            diff.Matches, context.Flag("soft"), null, counts.ToJsonString(), "baseline"))
            with
        { Published = new Dictionary<string, JsonNode?> { ["diff"] = counts } };
    }

    /// <summary>
    /// Labels the run, so a report can group by what was being tested.
    ///
    /// The tags go into a run variable rather than nowhere, which is the difference between a node
    /// that records something and a node that looks like it does.
    /// </summary>
    private static NodeOutcome Tag(NodeContext context)
    {
        var tags = (context.Property("tags") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tags.Length == 0) return NodeOutcome.Ok();

        context.SetVariable("tags", new JsonArray([.. tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]));
        return NodeOutcome.Ok();
    }

    /// <summary>Appends the query rows to an address, keeping whatever it already had.</summary>
    private static string Query(string url, string? rows)
    {
        var pairs = ReadPairs(rows);
        if (pairs.Count == 0) return url;

        var query = string.Join("&", pairs.Select(pair =>
            $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(pair.Value)}"));

        return url + (url.Contains('?') ? "&" : "?") + query;
    }

    private static NodeOutcome SoftFail(NodeContext context)
    {
        var message = context.Property("message") ?? "Something is wrong.";
        context.Record(new(message, false, Soft: true));
        context.Log(RunEventLevel.Warning, message, null);
        return NodeOutcome.Ok();
    }

    // ---- shared ------------------------------------------------------------------------------

    /// <summary>Reads a JSON path, returning the first match or null.</summary>
    internal static JsonNode? Read(JsonNode? document, string path)
    {
        if (document is null) return null;

        try
        {
            if (!JsonPath.TryParse(path, out var parsed)) return null;
            var result = parsed.Evaluate(document);
            return result.Matches.Count > 0 ? result.Matches[0].Value : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Key/value properties are stored as the editor's rows; this is the reading half.</summary>
    private static IReadOnlyList<(string Name, string Value)> ReadPairs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var rows = JsonSerializer.Deserialize<List<PairRow>>(json, Json) ?? [];
            return [.. rows
                .Where(row => row.Enabled && !string.IsNullOrWhiteSpace(row.Name))
                .Select(row => (row.Name!, row.Value ?? string.Empty))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record PairRow
    {
        public string? Name { get; init; }
        public string? Value { get; init; }
        public bool Enabled { get; init; } = true;
    }
}
