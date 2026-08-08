using ProofFlow.Domain.Common;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Scenarios;

namespace ProofFlow.Domain.Runs;

/// <summary>
/// One execution of one scenario.
///
/// The run keeps its own copy of the graph it ran. That is the decision this whole area turns on:
/// a run that points at the live scenario cannot answer "what did this actually do" once somebody
/// edits it, and the first thing anybody does after a failing run is edit the scenario. A report
/// from March has to still be readable in June.
/// </summary>
public class TestRun : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public Guid ScenarioId { get; set; }

    public TestScenario? Scenario { get; set; }

    /// <summary>The version that was run. Kept as a reference as well as a snapshot.</summary>
    public Guid ScenarioVersionId { get; set; }

    public Guid? EnvironmentId { get; set; }

    public ProjectEnvironment? Environment { get; set; }

    /// <summary>The data set version, when the run was over one. Null for a single pass.</summary>
    public Guid? DataSetVersionId { get; set; }

    public DataSetVersion? DataSetVersion { get; set; }

    /// <summary>
    /// The whole graph as it stood, as JSON.
    ///
    /// Not a foreign key to the version's rows: a version can be superseded and a scenario can be
    /// deleted, and neither should erase what a run did. This is the record.
    /// </summary>
    public string? DefinitionJson { get; set; }

    /// <summary>
    /// The batch this was started as part of, when it was one of several.
    ///
    /// Null for an ordinary single run. A run does not behave differently for being in a batch —
    /// it is the same run, recorded the same way — the batch only says it was started alongside
    /// others, which is what makes a matrix readable.
    /// </summary>
    public Guid? BatchId { get; set; }

    public RunBatch? Batch { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Queued;

    public RunTrigger Trigger { get; set; } = RunTrigger.Person;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public double DurationMs { get; set; }

    public int StepsRun { get; set; }

    public int StepsFailed { get; set; }

    public int AssertionsPassed { get; set; }

    public int AssertionsFailed { get; set; }

    /// <summary>
    /// Why it ended the way it did, in words a reader can act on.
    ///
    /// Set for every terminal state including success, because "finished, nothing to report" is
    /// itself worth saying rather than leaving as an empty field.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>
    /// True once retention has thrown away this run's bodies, log and artefacts.
    ///
    /// Recorded rather than inferred from the age, and shown rather than hidden: a run whose console
    /// is empty because it was cleared and one that genuinely produced nothing look identical, and
    /// somebody investigating last quarter's failure needs to know which of the two they are looking
    /// at. It also stops the sweep from walking the same rows every hour for ever.
    /// </summary>
    public bool PayloadsCleared { get; set; }

    /// <summary>
    /// The runner this run is waiting for, or null when it runs here.
    ///
    /// Copied from the environment when the run is queued rather than read through it later: an
    /// environment can be pointed at a different agent tomorrow, and a run that is halfway through
    /// must not change hands because somebody edited a setting.
    /// </summary>
    public Guid? RunnerId { get; set; }

    /// <summary>When an agent took it. Null while it is still waiting for one.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>
    /// The step this run began at, or null for the whole scenario from its Start.
    ///
    /// Recorded rather than inferred, and it has to be: a run that began in the middle did not skip
    /// its earlier steps by failing them, and a reader looking at three steps in a scenario of nine
    /// needs the page to say so. It is also what makes the record honest afterwards — "passed" on a
    /// run that only did the last third is a different sentence from "passed".
    /// </summary>
    public string? StartNodeId { get; set; }

    public Guid? StartedByUserId { get; set; }

    /// <summary>Who asked it to stop, when somebody did.</summary>
    public Guid? CancelledByUserId { get; set; }

    public ICollection<NodeRun> Nodes { get; set; } = [];

    public ICollection<RunEvent> Events { get; set; } = [];
}

/// <summary>
/// Where a run is.
///
/// Numbered explicitly and never renumbered: the value is persisted, and shifting it would silently
/// turn every stored failure into a pass.
/// </summary>
public enum RunStatus
{
    /// <summary>Accepted, not started. What a scheduled run looks like before its turn.</summary>
    Queued = 1,

    Running = 2,

    /// <summary>Finished, and everything that was checked held.</summary>
    Passed = 3,

    /// <summary>Finished, and something that was checked did not hold. The useful failure.</summary>
    Failed = 4,

    /// <summary>Stopped by a person. Whatever ran before that is kept.</summary>
    Cancelled = 5,

    /// <summary>
    /// Stopped by something that was not the API's fault — a graph that could not be read, a
    /// runner that died. Kept apart from Failed, because "your API is broken" and "our runner is
    /// broken" are different news.
    /// </summary>
    Errored = 6,
}

