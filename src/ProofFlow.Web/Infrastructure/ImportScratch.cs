using Microsoft.Extensions.Caching.Memory;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Holds the file somebody is importing, between the preview and the confirmation.
///
/// The preview and the apply have to read the same bytes. Re-uploading between the two steps would
/// be absurd, and putting the text in a hidden field means an eight-megabyte OpenAPI document goes
/// up and down the wire twice and sits in the page where the browser will offer to restore it.
///
/// Not TempData, which is cookie-backed here. Not the database, because a preview somebody
/// abandoned should leave nothing behind. So: memory, keyed to the person who uploaded it, with a
/// short life and a clear message when it has gone.
/// </summary>
public sealed class ImportScratch(IMemoryCache cache)
{
    /// <summary>
    /// Long enough to read a preview and decide. Not long enough that a forgotten tab holds a
    /// document in memory for the afternoon.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    public string Hold(Guid userId, HeldImport held)
    {
        var ticket = Guid.CreateVersion7().ToString("N");

        cache.Set(Key(userId, ticket), held, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,

            // Sized so a handful of large documents cannot crowd everything else out of the cache.
            Size = Math.Max(1, held.Text.Length / 1024),
        });

        return ticket;
    }

    /// <summary>
    /// Reads without removing.
    ///
    /// Somebody who previews, goes back to change the target project, and previews again should not
    /// be told their file expired because they looked at it once.
    /// </summary>
    public HeldImport? Take(Guid userId, string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;

        cache.TryGetValue(Key(userId, ticket), out HeldImport? held);
        return held;
    }

    public void Release(Guid userId, string ticket) => cache.Remove(Key(userId, ticket));

    private static string Key(Guid userId, string ticket) => $"pf:import:{userId}:{ticket}";
}

/// <summary>The text of the file and which reader it was given to.</summary>
public sealed record HeldImport(string Text, string Source, string? FileName);
