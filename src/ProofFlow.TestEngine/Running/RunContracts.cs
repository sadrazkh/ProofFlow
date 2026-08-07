using System.Text.Json.Nodes;
using ProofFlow.Domain.Runs;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.TestEngine.Running;

/// <summary>
/// What a node execution is handed, and what it may do.
///
/// A narrow surface on purpose. A node can read its own properties resolved, ask for a value from
/// a socket, write to the log, and record a check. It cannot reach the database, the current user,
/// or another node's internals — which is what keeps seventy behaviours from becoming seventy ways
/// to reach into the runner.
/// </summary>
public sealed class NodeContext
{
    public required GraphNode Node { get; init; }

    public required NodeSpec Spec { get; init; }

    /// <summary>Resolves <c>{{…}}</c> against the scopes in force for this step.</summary>
    public required VariableResolver Resolver { get; init; }

    /// <summary>What earlier steps published, by socket. Read through <see cref="Input"/>.</summary>
    public required IReadOnlyDictionary<string, JsonNode?> Inputs { get; init; }

    /// <summary>Which pass through the surrounding loop this is. Zero outside one.</summary>
    public int Iteration { get; init; }

    /// <summary>
    /// What the current data-set row is filed under, when there is one.
    ///
    /// A captured sample needs to be stored against the input it came from, and the input is the
    /// loop's business rather than the capturing node's.
    /// </summary>
    public string? LoopKey { get; init; }

    public required CancellationToken Cancellation { get; init; }

    /// <summary>Writes a line to the run's log.</summary>
    public required Action<RunEventLevel, string, JsonNode?> Log { get; init; }

    /// <summary>Records a check and whether it held.</summary>
    public required Action<AssertionRecord> Record { get; init; }

    /// <summary>
    /// Adds a value to what is hidden in logs, stored bodies and exports.
    ///
    /// Every node that mints a credential calls this. A token fetched at run time was never typed
    /// into a secret box, so nothing else in the system knows it is one — and the run log is the
    /// artefact people forward.
    /// </summary>
    public required Action<string?> Remember { get; init; }

    /// <summary>Keeps something alongside the run: a payload, a generated id, a rendered body.</summary>
    public required Action<string, string, bool> Attach { get; init; }

    /// <summary>Sets a run variable that later steps read as <c>{{vars.name}}</c>.</summary>
    public required Action<string, JsonNode?> SetVariable { get; init; }

    /// <summary>
    /// A property, resolved.
    ///
    /// Resolution happens here rather than up front so a property inside a loop sees the current
    /// iteration's values, and so an unresolvable reference fails the step that used it rather than
    /// the whole run.
    /// </summary>
    public string? Property(string name)
    {
        Node.Properties.TryGetValue(name, out var raw);
        if (string.IsNullOrEmpty(raw)) return Spec.Properties.FirstOrDefault(p => p.Name == name)?.Default;

        return Resolver.Resolve(raw);
    }

    public string? Raw(string name) =>
        Node.Properties.TryGetValue(name, out var raw) && !string.IsNullOrEmpty(raw)
            ? raw
            : Spec.Properties.FirstOrDefault(p => p.Name == name)?.Default;

    public bool Flag(string name) =>
        string.Equals(Property(name), "true", StringComparison.OrdinalIgnoreCase);

