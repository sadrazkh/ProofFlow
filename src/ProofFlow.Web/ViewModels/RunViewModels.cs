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

public sealed class RunConsoleViewModel
{
    public required Guid ProjectId { get; init; }

    public required Guid RunId { get; init; }

    public required Guid ScenarioId { get; init; }

    public required string ScenarioName { get; init; }

    public RunStatus Status { get; init; }

    public bool CanCancel { get; init; }
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
