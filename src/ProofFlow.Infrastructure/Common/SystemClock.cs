using ProofFlow.Application.Abstractions;

namespace ProofFlow.Infrastructure.Common;

/// <summary>The real clock, always in UTC. Local time enters only at the point of display.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
