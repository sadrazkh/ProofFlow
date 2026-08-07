using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure.Auditing;
using ProofFlow.Infrastructure.Common;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Http;
using ProofFlow.Infrastructure.Security;
using ProofFlow.TestEngine.Http;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Everything that talks to the outside world: the database, the clock, the audit trail.
    ///
    /// The caller must have registered an <see cref="IWorkspaceScope"/> and an
    /// <see cref="ICurrentUser"/> first — those are host concerns. The web application resolves
    /// them from the request; the worker resolves a system scope that spans tenants. Registering a
    /// default here would mean a host that forgot silently got the wrong one, and the wrong one is
    /// a tenant boundary.
    /// </summary>
    public static IServiceCollection AddProofFlowInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var provider = DatabaseProviderSelector.Resolve(configuration);
        var connection = DatabaseProviderSelector.ConnectionString(configuration, provider);

        switch (provider)
        {
            case DatabaseProvider.Postgres:
                services.AddDbContext<PostgresProofFlowDbContext>(options => options
                    .UseNpgsql(connection, npgsql => npgsql.CommandTimeout(60)));
                services.AddScoped<ProofFlowDbContext>(sp =>
                    sp.GetRequiredService<PostgresProofFlowDbContext>());
                break;

            case DatabaseProvider.Sqlite:
                services.AddDbContext<SqliteProofFlowDbContext>(options => options
                    .UseSqlite(connection));
                services.AddScoped<ProofFlowDbContext>(sp =>
                    sp.GetRequiredService<SqliteProofFlowDbContext>());
                break;

            default:
                throw new InvalidOperationException($"Unhandled database provider {provider}.");
        }

        services.AddSingleton(new DatabaseSettings(provider, connection));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IAuditLog, AuditLog>();

        services.AddSingleton<ISecretCipher, AesGcmSecretCipher>();
        services.AddProofFlowHttpClients();
        services.AddScoped<IHttpExecutor, GuardedHttpExecutor>();
        services.AddScoped<EnvironmentContextBuilder>();
        services.AddScoped<Baselines.BaselineService>();
        services.AddScoped<Capture.CaptureService>();
        services.AddScoped<Data.DataSetService>();

        return services;
    }
}

/// <summary>
/// Which database this process ended up on, available to anything that needs to say so — the
/// diagnostics page, the migration runner, and the tests that assert both providers agree.
/// </summary>
public sealed record DatabaseSettings(DatabaseProvider Provider, string ConnectionString)
{
    public bool IsPostgres => Provider == DatabaseProvider.Postgres;

    public bool IsSqlite => Provider == DatabaseProvider.Sqlite;
}
