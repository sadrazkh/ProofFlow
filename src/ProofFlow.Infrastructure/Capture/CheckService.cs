using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Capture;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Infrastructure.Capture;

/// <summary>
/// One press, every endpoint.
///
/// The gap this closes is the one the quick-add form opened. Recording forty endpoints takes a
/// minute; checking them took forty presses on forty pages, so in practice nobody did it — and the
/// bell, the badge and the schedules all had nothing to report, because nothing was ever run.
///
/// Every cell is an ordinary <see cref="CaptureSession"/>, queued the same way one pressed by hand
/// is, and carried out by the same worker. There is no batch runner and no second code path: what
/// happened to each endpoint is on its own session, so there is only ever one answer to "did this
/// one change".
/// </summary>
public sealed class CheckService(
    ProofFlowDbContext db,
    CaptureService capture,
    ICheckQueue queue,
    ICurrentUser me)
{
    /// <summary>
    /// How many checks one press may start.
    ///
    /// Higher than the matrix's sixty because a cell here is one request rather than a whole
    /// scenario, and a project with two hundred endpoints is an ordinary project. Still bounded:
    /// the number exists so that a schedule with a mistake in it is refused at the door rather
    /// than discovered as traffic on somebody's API.
    /// </summary>
    public const int MaxChecks = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Writes a check for every endpoint asked for, then hands them all to the worker.
    ///
    /// Two phases, like the matrix: every session is written before any is enqueued, so the page
    /// that opens next already shows every row rather than growing one at a time.
    /// </summary>
    /// <param name="baselineIds">
    /// Which endpoints. Empty means every one in the project that has a request to send.
    /// </param>
    /// <param name="environmentIds">
    /// Which environments. Empty means each endpoint's own — which is what pressing its button
    /// does, and therefore what "check everything" should mean without asking anybody a question.
    /// </param>
    public async Task<CheckBatch> QueueAsync(
        Guid projectId,
        IReadOnlyList<Guid> baselineIds,
        IReadOnlyList<Guid> environmentIds,
        string? name,
        RunTrigger trigger,
        CancellationToken cancellation = default)
    {
        var wanted = db.Baselines
            .Where(b => b.ProjectId == projectId && b.ArchivedAt == null && b.RequestJson != null);

        if (baselineIds.Count > 0) wanted = wanted.Where(b => baselineIds.Contains(b.Id));

        var endpoints = await wanted
            .OrderBy(b => b.Name)
            .Select(b => new { b.Id, b.WorkspaceId, b.DataSetId })
            .ToListAsync(cancellation);

        if (endpoints.Count == 0)
            throw new InvalidOperationException("There is nothing here to check yet.");

        // Re-checked against this project rather than trusted from the form. The tenant filter
        // already stops another workspace's environment; this stops another project's.
        var chosen = environmentIds.Count == 0
            ? new List<Guid>()
            : await db.Environments
                .Where(e => e.ProjectId == projectId && environmentIds.Contains(e.Id))
                .Select(e => e.Id)
                .ToListAsync(cancellation);

        // Empty means "each endpoint's own", which is one cell per endpoint. A single null stands
        // in for that so the loop below has one shape rather than two.
        var against = chosen.Count == 0
            ? new List<Guid?> { null }
            : chosen.Select(id => (Guid?)id).ToList();

        var cells = endpoints.Count * against.Count;

        if (cells > MaxChecks)
        {
            throw new InvalidOperationException(
                $"That is {cells} calls to a real API. {MaxChecks} is the most one press starts.");
        }

        // Which version of each endpoint's inputs to sweep. One query rather than one per endpoint,
        // and a set with no saved version yet means that endpoint is sent once instead — better
        // than refusing the whole press over one unfinished set.
        var setIds = endpoints
            .Where(e => e.DataSetId is not null)
            .Select(e => e.DataSetId!.Value)
            .ToList();

        var versions = setIds.Count == 0
            ? []
            : await db.DataSets
                .Where(d => setIds.Contains(d.Id) && d.CurrentVersionId != null)
                .ToDictionaryAsync(d => d.Id, d => d.CurrentVersionId!.Value, cancellation);

        var batch = new CheckBatch
        {
            WorkspaceId = endpoints[0].WorkspaceId,
            ProjectId = projectId,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Trigger = trigger,
            StartedByUserId = me.UserId,
            Total = cells,
        };

        db.CheckBatches.Add(batch);
        await db.SaveChangesAsync(cancellation);

        var queued = new List<QueuedCheck>(cells);

        foreach (var endpoint in endpoints)
        {
            var versionId = endpoint.DataSetId is { } setId && versions.TryGetValue(setId, out var found)
                ? found
                : (Guid?)null;

            foreach (var environmentId in against)
            {
                var session = await capture.QueueAsync(
                    new StartCaptureCommand
                    {
                        BaselineId = endpoint.Id,
                        DataSetVersionId = versionId,
                        EnvironmentId = environmentId,
                        Mode = "Regression",
                        BatchId = batch.Id,
                    },
                    cancellation);

                queued.Add(new QueuedCheck(session.Id, session.WorkspaceId));
            }
        }

        foreach (var check in queued) await queue.EnqueueAsync(check, cancellation);

        return batch;
    }

    /// <summary>
    /// What a batch looks like now, and whether it is over.
    ///
    /// Stamps <see cref="CheckBatch.FinishedAt"/> the first time it is read after the last check
    /// lands — the same trick the matrix uses, and for the same reason: there is no batch runner
    /// to notice, and a background sweep existing solely to write that timestamp would be a timer
    /// nobody has asked for.
    /// </summary>
    public async Task<CheckBatchDto?> ReadAsync(Guid batchId, CancellationToken cancellation = default)
    {
        var batch = await db.CheckBatches.FirstOrDefaultAsync(b => b.Id == batchId, cancellation);
        if (batch is null) return null;

        var raw = await db.CaptureSessions
            .Where(s => s.BatchId == batchId)
            .OrderBy(s => s.Baseline!.Name)
            .Select(s => new
            {
                s.Id,
                s.BaselineId,
                s.Baseline!.Name,
                s.Baseline.RequestJson,
                EnvironmentName = s.Environment != null ? s.Environment.Name : null,
                s.Status,
                s.TotalRows,
                s.Completed,
                s.Differing,
                s.Failed,
                s.Unmatched,
                s.Slow,
                s.StoppedReason,
            })
            .ToListAsync(cancellation);

        var rows = raw.Select(s =>
        {
            var request = Request(s.RequestJson);

            return new CheckRowDto
            {
                SessionId = s.Id,
                BaselineId = s.BaselineId,
                Name = s.Name,
                Method = request?.Method ?? "GET",
                Url = request?.Url ?? string.Empty,
                EnvironmentName = s.EnvironmentName,
                Status = s.Status.ToString(),
                TotalRows = s.TotalRows,
                Completed = s.Completed,
                Differing = s.Differing,
                Failed = s.Failed,
                Unmatched = s.Unmatched,
                Slow = s.Slow,
                StoppedReason = s.StoppedReason,
            };
        }).ToList();

        var settled = rows.Count >= batch.Total
            && rows.TrueForAll(r => r.Status is not ("Queued" or "Running"));

        if (settled && batch.FinishedAt is null)
        {
            batch.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellation);
        }

        return new CheckBatchDto
        {
            Id = batch.Id,
            ProjectId = batch.ProjectId,
            Name = batch.Name,
            Total = batch.Total,
            Settled = settled,
            StartedAt = batch.CreatedAt,
            FinishedAt = batch.FinishedAt,
            Rows = rows,
        };
    }

    /// <summary>The stored request, or nothing when it cannot be read. Never throws at a reader.</summary>
    private static HttpRequestDefinition? Request(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<HttpRequestDefinition>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
