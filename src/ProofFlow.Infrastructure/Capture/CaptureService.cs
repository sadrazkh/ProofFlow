using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Capture;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Data;
using ProofFlow.Infrastructure.Baselines;
using ProofFlow.Infrastructure.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Variables;

namespace ProofFlow.Infrastructure.Capture;

/// <summary>
/// Sends one request once per row of a data set, and keeps what came back.
///
/// This is the half of section 5 that is not the interface. Two thousand identifiers, two thousand
/// calls, two thousand answers to file — and the whole point is that it stays honest at that size:
/// a row that fails is a failed row and not a failed sweep, a sweep that is cancelled keeps what it
/// already has, and every sample says which data-set version produced it.
///
/// Nothing here approves anything. Capturing records what the API does today, which is a fact;
/// whether today is correct is a separate question asked by a person in the review queue.
/// </summary>
public sealed class CaptureService(
    ProofFlowDbContext db,
    EnvironmentContextBuilder environments,
    IHttpExecutor executor,
    EnvironmentAuthenticator authenticator,
    ICurrentUser me,
    IClock clock,
    Notifications.NotificationWriter? notifications = null)
{
    /// <summary>
    /// How many requests are in flight at once.
    ///
    /// Four rather than "as many as possible". The thing on the other end is somebody's real API,
    /// often the one their customers are using, and a test tool that opens two hundred connections
    /// to it is a denial of service with a friendly name. Four is enough to make a long sweep
    /// bearable and small enough to stay under any sane rate limit.
    /// </summary>
    public const int Concurrency = 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Runs a sweep to completion, writing samples as it goes.
    ///
    /// Written in chunks rather than one row at a time or all at the end. One at a time means two
    /// thousand round trips to the database; all at the end means a cancelled sweep — or a crashed
    /// one — leaves nothing behind, which is the opposite of what somebody who has just waited
    /// twenty minutes wants.
    /// </summary>
    public async Task<CaptureSession> RunAsync(
        StartCaptureCommand command, CancellationToken cancellationToken = default)
    {
        var baseline = await db.Baselines
            .FirstOrDefaultAsync(b => b.Id == command.BaselineId, cancellationToken)
            ?? throw new InvalidOperationException("No such baseline in this workspace.");

        var version = await db.DataSetVersions
            .FirstOrDefaultAsync(v => v.Id == command.DataSetVersionId, cancellationToken)
            ?? throw new InvalidOperationException("No such data-set version in this workspace.");

        var rows = await db.DataSetRows
            .Where(r => r.DataSetVersionId == version.Id && r.Enabled)
            .OrderBy(r => r.Ordinal)
            .Take(command.Limit is > 0 ? command.Limit.Value : int.MaxValue)
            .ToListAsync(cancellationToken);

        var session = new CaptureSession
        {
            WorkspaceId = baseline.WorkspaceId,
            ProjectId = baseline.ProjectId,
            BaselineId = baseline.Id,
            DataSetVersionId = version.Id,
            EnvironmentId = command.EnvironmentId ?? baseline.EnvironmentId,
            Mode = Enum.TryParse<CaptureMode>(command.Mode, out var mode) ? mode : CaptureMode.Capture,
            TotalRows = rows.Count,
            StartedAt = clock.UtcNow,
            StartedByUserId = me.UserId ?? Guid.Empty,
        };

        db.CaptureSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        var request = ReadRequest(baseline);
        if (request is null)
        {
            session.Status = CaptureSessionStatus.Failed;
            session.StoppedReason = "This baseline has no stored request, so it cannot be replayed.";
            session.FinishedAt = clock.UtcNow;
            notifications?.SweepFailed(session, baseline.ProjectId, baseline.Name);
            await db.SaveChangesAsync(cancellationToken);
            return session;
        }

        var rules = new ComparisonRuleSet(await LoadRulesAsync(baseline.Id, cancellationToken));

        // Loaded once, by key. Two thousand individual lookups is two thousand round trips, and
        // the approved bodies are the same bodies the diff needs anyway.
        var approved = await db.BaselineSamples
            .Where(s => s.BaselineId == baseline.Id)
            .ToDictionaryAsync(s => s.Key, cancellationToken);

        var context = session.EnvironmentId is { } environmentId
            ? await environments.BuildAsync(environmentId, cancellationToken)
            : null;

        var policy = context?.Policy ?? new UrlPolicy();

        // Signed in once for the whole sweep, before the first row rather than during it. Two
        // thousand rows must not be two thousand logins — and a failure here stops the sweep with
        // the reason, because two thousand 401s reported as «the answer changed» is the least
        // useful true statement this product could make.
        var inherited = Array.Empty<KeyValueEntry>() as IReadOnlyList<KeyValueEntry>;

        if (context is not null)
        {
            var outcome = await authenticator.HeadersAsync(
                context.Auth, context.Environment.BaseUrl, context.Resolver(), policy,
                context.TokenKey, cancellationToken);

            if (!outcome.Ok)
            {
                session.Status = CaptureSessionStatus.Failed;
                session.StoppedReason = outcome.Problem;
                session.FinishedAt = clock.UtcNow;

                // The 3am case this whole feature exists for: a password changed and every
                // scheduled sweep will stop exactly here until somebody hears about it.
                notifications?.SweepFailed(session, baseline.ProjectId, baseline.Name);
                await db.SaveChangesAsync(cancellationToken);
                return session;
            }

            inherited = outcome.Headers;
        }

        try
        {
            foreach (var chunk in rows.Chunk(Concurrency))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var results = await Task.WhenAll(chunk.Select(row =>
                    SendAsync(row, request, context, policy, inherited, cancellationToken)));

                foreach (var (row, result) in chunk.Zip(results))
                {
                    db.CaptureSamples.Add(
                        Judge(session, row, result, rules, approved, baseline.MaxDurationMs, baseline.ContractJson));
                }

                session.Completed += chunk.Length;
                await db.SaveChangesAsync(cancellationToken);
            }

            session.Status = CaptureSessionStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            // Whatever was written stays written. A sweep that discards its own work on being
            // stopped teaches people never to stop it.
            session.Status = CaptureSessionStatus.Cancelled;
            session.StoppedReason = "Stopped before it finished.";
        }

        session.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        return session;
    }

    /// <summary>One row: resolve, send, redact. No judgement, no database.</summary>
    private async Task<SampleResult> SendAsync(
        DataSetRow row, HttpRequestDefinition request, EnvironmentContext? context,
        UrlPolicy policy, IReadOnlyList<KeyValueEntry> inherited,
        CancellationToken cancellationToken)
    {
        var scopes = context?.Scopes ?? new VariableScopes();

        // A fresh scope object per row: `dataset.current` is per-row state, and sharing one across
        // four concurrent sends would let rows read each other's values.
        var perRow = new VariableScopes
        {
            Environment = scopes.Environment,
            Variables = scopes.Variables,
            Secrets = scopes.Secrets,
            Run = scopes.Run,
            Dataset = new JsonObject { ["current"] = ParseRow(row.ValuesJson) },
        };

        var resolver = new VariableResolver(perRow, context?.Redaction);

        HttpRequestDefinition resolved;
        try
        {
            resolved = request with
            {
                // Joined onto the environment when the stored address is a path rather than a
                // whole URL — an OpenAPI import stores «/records/{id}» verbatim, and an absolute
                // one comes back from Combine untouched.
                Url = EnvironmentAuthenticator.Combine(
                    context?.Environment.BaseUrl, resolver.Resolve(request.Url)),
                Headers = [.. request.Headers.Select(h => h with { Value = resolver.Resolve(h.Value) })],
                Body = request.Body is null ? null : request.Body with
                {
                    Content = resolver.Resolve(request.Body.Content ?? string.Empty),
                },
            };
        }
        catch (VariableResolutionException ex)
        {
            return new SampleResult(null, null, 0, null, 0, ex.Message);
        }

        resolved = InheritedHeaders.Apply(
            resolved, inherited, context?.Environment.DefaultHeadersJson);

        var response = await executor.SendAsync(resolved, policy, cancellationToken);

        if (!response.Succeeded)
        {
            return new SampleResult(
                response.ResolvedUrl, null, response.StatusCode, null,
                response.Duration.TotalMilliseconds, response.Failure!.Message);
        }

        var body = context?.Redaction.Apply(response.Body) ?? response.Body;

        return new SampleResult(
            response.ResolvedUrl, body, response.StatusCode, response.ContentType,
            response.Duration.TotalMilliseconds, null);
    }

    /// <summary>
    /// Turns one response into a sample, and says whether it moved.
    ///
    /// The cheap test first: a hash under the rules answers "did anything change?" without walking
    /// two trees, which is what makes two thousand samples finish. Only a mismatch pays for the
    /// real diff, and then only for its summary — the rows themselves are built on demand when
    /// somebody opens that sample.
    /// </summary>
    private CaptureSample Judge(
        CaptureSession session, DataSetRow row, SampleResult result,
        ComparisonRuleSet rules, IReadOnlyDictionary<string, BaselineSample> approved,
        int? maxDurationMs, string? contractJson)
    {
        var sample = new CaptureSample
        {
            WorkspaceId = session.WorkspaceId,
            CaptureSessionId = session.Id,
            Key = row.Key,
            Ordinal = row.Ordinal,
            ResolvedUrl = result.Url,
            StatusCode = result.StatusCode,
            ContentType = result.ContentType,
            Body = result.Body,
            DurationMs = result.DurationMs,
            FailureMessage = result.Failure,
        };

        if (result.Failure is not null || result.Body is null)
        {
            sample.Status = SampleStatus.Failed;
            session.Failed++;
            return sample;
        }

        // The budget check happens even for rows with nothing to compare against — «too slow» is a
        // fact about this answer, not about the previous one.
        sample.TooSlow = maxDurationMs is { } budget && result.DurationMs > budget;

        sample.NormalizedHash = BaselineService.Hash(result.Body, rules);

        if (!approved.TryGetValue(row.Key, out var baseline))
        {
            // Nothing to differ from. In a capture this is the ordinary case; in a regression it
            // means the set grew, which is worth seeing rather than counting as a pass — and it
            // used to be counted as one, because nothing here incremented anything and «passed»
            // is derived from what is left over.
            session.Unmatched++;
            return sample;
        }

        // The status code is part of the answer. It used to be recorded and never compared, so a
        // 500 whose error body happened to hash-match the approved body counted as a pass — and a
        // 200 with a 401's body did too, which is why negative tests only half-worked.
        var statusDiffers = baseline.StatusCode != 0 && result.StatusCode != baseline.StatusCode;

        // And the contract, when the endpoint came from a document that made one. Checked even
        // when the hash matches: a rule can silence a field that changes, and none of them says
        // «this field may stop being what the documentation promised».
        var broken = ContractCheck.Check(contractJson, result.Body);

        var bodyMatches = baseline.NormalizedHash == sample.NormalizedHash;
        DiffResult? diff = null;

        if (!bodyMatches)
        {
            diff = SemanticDiff.CompareText(baseline.Body, result.Body, rules);
            bodyMatches = diff.Matches;
        }

        if (bodyMatches && !statusDiffers && broken.Count == 0)
        {
            // A correct answer that took too long is its own category — not a pass, because
            // «passed» is derived from what none of the counters claimed.
            if (sample.TooSlow) session.Slow++;
            return sample;
        }

        sample.Differs = true;

        var counts = diff is null || diff.Matches
            ? new Dictionary<string, int>()
            : diff.Counts.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);

        if (statusDiffers) counts["Status"] = 1;
        if (broken.Count > 0) counts["Contract"] = broken.Count;

        // The first violation in words, on the row: a reader looking at two thousand samples needs
        // to know which of them broke the promise and how, without opening each one.
        if (broken.Count > 0) sample.FailureMessage = $"{broken[0].Path}: {broken[0].Message}";

        sample.DiffSummaryJson = JsonSerializer.Serialize(counts, Json);

        session.Differing++;
        return sample;
    }

    /// <summary>
    /// Records what somebody decided, and writes the approved ones into the baseline.
    ///
    /// Approving is the only one of the three that changes what future runs compare against, which
    /// is why it is the only one that writes outside the session.
    /// </summary>
    public async Task<int> ReviewAsync(
        Guid sessionId, ReviewSamplesCommand command, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<SampleStatus>(command.Status, out var status)
            || status is not (SampleStatus.Approved or SampleStatus.Rejected or SampleStatus.Reviewed))
        {
            throw new ArgumentException($"«{command.Status}» is not a decision a person makes.");
        }

        var session = await db.CaptureSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new InvalidOperationException("No such capture session in this workspace.");

        var samples = await db.CaptureSamples
            .Where(s => s.CaptureSessionId == sessionId && command.SampleIds.Contains(s.Id))
            .ToListAsync(cancellationToken);

        var existing = await db.BaselineSamples
            .Where(s => s.BaselineId == session.BaselineId)
            .ToDictionaryAsync(s => s.Key, cancellationToken);

        var now = clock.UtcNow;

        foreach (var sample in samples)
        {
            // A failed request cannot be approved: there is no response to bless, and letting one
            // through would write a null body into the baseline for that key.
            if (sample.Status == SampleStatus.Failed && status == SampleStatus.Approved) continue;

            sample.Status = status;
            sample.ReviewedByUserId = me.UserId;
            sample.ReviewedAt = now;
            sample.ReviewNote = command.Note;

            if (status != SampleStatus.Approved || sample.Body is null) continue;

            if (existing.TryGetValue(sample.Key, out var target))
            {
                target.Body = sample.Body;
                target.ContentType = sample.ContentType;
                target.StatusCode = sample.StatusCode;
                target.NormalizedHash = sample.NormalizedHash;
                target.DataSetVersionId = session.DataSetVersionId;
                target.ApprovedFromSampleId = sample.Id;
                target.ApprovedByUserId = me.UserId ?? Guid.Empty;
                target.ApprovedAt = now;
            }
            else
            {
                var created = new BaselineSample
                {
                    WorkspaceId = session.WorkspaceId,
                    BaselineId = session.BaselineId,
                    Key = sample.Key,
                    Body = sample.Body,
                    ContentType = sample.ContentType,
                    StatusCode = sample.StatusCode,
                    NormalizedHash = sample.NormalizedHash,
                    DataSetVersionId = session.DataSetVersionId,
                    ApprovedFromSampleId = sample.Id,
                    ApprovedByUserId = me.UserId ?? Guid.Empty,
                    ApprovedAt = now,
                };

                db.BaselineSamples.Add(created);
                existing[sample.Key] = created;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return samples.Count;
    }

    /// <summary>The full diff for one sample, built when somebody actually opens it.</summary>
    public async Task<DiffResult?> DiffAsync(Guid sampleId, CancellationToken cancellationToken = default)
    {
        var sample = await db.CaptureSamples
            .FirstOrDefaultAsync(s => s.Id == sampleId, cancellationToken);

        if (sample?.Body is null) return null;

        var session = await db.CaptureSessions
            .FirstAsync(s => s.Id == sample.CaptureSessionId, cancellationToken);

        var baseline = await db.BaselineSamples
            .FirstOrDefaultAsync(s => s.BaselineId == session.BaselineId && s.Key == sample.Key,
                cancellationToken);

        var rules = new ComparisonRuleSet(await LoadRulesAsync(session.BaselineId, cancellationToken));

        // No approved answer for this key: compared against itself, so the viewer shows the
        // response rather than an empty screen, and every row reads as unchanged.
        return SemanticDiff.CompareText(baseline?.Body ?? sample.Body, sample.Body, rules);
    }

    private async Task<List<ComparisonRule>> LoadRulesAsync(Guid baselineId, CancellationToken cancellationToken)
    {
        var rows = await db.BaselineRules
            .Where(r => r.BaselineId == baselineId)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new ComparisonRule
        {
            Path = row.Path,
            Kind = Enum.TryParse<MatcherKind>(row.Matcher, ignoreCase: true, out var kind) ? kind : MatcherKind.Exact,
            Text = row.Text,
            Number = row.Number,
            Number2 = row.Number2,
            Note = row.Note,
            Enabled = row.Enabled,
        })];
    }

    private static JsonNode ParseRow(string valuesJson)
    {
        try
        {
            return JsonNode.Parse(valuesJson) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static HttpRequestDefinition? ReadRequest(Baseline baseline)
    {
        if (string.IsNullOrWhiteSpace(baseline.RequestJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<HttpRequestDefinition>(
                baseline.RequestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SampleResult(
        string? Url, string? Body, int StatusCode, string? ContentType, double DurationMs, string? Failure);
}
