using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runs;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// The live connection between a run and whoever is watching it.
///
/// One group per run rather than one per user, because two people can watch the same run and
/// neither should have to reload to see what the other already sees.
///
/// Joining is checked. A run id is a GUID and unguessable, but "unguessable" is not a permission —
/// somebody who has one from a shared link, an old bookmark or a log file must still be a member of
/// the workspace it belongs to.
/// </summary>
[Authorize]
public sealed class RunHub(ProofFlowDbContext db, ICurrentUser me) : Hub
{
    public const string Path = "/hubs/runs";

    public async Task Watch(string runId)
    {
        if (!Guid.TryParse(runId, out var id)) return;

        if (!me.Can(Capability.ViewProject)) return;

        var exists = await db.Runs.AnyAsync(run => run.Id == id);
        if (!exists) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(id));
    }

    public Task Unwatch(string runId) =>
        Guid.TryParse(runId, out var id)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(id))
            : Task.CompletedTask;

    public static string Group(Guid runId) => $"run:{runId}";
}

/// <summary>
/// Pushes what a run is doing to the browsers watching it.
///
/// Fire and forget on purpose. A run must not slow down, stall or fail because a browser is slow to
/// read — the database is the record, and this is the view.
/// </summary>
public sealed class SignalRRunWatchers(
    IHubContext<RunHub> hub,
    ILogger<SignalRRunWatchers> logger) : IRunWatchers
{
    public void NodeChanged(Guid runId, NodeUpdate update) => Send(runId, "node", update);

    public void AssertionRecorded(Guid runId, AssertionUpdate update) => Send(runId, "assertion", update);

    public void Logged(Guid runId, LogLine line) => Send(runId, "log", line);

    public void StatusChanged(Guid runId, RunStatus status, RunTotals totals) =>
        Send(runId, "status", new { status = status.ToString(), totals });

    private void Send(Guid runId, string name, object payload)
    {
        _ = hub.Clients.Group(RunHub.Group(runId))
            .SendAsync(name, payload)
            .ContinueWith(
                task => logger.LogDebug(task.Exception, "A run update did not reach a watcher."),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}
