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

// The worker has no requests, so it has no workspace — and that is exactly the trap this line
// avoids. Registered with the system scope, background work sees every tenant; registered with a
// request scope it would see none, read an empty database, do nothing, and report success.
builder.Services.AddSingleton<IWorkspaceScope, SystemWorkspaceScope>();
builder.Services.AddSingleton<ICurrentUser>(_ => new SystemUser());
builder.Services.AddProofFlowInfrastructure(builder.Configuration);

var host = builder.Build();

// Runners, schedulers and the retention sweeper are registered by the phases that introduce them.
// Until then this process starts, connects, and idles — which is a real, verifiable state, and a
// better starting point than a host that does not build.
await host.RunAsync();
