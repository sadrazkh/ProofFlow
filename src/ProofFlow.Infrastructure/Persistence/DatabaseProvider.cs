using Microsoft.Extensions.Configuration;

namespace ProofFlow.Infrastructure.Persistence;

public enum DatabaseProvider
{
    /// <summary>The zero-install default: a file, created on first run.</summary>
    Sqlite = 1,

    /// <summary>The supported production target.</summary>
    Postgres = 2,
}

/// <summary>
/// Decides which database this process is talking to, and refuses to guess.
///
/// Two providers is a deliberate cost. It buys a checkout that runs with nothing installed but the
/// .NET SDK — which is what makes the demo data, the screenshots and the whole integration suite
/// runnable on a laptop — while production still gets PostgreSQL. The price is that every query
/// has to translate on both, and <c>SqlSyntaxTests</c> exists to keep that true.
/// </summary>
public static class DatabaseProviderSelector
{
    public const string ProviderKey = "Database:Provider";
    public const string ConnectionName = "Default";

    public static DatabaseProvider Resolve(IConfiguration configuration)
    {
        var configured = configuration[ProviderKey];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().ToLowerInvariant() switch
            {
                "postgres" or "postgresql" or "npgsql" => DatabaseProvider.Postgres,
                "sqlite" => DatabaseProvider.Sqlite,
                _ => throw new InvalidOperationException(
                    $"'{configured}' is not a database provider ProofFlow knows. Use 'sqlite' or 'postgres'."),
            };
        }

        // No explicit choice. A connection string that names a PostgreSQL host is a clear enough
        // signal to follow; anything else means the developer default.
        var connection = configuration.GetConnectionString(ConnectionName);
        return LooksLikePostgres(connection) ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite;
    }

    public static string ConnectionString(IConfiguration configuration, DatabaseProvider provider)
    {
        var configured = configuration.GetConnectionString(ConnectionName);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        if (provider == DatabaseProvider.Postgres)
            throw new InvalidOperationException(
                "PostgreSQL was selected but ConnectionStrings:Default is empty. Supply it through " +
                "user-secrets or the environment — a password in appsettings.json ends up in a commit.");

        // The SQLite default is a file beside the application's data, not in the repository.
        var dataDirectory = configuration["Database:SqliteDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
            dataDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data");

        Directory.CreateDirectory(dataDirectory);
        var file = Path.Combine(dataDirectory, "proofflow.db");

        // Write-ahead logging: the runner writes node-run rows while the browser reads them, and
        // the default journal mode makes those two block each other.
        return $"Data Source={file};Cache=Shared;Foreign Keys=True";
    }

    private static bool LooksLikePostgres(string? connection) =>
        !string.IsNullOrWhiteSpace(connection)
        && (connection.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            || connection.StartsWith("postgres", StringComparison.OrdinalIgnoreCase));
}
