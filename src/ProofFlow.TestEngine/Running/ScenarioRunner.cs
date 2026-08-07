using System.Diagnostics;
using System.Text.Json.Nodes;
using ProofFlow.Domain.Runs;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.TestEngine.Running;

/// <summary>
/// Walks the graph and does what it says.
///
/// Iterative rather than recursive: a scenario with a loop of two thousand rows around a branch
/// twelve deep would put two thousand frames on the stack, and the failure mode of that is the
/// process, not the run.
///
/// Three rules shape the walk. Control follows the port the node leaves by, so a branch is a node
/// deciding rather than the runner interpreting. A container's contents are reached through the
/// container rather than by an edge, so a loop body is not something an edge can jump into halfway.
/// And everything is bounded — iterations, depth, total steps — because an unbounded test is a
/// build agent that stops answering and a person who has to find out why.
/// </summary>
public sealed class ScenarioRunner(NodeExecutors executors, IRunSink sink)
{
    /// <summary>
    /// The most steps one run may take.
    ///
    /// Not a guess at what a scenario needs — a ceiling on what a mistake can cost. Two thousand
    /// rows times twenty steps is forty thousand, so this is generous; what it stops is a graph
    /// whose loops multiply into millions.
    /// </summary>
    public const int MaxSteps = 200_000;

    /// <summary>How deep containers may nest before the graph is refusing to be read.</summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// The node kinds the runner handles itself rather than through the executor table.
    ///
    /// Listed rather than inferred so that a test can hold the two halves against the catalogue and
    /// fail when a node type is added to the palette with nothing behind it. A node that silently
    /// does nothing is worse than one that fails: the run comes back green having tested nothing.
    /// </summary>
    public static readonly IReadOnlySet<string> Controls = new HashSet<string>(StringComparer.Ordinal)
    {
        "flow.if", "flow.skipIf", "flow.switch", "flow.repeat", "flow.while", "flow.forEach",
        "flow.forEachRow", "flow.rateLimit", "flow.retry", "flow.pollUntil", "flow.tryCatch",
        "flow.cleanup", "flow.break", "flow.continue",
        "core.group", "core.parallel", "core.join", "test.expectFailure",
    };

    public async Task<RunSummary> RunAsync(
        Graph graph, RunScopes scopes, CancellationToken cancellation = default)
    {
        var state = new RunnerState(graph, scopes, sink);
        var stopwatch = Stopwatch.StartNew();

        var start = graph.Nodes.FirstOrDefault(node => NodeCatalogue.Find(node.Key)?.IsStart == true);

        if (start is null)
        {
            sink.Log(RunEventLevel.Error, "This scenario has no starting point.", null, null);
            return new RunSummary(RunStatus.Errored, 0, 0, 0, 0, stopwatch.Elapsed.TotalMilliseconds,
                "This scenario has no starting point.");
        }

        RunStatus status;
        string? outcome = null;

        try
        {
            var verdict = await WalkAsync(state, start.Id, depth: 0, cancellation);

            status = verdict switch
            {
                NodeVerdict.Failed => RunStatus.Failed,
                _ => state.AssertionsFailed > 0 ? RunStatus.Failed : RunStatus.Passed,
            };

            outcome = state.Outcome ?? (status == RunStatus.Passed
                ? "Everything that was checked held."
                : $"{state.AssertionsFailed} checks did not hold.");
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Cancelled;
            outcome = "Stopped before it finished.";
        }
        catch (RunLimitReached limit)
        {
            status = RunStatus.Errored;
            outcome = limit.Message;
            sink.Log(RunEventLevel.Error, limit.Message, null, null);
        }

        // Cleanup runs whatever happened, including after a cancellation — undoing what a test
        // created is the one thing that must not be skipped because the test went wrong.
        await RunCleanupAsync(state, cancellation);

        stopwatch.Stop();

        return new RunSummary(
            status, state.Steps, state.StepsFailed, state.AssertionsPassed, state.AssertionsFailed,
            stopwatch.Elapsed.TotalMilliseconds, outcome);
    }

