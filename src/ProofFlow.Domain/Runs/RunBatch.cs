using ProofFlow.Domain.Common;
using ProofFlow.Domain.Projects;

namespace ProofFlow.Domain.Runs;

/// <summary>
/// One press of "run these, over there": a set of runs that were started together.
///
/// A batch is a grouping and nothing more. Every cell of the matrix is an ordinary
/// <see cref="TestRun"/>, with its own log, its own timeline and its own console — which is the
/// whole reason to model it this way. A separate "matrix run" type would need its own record of
/// what happened, and then there would be two answers to "what did step three return" that could
/// disagree.
///
/// It is what makes the question the product exists for askable: the same test, the same moment,
/// two environments, side by side.
/// </summary>
public class RunBatch : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>
    /// What the person called it, when they said.
    ///
    /// Optional, because most batches are read within the hour of being started and the scenarios
    /// and environments name them well enough. It earns its place on the ones somebody comes back
    /// to in a month.
    /// </summary>
    public string? Name { get; set; }

    public RunTrigger Trigger { get; set; } = RunTrigger.Person;

    public Guid? StartedByUserId { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// How many runs it was made of.
    ///
    /// Stored rather than counted, so a matrix of forty cells does not need forty rows read to
    /// know whether it is still going.
    /// </summary>
    public int Total { get; set; }

    public ICollection<TestRun> Runs { get; set; } = [];
}

/// <summary>
/// Where a batch is, taken from the runs in it.
///
/// Derived rather than stored: a batch has no state of its own, and a stored status would be a
/// second answer that drifts from the runs the moment one of them is cancelled.
/// </summary>
public enum BatchState
{
    /// <summary>Nothing has started yet.</summary>
    Queued = 1,

    Running = 2,

    /// <summary>Every run finished and every one of them passed.</summary>
    Passed = 3,

    /// <summary>Every run finished and at least one did not pass.</summary>
    Failed = 4,
}
