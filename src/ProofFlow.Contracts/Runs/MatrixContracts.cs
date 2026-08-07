namespace ProofFlow.Contracts.Runs;

/// <summary>
/// A matrix as the browser reads it: a row per scenario, a column per environment.
///
/// Statuses are words rather than enum numbers, the same rule as everywhere else that crosses this
/// boundary — a status that arrives as 3 is one the interface cannot read and does not complain
/// about.
/// </summary>
public sealed record MatrixDto
{
    public required Guid BatchId { get; init; }

    public string? Name { get; init; }

    /// <summary>Queued, Running, Passed or Failed — worked out from the runs, not stored.</summary>
    public required string State { get; init; }

    public int Total { get; init; }

    /// <summary>How many cells have reached a verdict. What the progress line counts.</summary>
    public int Done { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public required IReadOnlyList<MatrixColumnDto> Columns { get; init; }

    public required IReadOnlyList<MatrixRowDto> Rows { get; init; }
}

public sealed record MatrixColumnDto
{
    public required Guid EnvironmentId { get; init; }

    public required string Name { get; init; }

    /// <summary>Marked in the header. Somebody should never wonder which column is production.</summary>
    public bool IsProduction { get; init; }
}

public sealed record MatrixRowDto
{
    public required Guid ScenarioId { get; init; }

    public required string Name { get; init; }

    /// <summary>One per column, in the columns' order. Null where no run was started.</summary>
    public required IReadOnlyList<MatrixCellDto?> Cells { get; init; }
}

public sealed record MatrixCellDto
{
    public required Guid RunId { get; init; }

    public required string Status { get; init; }

    public double DurationMs { get; init; }

    public int AssertionsPassed { get; init; }

    public int AssertionsFailed { get; init; }

    public string? Outcome { get; init; }
}

/// <summary>
/// Two environments' answers to the same scenario, step by step.
///
/// The diff inside each step is the same <see cref="Baselines.DiffResultDto"/> the baseline
/// workbench renders, so the viewer that draws it is the same component — that is the point.
/// </summary>
public sealed record ComparisonDto
{
    public required Guid BatchId { get; init; }

    public required Guid ScenarioId { get; init; }

    public required Guid LeftEnvironmentId { get; init; }

    public required Guid RightEnvironmentId { get; init; }

    public required string LeftName { get; init; }

    public required string RightName { get; init; }

    public required string LeftStatus { get; init; }

    public required string RightStatus { get; init; }

    public required Guid LeftRunId { get; init; }

    public required Guid RightRunId { get; init; }

    public required IReadOnlyList<ComparisonStepDto> Steps { get; init; }

    /// <summary>How many steps were left out of a long run. Counted rather than silently cut.</summary>
    public int StepsNotShown { get; init; }

    /// <summary>Steps only one side reached — usually a branch that went the other way.</summary>
    public IReadOnlyList<string> OnlyLeft { get; init; } = [];

    public IReadOnlyList<string> OnlyRight { get; init; } = [];
}

public sealed record ComparisonStepDto
{
    public required string NodeId { get; init; }

    public required string NodeName { get; init; }

    /// <summary>Which pass through the surrounding loop. Zero when there is no loop.</summary>
    public int Iteration { get; init; }

    public int LeftStatus { get; init; }

    public int RightStatus { get; init; }

    public double LeftDurationMs { get; init; }

    public double RightDurationMs { get; init; }

    public required Baselines.DiffResultDto Diff { get; init; }

    /// <summary>Fields that look like they differ by nature rather than by fault.</summary>
    public IReadOnlyList<Baselines.SuggestionDto> Suggestions { get; init; } = [];
}