    /// <summary>
    /// Follows control from one node until the branch ends.
    ///
    /// Returns the verdict the branch reached, which is what lets a container decide whether its
    /// body succeeded without knowing what is inside it.
    /// </summary>
    private async Task<NodeVerdict> WalkAsync(
        RunnerState state, string? nodeId, int depth, CancellationToken cancellation)
    {
        var verdict = NodeVerdict.Passed;

        while (nodeId is not null)
        {
            cancellation.ThrowIfCancellationRequested();

            if (state.Steps >= MaxSteps)
                throw new RunLimitReached($"This run passed {MaxSteps:N0} steps, which is where ProofFlow stops.");

            if (!state.ById.TryGetValue(nodeId, out var node)) break;
            if (state.Stopped || state.Breaking || state.Continuing) break;

            // A parallel branch stops at the join rather than walking through it, so the steps
            // after the join run once instead of once per branch.
            if (nodeId == state.JoinAt) break;

            var spec = NodeCatalogue.Find(node.Key);
            if (spec is null)
            {
                sink.Log(RunEventLevel.Error,
                    $"«{node.Name}» is of a kind this version does not know.", node.Id, node.Name);
                return NodeVerdict.Failed;
            }

            // Cleanup is collected on the way past and run at the end, not here.
            if (node.Key == "flow.cleanup")
            {
                state.Cleanups.Add(node.Id);
                nodeId = state.Next(node.Id, "out");
                continue;
            }

            var outcome = node.Disabled
                ? NodeOutcome.Skipped()
                : await StepAsync(state, node, spec, depth, cancellation);

            if (outcome.Verdict == NodeVerdict.Failed) verdict = NodeVerdict.Failed;

            if (outcome.Port is null)
            {
                // A terminal node. The run stops here with whatever it decided.
                state.Stopped = true;
                state.Outcome = outcome.Failure;
                return outcome.Verdict;
            }

            if (state.Breaking || state.Continuing) return verdict;

            var next = outcome.ContinueAt ?? state.Next(node.Id, outcome.Port);

            if (next is null && outcome.Verdict == NodeVerdict.Failed)
            {
                // Failed with nowhere to go: the branch ends, and the run has failed. This is what
                // makes a scenario without an error path still report the error — and the step's
                // own words are kept, because "it went round 100 times without the condition coming
                // true" tells somebody what to do and "1 check did not hold" does not.
                state.Outcome ??= outcome.Failure;
                return NodeVerdict.Failed;
            }

            nodeId = next;
        }

        return verdict;
    }

    /// <summary>
    /// One node's turn, including the ones the runner handles itself.
    ///
    /// Containers and branching live here rather than in the executor table because they decide
    /// where control goes, and a node that could move the runner would be a node that could move it
    /// anywhere.
    /// </summary>
    private async Task<NodeOutcome> StepAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        if (depth > MaxDepth)
            throw new RunLimitReached($"The steps nest more than {MaxDepth} deep.");

