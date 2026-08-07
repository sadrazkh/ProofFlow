using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Contracts.Baselines;
using ProofFlow.Contracts.Runs;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Comparison;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// What staging returns and what production returns, side by side.
///
/// The same diff engine as a baseline comparison, deliberately. A second way of showing two JSON
/// documents differing would mean two colour languages, two ideas of what "ignored" looks like, and
/// a reader who has to learn the product twice. The only difference is what is on each side: there
/// it is an approved answer against today's; here it is one environment against another.
///
/// The rules are empty by default and that is honest rather than lazy. Two environments differ in
/// ids, timestamps and hostnames as a matter of course, and pretending to know which of those to
/// hide would hide a real one. Instead the dynamic-field detector runs over the pair and offers
/// them — the same list the baseline workbench shows — so the reader decides.
/// </summary>
public sealed class EnvironmentComparison(ProofFlowDbContext db)
{
    /// <summary>
    /// How many steps one comparison covers.
    ///
    /// A scenario looping over two thousand rows produces two thousand responses per side, and a
    /// page holding four thousand diffs is a page that never renders. What is dropped is counted
    /// and said out loud rather than silently cut.
    /// </summary>
    public const int MaxSteps = 40;

    public async Task<ComparisonDto?> CompareAsync(
        Guid batchId, Guid scenarioId, Guid leftEnvironmentId, Guid rightEnvironmentId,
        CancellationToken cancellation = default)
    {
        var runs = await db.Runs
            .Where(run => run.BatchId == batchId && run.ScenarioId == scenarioId)
            .Select(run => new { run.Id, run.EnvironmentId, run.Status })
            .ToListAsync(cancellation);

        var left = runs.FirstOrDefault(run => run.EnvironmentId == leftEnvironmentId);
        var right = runs.FirstOrDefault(run => run.EnvironmentId == rightEnvironmentId);

        if (left is null || right is null) return null;

        var names = await db.Environments
            .Where(environment => environment.Id == leftEnvironmentId
                                  || environment.Id == rightEnvironmentId)
            .ToDictionaryAsync(environment => environment.Id, environment => environment.Name, cancellation);

        var leftSteps = await ResponsesAsync(left.Id, cancellation);
        var rightSteps = await ResponsesAsync(right.Id, cancellation);

        // Matched by which node it was and which pass through the loop — not by position. A branch
        // that went one way in staging and the other in production produces two different lists,
        // and comparing them by index would diff step three against step four.
        var keys = leftSteps.Keys.Intersect(rightSteps.Keys).ToList();

        var steps = new List<ComparisonStepDto>();

        foreach (var key in keys.Take(MaxSteps))
        {
            var a = leftSteps[key];
            var b = rightSteps[key];

            var diff = SemanticDiff.CompareText(a.Body, b.Body, new ComparisonRuleSet([]));

            steps.Add(new ComparisonStepDto
            {
                NodeId = key.NodeId,
                NodeName = a.NodeName,
                Iteration = key.Iteration,
                LeftStatus = a.StatusCode,
                RightStatus = b.StatusCode,
                LeftDurationMs = a.DurationMs,
                RightDurationMs = b.DurationMs,
                Diff = BaselineService.Flatten(diff, names.GetValueOrDefault(leftEnvironmentId) ?? "—",
                    b.StatusCode, b.DurationMs),
                Suggestions = Suggestions(a.Body, b.Body),
            });
        }

        return new ComparisonDto
        {
            BatchId = batchId,
            ScenarioId = scenarioId,
            LeftEnvironmentId = leftEnvironmentId,
            RightEnvironmentId = rightEnvironmentId,
            LeftName = names.GetValueOrDefault(leftEnvironmentId) ?? "—",
            RightName = names.GetValueOrDefault(rightEnvironmentId) ?? "—",
            LeftStatus = left.Status.ToString(),
            RightStatus = right.Status.ToString(),
            LeftRunId = left.Id,
            RightRunId = right.Id,
            Steps = steps,

            // Said out loud. A comparison that quietly showed the first forty of two hundred steps
            // would read as "these two environments agree".
            StepsNotShown = Math.Max(0, keys.Count - steps.Count),

            // Steps one side has and the other does not — a branch that went differently, or a
            // request that never completed. This is often the whole answer.
            OnlyLeft = [.. leftSteps.Keys.Except(rightSteps.Keys).Select(key => leftSteps[key].NodeName).Distinct()],
            OnlyRight = [.. rightSteps.Keys.Except(leftSteps.Keys).Select(key => rightSteps[key].NodeName).Distinct()],
        };
    }

    private static IReadOnlyList<SuggestionDto> Suggestions(string leftBody, string rightBody)
    {
        JsonNode? left;
        JsonNode? right;

        try
        {
            left = JsonNode.Parse(leftBody);
            right = JsonNode.Parse(rightBody);
        }
        catch (JsonException)
        {
            return [];
        }

        return
        [
            .. DynamicFieldDetector.SuggestFromPair(left, right)
                .Select(suggestion => new SuggestionDto(
                    suggestion.Path,
                    suggestion.Reason.ToString(),
                    suggestion.Confidence.ToString(),
                    suggestion.Rule.Kind.ToString(),
                    suggestion.Rule.Note,
                    suggestion.Sample)),
        ];
    }

    /// <summary>
    /// Every response one run produced, by the step and pass that produced it.
    ///
    /// The highest attempt wins: a step that failed twice and worked on the third go returned what
    /// the third go returned, and comparing an abandoned attempt against the other side's final
    /// answer would report a difference that no longer exists.
    /// </summary>
    private async Task<Dictionary<StepKey, StepResponse>> ResponsesAsync(
        Guid runId, CancellationToken cancellation)
    {
        var nodes = await db.NodeRuns
            .Where(node => node.TestRunId == runId && node.OutputJson != null)
            .OrderBy(node => node.SortOrder)
            .Select(node => new
            {
                node.NodeId,
                node.NodeName,
                node.Iteration,
                node.Attempt,
                node.DurationMs,
                node.OutputJson,
            })
            .ToListAsync(cancellation);

        var found = new Dictionary<StepKey, StepResponse>();
        var attempts = new Dictionary<StepKey, int>();

        foreach (var node in nodes)
        {
            var key = new StepKey(node.NodeId, node.Iteration);

            if (attempts.TryGetValue(key, out var seen) && seen >= node.Attempt) continue;

            JsonNode? output;
            try
            {
                output = JsonNode.Parse(node.OutputJson!);
            }
            catch (JsonException)
            {
                continue;
            }

            if (output?["response"] is not JsonNode response) continue;

            var body = response["text"]?.ToString();
            if (body is null) continue;

            attempts[key] = node.Attempt;
            found[key] = new StepResponse(
                node.NodeName, body,
                response["statusCode"]?.GetValue<int>() ?? 0,
                node.DurationMs);
        }

        return found;
    }

    private readonly record struct StepKey(string NodeId, int Iteration);

    private sealed record StepResponse(string NodeName, string Body, int StatusCode, double DurationMs);
}
