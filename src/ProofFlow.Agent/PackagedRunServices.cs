using System.Text.Json;
using System.Text.Json.Nodes;
using ProofFlow.Contracts.Runners;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Running;

namespace ProofFlow.Agent;

/// <summary>
/// The outside world as the engine sees it, on a machine with no database.
///
/// The server's implementation of the same interface reads rows; this one reads the package that
/// travelled with the job. Everything else — how a request is sent, how a body is redacted, what a
/// comparison means — is the same code in the same assembly, because <see cref="IRunServices"/> is
/// the only seam and there is no second engine.
///
/// The one real difference is <see cref="CaptureBaselineAsync"/>. An agent cannot file anything, so
/// it collects what a run captured and hands it back with the report; the server writes it into the
/// same review queue a local run would have.
/// </summary>
public sealed class PackagedRunServices(
    JobPackage package,
    IHttpExecutor executor,
    UrlPolicy policy,
    RedactionScope redaction,
    IReadOnlyList<KeyValueEntry> inherited) : IRunServices
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Answers the run captured, in order, for the server to file.</summary>
    public List<JobCapture> Captures { get; } = [];

    public async Task<HttpNodeResult> SendAsync(HttpNodeRequest request, CancellationToken cancellation)
    {
        var definition = new HttpRequestDefinition
        {
            Method = request.Method,
            Url = request.Url,
            Headers = [.. request.Headers.Select(pair => new KeyValueEntry(pair.Name, pair.Value))],
            Body = request.Body is null ? null : new RequestBody
            {
                Kind = ParseBody(request.BodyKind),
                Content = request.Body,
            },
            TimeoutSeconds = request.Timeout is { } timeout ? (int)timeout.TotalSeconds : null,
        };

        // The same inheritance the server applies, from the same helper. A remote run and a local
        // run differ in transport and in nothing else.
        definition = InheritedHeaders.Apply(
            definition, inherited, package.Environment?.DefaultHeadersJson);

        var result = await executor.SendAsync(definition, policy, cancellation);

        if (!result.Succeeded)
        {
            return new HttpNodeResult(false, result.StatusCode, result.ReasonPhrase, [],
                string.Empty, null, result.Duration.TotalMilliseconds,
                redaction.Apply(result.Failure!.Message), redaction.Apply(result.ResolvedUrl));
        }

        // Unmasked to the engine, masked in the sink — which on this side is what gets packed up and
        // sent back, so nothing leaves the machine in the clear. The engine has to see the real
        // value or a scenario cannot use a token it was just handed; see the note in
        // <c>EngineRunServices</c>, which this deliberately mirrors.
        return new HttpNodeResult(
            true, result.StatusCode, result.ReasonPhrase,
            [.. result.ResponseHeaders.Select(entry => (entry.Name, entry.Value))],
            result.Body, result.ContentType,
            result.Duration.TotalMilliseconds, null, result.ResolvedUrl);
    }

    public Task<IReadOnlyList<JsonNode>> DataSetRowsAsync(
        string reference, CancellationToken cancellation)
    {
        var set = Find(package.DataSets, reference, candidate => candidate.Name, candidate => candidate.Id);

        IReadOnlyList<JsonNode> rows = set is null
            ? []
            : [.. set.Rows.Select(Parse).OfType<JsonNode>()];

        return Task.FromResult(rows);
    }

    public Task<BaselineAnswer?> BaselineAsync(
        string reference, string? key, CancellationToken cancellation)
    {
        var baseline = Find(package.Baselines, reference,
            candidate => candidate.Name, candidate => candidate.Id);

        if (baseline is null) return Task.FromResult<BaselineAnswer?>(null);

        var rules = new ComparisonRuleSet(ReadRules(baseline.RulesJson));

        // A key means one input of a sample-based baseline; no key means the single approved answer.
        var body = string.IsNullOrWhiteSpace(key)
            ? baseline.ApprovedBody
            : baseline.Samples.GetValueOrDefault(key);

        return Task.FromResult(body is null ? null : new BaselineAnswer(body, rules));
    }

    public Task CaptureBaselineAsync(
        string reference, string? key, CapturedAnswer answer, bool approve,
        CancellationToken cancellation)
    {
        // Collected rather than filed. The agent has nowhere to put it, and inventing a second
        // holding area that only remote runs know about is exactly the thing the server-side
        // implementation refused to do for local ones.
        Captures.Add(new JobCapture
        {
            Baseline = reference,
            Key = key,
            Body = redaction.Apply(answer.Body),
            ContentType = answer.ContentType,
            StatusCode = answer.StatusCode,
            Url = redaction.Apply(answer.Url),
            DurationMs = answer.DurationMs,
            Approve = approve,
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Matches a reference the way the server does: by id when it is one, by name otherwise.
    ///
    /// The graph holds whatever somebody typed on the canvas, which is usually a name.
    /// </summary>
    private static T? Find<T>(
        IReadOnlyList<T> items, string reference, Func<T, string> name, Func<T, Guid> id)
        where T : class =>
        Guid.TryParse(reference, out var parsed)
            ? items.FirstOrDefault(item => id(item) == parsed)
            : items.FirstOrDefault(item =>
                string.Equals(name(item), reference, StringComparison.Ordinal));

    private static IReadOnlyList<ComparisonRule> ReadRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ComparisonRule>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            // A rule set that will not parse is a rule set the server wrote and this cannot read,
            // which is worth failing loudly on rather than silently comparing without rules — but
            // the engine has no way to be told that here, so the comparison runs strict and the
            // difference shows up as findings the reader can see.
            return [];
        }
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BodyKind ParseBody(string? kind) => kind switch
    {
        "json" => BodyKind.Json,
        "form" => BodyKind.FormUrlEncoded,
        "text" or "raw" => BodyKind.Text,
        _ => BodyKind.None,
    };
}
