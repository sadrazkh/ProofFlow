using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Redaction;
using ProofFlow.TestEngine.Running;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// The outside world, as the engine is allowed to see it.
///
/// The engine declares three things it needs — send this, give me those rows, what was approved for
/// this input — and this supplies all three from the real system. Which is what keeps the guard on:
/// a node cannot reach the network except through here, so every request a scenario makes goes
/// through the same URL policy, the same redirect handling and the same size cap as a request typed
/// into the builder by hand.
/// </summary>
public sealed class EngineRunServices(
    ProofFlowDbContext db,
    IHttpExecutor executor,
    BaselineService baselines,
    IClock clock,
    UrlPolicy policy,
    RedactionScope redaction,
    Guid projectId) : IRunServices
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Every request a run made, in order. What the run report shows.</summary>
    public List<RequestRecord> Exchanges { get; } = [];

    public async Task<HttpNodeResult> SendAsync(HttpNodeRequest request, CancellationToken cancellation)
    {
        var definition = new HttpRequestDefinition
        {
            Method = request.Method,
            Url = request.Url,
            Headers = [.. request.Headers.Select(pair => new KeyValueEntry(pair.Name, pair.Value))],
            Body = request.Body is null ? null : new RequestBody
            {
                Kind = ParseBody(request.BodyKind),
                Content = request.Body,
            },
            TimeoutSeconds = request.Timeout is { } timeout ? (int)timeout.TotalSeconds : null,
        };

        var result = await executor.SendAsync(definition, policy, cancellation);

        // The address as well as the body. A secret used in a path — «/records/{{secrets.token}}»
        // is an ordinary shape — ends up in the resolved URL, and that URL is written into the
        // step's output, shown in the console and carried into a report. Redacting the body and
        // leaving the address is the same as not redacting.
        var url = redaction.Apply(result.ResolvedUrl);

        Exchanges.Add(new RequestRecord(
            request.Method, url, result.StatusCode,
            result.Duration.TotalMilliseconds, clock.UtcNow));

        if (!result.Succeeded)
        {
            return new HttpNodeResult(false, result.StatusCode, result.ReasonPhrase, [],
                string.Empty, null, result.Duration.TotalMilliseconds,
                redaction.Apply(result.Failure!.Message), url);
        }

        // Redacted here rather than at the edge. Anything a node reads out of a body ends up in a
        // log line, a variable and a stored output, and hiding a secret in only one of those is the
        // same as not hiding it.
        var body = redaction.Apply(result.Body);
        var headers = redaction.Apply(result.ResponseHeaders);

        return new HttpNodeResult(
            true, result.StatusCode, result.ReasonPhrase,
            [.. headers.Select(entry => (entry.Name, entry.Value))],
            body, result.ContentType, result.Duration.TotalMilliseconds, null, url);
    }

    private static BodyKind ParseBody(string? kind) => kind switch
    {
        "json" => BodyKind.Json,
        "form" => BodyKind.FormUrlEncoded,
        "text" or "raw" => BodyKind.Text,
        _ => BodyKind.None,
    };

    /// <summary>
    /// A data set's rows, by name or by id.
    ///
    /// The reference is whatever the property holds, and on the canvas that is a name somebody
    /// chose — so both are accepted rather than making the graph carry an id nobody can read.
    /// </summary>
    public async Task<IReadOnlyList<JsonNode>> DataSetRowsAsync(
        string reference, CancellationToken cancellation)
    {
        var query = db.DataSets.Where(set => set.ProjectId == projectId);

        query = Guid.TryParse(reference, out var id)
            ? query.Where(set => set.Id == id)
            : query.Where(set => set.Name == reference);

        var dataSet = await query.FirstOrDefaultAsync(cancellation);
        if (dataSet is null) return [];

        var version = await db.DataSetVersions
            .Where(candidate => candidate.DataSetId == dataSet.Id)
            .OrderByDescending(candidate => candidate.Number)
            .FirstOrDefaultAsync(cancellation);

        if (version is null) return [];

        var rows = await db.DataSetRows
            .Where(row => row.DataSetVersionId == version.Id && row.Enabled)
            .OrderBy(row => row.Ordinal)
            .Select(row => row.ValuesJson)
            .ToListAsync(cancellation);

        return [.. rows.Select(Parse).OfType<JsonNode>()];
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<BaselineAnswer?> BaselineAsync(
        string reference, string? key, CancellationToken cancellation)
    {
        var baseline = await FindBaselineAsync(reference, cancellation);
        if (baseline is null) return null;

        var rules = new ComparisonRuleSet(await baselines.LoadRulesAsync(baseline.Id, cancellation));

        // A key means a sample of a sample-based baseline; no key means the single approved answer.
        if (!string.IsNullOrWhiteSpace(key))
        {
            var sample = await db.BaselineSamples
                .FirstOrDefaultAsync(candidate =>
                    candidate.BaselineId == baseline.Id && candidate.Key == key, cancellation);

            return sample is null ? null : new BaselineAnswer(sample.Body, rules);
        }

        var version = await baselines.ApprovedVersionAsync(baseline.Id, cancellation);
        return version is null ? null : new BaselineAnswer(version.Body, rules);
    }

    /// <summary>
    /// Files an answer against a baseline.
    ///
    /// Never approved unless asked. An unapproved answer goes where an unapproved answer goes
    /// everywhere else in ProofFlow — the review queue — rather than into a second holding area
    /// that only scenario runs know about. A person looking at "what is waiting for me" sees one
    /// list whether the answer came from a sweep or from a run.
    /// </summary>
    public async Task CaptureBaselineAsync(
        string reference, string? key, CapturedAnswer answer, bool approve,
        CancellationToken cancellation)
    {
        var baseline = await FindBaselineAsync(reference, cancellation)
            ?? throw new InvalidOperationException($"There is no baseline called «{reference}».");

        var body = redaction.Apply(answer.Body);

        if (string.IsNullOrWhiteSpace(key))
        {
            var version = await baselines.CaptureAsync(
                baseline, body, answer.ContentType, answer.StatusCode, null, cancellation);

            if (approve) await baselines.ApproveAsync(version, cancellation);
            return;
        }

        var session = await SessionAsync(baseline, cancellation);

        db.CaptureSamples.Add(new CaptureSample
        {
            WorkspaceId = baseline.WorkspaceId,
            CaptureSessionId = session.Id,
            Key = key,
            Ordinal = session.Completed,
            Status = SampleStatus.Captured,
            ResolvedUrl = redaction.Apply(answer.Url),
            StatusCode = answer.StatusCode,
            ContentType = answer.ContentType,
            Body = body,
            DurationMs = answer.DurationMs,
        });

        session.Completed++;
        session.TotalRows = session.Completed;

        if (approve)
        {
            var sample = await db.BaselineSamples
                .FirstOrDefaultAsync(candidate =>
                    candidate.BaselineId == baseline.Id && candidate.Key == key, cancellation);

            if (sample is null)
            {
                sample = new BaselineSample
                {
                    WorkspaceId = baseline.WorkspaceId,
                    BaselineId = baseline.Id,
                    Key = key,
                    Body = body,
                };

                db.BaselineSamples.Add(sample);
            }

            sample.Body = body;
            sample.ContentType = answer.ContentType;
            sample.StatusCode = answer.StatusCode;
            sample.ApprovedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellation);
    }

    private CaptureSession? _session;

    /// <summary>
    /// The capture session this run's samples are filed under, made on first use.
    ///
    /// One per run rather than one per sample: two thousand rows are two thousand answers to one
    /// question, and a review queue with two thousand sessions of one sample each is a queue nobody
    /// can work through.
    /// </summary>
    private async Task<CaptureSession> SessionAsync(Baseline baseline, CancellationToken cancellation)
    {
        if (_session is not null) return _session;

        _session = new CaptureSession
        {
            WorkspaceId = baseline.WorkspaceId,
            ProjectId = baseline.ProjectId,
            BaselineId = baseline.Id,
            Mode = CaptureMode.Capture,
            Status = CaptureSessionStatus.Running,
            StartedAt = clock.UtcNow,
        };

        db.CaptureSessions.Add(_session);
        await db.SaveChangesAsync(cancellation);

        return _session;
    }

    /// <summary>Closes the run's capture session, if it opened one.</summary>
    public async Task FinishAsync(CancellationToken cancellation)
    {
        if (_session is null) return;

        _session.Status = CaptureSessionStatus.Completed;
        _session.FinishedAt = clock.UtcNow;

        await db.SaveChangesAsync(cancellation);
    }

    private Task<Baseline?> FindBaselineAsync(string reference, CancellationToken cancellation)
    {
        var query = db.Baselines.Where(baseline => baseline.ProjectId == projectId);

        query = Guid.TryParse(reference, out var id)
            ? query.Where(baseline => baseline.Id == id)
            : query.Where(baseline => baseline.Name == reference);

        return query.FirstOrDefaultAsync(cancellation);
    }
}

/// <summary>One request a run made, for the summary line the console shows.</summary>
public sealed record RequestRecord(
    string Method, string Url, int StatusCode, double DurationMs, DateTimeOffset At);
