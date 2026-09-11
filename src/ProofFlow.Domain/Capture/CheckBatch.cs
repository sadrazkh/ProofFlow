using ProofFlow.Domain.Common;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Runs;

namespace ProofFlow.Domain.Capture;

/// <summary>
/// One press of "check these": the endpoints that were checked together.
///
/// The same idea as <see cref="RunBatch"/> and deliberately not the same table. A run batch is
/// scenarios across environments and every cell of it is a <see cref="Domain.Runs.TestRun"/>; this
/// is endpoints, and every cell is a <see cref="CaptureSession"/>. Sharing one table would have
/// meant the matrix list showing batches with no runs in them, and the matrix grid trying to draw
/// a scenario column for something that has none.
///
/// Like a run batch, it is a grouping and nothing more. Everything that happened is on the
/// sessions, so there is only ever one answer to "what did this endpoint return".
/// </summary>
public class CheckBatch : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>What it was called — the schedule's name, usually. Null when nobody said.</summary>
    public string? Name { get; set; }

    public RunTrigger Trigger { get; set; } = RunTrigger.Person;

    public Guid? StartedByUserId { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// How many checks it was made of.
    ///
    /// Stored rather than counted, so a page showing forty batches does not read every session of
    /// every one of them to say whether each is still going.
    /// </summary>
    public int Total { get; set; }

    public ICollection<CaptureSession> Checks { get; set; } = [];
}