    public int Number(string name, int fallback)
    {
        var value = Property(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// A duration written the way a person writes one: <c>2s</c>, <c>500ms</c>, <c>1m</c>, or a
    /// bare number of seconds.
    /// </summary>
    public TimeSpan Duration(string name, TimeSpan fallback)
    {
        var value = Property(name)?.Trim();
        if (string.IsNullOrEmpty(value)) return fallback;

        var (number, unit) = value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            ? (value[..^2], "ms")
            : value.EndsWith('s') ? (value[..^1], "s")
            : value.EndsWith('m') ? (value[..^1], "m")
            : (value, "s");

        if (!double.TryParse(number, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            return fallback;
        }

        return unit switch
        {
            "ms" => TimeSpan.FromMilliseconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.FromSeconds(amount),
        };
    }

    public JsonNode? Input(string port) => Inputs.GetValueOrDefault(port);
}

/// <summary>
/// What one step did.
///
/// <paramref name="Port"/> is the output the run leaves by, and it is how branching works: a node
/// decides, and the runner follows. A node that does not care returns "out".
/// </summary>
public sealed record NodeOutcome(
    NodeVerdict Verdict,
    string? Port = "out",
    IReadOnlyDictionary<string, JsonNode?>? Published = null,
    string? Failure = null)
{
    /// <summary>
    /// Where the walk resumes, when it is not the node after this one.
    ///
    /// Only the runner sets this — a parallel block continues past its join, and nothing else needs
    /// to move control anywhere. The setter is internal so that a node cannot: a node that could
    /// send the run to an arbitrary step would make the graph a suggestion.
    /// </summary>
    internal string? ContinueAt { get; init; }

    public static NodeOutcome Ok(params (string Port, JsonNode? Value)[] published) =>
        new(NodeVerdict.Passed, "out",
            published.Length == 0 ? null : published.ToDictionary(p => p.Port, p => p.Value));

    public static NodeOutcome Leaves(string port, params (string Port, JsonNode? Value)[] published) =>
        new(NodeVerdict.Passed, port,
            published.Length == 0 ? null : published.ToDictionary(p => p.Port, p => p.Value));

    /// <summary>
    /// The step did not do what it was asked.
    ///
    /// The runner takes the failure port when there is one and stops the branch when there is not,
    /// which is what makes "if it fails, clean up and carry on" expressible on the canvas.
    /// </summary>
    public static NodeOutcome Failed(string message,
                                     IReadOnlyDictionary<string, JsonNode?>? published = null) =>
        new(NodeVerdict.Failed, "failure", published, message);

    /// <summary>Nothing to do here, carry on. A disabled step, or one a condition skipped.</summary>
    public static NodeOutcome Skipped(string? port = "out") => new(NodeVerdict.Skipped, port);

    /// <summary>The run should stop here, with this result.</summary>
    public static NodeOutcome Ends(NodeVerdict verdict, string? message = null) =>
        new(verdict, null, null, message);
}

public enum NodeVerdict
{
    Passed = 1,
    Failed = 2,
    Skipped = 3,
}

/// <summary>One check, as a node reports it.</summary>
public sealed record AssertionRecord(
    string Description,
    bool Passed,
    bool Soft = false,
    string? Expected = null,
    string? Actual = null,
    string? Target = null);

/// <summary>
/// Everything a node needs from outside the engine.
///
/// Ports rather than concrete services, so the engine keeps knowing nothing about databases,
/// HTTP clients or the current user. The infrastructure supplies them.
/// </summary>
public interface IRunServices
{
    /// <summary>Sends a request under the environment's policy, redacting what comes back.</summary>
    Task<HttpNodeResult> SendAsync(HttpNodeRequest request, CancellationToken cancellation);

    /// <summary>The rows of a data set version, in order.</summary>
    Task<IReadOnlyList<JsonNode>> DataSetRowsAsync(string reference, CancellationToken cancellation);

    /// <summary>The approved answer for one input of a baseline, and the rules to compare under.</summary>
    Task<BaselineAnswer?> BaselineAsync(string reference, string? key, CancellationToken cancellation);

    /// <summary>
    /// Records an answer against a baseline as a candidate.
    ///
    /// <paramref name="approve"/> is what the <c>approve</c> property asks for, and it is off by
    /// default at every layer: a capture that approves itself is a test that can never fail.
    /// </summary>
    Task CaptureBaselineAsync(string reference, string? key, CapturedAnswer answer, bool approve,
                              CancellationToken cancellation);
}

public sealed record HttpNodeRequest(
    string Method,
    string Url,
    IReadOnlyList<(string Name, string Value)> Headers,
    string? Body,
    string? BodyKind,
    TimeSpan? Timeout);

public sealed record HttpNodeResult(
    bool Succeeded,
    int StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<(string Name, string Value)> Headers,
    string Body,
    string? ContentType,
    double DurationMs,
    string? Failure,
    string? ResolvedUrl);

/// <summary>
/// The approved answer for one input, with the rules it should be compared under.
///
/// The rules travel with the answer rather than being fetched separately, because a comparison run
/// under a different rule set from the one that approved the answer is a comparison nobody asked
/// for.
/// </summary>
public sealed record BaselineAnswer(string Body, ComparisonRuleSet Rules);

/// <summary>
/// An answer being recorded, with the parts a reviewer needs beside the body.
///
/// The status and the address travel with it because a sample that came back 500 and a sample that
/// came back 200 are not the same evidence, and a review queue showing only bodies makes that
/// invisible.
/// </summary>
public sealed record CapturedAnswer(
    string Body, string? ContentType, int StatusCode, double DurationMs, string? Url);
