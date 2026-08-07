using Microsoft.Extensions.Caching.Memory;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Holds the response a reader is currently looking at, between comparing it and accepting part
/// of it.
///
/// It has to be held somewhere. Accepting three fields means merging them out of the exact bytes
/// that produced the diff on screen — fetching the endpoint a second time would merge from a
/// response nobody reviewed, and on anything with a clock or a counter in it that is a different
/// response every time.
///
/// Not TempData, which is cookie-backed here: a 200 KB response body becomes 50 cookies and the
/// next request dies on a header-size limit. Not the database either, because this is scratch —
/// it is worth nothing ten minutes later, and a rejected comparison should leave no trace. So:
/// memory, per user and baseline, with an expiry, and a clear message when it has gone.
/// </summary>
public sealed class ComparisonScratch(IMemoryCache cache)
{
    /// <summary>
    /// Beyond this the response is not held at all and acceptance is refused with the same
    /// "compare again" message. A cap that is quietly ignored is a memory leak with a number
    /// written next to it.
    /// </summary>
    public const int MaximumBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public void Hold(Guid userId, Guid baselineId, HeldResponse response)
    {
        if (response.Body.Length > MaximumBytes) return;

        cache.Set(Key(userId, baselineId), response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,
        });
    }

    public HeldResponse? Take(Guid userId, Guid baselineId)
    {
        // Read without removing: a reader who accepts two fields, looks again and accepts a third
        // should not be told the comparison expired because they used it once.
        cache.TryGetValue(Key(userId, baselineId), out HeldResponse? held);
        return held;
    }

    public void Release(Guid userId, Guid baselineId) => cache.Remove(Key(userId, baselineId));

    private static string Key(Guid userId, Guid baselineId) => $"pf:compare:{userId}:{baselineId}";
}

public sealed record HeldResponse(string Body, string? ContentType, int StatusCode);
