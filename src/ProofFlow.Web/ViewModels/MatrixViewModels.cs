namespace ProofFlow.Web.ViewModels;

/// <summary>One batch, as the list shows it.</summary>
public sealed record BatchSummaryRow(
    Guid Id,
    string? Name,
    int Total,
    int Passed,
    int Failed,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>A scenario or an environment to tick before starting a batch.</summary>
public sealed record MatrixChoice(Guid Id, string Name, bool IsProduction);

public sealed class MatrixListViewModel
{
    public required Guid ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required IReadOnlyList<BatchSummaryRow> Batches { get; init; }

    public required IReadOnlyList<MatrixChoice> Scenarios { get; init; }

    public required IReadOnlyList<MatrixChoice> Environments { get; init; }

    public bool CanRun { get; init; }
}

public sealed class MatrixGridViewModel
{
    public required Guid ProjectId { get; init; }

    public required Guid BatchId { get; init; }

    public string? Name { get; init; }
}
