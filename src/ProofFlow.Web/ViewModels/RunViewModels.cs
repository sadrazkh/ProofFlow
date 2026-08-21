using ProofFlow.Domain.Runs;

namespace ProofFlow.Web.ViewModels;

/// <summary>
/// What the run history shows.
///
/// Counts rather than a verdict word alone: "passed" and "passed, 40 of 41 checks" are different
/// facts, and the second is the one that tells somebody a soft assertion is quietly failing.
/// </summary>
public sealed record RunSummaryRow(
    Guid Id,
    Guid ScenarioId,
    string? ScenarioName,
    string? EnvironmentName,
    RunStatus Status,
    RunTrigger Trigger,
    double DurationMs,
    int AssertionsPassed,
    int AssertionsFailed,
    DateTimeOffset StartedAt);

public sealed class RunListViewModel
{
    public required Guid ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required IReadOnlyList<RunSummaryRow> Runs { get; init; }

    public bool CanRun { get; init; }
}

/// <summary>
/// Which status colour a verdict wears.
///
/// One map for every page that shows one. The runs list, the dashboard panel and the console all
/// draw the same five words, and a run that is amber in one place and red in another is two
/// different runs as far as the reader is concerned.
/// </summary>
public static class Verdicts
{
    public static string Tone(RunStatus status) => status switch
    {
        RunStatus.Passed => "pass",
        RunStatus.Failed => "fail",
        RunStatus.Errored => "warn",
        RunStatus.Running => "running",
        _ => "idle",
    };
}

public sealed class RunConsoleViewModel
{
    public required Guid ProjectId { get; init; }

    public required Guid RunId { get; init; }

    public required Guid ScenarioId { get; init; }

    public required string ScenarioName { get; init; }

    public RunStatus Status { get; init; }

    public bool CanCancel { get; init; }

    /// <summary>
    /// The name of the step this run began at, when it did not begin at the beginning.
    ///
    /// The name rather than the id, because the console shows it to a person: "from «Fetch the
    /// order» onwards" is the sentence, and an id would be the sentence's worst possible subject.
    /// </summary>
    public string? StartedFrom { get; init; }
}

/// <summary>
/// One step's turn, as the console draws it.
///
/// The status is a word rather than the enum. An enum serialised by number arrives in the browser
/// as 3, which no amount of TypeScript catches and which the console silently drops — the same
/// decision as <c>GraphProblem.Severity</c>, and for the same reason.
/// </summary>
public sealed record NodeRunRow(
    Guid Id,
    string NodeId,
    string NodeName,
    string NodeKey,
    string Status,
    int Iteration,
    int Attempt,
    double DurationMs,
    string? TakenPort,
    string? FailureMessage,
    DateTimeOffset StartedAt);

public sealed record AssertionRow(
    Guid NodeRunId,
    string Description,
    bool Passed,
    bool Soft,
    string? Expected,
    string? Actual,
    string? Target);

public sealed record RunEventRow(
    long Sequence,
    string Level,
    string Message,
    string? NodeId,
    string? NodeName,
    DateTimeOffset At,
    string? DataJson);

/// <summary>
/// A run's result as somebody without an account sees it.
///
/// Every field here has been through the redaction scope. What is not here is the point: no log,
/// no payloads, no graph and no inputs.
/// </summary>
public sealed record SharedRunViewModel
{
    public required string ProjectName { get; init; }
    public required string ScenarioName { get; init; }
    public string? EnvironmentName { get; init; }
    public required RunStatus Status { get; init; }
    public string? Outcome { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public double DurationMs { get; init; }
    public int AssertionsPassed { get; init; }
    public int AssertionsFailed { get; init; }
    public required IReadOnlyList<SharedStep> Steps { get; init; }
}

public sealed record SharedStep(string? Name, string Status, double DurationMs, int Iteration);
