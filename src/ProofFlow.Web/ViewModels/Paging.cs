namespace ProofFlow.Web.ViewModels;

/// <summary>
/// One page of a longer list, and enough to draw the control that moves between them.
///
/// This exists because two lists in this application stopped being lists. A Postman collection of
/// two thousand requests turned into two thousand rows, and the page that rendered them did not
/// fail — it took eleven seconds, produced a document the browser could not scroll smoothly, and
/// gave no way to reach row 1900 other than the keyboard's End key. «Show everything» is a
/// perfectly good default right up to the moment somebody imports something real.
///
/// Deliberately not a general-purpose grid. There is no sorting and no column choosing here: the
/// order is the one the page decided, and a control that offers to reorder ten thousand rows is
/// offering to run ten thousand rows through the database again.
/// </summary>
public sealed record Paging
{
    /// <summary>1-based, because it is shown to people.</summary>
    public required int Page { get; init; }

    public required int PageSize { get; init; }

    /// <summary>How many rows there are in total, not how many are on this page.</summary>
    public required int Total { get; init; }

    /// <summary>Where this page's links point, without the page number — «/projects/x/endpoints».</summary>
    public required string Path { get; init; }

    /// <summary>Anything else in the query string that has to survive a page change, already
    /// escaped and starting with «&amp;» — a search term, a filter.</summary>
    public string Query { get; init; } = string.Empty;

    public int LastPage => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < LastPage;

    /// <summary>The ordinal of the first row on this page, 1-based. Zero when there are none.</summary>
    public int From => Total == 0 ? 0 : ((Page - 1) * PageSize) + 1;

    public int To => Math.Min(Page * PageSize, Total);

    public string Link(int page) => $"{Path}?page={page}{Query}";

    /// <summary>
    /// Reads a page number that may be anything, including absent, negative, or past the end.
    ///
    /// Clamped rather than refused: «?page=0» in a pasted link should show the first page, not a
    /// 400, and a page that has been emptied by somebody else's deletion should show the last one
    /// that still exists rather than an empty table with no way back.
    /// </summary>
    public static int Clamp(int? requested, int pageSize, int total)
    {
        var last = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        return Math.Clamp(requested ?? 1, 1, last);
    }

    /// <summary>The default, and the only size on offer. Twenty-five rows is a screen.</summary>
    public const int DefaultPageSize = 25;
}
