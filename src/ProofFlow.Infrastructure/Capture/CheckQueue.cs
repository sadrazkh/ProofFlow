using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.Infrastructure.Capture;

/// <summary>
/// Endpoint checks waiting to be carried out.
///
/// The same shape as the run queue next door, and deliberately not the same queue. A check has no
/// graph, no node stream and no runner to hand itself to — but the reason it is separate is
/// simpler than that: runs and checks are started by different people for different reasons, and
/// one channel means somebody pressing «check this endpoint» waits behind a sixty-cell matrix that
/// has nothing to do with them.
///
/// Two workers means at most two things talking to somebody's API from this process at once. That
/// is a deliberate ceiling, not an oversight — see <see cref="CheckWorker"/>.
/// </summary>
public interface ICheckQueue
{
    ValueTask EnqueueAsync(QueuedCheck check, CancellationToken cancellation = default);

    IAsyncEnumerable<QueuedCheck> ReadAllAsync(CancellationToken cancellation);

    /// <summary>Asks a check in progress to stop. Nothing happens if it is not running here.</summary>
    bool Cancel(Guid sessionId);

    /// <summary>Registers a running check so it can be cancelled, and gives back its token.</summary>
    CancellationTokenSource Track(Guid sessionId, CancellationToken linkedTo);

    void Release(Guid sessionId);
}

/// <summary>
/// A check and the workspace it belongs to.
///
/// The workspace travels with it for the same reason it travels with a queued run: the worker has
/// no request and therefore no tenant, and a background check that read whichever workspace
/// happened to be last would be a cross-tenant failure that looks like a successful sweep.
/// </summary>
public sealed record QueuedCheck(Guid SessionId, Guid WorkspaceId);

public sealed class ChannelCheckQueue : ICheckQueue
{
    /// <summary>
    /// How many checks may wait.
    ///
    /// A thousand, matching the run queue. One press of «check everything» on a project with a
    /// hundred endpoints is a hundred of these, so the bound has to be comfortably above the
    /// biggest honest press and still low enough that a misfiring schedule is refused at the door.
    /// </summary>
    public const int Capacity = 1_000;

    private readonly Channel<QueuedCheck> _channel = Channel.CreateBounded<QueuedCheck>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait });

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public ValueTask EnqueueAsync(QueuedCheck check, CancellationToken cancellation = default) =>
        _channel.Writer.WriteAsync(check, cancellation);

    public IAsyncEnumerable<QueuedCheck> ReadAllAsync(CancellationToken cancellation) =>
        _channel.Reader.ReadAllAsync(cancellation);

    public bool Cancel(Guid sessionId)
    {
        if (!_running.TryGetValue(sessionId, out var source)) return false;

        source.Cancel();
        return true;
    }

    public CancellationTokenSource Track(Guid sessionId, CancellationToken linkedTo)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        _running[sessionId] = source;
        return source;
    }

    public void Release(Guid sessionId)
    {
        if (_running.TryRemove(sessionId, out var source)) source.Dispose();
    }
}

/// <summary>
/// The thing that actually carries them out.
///
/// One check at a time. A check is already four requests in flight — <see cref="CaptureService"/>
/// says why four — so running two checks at once would be eight, and «check everything» on a
/// forty-endpoint project would be a load test with a friendly name. Sequential also means the
/// counters somebody is watching move for one reason at a time.
/// </summary>
public sealed class CheckWorker(
    ICheckQueue queue,
    IServiceScopeFactory scopes,
    ILogger<CheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var queued in queue.ReadAllAsync(stoppingToken))
        {
            using var source = queue.Track(queued.SessionId, stoppingToken);

            try
            {
                await CheckOneAsync(queued, source.Token);
            }
            catch (Exception ex)
            {
                // The worker outlives any one check. CaptureService.ExecuteAsync already records
                // its own failures, so anything arriving here is the unexpected kind — and taking
                // the loop down for it would stop every other endpoint in the workspace.
                logger.LogError(ex, "Check {SessionId} came out of the worker.", queued.SessionId);
            }
            finally
            {
                queue.Release(queued.SessionId);
            }
        }
    }

    private async Task CheckOneAsync(QueuedCheck queued, CancellationToken cancellation)
    {
        // A scope pinned to the check's workspace, set before anything that reads it is resolved.
        // CaptureService finds its baseline through the tenant query filter and nothing else, so an
        // empty scope here would find no endpoint and report a check that never happened.
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackgroundWorkspace>().ActFor(queued.WorkspaceId);

        var service = scope.ServiceProvider.GetRequiredService<CaptureService>();
        await service.ExecuteAsync(queued.SessionId, cancellation);
    }
}
