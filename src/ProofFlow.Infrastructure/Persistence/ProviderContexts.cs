using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProofFlow.Application.Abstractions;

namespace ProofFlow.Infrastructure.Persistence;

/// <summary>
/// The SQLite shape of the model. Its migrations live in <c>Persistence/Migrations/Sqlite</c>.
///
/// SQLite has no native JSON column type — the <c>json1</c> extension works over ordinary text — so
/// every JSON-bearing property is TEXT here and <c>jsonb</c> under PostgreSQL. That difference is
/// exactly why the two providers cannot share a migration set.
/// </summary>
public class SqliteProofFlowDbContext(DbContextOptions<SqliteProofFlowDbContext> options, IWorkspaceScope scope)
    : ProofFlowDbContext(options, scope)
{
    /// <summary>
    /// Fixed width, always UTC, always the same number of fractional digits.
    ///
    /// Every part of that matters for the ordering below: TEXT comparison is lexicographic, so
    /// "2026-08-06T09:00:00.1Z" would sort after "2026-08-06T09:00:00.05Z" if the fraction were
    /// variable-length, and a non-zero offset would make two identical instants compare unequal.
    /// </summary>
    private const string InstantFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        foreach (var property in JsonProperties(builder))
            property.SetColumnType("TEXT");

        ApplyInstantConversion(builder);
    }

    /// <summary>
    /// Stores every <see cref="DateTimeOffset"/> as sortable UTC text.
    ///
    /// Without this, SQLite refuses outright: "SQLite does not support expressions of type
    /// 'DateTimeOffset' in ORDER BY clauses". Every list in the application is ordered by a
    /// timestamp — runs, the activity log, projects by last change — so on the development and
    /// test provider that is not an edge case, it is every page.
    ///
    /// Converting rather than reaching for ticks keeps the column readable in a database browser,
    /// which matters when the fastest way to understand a failing run is to look at the rows.
    /// Comparison and range filters still work, because a fixed-width UTC string orders the same
    /// way the instants do.
    /// </summary>
    private static void ApplyInstantConversion(ModelBuilder builder)
    {
        var converter = new ValueConverter<DateTimeOffset, string>(
            value => value.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture),
            text => DateTimeOffset.ParseExact(text, InstantFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

        var nullableConverter = new ValueConverter<DateTimeOffset?, string?>(
            value => value == null
                ? null
                : value.Value.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture),
            text => text == null
                ? null
                : DateTimeOffset.ParseExact(text, InstantFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                    property.SetValueConverter(converter);
                else if (property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(nullableConverter);
            }
        }
    }

    internal static IEnumerable<IMutableProperty> JsonProperties(ModelBuilder builder) =>
        builder.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.Name.EndsWith("Json", StringComparison.Ordinal) && p.ClrType == typeof(string));
}

/// <summary>
/// The PostgreSQL shape. Migrations in <c>Persistence/Migrations/Postgres</c>.
///
/// No timestamp conversion here: <c>timestamptz</c> is a real instant type that sorts and compares
/// natively. It does insist the offset be zero, which is why every write is normalised to UTC in
/// <see cref="ProofFlowDbContext"/> rather than at the call sites.
/// </summary>
public class PostgresProofFlowDbContext(DbContextOptions<PostgresProofFlowDbContext> options, IWorkspaceScope scope)
    : ProofFlowDbContext(options, scope)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // jsonb, not json: stored parsed, so it can be indexed and queried. The extra write cost is
        // irrelevant next to the payload sizes this application keeps.
        foreach (var property in SqliteProofFlowDbContext.JsonProperties(builder))
            property.SetColumnType("jsonb");
    }
}
