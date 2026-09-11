namespace ProofFlow.Contracts.Capture;

/// <summary>What a sweep is asked to do.</summary>
public sealed record StartCaptureCommand
{
    public required Guid BaselineId { get; init; }

    /// <summary>
    /// Which rows to sweep, or nothing to send it once.
    ///
    /// Null is the ordinary case rather than the exception: an endpoint added from the list or the
    /// request lab has no data set until somebody gives it one, and «check this» has to mean
    /// something for it anyway. One send, compared against the approved answer.
    /// </summary>
    public Guid? DataSetVersionId { get; init; }

    public Guid? EnvironmentId { get; init; }

    /// <summary>Capture records; Regression judges. The same machinery, two different questions.</summary>
    public string Mode { get; init; } = "Capture";

    /// <summary>
    /// Stop after this many rows.
    ///
    /// Exists because the first sweep of a two-thousand-row set is usually a mistake somebody
    /// wants to find after ten, not after twenty minutes and two thousand real calls to a real API.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>The press this check was part of, when it was one of many. Null for a single one.</summary>
    public Guid? BatchId { get; init; }
}

/// <summary>One press of «check these», as a page reads it.</summary>
public sealed record CheckBatchDto
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public string? Name { get; init; }

    /// <summary>How many checks were started. The rows may be fewer while they are still being written.</summary>
    public required int Total { get; init; }

    /// <summary>Every check has finished, one way or another. The page stops polling on this.</summary>
    public required bool Settled { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    public required IReadOnlyList<CheckRowDto> Rows { get; init; }
}

/// <summary>One endpoint in a batch, and what its check found.</summary>
public sealed record CheckRowDto
{
    public required Guid SessionId { get; init; }
    public required Guid BaselineId { get; init; }
    public required string Name { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }

    /// <summary>Which environment it was sent to, when the endpoint is tied to one.</summary>
    public string? EnvironmentName { get; init; }

    public required string Status { get; init; }
    public required int TotalRows { get; init; }
    public required int Completed { get; init; }
    public required int Differing { get; init; }
    public required int Failed { get; init; }
    public required int Unmatched { get; init; }
    public required int Slow { get; init; }
    public string? StoppedReason { get; init; }
}

/// <summary>One row in the review queue. Deliberately without the body — that is fetched on demand.</summary>
public sealed record SampleRowDto
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required int Ordinal { get; init; }
    public required string Status { get; init; }
    public required bool Differs { get; init; }
    public int StatusCode { get; init; }
    public double DurationMs { get; init; }
    public string? FailureMessage { get; init; }

    /// <summary>Counts per diff category, so the queue shows a shape without loading two bodies.</summary>
    public IReadOnlyDictionary<string, int> DiffCounts { get; init; } =
        new Dictionary<string, int>();

    public string? ReviewNote { get; init; }
}

public sealed record CaptureSessionDto
{
    public required Guid Id { get; init; }
    public required string Mode { get; init; }
    public required string Status { get; init; }
    public required int TotalRows { get; init; }
    public required int Completed { get; init; }
    public required int Differing { get; init; }

    /// <summary>Rows with no approved answer to compare against — checked by nothing.</summary>
    public int Unmatched { get; init; }
    public required int Failed { get; init; }
    public string? StoppedReason { get; init; }

    /// <summary>How many samples sit in each state — the six numbers the queue is filtered by.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
}

/// <summary>A decision about some samples, taken together.</summary>
public sealed record ReviewSamplesCommand
{
    public IReadOnlyList<Guid> SampleIds { get; init; } = [];

    /// <summary>Approved, Rejected or Reviewed. Nothing else is a decision a person makes.</summary>
    public required string Status { get; init; }

    public string? Note { get; init; }
}

/// <summary>Rows as the editor holds them, before they become a version.</summary>
public sealed record DataSetDraft
{
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; } = [];
    public string? KeyColumn { get; init; }
    public string? Description { get; init; }
}

/// <summary>What the paste parser made of what was pasted, before anything is imported.</summary>
public sealed record ParsedPasteDto
{
    /// <summary>Csv, Tsv, Json, Lines — named so the interface can say what it thinks it read.</summary>
    public required string Format { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; }

    /// <summary>Rows that could not be read, with the line number and why. Never silently dropped.</summary>
    public IReadOnlyList<PasteProblem> Problems { get; init; } = [];

    public int TotalLines { get; init; }
}

public sealed record PasteProblem(int Line, string Text, string Reason);