public enum RunTrigger
{
    Person = 1,
    Schedule = 2,

    /// <summary>Started over the API or from a build.</summary>
    Api = 3,

    /// <summary>Started again from a previous run, keeping its inputs.</summary>
    Rerun = 4,
}

/// <summary>
/// One step's turn.
///
/// A node can run more than once — inside a loop, or after a retry — so this is per attempt rather
/// than per node, and <see cref="Iteration"/> and <see cref="Attempt"/> say which turn it was.
/// </summary>
public class NodeRun : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid TestRunId { get; set; }

    public TestRun? Run { get; set; }

    /// <summary>The node in the graph, by the id the snapshot uses.</summary>
    public required string NodeId { get; set; }

    public required string NodeKey { get; set; }

    /// <summary>The name it had, copied so a report reads without the graph beside it.</summary>
    public required string NodeName { get; set; }

    /// <summary>Which pass through the surrounding loop. Zero when there is no loop.</summary>
    public int Iteration { get; set; }

    /// <summary>1 on the first go. Higher after a retry.</summary>
    public int Attempt { get; set; } = 1;

    public NodeRunStatus Status { get; set; } = NodeRunStatus.Running;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public double DurationMs { get; set; }

    /// <summary>Which output the run left by. What makes a branch legible after the fact.</summary>
    public string? TakenPort { get; set; }

    /// <summary>What the step produced, redacted, as JSON. Null when it produced nothing.</summary>
    public string? OutputJson { get; set; }

    /// <summary>Why it failed, in words. Null when it did not.</summary>
    public string? FailureMessage { get; set; }

    public int SortOrder { get; set; }

    public ICollection<AssertionResult> Assertions { get; set; } = [];
}

/// <summary>
/// The eight states a node can be in, matching the ring the canvas draws.
///
/// The set is the same in both places on purpose: watching a run is watching the same picture the
/// test was built on, and a state the canvas cannot draw is a state nobody sees.
/// </summary>
public enum NodeRunStatus
{
    Idle = 1,
    Running = 2,
    Passed = 3,
    Failed = 4,

    /// <summary>Not reached, or deliberately left out.</summary>
    Skipped = 5,

    /// <summary>Waiting on something — a poll, a delay, a branch that has not finished.</summary>
    Waiting = 6,

    /// <summary>Failed and about to be tried again.</summary>
    Retrying = 7,

    Cancelled = 8,
}

/// <summary>
/// One check, and whether it held.
///
/// Separate from the node run because a step can check several things — and because a report that
/// says "fifteen checks, one failed" needs the fifteen, not a summary line.
/// </summary>
public class AssertionResult : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid NodeRunId { get; set; }

    public NodeRun? NodeRun { get; set; }

    /// <summary>What was being checked, as a sentence.</summary>
    public required string Description { get; set; }

    public bool Passed { get; set; }

    /// <summary>Recorded and carried on past. Section 6's soft assertions.</summary>
    public bool Soft { get; set; }

    public string? Expected { get; set; }

    public string? Actual { get; set; }

    /// <summary>The path or header the check was about, so a reader can find it.</summary>
    public string? Target { get; set; }
}

/// <summary>
/// One line of the live log.
///
/// Kept as rows rather than a text blob so the console can filter by level and by node without
/// re-parsing, and so a run that produced forty thousand lines can be paged.
/// </summary>
public class RunEvent : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid TestRunId { get; set; }

    public TestRun? Run { get; set; }

    /// <summary>Monotonic within the run. What the console orders by and resumes from.</summary>
    public long Sequence { get; set; }

    public RunEventLevel Level { get; set; } = RunEventLevel.Info;

    public required string Message { get; set; }

    /// <summary>Which step it came from, when it came from one.</summary>
    public string? NodeId { get; set; }

    public string? NodeName { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Anything structured worth keeping with the line, redacted, as JSON.</summary>
    public string? DataJson { get; set; }
}

public enum RunEventLevel
{
    /// <summary>The detail somebody turns on when a run is not doing what they expected.</summary>
    Debug = 1,

    Info = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>
/// Something too big to keep in a column: a response body, a diff, a payload.
///
/// Split out so the retention sweeper can remove the bulk without losing the run — the counts, the
/// timings and the assertion results are what a report is made of, and they are small.
/// </summary>
public class RunArtifact : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid TestRunId { get; set; }

    public Guid? NodeRunId { get; set; }

    public required string Name { get; set; }

    public required string Kind { get; set; }

    public string? ContentType { get; set; }

    public int SizeBytes { get; set; }

    /// <summary>The content itself, redacted. Moves to object storage behind a port later.</summary>
    public string? Content { get; set; }
}