        return node.Key switch
        {
            "flow.if" => Branch(state, node, cancellation),
            "flow.skipIf" => SkipIf(state, node),
            "flow.switch" => Switch(state, node),
            "flow.repeat" => await RepeatAsync(state, node, spec, depth, cancellation),
            "flow.while" => await WhileAsync(state, node, spec, depth, cancellation),
            "flow.forEach" => await ForEachAsync(state, node, spec, depth, cancellation),
            "flow.retry" => await RetryAsync(state, node, spec, depth, cancellation),
            "flow.pollUntil" => await PollAsync(state, node, spec, depth, cancellation),
            "flow.tryCatch" => await TryCatchAsync(state, node, spec, depth, cancellation),
            "flow.forEachRow" => await ForEachRowAsync(state, node, spec, depth, cancellation),
            "flow.rateLimit" => await RateLimitAsync(state, node, spec, depth, cancellation),
            "flow.break" => Leave(state, node, breaking: true),
            "flow.continue" => Leave(state, node, breaking: false),
            "core.group" => await GroupAsync(state, node, spec, depth, cancellation),
            "core.parallel" => await ParallelAsync(state, node, spec, depth, cancellation),
            "core.join" => NodeOutcome.Ok(),
            "test.expectFailure" => await ExpectFailureAsync(state, node, spec, depth, cancellation),
            _ => await ExecuteAsync(state, node, spec, cancellation),
        };
    }

    /// <summary>Runs a leaf node, timing it and recording what it produced.</summary>
    private async Task<NodeOutcome> ExecuteAsync(
        RunnerState state, GraphNode node, NodeSpec spec, CancellationToken cancellation)
    {
        // Waited for before the record starts, so a step held back by a rate limit does not read as
        // a step that took two seconds.
        if (state.Pace is { } pace) await pace.WaitAsync(cancellation);

        var record = sink.Begin(node, state.Iteration, state.Attempt);
        var stopwatch = Stopwatch.StartNew();
        state.CountStep();

        var context = new NodeContext
        {
            Node = node,
            Spec = spec,
            Resolver = state.Resolver(),
            Inputs = state.InputsFor(node.Id),
            Iteration = state.Iteration,
            LoopKey = state.LoopKey,
            Cancellation = cancellation,
            Log = (level, message, data) =>
                sink.Log(level, state.Redact(message), node.Id, node.Name, data),
            Record = assertion =>
            {
                sink.Assertion(record, assertion);
                state.CountAssertion(assertion.Passed);

                // The log is the narrative of the run, and the checks are the plot. A console whose
                // log holds only the requests makes somebody open every step to find out what was
                // actually verified.
                sink.Log(
                    assertion.Passed ? RunEventLevel.Info
                        : assertion.Soft ? RunEventLevel.Warning : RunEventLevel.Error,
                    assertion.Description, node.Id, node.Name);
            },
            Remember = state.Remember,
            Attach = (name, content, redact) =>
                sink.Artifact(name, redact ? state.Redact(content) : content, node.Id),
            SetVariable = state.SetVariable,
        };

        NodeOutcome outcome;
        try
        {
            outcome = await executors.RunAsync(context);
        }
        catch (OperationCanceledException)
        {
            sink.Finish(record, NodeRunStatus.Cancelled, null, stopwatch.Elapsed.TotalMilliseconds, null, null);
            throw;
        }
        catch (VariableResolutionException resolution)
        {
            // A reference that does not resolve fails the step that used it, not the run: the rest
            // of the graph may not depend on it, and the message names the reference.
            outcome = NodeOutcome.Failed(resolution.Message);
        }
        catch (Exception ex)
        {
            outcome = NodeOutcome.Failed($"«{node.Name}» could not be run: {ex.Message}");
        }

        stopwatch.Stop();

        if (outcome.Published is { } published) state.Publish(node, published);

        if (outcome.Verdict == NodeVerdict.Failed)
        {
            state.CountFailedStep();

            // Every failure reaches the log. Without this a run whose only step failed before it
            // could log anything — an address that would not resolve, a body that would not parse —
            // opens a console with an empty log and a red badge, and the reason is nowhere on the
            // page the person is looking at.
            sink.Log(RunEventLevel.Error,
                outcome.Failure ?? $"«{node.Name}» did not do what it was asked.",
                node.Id, node.Name);
        }
        else if (outcome.Verdict == NodeVerdict.Passed && spec.Reaches is false)
        {
            // Steps that do not reach anything are quiet by default; this is the line that makes a
            // run readable as a sequence rather than as a summary.
            sink.Log(RunEventLevel.Debug,
                $"{node.Name} · {Math.Round(stopwatch.Elapsed.TotalMilliseconds)}ms",
                node.Id, node.Name);
        }

        sink.Finish(record, Status(outcome.Verdict), outcome.Port,
            stopwatch.Elapsed.TotalMilliseconds, state.Snapshot(node.Id), outcome.Failure);

        return outcome;
    }

    private static NodeRunStatus Status(NodeVerdict verdict) => verdict switch
    {
        NodeVerdict.Failed => NodeRunStatus.Failed,
        NodeVerdict.Skipped => NodeRunStatus.Skipped,
        _ => NodeRunStatus.Passed,
    };

    // ---- branching ---------------------------------------------------------------------------

    /// <summary>
    /// Resolves a condition without throwing when part of it is missing.
    ///
    /// A branch is how a scenario copes with a step that did not run, so a condition mentioning
    /// that step must not be the thing that ends the run. What cannot be resolved stays as written,
    /// which no comparison matches, so the answer is "no" — and that is the branch somebody drew
    /// for exactly this case.
    /// </summary>
    private string Condition(RunnerState state, GraphNode node, string name)
    {
        var result = state.Resolver().TryResolve(node.Properties.GetValueOrDefault(name) ?? string.Empty);

        foreach (var missing in result.Unresolved)
        {
            sink.Log(RunEventLevel.Debug, missing.Explanation, node.Id, node.Name);
        }

        return result.Text;
    }

    private NodeOutcome Branch(RunnerState state, GraphNode node, CancellationToken cancellation)
    {
        _ = cancellation;
        var condition = Condition(state, node, "condition");
        var taken = Expressions.IsTrue(condition);

        sink.Log(RunEventLevel.Debug, taken ? "Yes." : "No.", node.Id, node.Name);
        state.CountStep();

        return NodeOutcome.Leaves(taken ? "true" : "false");
    }

    private NodeOutcome SkipIf(RunnerState state, GraphNode node)
    {
        var condition = Condition(state, node, "condition");
        state.CountStep();

        if (!Expressions.IsTrue(condition)) return NodeOutcome.Ok();

        var reason = node.Properties.GetValueOrDefault("reason");
        sink.Log(RunEventLevel.Info, reason ?? "Skipped.", node.Id, node.Name);
        return NodeOutcome.Leaves("skipped");
    }

    private NodeOutcome Switch(RunnerState state, GraphNode node)
    {
        var resolver = state.Resolver();
        var value = resolver.Resolve(node.Properties.GetValueOrDefault("value") ?? string.Empty);
        state.CountStep();

        var cases = ReadCases(node.Properties.GetValueOrDefault("cases"));
        var match = cases.FirstOrDefault(entry => entry.Value == value);

        var port = match.Key ?? "default";
        sink.Log(RunEventLevel.Debug, $"«{value}» → {port}", node.Id, node.Name);

        return NodeOutcome.Leaves(port);
    }

    /// <summary>The switch's cases: a value on the left, the port it takes on the right.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> ReadCases(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<CaseRow>>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                }) ?? [];

            return [.. rows
                .Where(row => row.Name is not null)
                .Select(row => new KeyValuePair<string, string>(row.Value ?? "default", row.Name!))];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private sealed record CaseRow
    {
        public string? Name { get; init; }
        public string? Value { get; init; }
    }

    // ---- containers --------------------------------------------------------------------------

    /// <summary>
    /// Runs what is inside a container once, and says whether a loop around it should go again.
    ///
    /// Both of the ways out of a loop body land here: <c>flow.break</c> stops the loop and
    /// <c>flow.continue</c> starts the next pass. Both are flags rather than exceptions because
    /// leaving a loop early is ordinary, and paying for a stack unwind two thousand times is not.
    /// </summary>
    private async Task<(NodeVerdict Verdict, bool KeepGoing)> BodyAsync(
        RunnerState state, GraphNode container, int depth, CancellationToken cancellation)
    {
        var verdict = await WalkAsync(state, state.FirstInside(container.Id), depth + 1, cancellation);

        // Headers a step inside asked to scope to this block stop applying at its edge.
        executors.DropScope(container.Id);

        if (state.Continuing)
        {
            state.Continuing = false;
            return (verdict, true);
        }

        if (state.Breaking)
        {
            state.Breaking = false;
            return (verdict, false);
        }

        return (verdict, true);
    }

    private NodeOutcome Leave(RunnerState state, GraphNode node, bool breaking)
    {
        state.CountStep();

        if (breaking) state.Breaking = true;
        else state.Continuing = true;

        sink.Log(RunEventLevel.Debug,
            breaking ? "Leaving the loop." : "On to the next one.", node.Id, node.Name);

        return NodeOutcome.Ok();
    }

    private async Task<NodeOutcome> GroupAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;
        var (verdict, _) = await BodyAsync(state, node, depth, cancellation);
        return verdict == NodeVerdict.Failed ? NodeOutcome.Failed("Something inside failed.") : NodeOutcome.Ok();
    }

    private async Task<NodeOutcome> RepeatAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;
        var times = Math.Clamp(Int(node, "times", 3), 0, 100_000);

        for (var index = 0; index < times; index++)
        {
            cancellation.ThrowIfCancellationRequested();

            NodeVerdict verdict;
            bool keepGoing;

            using (state.Iterate(index, new JsonObject { ["index"] = index }))
            {
                (verdict, keepGoing) = await BodyAsync(state, node, depth, cancellation);
            }

            if (verdict == NodeVerdict.Failed) return NodeOutcome.Failed($"Pass {index + 1} failed.");
            if (!keepGoing) return NodeOutcome.Ok(("index", JsonValue.Create(index + 1)));
        }

        return NodeOutcome.Ok(("index", JsonValue.Create(times)));
    }

    private async Task<NodeOutcome> WhileAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;

        // The ceiling is required by the specification, so it is always here. A while loop with no
        // limit is a run that hangs a build agent until a timeout nobody set.
        var ceiling = Math.Clamp(Int(node, "maxIterations", 100), 1, 100_000);

        for (var index = 0; index < ceiling; index++)
        {
            cancellation.ThrowIfCancellationRequested();

            if (!Expressions.IsTrue(Condition(state, node, "condition")))
                return NodeOutcome.Ok();

            NodeVerdict verdict;
            bool keepGoing;

            using (state.Iterate(index, new JsonObject { ["index"] = index }))
            {
                (verdict, keepGoing) = await BodyAsync(state, node, depth, cancellation);
            }

            if (verdict == NodeVerdict.Failed) return NodeOutcome.Failed($"Pass {index + 1} failed.");
            if (!keepGoing) return NodeOutcome.Ok();
        }

        return NodeOutcome.Failed($"It went round {ceiling} times without the condition coming true.");
    }

    private async Task<NodeOutcome> ForEachAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;

        var list = state.InputsFor(node.Id).GetValueOrDefault("list") as JsonArray;
        if (list is null) return NodeOutcome.Failed("There is no list to go through.");

        var ceiling = Math.Clamp(Int(node, "maxIterations", 1000), 1, 100_000);
        var stopOnFailure = node.Properties.GetValueOrDefault("stopOnFailure") is not "false";
        var failures = 0;

        for (var index = 0; index < list.Count && index < ceiling; index++)
        {
            cancellation.ThrowIfCancellationRequested();

            var item = list[index]?.DeepClone();

            NodeVerdict verdict;
            bool keepGoing;

            using (state.Iterate(index, new JsonObject { ["index"] = index, ["item"] = item }))
            {
                state.Publish(node, new Dictionary<string, JsonNode?>
                {
                    ["item"] = item,
                    ["index"] = JsonValue.Create(index),
                });

                (verdict, keepGoing) = await BodyAsync(state, node, depth, cancellation);
            }

            if (verdict == NodeVerdict.Failed)
            {
                failures++;
                if (stopOnFailure) return NodeOutcome.Failed($"Item {index + 1} failed.");
            }

            if (!keepGoing) break;
        }

        return failures == 0
            ? NodeOutcome.Ok()
            : NodeOutcome.Failed($"{failures} of {list.Count} items failed.");
    }

    /// <summary>
    /// Runs the body again when it fails, waiting longer each time by default.
    ///
    /// The attempt number reaches the log and the node run, because "it passed on the third go" is
    /// a different fact from "it passed", and the flaky-test detection in a later phase reads it.
    /// </summary>
    private async Task<NodeOutcome> RetryAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;

        var attempts = Math.Clamp(Int(node, "attempts", 3), 1, 20);
        var delay = ParseDuration(node.Properties.GetValueOrDefault("delay"), TimeSpan.FromSeconds(1));
        var exponential = node.Properties.GetValueOrDefault("backoff") is not "fixed";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();

            // Each attempt's checks are held aside. Only the attempt that decided the outcome is
            // added to the run's tally, so a step that worked on the second go reports a run that
            // passed — while the first attempt stays in the log and the timeline, which is where
            // flaky-test detection reads it from.
            var tally = new Tally();
            NodeVerdict verdict;

            using (state.Attempting(attempt))
            using (state.Aside(tally))
            {
                (verdict, _) = await BodyAsync(state, node, depth, cancellation);
            }

            if (verdict != NodeVerdict.Failed)
            {
                state.Absorb(tally);

                if (attempt > 1)
                {
                    sink.Log(RunEventLevel.Info, $"It worked on attempt {attempt}.", node.Id, node.Name);
                }

                return NodeOutcome.Ok(("attempts", JsonValue.Create(attempt)));
            }

            if (attempt == attempts)
            {
                state.Absorb(tally);
                break;
            }

            var wait = exponential ? delay * Math.Pow(2, attempt - 1) : delay;
            sink.Log(RunEventLevel.Warning,
                $"Attempt {attempt} failed; trying again in {Math.Round(wait.TotalSeconds, 1)}s.",
                node.Id, node.Name);

            await Task.Delay(wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait, cancellation);
        }

        return NodeOutcome.Failed($"It failed all {attempts} attempts.");
    }

    private async Task<NodeOutcome> PollAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;

        var interval = ParseDuration(node.Properties.GetValueOrDefault("interval"), TimeSpan.FromSeconds(2));
        var timeout = ParseDuration(node.Properties.GetValueOrDefault("timeout"), TimeSpan.FromSeconds(60));
        var deadline = Stopwatch.StartNew();
        var round = 0;

        while (deadline.Elapsed < timeout)
        {
            cancellation.ThrowIfCancellationRequested();

            bool keepGoing;
            bool ready;

            // The question is asked inside the pass, so a condition about this round can read
            // {{run.loop.index}} — outside it, the pass has already been unwound.
            using (state.Iterate(round, new JsonObject { ["index"] = round }))
            {
                (_, keepGoing) = await BodyAsync(state, node, depth, cancellation);
                ready = Expressions.IsTrue(Condition(state, node, "condition"));
            }

            if (!keepGoing) return NodeOutcome.Ok();

            if (ready)
            {
                sink.Log(RunEventLevel.Info,
                    $"Ready after {Math.Round(deadline.Elapsed.TotalSeconds, 1)}s.", node.Id, node.Name);
                return NodeOutcome.Ok();
            }

            round++;
            await Task.Delay(interval, cancellation);
        }

        return NodeOutcome.Failed(
            $"It was still not ready after {Math.Round(timeout.TotalSeconds)}s.");
    }

    private async Task<NodeOutcome> TryCatchAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;

        var (verdict, _) = await BodyAsync(state, node, depth, cancellation);

        if (verdict != NodeVerdict.Failed) return NodeOutcome.Ok();

        sink.Log(RunEventLevel.Info, "It failed, and the other path was taken.", node.Id, node.Name);

        // The failure is caught, so the run carries on — but the step count still records it,
        // because "nothing went wrong" and "something went wrong and was handled" are different.
        return NodeOutcome.Leaves("caught",
            ("error", JsonValue.Create("Something inside failed.")));
    }

    /// <summary>
    /// Goes through a data set's rows, one row per pass.
    ///
    /// The link between the canvas and sample-based regression: the row becomes
    /// <c>{{dataset.current}}</c>, and its key is what a baseline comparison inside files its answer
    /// under, so two thousand inputs produce two thousand separately-approvable answers.
    ///
    /// Rows run one at a time. Every step publishes under <c>{{steps.name}}</c>, which is one scope
    /// for the whole run, so two rows at once would read each other's responses — the concurrency
    /// that is safe here is the capture engine's, where a row is one request and nothing is shared.
    /// </summary>
    private async Task<NodeOutcome> ForEachRowAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;

        var reference = node.Properties.GetValueOrDefault("dataSet");
        if (string.IsNullOrWhiteSpace(reference)) return NodeOutcome.Failed("No data set was chosen.");

        IReadOnlyList<JsonNode> rows;
        try
        {
            rows = await executors.RowsAsync(reference, cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NodeOutcome.Failed($"That data set could not be read: {ex.Message}");
        }

        if (rows.Count == 0)
        {
            sink.Log(RunEventLevel.Warning, "That data set has no rows.", node.Id, node.Name);
            return NodeOutcome.Ok();
        }

        var limit = Int(node, "limit", 0);
        var ceiling = limit > 0 ? Math.Min(limit, rows.Count) : rows.Count;
        var failures = 0;

        for (var index = 0; index < ceiling; index++)
        {
            cancellation.ThrowIfCancellationRequested();

            var row = rows[index].DeepClone();
            var key = KeyOf(row, index);

            NodeVerdict verdict;
            bool keepGoing;

            using (state.Iterate(index, new JsonObject { ["index"] = index, ["key"] = key }, key, row))
            {
                state.Publish(node, new Dictionary<string, JsonNode?>
                {
                    ["row"] = row,
                    ["key"] = JsonValue.Create(key),
                });

                (verdict, keepGoing) = await BodyAsync(state, node, depth, cancellation);
            }

            if (verdict == NodeVerdict.Failed) failures++;
            if (!keepGoing) break;
        }

        return failures == 0
            ? NodeOutcome.Ok()
            : NodeOutcome.Failed($"{failures} of {ceiling} rows failed.");
    }

    /// <summary>
    /// What a row is called.
    ///
    /// An <c>id</c> or <c>key</c> column if there is one, because that is what somebody reviewing
    /// two thousand samples wants to see next to each answer. Otherwise the row number, which at
    /// least stays stable as long as the data set does.
    /// </summary>
    private static string KeyOf(JsonNode row, int index)
    {
        if (row is JsonObject obj)
        {
            foreach (var candidate in (string[])["id", "key", "Id", "ID", "code"])
            {
                if (obj.TryGetPropertyValue(candidate, out var value) && value is not null)
                    return value.ToString();
            }
        }

        return (index + 1).ToString();
    }

    /// <summary>
    /// Runs the branches at the same time and carries on past the join.
    ///
    /// Genuinely at the same time — the state is behind a lock so the branches cannot corrupt each
    /// other's records. What it does not promise is that a step in one branch can read a step in
    /// another: that is a race in any test runner, and the answer is not to write scenarios that
    /// way.
    /// </summary>
    private async Task<NodeOutcome> ParallelAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        state.CountStep();

        var branches = spec.Outputs
            .Where(port => port.Kind == PortKind.Control)
            .Select(port => state.Next(node.Id, port.Name))
            .OfType<string>()
            .ToArray();

        if (branches.Length == 0) return NodeOutcome.Ok();

        var join = FindJoin(state, branches);
        var previousJoin = state.JoinAt;
        state.JoinAt = join;

        var limit = Math.Clamp(Int(node, "maxConcurrent", 3), 1, 16);
        using var slots = new SemaphoreSlim(limit, limit);
        var finished = 0;

        var results = await Task.WhenAll(branches.Select(async id =>
        {
            await slots.WaitAsync(cancellation);
            try
            {
                var verdict = await WalkAsync(state, id, depth + 1, cancellation);
                return (Order: Interlocked.Increment(ref finished), Verdict: verdict);
            }
            finally
            {
                slots.Release();
            }
        }));

        state.JoinAt = previousJoin;

        var failed = Waited(state, join) switch
        {
            "any" => results.All(result => result.Verdict == NodeVerdict.Failed),
            "first" => results.MinBy(result => result.Order).Verdict == NodeVerdict.Failed,
            _ => results.Any(result => result.Verdict == NodeVerdict.Failed),
        };

        // Every branch is awaited whatever the join says, including under "first". A branch left
        // running after the run moved on would write into a record nobody is reading any more.
        var outcome = failed
            ? NodeOutcome.Failed($"{results.Count(r => r.Verdict == NodeVerdict.Failed)} of "
                                 + $"{branches.Length} branches failed.")
            : NodeOutcome.Ok();

        return join is null ? outcome : outcome with { ContinueAt = join };
    }

    private static string Waited(RunnerState state, string? join) =>
        join is not null && state.ById.TryGetValue(join, out var node)
            ? node.Properties.GetValueOrDefault("wait") ?? "all"
            : "all";

    /// <summary>
    /// The join the branches converge on.
    ///
    /// Found by following the edges rather than declared, because on the canvas somebody draws
    /// three lines into one node and expects that to be the meeting point — nothing asks them to
    /// name it.
    /// </summary>
    private static string? FindJoin(RunnerState state, IReadOnlyList<string> branches)
    {
        foreach (var branch in branches)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(branch);

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!seen.Add(id) || !state.ById.TryGetValue(id, out var node)) continue;
                if (node.Key == "core.join") return id;

                var spec = NodeCatalogue.Find(node.Key);
                if (spec is null) continue;

                foreach (var port in spec.Outputs)
                {
                    if (state.Next(id, port.Name) is { } next) queue.Enqueue(next);
                }
            }
        }

        return null;
    }

    /// <summary>Runs the body no faster than the given number of steps a second.</summary>
    private async Task<NodeOutcome> RateLimitAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;

        var perSecond = Math.Clamp(Int(node, "perSecond", 5), 1, 1000);
        var previous = state.Pace;
        state.Pace = new Pacer(perSecond);

        try
        {
            var (verdict, _) = await BodyAsync(state, node, depth, cancellation);
            return verdict == NodeVerdict.Failed
                ? NodeOutcome.Failed("Something inside failed.")
                : NodeOutcome.Ok();
        }
        finally
        {
            state.Pace = previous;
        }
    }

    /// <summary>
    /// Turns the body's failure into this node's success.
    ///
    /// For the test that says a bad request is rejected. The checks inside are not counted against
    /// the run — a scenario proving that the API says no would otherwise report a failure for the
    /// no it was looking for.
    /// </summary>
    private async Task<NodeOutcome> ExpectFailureAsync(
        RunnerState state, GraphNode node, NodeSpec spec, int depth, CancellationToken cancellation)
    {
        _ = spec;

        var reason = node.Properties.GetValueOrDefault("reason") ?? "It was expected to fail.";

        NodeVerdict verdict;
        using (state.Inverting())
        {
            (verdict, _) = await BodyAsync(state, node, depth, cancellation);
        }

        if (verdict == NodeVerdict.Failed)
        {
            sink.Log(RunEventLevel.Info, $"It failed, as expected: {reason}", node.Id, node.Name);
            return NodeOutcome.Ok();
        }

        return NodeOutcome.Failed($"This was expected to fail, and it did not: {reason}");
    }

    /// <summary>
    /// Runs every cleanup block that was passed on the way through, in reverse.
    ///
    /// Reverse because cleanup undoes creation, and the last thing created is the first thing that
    /// has to go. Failures inside a cleanup are logged and do not change the run's verdict: a test
    /// that passed and then failed to tidy up did still pass.
    /// </summary>
    private async Task RunCleanupAsync(RunnerState state, CancellationToken cancellation)
    {
        if (state.Cleanups.Count == 0) return;

        state.Stopped = false;

        for (var index = state.Cleanups.Count - 1; index >= 0; index--)
        {
            var id = state.Cleanups[index];
            if (!state.ById.TryGetValue(id, out var node)) continue;

            sink.Log(RunEventLevel.Info, "Cleaning up.", node.Id, node.Name);

            try
            {
                // A fresh token: cleanup has to run even when the run was cancelled, which is the
                // moment there is most likely to be something left behind.
                await WalkAsync(state, state.FirstInside(node.Id), depth: 1, CancellationToken.None);
            }
            catch (Exception ex)
            {
                sink.Log(RunEventLevel.Warning, $"Cleaning up did not finish: {ex.Message}",
                    node.Id, node.Name);
            }
        }

        _ = cancellation;
    }

    private static int Int(GraphNode node, string name, int fallback) =>
        int.TryParse(node.Properties.GetValueOrDefault(name), out var value) ? value : fallback;

    private static TimeSpan ParseDuration(string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var text = value.Trim();
        var (number, unit) = text.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? (text[..^2], "ms")
            : text.EndsWith('s') ? (text[..^1], "s")
            : text.EndsWith('m') ? (text[..^1], "m")
            : (text, "s");

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

    /// <summary>Raised when a run hits one of the ceilings. Not a failure of the API under test.</summary>
    private sealed class RunLimitReached(string message) : Exception(message);
}

