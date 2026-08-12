using ProofFlow.Domain.Common;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;

namespace ProofFlow.Domain.Scenarios;

/// <summary>
/// A folder for scenarios, and the place a shared setting hangs.
///
/// Kept because a project with forty scenarios is a project where "which ones do I run before a
/// release" is a real question, and the answer is a suite rather than a naming convention.
/// </summary>
public class TestSuite : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>
/// One test, drawn as a graph.
///
/// The scenario is the named, durable thing; the graph lives in versions. Section 20 of the brief
/// forbids storage a change cannot be seen in, and that is what this split buys: a version is
/// immutable once anything has run against it, so "what did this test do in March" has an answer,
/// and two versions can be put side by side.
/// </summary>
public class TestScenario : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public Guid? TestSuiteId { get; set; }

    public TestSuite? Suite { get; set; }

    /// <summary>The environment a run uses unless one is named. Null means "ask each time".</summary>
    public Guid? EnvironmentId { get; set; }

    public ProjectEnvironment? Environment { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The version an ordinary run uses. Null while the first draft is still being drawn.</summary>
    public Guid? PublishedVersionId { get; set; }

    /// <summary>
    /// The version being edited.
    ///
    /// Separate from the published one so that opening the canvas and moving a node does not change
    /// what a schedule runs tonight.
    /// </summary>
    public Guid? DraftVersionId { get; set; }

    /// <summary>
    /// What this test has to be told before it can run, as JSON.
    ///
    /// The difference between an input and a variable is who supplies it and when. A variable
    /// belongs to an environment and is the same for every run against it; an input is answered per
    /// run — the order to look up, the customer to sign in as — by a person filling a form or by a
    /// build agent sending a body. Inside the graph both read the same way, `{{inputs.orderId}}`
    /// beside `{{vars.pageSize}}`.
    ///
    /// JSON rather than a table because it is a small ordered list that is always read whole, always
    /// with the scenario, and never queried across scenarios.
    /// </summary>
    public string? InputsJson { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>
    /// Set when somebody decides this test is too unreliable to fail a build on.
    ///
    /// Quarantine is not deletion and not disabling. The scenario still runs, still records what it
    /// found, and still shows in every list — it simply stops being allowed to fail the suite. A
    /// flaky test that gets deleted takes its coverage with it and nobody ever notices; a flaky test
    /// that gets quarantined stays visible until somebody fixes it.
    /// </summary>
    public DateTimeOffset? QuarantinedAt { get; set; }

    /// <summary>Why, in the words of whoever decided. A quarantine with no reason is a mystery.</summary>
    public string? QuarantineReason { get; set; }

    public Guid? QuarantinedByUserId { get; set; }

    public ICollection<ScenarioVersion> Versions { get; set; } = [];
}

/// <summary>
/// One state of the graph.
///
/// The nodes and connections are rows rather than one JSON blob, and that is the decision the
/// brief's "no un-versionable graph storage" turns on: rows can be diffed, indexed and reported on
/// — "this version added three assertions and removed a retry" is a query, not a text comparison
/// of two documents.
/// </summary>
public class ScenarioVersion : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ScenarioId { get; set; }

    public TestScenario? Scenario { get; set; }

    /// <summary>1, 2, 3… within the scenario. Shown to people; never reused.</summary>
    public int Number { get; set; }

    public ScenarioVersionStatus Status { get; set; } = ScenarioVersionStatus.Draft;

    public string? Description { get; set; }

    /// <summary>
    /// Canvas state that is not the graph: the viewport, the grid, whether the minimap is open.
    ///
    /// Kept apart from the nodes so that panning the canvas is not a change to the test.
    /// </summary>
    public string? CanvasJson { get; set; }

    /// <summary>
    /// What the validator said when this version was last saved.
    ///
    /// Stored rather than recomputed on every list: the scenario list shows whether each one is
    /// runnable, and re-validating forty graphs to draw a table is forty graph walks.
    /// </summary>
    public string? ValidationJson { get; set; }

    public bool IsValid { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public ICollection<WorkflowNode> Nodes { get; set; } = [];

    public ICollection<WorkflowConnection> Connections { get; set; } = [];
}

public enum ScenarioVersionStatus
{
    /// <summary>Being drawn. Runnable by hand, never by a schedule.</summary>
    Draft = 1,

    /// <summary>In force. What a schedule and a CI trigger run.</summary>
    Published = 2,

    /// <summary>Was published; a later version replaced it. Kept, because history is the point.</summary>
    Superseded = 3,

    /// <summary>Put away deliberately.</summary>
    Archived = 4,
}

/// <summary>
/// One box on the canvas.
///
/// <see cref="Key"/> names its type in the catalogue and <see cref="PropertiesJson"/> holds what
/// somebody filled in. Deliberately not one column per property: seventy node types have some
/// hundreds of properties between them, and a table with hundreds of mostly-null columns is a table
/// no migration can keep up with.
/// </summary>
public class WorkflowNode : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ScenarioVersionId { get; set; }

    public ScenarioVersion? Version { get; set; }

    /// <summary>The node type, from <c>NodeCatalogue</c>. A key nothing defines is a broken graph.</summary>
    public required string Key { get; set; }

    /// <summary>
    /// The name a person gave it, and what <c>{{steps.<em>name</em>.response}}</c> refers to.
    ///
    /// Which is why it is unique within a version: two steps called "login" would make that
    /// reference ambiguous, and the ambiguity would only show up at run time.
    /// </summary>
    public required string Name { get; set; }

    public string? Note { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>The container this sits inside — a loop body, a try block. Null for the top level.</summary>
    public Guid? ParentNodeId { get; set; }

    /// <summary>What the inspector filled in, as a flat JSON object of property name to value.</summary>
    public string? PropertiesJson { get; set; }

    /// <summary>
    /// Left in the graph and skipped at run time.
    ///
    /// Better than deleting: a step somebody is temporarily working around should come back with
    /// its properties and its edges intact.
    /// </summary>
    public bool Disabled { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// One edge.
///
/// Both ends name a port as well as a node, because a node has several: which output an edge leaves
/// from is the difference between "then" and "if this failed, then".
/// </summary>
public class WorkflowConnection : Entity, IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid ScenarioVersionId { get; set; }

    public ScenarioVersion? Version { get; set; }

    public Guid FromNodeId { get; set; }

    public required string FromPort { get; set; }

    public Guid ToNodeId { get; set; }

    public required string ToPort { get; set; }

    /// <summary>A word on the edge. For a switch's cases, mostly.</summary>
    public string? Label { get; set; }
}
