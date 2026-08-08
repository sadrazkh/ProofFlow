using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Baselines;

/// <summary>
/// Everything waiting on somebody's decision, in one list.
///
/// The point is that it is one list. A proposed baseline version lives on a baseline page and a
/// captured sample lives in a review queue, and a reviewer who has to visit both to find out
/// whether they have anything to do is a reviewer who checks neither. What is waiting is a property
/// of the person, not of the page they happen to be on.
///
/// It says who recorded each one, and whether the reader is that person — because the answer
/// changes what they can do with it, and finding that out only after pressing Approve is a bad way
/// to learn a rule.
/// </summary>
public sealed class ApprovalInbox(ProofFlowDbContext db, Separation separation, ICurrentUser me)
{
    /// <summary>How many of each kind the inbox shows before it starts counting instead.</summary>
    public const int PageSize = 50;

    public async Task<ApprovalInboxView> ReadAsync(
        Guid projectId, CancellationToken cancellation = default)
    {
        var versions = await db.BaselineVersions
            .Where(version => (version.Status == BaselineStatus.PendingApproval
                               || version.Status == BaselineStatus.Draft)
                              && db.Baselines.Any(baseline => baseline.Id == version.BaselineId
                                                              && baseline.ProjectId == projectId))
            .OrderBy(version => version.CreatedAt)
            .Take(PageSize + 1)
            .Select(version => new
            {
                version.Id,
                version.BaselineId,
                version.Number,
                version.CreatedAt,
                version.CreatedByUserId,
                version.StatusCode,
                Name = db.Baselines
                    .Where(baseline => baseline.Id == version.BaselineId)
                    .Select(baseline => baseline.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellation);

        // Sessions rather than samples. Two thousand rows waiting on one decision is one decision,
        // and an inbox that listed them individually would be a list nobody could read to the end.
        var sessions = await db.CaptureSessions
            .Where(session => session.ProjectId == projectId
                              && db.CaptureSamples.Any(sample =>
                                  sample.CaptureSessionId == session.Id
                                  && sample.Status == SampleStatus.Captured))
            .OrderBy(session => session.StartedAt)
            .Take(PageSize + 1)
            .Select(session => new
            {
                session.Id,
                session.BaselineId,
                session.StartedAt,
                session.StartedByUserId,
                Name = db.Baselines
                    .Where(baseline => baseline.Id == session.BaselineId)
                    .Select(baseline => baseline.Name)
                    .FirstOrDefault(),
                Waiting = db.CaptureSamples.Count(sample =>
                    sample.CaptureSessionId == session.Id && sample.Status == SampleStatus.Captured),
                Differing = db.CaptureSamples.Count(sample =>
                    sample.CaptureSessionId == session.Id
                    && sample.Status == SampleStatus.Captured
                    && sample.Differs),
            })
            .ToListAsync(cancellation);

        var names = await AuthorsAsync(
            versions.Select(version => version.CreatedByUserId)
                .Concat(sessions.Select(session => session.StartedByUserId))
                .Distinct()
                .ToList(),
            cancellation);

        var elseWhere = await separation.SomebodyElseAsync(cancellation);

        return new ApprovalInboxView
        {
            Versions =
            [
                .. versions.Take(PageSize).Select(version => new PendingVersion(
                    version.Id,
                    version.BaselineId,
                    version.Name ?? "—",
                    version.Number,
                    version.CreatedAt,
                    names.GetValueOrDefault(version.CreatedByUserId) ?? "—",
                    version.CreatedByUserId == me.UserId,
                    version.StatusCode)),
            ],
            Sessions =
            [
                .. sessions.Take(PageSize).Select(session => new PendingSession(
                    session.Id,
                    session.BaselineId,
                    session.Name ?? "—",
                    session.StartedAt,
                    names.GetValueOrDefault(session.StartedByUserId) ?? "—",
                    session.StartedByUserId == me.UserId,
                    session.Waiting,
                    session.Differing)),
            ],

            // Said out loud rather than silently cut, the same rule as everywhere else that pages.
            MoreVersions = Math.Max(0, versions.Count - PageSize),
            MoreSessions = Math.Max(0, sessions.Count - PageSize),

            // Whether the separation rule can bind at all here. A workspace of one person is not a
            // governance failure, and the page should not imply it is.
            SomebodyElseCanApprove = elseWhere,
        };
    }

    private async Task<Dictionary<Guid, string>> AuthorsAsync(
        List<Guid> ids, CancellationToken cancellation)
    {
        if (ids.Count == 0) return [];

        return await db.Users
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName ?? "—", cancellation);
    }
}

public sealed class ApprovalInboxView
{
    public required IReadOnlyList<PendingVersion> Versions { get; init; }

    public required IReadOnlyList<PendingSession> Sessions { get; init; }

    public int MoreVersions { get; init; }

    public int MoreSessions { get; init; }

    public bool SomebodyElseCanApprove { get; init; }

    public int Total => Versions.Count + Sessions.Count;
}

/// <summary>A proposed whole-response baseline, waiting.</summary>
public sealed record PendingVersion(
    Guid VersionId,
    Guid BaselineId,
    string BaselineName,
    int Number,
    DateTimeOffset RecordedAt,
    string RecordedBy,

    /// <summary>True when the reader recorded it, which is what the separation rule turns on.</summary>
    bool ByYou,
    int StatusCode);

/// <summary>A sweep whose samples nobody has decided about yet.</summary>
public sealed record PendingSession(
    Guid SessionId,
    Guid BaselineId,
    string BaselineName,
    DateTimeOffset StartedAt,
    string StartedBy,
    bool ByYou,
    int Waiting,

    /// <summary>How many of those differ from what was approved. The ones worth opening first.</summary>
    int Differing);
