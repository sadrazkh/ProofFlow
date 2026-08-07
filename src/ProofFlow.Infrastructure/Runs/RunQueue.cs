using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProofFlow.Infrastructure.Tenancy;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// Runs waiting to be carried out.
///
/// In process, for now, and the shape is chosen so that it does not have to stay that way: the
/// queue is an interface and the worker takes ids rather than objects, so the same worker reads
/// from a database table or a message broker in a later phase without the caller changing.
///
/// The request that starts a run does not wait for it. A scenario over two thousand rows takes
/// twenty minutes, and a browser holding a connection open for twenty minutes is a browser that has
/// already given up.
/// </summary>
public interface IRunQueue
{
    ValueTask EnqueueAsync(QueuedRun run, CancellationToken cancellation = default);

    IAsyncEnumerable<QueuedRun> ReadAllAsync(CancellationToken cancellation);

    /// <summary>Asks a run in progress to stop. Nothing happens if it is not running here.</summary>
    bool Cancel(Guid runId);

    /// <summary>Registers a running run so it can be cancelled, and gives back its token.</summary>
    CancellationTokenSource Track(Guid runId, CancellationToken linkedTo);

    void Release(Guid runId);
}

/// <summary>
/// A run and the workspace it belongs to.
///
/// The workspace travels with it because the worker has no request and therefore no tenant: taking
/// it from anywhere else would mean a background run reading whichever workspace happened to be
/// last, which is the quiet cross-tenant failure the scope exists to prevent.
/// </summary>
public sealed record QueuedRun(Guid RunId, Guid WorkspaceId);

public sealed class ChannelRunQueue : IRunQueue
{
    /// <summary>
    /// How many runs may wait.
    ///
    /// Bounded rather than unbounded: a schedule that misfires and asks for ten thousand runs
    /// should be refused at the door, not accepted and then discovered as memory.
    /// </summary>
    public const int Capacity = 1_000;

    private readonly Channel<QueuedRun> _channel = Channel.CreateBounded<QueuedRun>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait });

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource>
        _running = new();

    public ValueTask EnqueueAsync(QueuedRun run, CancellationToken cancellation = default) =>
        _channel.Writer.WriteAsync(run, cancellation);

    public IAsyncEnumerable<QueuedRun> ReadAllAsync(CancellationToken cancellation) =>
        _channel.Reader.ReadAllAsync(cancellation);

    public bool Cancel(Guid runId)
    {
        if (!_running.TryGetValue(runId, out var source)) return false;

        source.Cancel();
        return true;
    }

    public CancellationTokenSource Track(Guid runId, CancellationToken linkedTo)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        _running[runId] = source;
        return source;
    }

    public void Release(Guid runId)
    {
        if (_running.TryRemove(runId, out var source)) source.Dispose();
    }
}

/// <summary>
/// The thing that actually runs them.
///
/// One run at a time by default. A test runner that starts six scenarios at once against the same
/// staging API is a load test nobody asked for, and the person watching cannot tell which run is
/// making the API slow.
/// </summary>
public sealed class RunWorker(
    IRunQueue queue,
    IServiceScopeFactory scopes,
    ILogger<RunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var queued in queue.ReadAllAsync(stoppingToken))
        {
            using var source = queue.Track(queued.RunId, stoppingToken);

            try
            {
                await RunOneAsync(queued, source.Token);
            }
            catch (Exception ex)
            {
                // The worker outlives any one run. A scenario that took the loop down would stop
                // every other run in the workspace, which is a much worse failure than the one that
                // caused it.
                logger.LogError(ex, "Run {RunId} came out of the worker.", queued.RunId);
            }
            finally
            {
                queue.Release(queued.RunId);
            }
        }
    }

    private async Task RunOneAsync(QueuedRun queued, CancellationToken cancellation)
    {
        // A scope pinned to the run's workspace, set before anything that reads it is resolved:
        // the query filter is the tenant boundary, and a background run with an empty scope would
        // read nothing and report success.
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackgroundWorkspace>().ActFor(queued.WorkspaceId);

        var service = scope.ServiceProvider.GetRequiredService<RunService>();
        await service.ExecuteAsync(queued.RunId, cancellation);
    }
}