/// <summary>
/// Lets a step through no more often than a given rate.
///
/// A gap between starts rather than a bucket that refills: a test that is asked for five a second
/// should send one every two hundred milliseconds, not five at once and then wait. The API being
/// tested is somebody's, and a burst is what gets a test runner blocked.
/// </summary>
internal sealed class Pacer(double perSecond)
{
    private readonly TimeSpan _gap = TimeSpan.FromSeconds(1.0 / Math.Max(perSecond, 0.001));
    private readonly SemaphoreSlim _turn = new(1, 1);
    private long _last;

    public async Task WaitAsync(CancellationToken cancellation)
    {
        await _turn.WaitAsync(cancellation);

        try
        {
            if (_last != 0)
            {
                var since = Stopwatch.GetElapsedTime(_last);
                if (since < _gap) await Task.Delay(_gap - since, cancellation);
            }

            _last = Stopwatch.GetTimestamp();
        }
        finally
        {
            _turn.Release();
        }
    }
}

/// <summary>What a run amounted to.</summary>
public sealed record RunSummary(
    RunStatus Status,
    int Steps,
    int StepsFailed,
    int AssertionsPassed,
    int AssertionsFailed,
    double DurationMs,
    string? Outcome);

/// <summary>
/// The scopes a run reads variables from.
///
/// Assembled outside the engine — environment values, decrypted secrets, the current data-set row —
/// and handed in, so the runner never learns where any of it came from.
/// </summary>
public sealed record RunScopes(VariableScopes Scopes, RedactionScope Redaction);

/// <summary>
/// Where a run's record goes.
///
/// A port, so the engine can be run in a test with a sink that keeps everything in a list, and in
/// production with one that writes rows and pushes them to a browser.
/// </summary>
public interface IRunSink
{
    /// <summary>Starts a node's record and returns a handle the finish refers to.</summary>
    object Begin(GraphNode node, int iteration, int attempt);

    void Finish(object record, NodeRunStatus status, string? takenPort,
                double durationMs, JsonNode? output, string? failure);

    void Assertion(object record, AssertionRecord assertion);

    void Log(RunEventLevel level, string message, string? nodeId, string? nodeName, JsonNode? data = null);

    /// <summary>Keeps something alongside the run for somebody to look at afterwards.</summary>
    void Artifact(string name, string content, string? nodeId);
}
