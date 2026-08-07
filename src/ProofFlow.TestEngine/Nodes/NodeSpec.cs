namespace ProofFlow.TestEngine.Nodes;

/// <summary>
/// What one kind of node is: what it accepts, what it produces, and what a person has to fill in.
///
/// A specification rather than a class per node, and that is the decision this whole area turns on.
/// Seventy behaviours as seventy classes means seventy places the canvas has to know about, seventy
/// property forms to hand-write, and a palette that has to be kept in step by hand. As data, the
/// canvas renders any node it is given, the inspector builds its form from
/// <see cref="Properties"/>, and adding a node is one record in one file.
///
/// The brief forbids hard-coding all of them in a single file, and for a better reason than tidiness:
/// a two-thousand-line switch is where nodes go to be forgotten. They live in five files, one per
/// group, each readable in a sitting.
/// </summary>
public sealed record NodeSpec
{
    /// <summary>
    /// The stable identifier, <c>group.thing</c>.
    ///
    /// Stored in every saved graph, so it is a promise: renaming one breaks every scenario that
    /// used it. Adding an alias is the way to change a name.
    /// </summary>
    public required string Key { get; init; }

    public required NodeGroup Group { get; init; }

    /// <summary>The lucide icon name. Registered in <c>lib/icons.ts</c> like every other one.</summary>
    public required string Icon { get; init; }

    /// <summary>
    /// Where control arrives. Empty for a start node, which is how the runner finds one.
    /// </summary>
    public IReadOnlyList<PortSpec> Inputs { get; init; } = [Port.In];

    /// <summary>
    /// Where control leaves, and what data the node publishes.
    ///
    /// Order matters: it is the order the ports are drawn down the node's edge, and a node whose
    /// failure port sits above its success port reads as though failure were the normal path.
    /// </summary>
    public IReadOnlyList<PortSpec> Outputs { get; init; } = [Port.Out];

    /// <summary>What the inspector asks for, in the order it asks.</summary>
    public IReadOnlyList<PropertySpec> Properties { get; init; } = [];

    /// <summary>
    /// A run cannot begin without one of these, and cannot have two.
    /// </summary>
    public bool IsStart { get; init; }

    /// <summary>
    /// True when the node ends the run rather than passing control on — so a graph that stops here
    /// is finished rather than broken.
    /// </summary>
    public bool IsTerminal { get; init; }

    /// <summary>
    /// True for a node that holds other nodes: a loop body, a try block, a group.
    ///
    /// The canvas draws these as containers and the validator lets their contents be unreachable
    /// from the start node, because they are reached through the parent rather than by an edge.
    /// </summary>
    public bool IsContainer { get; init; }

    /// <summary>
    /// Makes a real request to the outside world.
    ///
    /// Surfaced because it is the property a reader cares about before pressing run against
    /// production, and because a dry run has to know which nodes to skip.
    /// </summary>
    public bool Reaches { get; init; }

    public string TitleKey => $"node.{Key}.title";

    public string SummaryKey => $"node.{Key}.summary";
}

/// <summary>
/// The five groups from section 7 of the brief.
///
/// Numbered explicitly: the value is stored in nothing, but the order is the order of the palette,
/// and that order is a claim about what somebody reaches for first.
/// </summary>
public enum NodeGroup
{
    /// <summary>Sending things and the shape of a run.</summary>
    Core = 1,

    /// <summary>Getting values out of responses and shaping them.</summary>
    Data = 2,

    /// <summary>Saying what should be true.</summary>
    Testing = 3,

    /// <summary>Branching, looping, retrying, cleaning up.</summary>
    Flow = 4,

    /// <summary>Getting a token and keeping it.</summary>
    Auth = 5,
}

/// <summary>
/// One socket on a node.
///
/// <see cref="Kind"/> separates the two things an edge can mean. A control edge says "then do
/// this"; a data edge says "this value goes there". Drawing both the same way is how a canvas
/// becomes unreadable at thirty nodes.
/// </summary>
public sealed record PortSpec
{
    public required string Name { get; init; }

    /// <summary>Shared across nodes: forty nodes have an "in" port and it is called the same thing.</summary>
    public required string LabelKey { get; init; }

    public PortKind Kind { get; init; } = PortKind.Control;

    public DataType Type { get; init; } = DataType.None;

    /// <summary>
    /// The path taken when the node did not do what it was asked.
    ///
    /// Marked rather than inferred from the name, because the canvas draws it differently — a
    /// diamond in <c>--fail</c> — and "which of these is the error path" should not be a guess.
    /// </summary>
    public bool IsFailure { get; init; }

    /// <summary>An input that must be connected for the graph to be valid.</summary>
    public bool Required { get; init; }
}

public enum PortKind
{
    /// <summary>Execution order. What most edges are.</summary>
    Control = 1,

