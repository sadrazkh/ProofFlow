using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ProofFlow.Application.Abstractions;

namespace ProofFlow.Infrastructure.Persistence;

/// <summary>
/// How <c>dotnet ef</c> builds a context without starting the application.
///
/// The connection strings here are placeholders that are never connected to: generating a
/// migration only needs the provider's type mappings, not a reachable database. Reading the real
/// connection string at design time is how a developer's credentials end up in a scaffolded file.
///
/// The scope is the system one, deliberately: at design time there is no request, and a null
/// workspace would make every query filter compile against a value that is never supplied.
/// </summary>
internal sealed class DesignTimeScope : IWorkspaceScope
{
    public Guid? WorkspaceId => null;
    public bool IsSystem => true;
}

public sealed class SqliteContextFactory : IDesignTimeDbContextFactory<SqliteProofFlowDbContext>
{
    public SqliteProofFlowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SqliteProofFlowDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new SqliteProofFlowDbContext(options, new DesignTimeScope());
    }
}

public sealed class PostgresContextFactory : IDesignTimeDbContextFactory<PostgresProofFlowDbContext>
{
    public PostgresProofFlowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PostgresProofFlowDbContext>()
            .UseNpgsql("Host=localhost;Database=proofflow_designtime;Username=postgres")
            .Options;

        return new PostgresProofFlowDbContext(options, new DesignTimeScope());
    }
}
