namespace ProofFlow.Application.Abstractions;

/// <summary>
/// Now, as a dependency.
///
/// The engine compares timestamps with tolerances, retries with backoff, and decides whether a
/// schedule is due. All three are untestable against a real clock, and all three are wrong in ways
/// that only show up at a month boundary or in a different time zone.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