    /// <summary>A value moving from one node to another.</summary>
    Data = 2,
}

/// <summary>
/// What a data port carries.
///
/// Deliberately small. Every type here is one a person building a test can name out loud, and the
/// point of the check is to catch "you have connected a list to something that wants a number"
/// before the run, not to build a type system.
/// </summary>
public enum DataType
{
    /// <summary>Control ports carry no value.</summary>
    None = 0,

    /// <summary>Accepts and satisfies anything. The escape hatch, used sparingly.</summary>
    Any = 1,

    Text = 2,
    Number = 3,
    Boolean = 4,

    /// <summary>A JSON document or fragment.</summary>
    Json = 5,

    /// <summary>An ordered collection.</summary>
    List = 6,

    /// <summary>A whole HTTP response: status, headers, body, timing.</summary>
    Response = 7,

    /// <summary>Rows and columns — a data set, or something shaped like one.</summary>
    Table = 8,

    /// <summary>Bytes that are not text.</summary>
    Binary = 9,

    /// <summary>A length of time.</summary>
    Duration = 10,

    /// <summary>A credential. Never rendered, never logged, and only accepted where one is wanted.</summary>
    Secret = 11,
}

/// <summary>
/// One field in the inspector.
///
/// <see cref="Kind"/> chooses the control, which is what makes a form for a node nobody wrote a
/// form for. <see cref="VisibleWhen"/> is what keeps that form short: a node with fourteen
/// properties of which four apply is a node people fill in wrongly.
/// </summary>
public sealed record PropertySpec
{
    public required string Name { get; init; }

    /// <summary>
    /// Shared where the idea is shared.
    ///
    /// Thirty nodes ask for a path and all of them should call it the same thing — both because it
    /// is one string to translate and because two words for one idea is how an interface starts
    /// feeling arbitrary.
    /// </summary>
    public required string LabelKey { get; init; }

    public PropertyKind Kind { get; init; } = PropertyKind.Text;

    public bool Required { get; init; }

    public string? Default { get; init; }

    /// <summary>A hint under the field. Null when the label says everything.</summary>
    public string? HelpKey { get; init; }

    /// <summary>Shown inside an empty field. Not a label, and never the only explanation.</summary>
    public string? Placeholder { get; init; }

    /// <summary>For <see cref="PropertyKind.Choice"/>: the values, labelled by <c>option.{value}</c>.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>
    /// Shown only when another property has one of these values.
    ///
    /// <c>("mode", ["regex"])</c> reads as "only when mode is regex".
    /// </summary>
    public PropertyCondition? VisibleWhen { get; init; }
}

public sealed record PropertyCondition(string Property, IReadOnlyList<string> Values);

public enum PropertyKind
{
    Text = 1,

    /// <summary>Several lines. A body, a script, a list of headers.</summary>
    LongText = 2,

    Number = 3,
    Boolean = 4,

    /// <summary>One of a fixed set.</summary>
    Choice = 5,

    /// <summary>A JSON path into a response. Offered by clicking a field rather than typed.</summary>
    JsonPath = 6,

    /// <summary>A URL, with the variable highlighting the request builder uses.</summary>
    Url = 7,

    /// <summary>A length of time, entered in the unit a person thinks in.</summary>
    Duration = 8,

    /// <summary>Names a secret. The value never comes to the browser.</summary>
    SecretRef = 9,

    /// <summary>One of the twenty comparison matchers.</summary>
    Matcher = 10,

    /// <summary>Names something else in the project: an environment, a data set, a baseline.</summary>
    Reference = 11,

    /// <summary>Rows of name and value.</summary>
    KeyValues = 12,

    /// <summary>A condition, written in the small expression language.</summary>
    Expression = 13,
}

/// <summary>
/// The ports almost every node has, so seventy specs do not each spell them out.
///
/// A node that needs something different says so; one that does not gets these, and the ones that
/// do not are then visible by their absence.
/// </summary>
public static class Port
{
    public static readonly PortSpec In = new()
    {
        Name = "in", LabelKey = "port.in", Kind = PortKind.Control,
    };

    public static readonly PortSpec Out = new()
    {
        Name = "out", LabelKey = "port.out", Kind = PortKind.Control,
    };

    /// <summary>The path taken when the node failed. Drawn as a diamond, in the failure colour.</summary>
    public static readonly PortSpec Failure = new()
    {
        Name = "failure", LabelKey = "port.failure", Kind = PortKind.Control, IsFailure = true,
    };

    public static PortSpec Data(string name, DataType type, string? labelKey = null) => new()
    {
        Name = name, LabelKey = labelKey ?? $"port.{name}", Kind = PortKind.Data, Type = type,
    };

    public static PortSpec Control(string name, string? labelKey = null) => new()
    {
        Name = name, LabelKey = labelKey ?? $"port.{name}", Kind = PortKind.Control,
    };
}
