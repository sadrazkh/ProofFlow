using ProofFlow.Domain.Common;
using ProofFlow.Domain.Projects;

namespace ProofFlow.Domain.Data;

/// <summary>
/// The inputs a scenario is run against, many times over.
///
/// Section 5 of the brief describes the case this exists for: two thousand study identifiers, each
/// one a separate call, each one with its own idea of what a correct answer looks like. A single
/// request with a single expected response cannot express that, and neither can two thousand
/// copies of a scenario.
///
/// The set is the named, durable thing; the rows live in versions. That split is not ceremony — a
/// regression report has to be able to say which data it ran against, and a set that can be edited
/// in place makes every report older than the last edit unreadable.
/// </summary>
public class DataSet : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The column that identifies a row across versions.
    ///
    /// This is what pairs a captured sample with the baseline for the same input six months later.
    /// Without it the only identity available is position, and position changes the first time
    /// somebody sorts the spreadsheet — which silently re-points every baseline at the wrong row.
    ///
    /// Null means the row's ordinal is its identity, which is honest for a small hand-typed set
    /// and dangerous for a large imported one. The interface says so.
    /// </summary>
    public string? KeyColumn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>The version rows are read from unless a run names another one.</summary>
    public Guid? CurrentVersionId { get; set; }

    public ICollection<DataSetVersion> Versions { get; set; } = [];
}

/// <summary>
/// One frozen set of rows.
///
/// Immutable once anything has run against it. Editing produces the next version, so a run from
/// March can still say exactly which two thousand identifiers it used.
/// </summary>
public class DataSetVersion : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid DataSetId { get; set; }

    public DataSet? DataSet { get; set; }

    /// <summary>1, 2, 3… within the set. Shown to people; never reused.</summary>
    public int Number { get; set; }

    /// <summary>The column names, in order, as JSON. Rows are objects; this is the header.</summary>
    public string? ColumnsJson { get; set; }

    public string? Description { get; set; }

    public int RowCount { get; set; }

    public Guid CreatedByUserId { get; set; }

    public ICollection<DataSetRow> Rows { get; set; } = [];
}

/// <summary>
/// One input.
///
/// The values are a JSON object rather than columns, because the shape is the user's and not the
/// schema's: one set holds study identifiers, the next holds a customer id and a currency and a
/// date. <c>{{dataset.current.studyId}}</c> is a lookup by name into this object.
/// </summary>
public class DataSetRow : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid DataSetVersionId { get; set; }

    public DataSetVersion? Version { get; set; }

    /// <summary>Position in the set, from zero. The fallback identity when there is no key column.</summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// The value of the key column, or the ordinal as text when there is none.
    ///
    /// Denormalised on purpose: every sample lookup and every regression pairing goes through it,
    /// and reading it out of the JSON on each of two thousand rows is the difference between a
    /// report that renders and one that times out.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>The row itself: a flat JSON object of column name to value.</summary>
    public required string ValuesJson { get; set; }

    /// <summary>
    /// Set aside without being deleted.
    ///
    /// A row that turned out to be bad test data — a deleted study, a customer who churned — should
    /// stop being run without vanishing from the history of runs that used it.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
