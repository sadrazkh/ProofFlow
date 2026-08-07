namespace ProofFlow.TestEngine.Nodes;

/// <summary>
/// Branching, looping, retrying, cleaning up.
///
/// The group that turns a list of steps into a scenario. Most of these are containers: the loop
/// body and the try block are drawn inside their node rather than reached by an edge, because a
/// canvas where a loop is three nodes and two edges back to the top is a canvas nobody can read at
/// thirty nodes.
/// </summary>
public static class FlowNodes
{
    public static IReadOnlyList<NodeSpec> All =>
    [
        new()
        {
            Key = "flow.if",
            Group = NodeGroup.Flow,
            Icon = "git-branch",
            Outputs = [Port.Control("true", "port.true"), Port.Control("false", "port.false")],
            Properties =
            [
                new()
                {
                    Name = "condition", LabelKey = "nodeprop.condition", Kind = PropertyKind.Expression,
                    Required = true, Placeholder = "{{steps.login.response.statusCode}} == 200",
                },
            ],
        },
        new()
        {
            Key = "flow.switch",
            Group = NodeGroup.Flow,
            Icon = "split",
            Outputs =
            [
                Port.Control("case1", "port.case1"),
                Port.Control("case2", "port.case2"),
                Port.Control("case3", "port.case3"),
                Port.Control("default", "port.default"),
            ],
            Properties =
            [
                new() { Name = "value", LabelKey = "nodeprop.value", Kind = PropertyKind.Text, Required = true },
                new()
                {
                    Name = "cases", LabelKey = "nodeprop.cases", Kind = PropertyKind.KeyValues,
                    Required = true, HelpKey = "nodeprop.cases.help",
                },
            ],
        },
        new()
        {
            Key = "flow.forEach",
            Group = NodeGroup.Flow,
            Icon = "repeat",
            IsContainer = true,
            Inputs = [Port.In, Port.Data("list", DataType.List) with { Required = true }],
            Outputs = [Port.Out, Port.Data("item", DataType.Any), Port.Data("index", DataType.Number)],
            Properties =
            [
                new()
                {
                    Name = "maxIterations", LabelKey = "nodeprop.maxIterations", Kind = PropertyKind.Number,
                    Default = "1000", HelpKey = "nodeprop.maxIterations.help",
                },
                new()
                {
                    Name = "stopOnFailure", LabelKey = "nodeprop.stopOnFailure", Kind = PropertyKind.Boolean,
                    Default = "true",
                },
            ],
        },
        new()
        {
            Key = "flow.forEachRow",
            Group = NodeGroup.Flow,
            Icon = "table-2",
            IsContainer = true,
            Outputs = [Port.Out, Port.Data("row", DataType.Json), Port.Data("key", DataType.Text)],
            Properties =
            [
                new()
                {
                    Name = "dataSet", LabelKey = "nodeprop.dataSet", Kind = PropertyKind.Reference,
                    Required = true,
                },
                new()
                {
                    Name = "limit", LabelKey = "nodeprop.limit", Kind = PropertyKind.Number,
                    HelpKey = "nodeprop.limit.help",
                },
                new()
                {
                    Name = "concurrency", LabelKey = "nodeprop.concurrency", Kind = PropertyKind.Number,
                    Default = "4", HelpKey = "nodeprop.concurrency.help",
                },
            ],
        },
        new()
        {
            Key = "flow.repeat",
            Group = NodeGroup.Flow,
            Icon = "rotate-ccw",
            IsContainer = true,
            Outputs = [Port.Out, Port.Data("index", DataType.Number)],
            Properties =
            [
                new() { Name = "times", LabelKey = "nodeprop.times", Kind = PropertyKind.Number, Required = true, Default = "3" },
            ],
        },
        new()
        {
            Key = "flow.while",
            Group = NodeGroup.Flow,
            Icon = "refresh-cw",
            IsContainer = true,
            Properties =
            [
                new()
                {
                    Name = "condition", LabelKey = "nodeprop.condition", Kind = PropertyKind.Expression,
                    Required = true,
                },
                new()
                {
                    // Not optional and not unbounded. A while loop with no ceiling in a test runner
                    // is a test that hangs a build agent until somebody notices.
                    Name = "maxIterations", LabelKey = "nodeprop.maxIterations", Kind = PropertyKind.Number,
                    Required = true, Default = "100", HelpKey = "nodeprop.maxIterations.help",
                },
            ],
        },
        new()
        {
            Key = "flow.break",
            Group = NodeGroup.Flow,
            Icon = "octagon-x",
            IsTerminal = true,
            Outputs = [],
        },
        new()
        {
            Key = "flow.continue",
            Group = NodeGroup.Flow,
            Icon = "skip-forward",
            IsTerminal = true,
            Outputs = [],
        },
        new()
        {
            Key = "flow.retry",
            Group = NodeGroup.Flow,
            Icon = "rotate-cw",
            IsContainer = true,
            Outputs = [Port.Out, Port.Failure, Port.Data("attempts", DataType.Number)],
            Properties =
            [
                new() { Name = "attempts", LabelKey = "nodeprop.attempts", Kind = PropertyKind.Number, Required = true, Default = "3" },
                new() { Name = "delay", LabelKey = "nodeprop.delay", Kind = PropertyKind.Duration, Default = "1s" },
                new()
                {
                    Name = "backoff", LabelKey = "nodeprop.backoff", Kind = PropertyKind.Choice,
                    Options = ["fixed", "exponential"], Default = "exponential",
                },
            ],
        },
        new()
        {
            Key = "flow.pollUntil",
            Group = NodeGroup.Flow,
            Icon = "loader",
            IsContainer = true,
            Outputs = [Port.Out, Port.Failure],
            Properties =
            [
                new()
                {
                    Name = "condition", LabelKey = "nodeprop.condition", Kind = PropertyKind.Expression,
                    Required = true, Placeholder = "{{steps.job.response.status}} == \"ready\"",
                },
                new() { Name = "interval", LabelKey = "nodeprop.interval", Kind = PropertyKind.Duration, Required = true, Default = "2s" },
                new() { Name = "timeout", LabelKey = "nodeprop.timeout", Kind = PropertyKind.Duration, Required = true, Default = "60s" },
            ],
        },
        new()
        {
            Key = "flow.tryCatch",
            Group = NodeGroup.Flow,
            Icon = "shield",
            IsContainer = true,
            Outputs = [Port.Out, Port.Control("caught", "port.caught"), Port.Data("error", DataType.Text)],
        },
        new()
        {
            Key = "flow.cleanup",
            Group = NodeGroup.Flow,
            Icon = "brush-cleaning",
            IsContainer = true,
            Properties =
            [
                new()
                {
                    // The whole reason this node exists: a scenario that creates a record has to
                    // delete it even when the assertion in the middle failed.
                    Name = "always", LabelKey = "nodeprop.always", Kind = PropertyKind.Boolean,
                    Default = "true", HelpKey = "node.flow.cleanup.help",
                },
            ],
        },
        new()
        {
            Key = "flow.rateLimit",
            Group = NodeGroup.Flow,
            Icon = "gauge",
            IsContainer = true,
            Properties =
            [
                new() { Name = "perSecond", LabelKey = "nodeprop.perSecond", Kind = PropertyKind.Number, Required = true, Default = "5" },
            ],
        },
        new()
        {
            Key = "flow.skipIf",
            Group = NodeGroup.Flow,
            Icon = "circle-slash",
            Outputs = [Port.Out, Port.Control("skipped", "port.skipped")],
            Properties =
            [
                new()
                {
                    Name = "condition", LabelKey = "nodeprop.condition", Kind = PropertyKind.Expression,
                    Required = true,
                },
                new() { Name = "reason", LabelKey = "nodeprop.reason", Kind = PropertyKind.Text },
            ],
        },
    ];
}
