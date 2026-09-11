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

    /// <summary>What this project's scenarios ask before they run, merged and asked once.</summary>
    public IReadOnlyList<ProofFlow.Contracts.Scenarios.ScenarioInputDto> Inputs { get; init; } = [];

    public bool CanRun { get; init; }
}

public sealed class MatrixGridViewModel
{
    public required Guid ProjectId { get; init; }

    public required Guid BatchId { get; init; }

    public string? Name { get; init; }
}

/// <summary>
/// One press of «check everything», as its page needs it.
///
/// Three fields, because everything else arrives from the state endpoint the island polls. The
/// page is deliberately not server-rendered with rows: the first render happens while the checks
/// are still queued, and a table of forty «waiting» rows written into HTML would have to be
/// replaced wholesale a second later anyway.
/// </summary>
public sealed class CheckBatchViewModel
{
    public required Guid ProjectId { get; init; }

    public required Guid BatchId { get; init; }

    public string? Name { get; init; }
}
