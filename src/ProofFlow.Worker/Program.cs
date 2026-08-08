using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure;
using ProofFlow.Infrastructure.Tenancy;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

// A second home for the background work, and not one to start by accident.
//
// The run worker, the scheduler and the retention sweeper are all registered by
// AddProofFlowInfrastructure — which the web application also calls. So running this process beside
// it means two schedulers deciding independently that a nightly suite is due, and two sweepers
// deleting the same rows. Meanwhile this one's run queue is an in-memory channel that nothing on
// this side ever writes to, so it would idle while doing that damage.
//
// Splitting them properly needs a queue both processes can see and a lease on the scheduler. Until
// that exists the supported deployment is one process — the web application — and this refuses to
// start rather than quietly becoming the second one.
if (!builder.Configuration.GetValue("Worker:Enabled", false))
{
    Console.Error.WriteLine(
        """
        ProofFlow.Worker did not start.

        Background work — the run worker, the scheduler and the retention sweeper — already runs
        inside the web application, and a second process pointed at the same database would run a
        second scheduler and a second sweeper against it.

        The supported deployment is the web application on its own; see docker-compose.yml. Set
        Worker:Enabled=true only if this process is the one running background work and the web
        application is not.
        """);

    return 2;
}

// The worker has no requests, so it has no workspace — and that is exactly the trap this line
// avoids. Registered with the system scope, background work sees every tenant; registered with a
// request scope it would see none, read an empty database, do nothing, and report success.
builder.Services.AddSingleton<IWorkspaceScope, SystemWorkspaceScope>();
builder.Services.AddSingleton<ICurrentUser>(_ => new SystemUser());
builder.Services.AddProofFlowInfrastructure(builder.Configuration);

await builder.Build().RunAsync();

return 0;
