using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The real application, against a database of its own.
///
/// A file rather than <c>:memory:</c>: the connection pool opens more than one connection, and an
/// in-memory SQLite database vanishes when the connection that created it closes — which produces
/// "no such table" errors that look like a migration bug and are a lifetime bug.
/// </summary>
public sealed class ProofFlowApplication : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseFile =
        Path.Combine(Path.GetTempPath(), $"proofflow-tests-{Guid.CreateVersion7():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);

        builder.UseSetting("Database:Provider", "sqlite");
        builder.UseSetting("Database:AutoMigrate", "true");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databaseFile}");

        // No demo account in a test run. Tests that need one create it through the real sign-up
        // path, which is also the only way to know that path still works.
        builder.UseSetting("Demo:Seed", "false");
    }

    public Task InitializeAsync()
    {
        // Touching the client is what actually builds the host, so migration failures surface here
        // rather than inside the first test that happens to make a request.
        _ = CreateClient();
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_databaseFile)) File.Delete(_databaseFile);
        }
        catch (IOException)
        {
            // A temp file left behind is not worth failing a green test run over.
        }
    }
}
