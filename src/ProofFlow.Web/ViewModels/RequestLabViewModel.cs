namespace ProofFlow.Web.ViewModels;

public sealed record RequestLabViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<RequestLabEnvironment> Environments { get; init; }
    public bool CanRun { get; init; }
}

/// <summary>
/// <paramref name="IsProduction"/> travels to the browser so the environment picker can mark it —
/// pressing Send against production is a different act from pressing it against staging, and the
/// only moment to say so is before the press.
/// </summary>
public sealed record RequestLabEnvironment(Guid Id, string Name, string? BaseUrl, bool IsProduction);
