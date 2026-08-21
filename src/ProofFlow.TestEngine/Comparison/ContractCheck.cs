using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace ProofFlow.TestEngine.Comparison;

/// <summary>
/// Does the answer still have the shape its own documentation promised?
///
/// A different question from the baseline's. The baseline says «this is what it returned last
/// time»; the contract says «this is what it said it would always return». An endpoint can return
/// a body identical to the approved one and still have broken its contract — a field that was a
/// number arriving as a string reads the same in a diff and fails every consumer downstream.
///
/// Which is why a comparison rule cannot silence one of these. Rules exist to say «this field
/// changes» — a timestamp, a generated id — and none of them says «this field may stop being what
/// the document promised».
/// </summary>
public static class ContractCheck
{
    /// <summary>How many violations are reported. The first is nearly always the cause of the rest.</summary>
    public const int MaxReported = 5;

    /// <summary>
    /// The violations, in the order a reader would meet them. Empty means it honours the contract —
    /// and an unreadable schema or a non-JSON body is empty too: neither is a statement about the
    /// API, and failing a test on the strength of a malformed import would be the wrong lesson.
    /// </summary>
    public static IReadOnlyList<ContractViolation> Check(string? contractJson, string? body)
    {
        if (string.IsNullOrWhiteSpace(contractJson) || string.IsNullOrWhiteSpace(body)) return [];

        JsonSchema schema;
        JsonNode? parsed;

        try
        {
            schema = JsonSchema.FromText(contractJson);
            parsed = JsonNode.Parse(body);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return [];
        }

        if (parsed is null) return [];

        var evaluation = schema.Evaluate(
            JsonSerializer.SerializeToElement(parsed),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        if (evaluation.IsValid) return [];

        return
        [
            .. Flatten(evaluation)
                .Where(node => node.Errors is { Count: > 0 })
                .SelectMany(node => node.Errors!.Select(error => new ContractViolation(
                    node.InstanceLocation.ToString() is { Length: > 0 } at ? at : "$",
                    error.Value)))
                .Take(MaxReported),
        ];
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        // Details is null rather than empty for a leaf in this library's shape.
        foreach (var child in results.Details ?? [])
        {
            foreach (var nested in Flatten(child)) yield return nested;
        }
    }
}

/// <summary>Where the answer stopped matching the promise, and how.</summary>
public sealed record ContractViolation(string Path, string Message);
