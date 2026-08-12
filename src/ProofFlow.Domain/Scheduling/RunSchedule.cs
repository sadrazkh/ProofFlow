using ProofFlow.Domain.Common;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Scenarios;

namespace ProofFlow.Domain.Scheduling;

/// <summary>
/// A standing instruction: run these, over there, on this rhythm.
///
/// The point of a regression suite is that nobody has to remember to run it. A schedule is what
/// turns a set of tests into a thing that tells you when the API changed, rather than a thing you
/// find out was broken when you next happened to look.
///
/// It starts a batch, not a run. Which environments to cover is part of the instruction — "every
/// morning against staging and production" is one sentence a person says, and splitting it into two
/// schedules would let them drift apart.
/// </summary>
public class RunSchedule : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// A five-field cron expression, kept exactly as it was typed.
    ///
    /// Stored raw rather than as a parsed structure. A person who wrote <c>0 6 * * 1-5</c> should
    /// see that back, because the next thing they do is edit it — and because a field this product
    /// cannot round-trip is a field it will eventually corrupt.
    /// </summary>
    public required string Cron { get; set; }

    /// <summary>
    /// Which clock the expression is read against, as an IANA identifier.
    ///
    /// Not optional and not UTC by default. "Every day at six" means six where the team is, and a
    /// schedule that drifts by an hour twice a year is one nobody trusts again. The zone is stored
    /// rather than derived from the author, because the author leaves and the schedule stays.
    /// </summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>
    /// Off means it does not fire. It does not mean deleted, and it keeps its history.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When it is next due, worked out from the cron and stored.
    ///
    /// Stored so that "what is due now" is an index scan rather than parsing every expression in
    /// the database on every tick. Recomputed whenever the expression, the zone or the last run
    /// changes — and a null means the expression could not be read, which is a schedule that says
    /// so rather than one that quietly never fires.
    /// </summary>
    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>The batch it started last time, so the list can link to what happened.</summary>
    public Guid? LastBatchId { get; set; }

    /// <summary>Why the expression could not be read, when it could not. Shown, not swallowed.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// The answers this schedule runs with, as names and values.
    ///
    /// A scenario that asks for a page or an order id still asks when nobody is at the keyboard, and
    /// «the defaults, every morning» is a decision somebody should be able to change without editing
    /// the scenario. Values rather than definitions: what to ask is the scenario's business, and a
    /// schedule that kept its own copy of the questions would answer ones that no longer exist.
    /// </summary>
    public string? InputsJson { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public ICollection<ScheduleScenario> Scenarios { get; set; } = [];

    public ICollection<ScheduleEnvironment> Environments { get; set; } = [];
}

/// <summary>
/// One scenario a schedule covers.
///
/// A row rather than a list in a column, so that deleting a scenario takes its schedule entries
/// with it. The alternative is a schedule that fires every morning at six against an id that no
/// longer exists and fails in a way nobody can act on.
/// </summary>
public class ScheduleScenario : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid RunScheduleId { get; set; }

    public RunSchedule? Schedule { get; set; }

    public Guid ScenarioId { get; set; }

    public TestScenario? Scenario { get; set; }
}

public class ScheduleEnvironment : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid RunScheduleId { get; set; }

    public RunSchedule? Schedule { get; set; }

    public Guid EnvironmentId { get; set; }

    public ProjectEnvironment? Environment { get; set; }
}
