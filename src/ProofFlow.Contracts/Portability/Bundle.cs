using ProofFlow.Contracts.Scenarios;

namespace ProofFlow.Contracts.Portability;

/// <summary>
/// A project, in a file somebody can put in a repository.
///
/// Three rules shape all of it.
///
/// <b>No identifiers.</b> Nothing in here is a GUID. Everything refers to everything else by slug,
/// because a GUID is a fact about one installation's database and putting it in a file means two
/// exports of the same project never match, a diff is unreadable, and importing into a second
/// instance either collides or silently makes orphans.
///
/// <b>No secrets.</b> Not the value, not the ciphertext, not the nonce. What a secret <i>is called</i>
/// travels, under <see cref="SecretsToSupply"/>, because a scenario that reads
/// <c>{{secret.token}}</c> is useless on the far side unless somebody knows to create it — but the
/// value is never in the file, and a reader who opens it in an editor can see that for themselves.
///
/// <b>No history.</b> No runs, no audit entries, no rejected versions. Those are the record of what
/// happened on one installation; this is the description of a test suite. A file that carried both
/// would be a backup, which is a different thing with different rules.
/// </summary>
public sealed record Bundle
{
    /// <summary>
    /// The format version, first key in the file.
    ///
    /// Read before anything else, so a file from a later version is refused with a sentence rather
    /// than half-imported by a reader that skipped the fields it did not recognise.
    /// </summary>
    public int ProofFlow { get; init; } = CurrentVersion;

    public const int CurrentVersion = 1;

    /// <summary>
    /// When it was written. Informational, and the one line that changes between two exports of an
    /// unchanged project — which is why it is the only one.
    /// </summary>
    public DateTimeOffset? ExportedAt { get; init; }

    public required BundleProject Project { get; init; }

    public IReadOnlyList<BundleEnvironment> Environments { get; init; } = [];

    public IReadOnlyList<BundleScenario> Scenarios { get; init; } = [];

    public IReadOnlyList<BundleBaseline> Baselines { get; init; } = [];

    public IReadOnlyList<BundleDataSet> DataSets { get; init; } = [];

    public IReadOnlyList<BundleSchedule> Schedules { get; init; } = [];

    /// <summary>
    /// The secrets the far side has to create before any of this will run.
    ///
    /// Names and descriptions only. Listing them is what makes the import usable; carrying their
    /// values is what this format exists not to do.
    /// </summary>
    public IReadOnlyList<BundleSecretName> SecretsToSupply { get; init; } = [];
}

public sealed record BundleProject
{
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? Description { get; init; }
    public string? Accent { get; init; }
}

public sealed record BundleEnvironment
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Kind { get; init; }
    public string? BaseUrl { get; init; }
    public bool IsProduction { get; init; }
    public int TimeoutSeconds { get; init; }
    public int MaxRedirects { get; init; }
    public int MaxResponseKilobytes { get; init; }

    /// <summary>
    /// Carried, and deliberately so: it is a decision about what this environment is allowed to
    /// reach, and an import that quietly dropped it would produce a suite that fails on the far
    /// side for a reason nobody can see.
    /// </summary>
    public string? AllowedHosts { get; init; }

    public bool AllowPrivateNetwork { get; init; }
    public bool AllowInvalidCertificate { get; init; }
    public string? DefaultHeadersJson { get; init; }
    public int SortOrder { get; init; }

    public IReadOnlyList<BundleVariable> Variables { get; init; } = [];
}

public sealed record BundleVariable
{
    public required string Name { get; init; }
    public string Value { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public sealed record BundleSecretName
{
    public required string Name { get; init; }

    /// <summary>The environment it belongs to, by slug, or null for a project-wide one.</summary>
    public string? Environment { get; init; }

    public string? Description { get; init; }
}

public sealed record BundleScenario
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>The environment it runs against by default, by slug.</summary>
    public string? Environment { get; init; }

    /// <summary>
    /// The graph, with nodes numbered n1…nN in draw order rather than carrying database ids.
    ///
    /// That renumbering is the whole reason this format diffs cleanly: moving a node changes one
    /// line, and re-exporting an unchanged scenario changes none.
    /// </summary>
    public required GraphDto Graph { get; init; }
}

public sealed record BundleBaseline
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Environment { get; init; }
    public string? RequestJson { get; init; }

    /// <summary>
    /// The approved version, and only that one.
    ///
    /// A baseline's rejected proposals are the history of an argument that happened on one
    /// installation. What travels is the answer everybody agreed on.
    /// </summary>
    public BundleBaselineVersion? Approved { get; init; }
}

public sealed record BundleBaselineVersion
{
    public required string Body { get; init; }
    public string? ContentType { get; init; }
    public int StatusCode { get; init; }
    public string? HeadersJson { get; init; }
    public string? RulesJson { get; init; }
    public string? Description { get; init; }
}

public sealed record BundleDataSet
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? KeyColumn { get; init; }
    public string? ColumnsJson { get; init; }
    public IReadOnlyList<BundleDataRow> Rows { get; init; } = [];
}

public sealed record BundleDataRow
{
    public required string Key { get; init; }
    public required string ValuesJson { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record BundleSchedule
{
    public required string Name { get; init; }
    public required string Cron { get; init; }
    public string? TimeZone { get; init; }
    public bool Enabled { get; init; }

    /// <summary>By slug, and only the ones that are in this bundle.</summary>
    public IReadOnlyList<string> Scenarios { get; init; } = [];

    public IReadOnlyList<string> Environments { get; init; } = [];
}

// ------------------------------------------------------------------------------------------------

/// <summary>
/// What an import would do, before it does it.
///
/// Counted rather than described: "4 scenarios, 2 environments, 1 skipped" is a sentence somebody
/// reads. A list of forty names is one they scroll past and confirm anyway.
/// </summary>
public sealed record ImportPreview
{
    public required string ProjectName { get; init; }

    /// <summary>True when the import would make a new project rather than add to one.</summary>
    public bool CreatesProject { get; init; }

    public required IReadOnlyList<ImportCount> Counts { get; init; }

    /// <summary>
    /// Names of things whose slug is already taken, which the import will leave alone.
    ///
    /// An import adds. It never overwrites: somebody who wanted the incoming version can delete
    /// theirs and import again, and somebody who did not would otherwise have no way back.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>Secret names the far side has to create. Empty is the common case.</summary>
    public IReadOnlyList<string> SecretsToSupply { get; init; } = [];

    /// <summary>Why this file cannot be imported at all, or null.</summary>
    public string? Refusal { get; init; }

    public int Total => Counts.Sum(count => count.Adding);
}

/// <summary>One row of the preview: what kind of thing, how many are new, how many exist already.</summary>
public sealed record ImportCount(string Kind, int Adding, int Existing);

/// <summary>What an import actually did.</summary>
public sealed record ImportResult
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required IReadOnlyList<ImportCount> Counts { get; init; }
    public IReadOnlyList<string> Skipped { get; init; } = [];
}
