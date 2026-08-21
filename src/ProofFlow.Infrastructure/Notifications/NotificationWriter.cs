using System.Text.Json;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Notifications;
using ProofFlow.Domain.Runs;
using ProofFlow.Domain.Scheduling;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Notifications;

/// <summary>
/// Writes the row that makes a failure somebody's news.
///
/// Only writes — the sentence is composed at render time from <c>Kind</c> and <c>ArgsJson</c>, and
/// delivery belongs to the worker. Rows are added to the caller's own context and ride its
/// <c>SaveChanges</c>, so a run cannot be marked failed without its notification or the other way
/// round.
///
/// Injected as an optional tail parameter where it is used: the services that call it are also
/// constructed by hand in a great many tests, and a failure-notification is not what those tests
/// are about.
/// </summary>
public sealed class NotificationWriter(ProofFlowDbContext db)
{
    public void RunFailed(TestRun run, string? scenarioName, string? environmentName)
    {
        db.Notifications.Add(new Notification
        {
            WorkspaceId = run.WorkspaceId,
            ProjectId = run.ProjectId,
            Kind = run.Status == RunStatus.Errored ? "run.errored" : "run.failed",
            ArgsJson = Args(scenarioName ?? "?", environmentName ?? "—"),
            LinkPath = $"/projects/{run.ProjectId}/runs/{run.Id}",
            TargetType = nameof(TestRun),
            TargetId = run.Id,
            TargetLabel = scenarioName,
        });
    }

    public void SweepFailed(CaptureSession session, Guid projectId, string endpointName)
    {
        db.Notifications.Add(new Notification
        {
            WorkspaceId = session.WorkspaceId,
            ProjectId = projectId,
            Kind = "sweep.failed",
            ArgsJson = Args(endpointName, session.StoppedReason ?? "?"),
            LinkPath = $"/projects/{projectId}/endpoints/{session.BaselineId}",
            TargetType = nameof(CaptureSession),
            TargetId = session.Id,
            TargetLabel = endpointName,
        });
    }

    public void ScheduleBroken(RunSchedule schedule, string problem)
    {
        db.Notifications.Add(new Notification
        {
            WorkspaceId = schedule.WorkspaceId,
            ProjectId = schedule.ProjectId,
            Kind = "schedule.broken",
            ArgsJson = Args(schedule.Name, problem),
            LinkPath = $"/projects/{schedule.ProjectId}/schedules",
            TargetType = nameof(RunSchedule),
            TargetId = schedule.Id,
            TargetLabel = schedule.Name,
        });
    }

    public void ApprovalWaiting(Guid workspaceId, Guid projectId, Guid baselineId, string endpointName, int number)
    {
        db.Notifications.Add(new Notification
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Kind = "approval.waiting",
            ArgsJson = Args(endpointName, $"v{number}"),
            LinkPath = $"/projects/{projectId}/endpoints/{baselineId}",
            TargetType = "Baseline",
            TargetId = baselineId,
            TargetLabel = endpointName,
        });
    }

    private static string Args(params string[] values) => JsonSerializer.Serialize(values);
}
