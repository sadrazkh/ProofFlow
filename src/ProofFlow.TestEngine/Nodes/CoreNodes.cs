namespace ProofFlow.TestEngine.Nodes;

/// <summary>
/// Sending things, and the shape of a run.
///
/// The group somebody meets first, so it is deliberately small: everything here is a thing you can
/// point at in a drawing of a test. Anything that needed a paragraph to justify belongs in one of
/// the other four.
/// </summary>
public static class CoreNodes
{
    public static IReadOnlyList<NodeSpec> All =>
    [
        new()
        {
            Key = "core.start",
            Group = NodeGroup.Core,
            Icon = "circle-play",
            IsStart = true,
            Inputs = [],
            Outputs = [Port.Out],
        },
        new()
        {
            Key = "core.end",
            Group = NodeGroup.Core,
            Icon = "circle-check",
            IsTerminal = true,
            Outputs = [],
            Properties =
            [
                new()
                {
                    Name = "outcome", LabelKey = "nodeprop.outcome", Kind = PropertyKind.Choice,
                    Options = ["passed", "failed", "skipped"], Default = "passed",
                },
            ],
        },
        new()
        {
            Key = "http.request",
            Group = NodeGroup.Core,
            Icon = "send",
            Reaches = true,
            Outputs = [Port.Out, Port.Failure, Port.Data("response", DataType.Response)],
            Properties =
            [
                new()
                {
                    Name = "method", LabelKey = "nodeprop.method", Kind = PropertyKind.Choice,
                    Options = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"],
                    Default = "GET", Required = true,
                },
                new()
                {
                    Name = "url", LabelKey = "nodeprop.url", Kind = PropertyKind.Url, Required = true,
                    Placeholder = "{{environment.baseUrl}}/records/{{dataset.current.id}}",
                    HelpKey = "nodeprop.url.help",
                },
                new() { Name = "headers", LabelKey = "nodeprop.headers", Kind = PropertyKind.KeyValues },
                new() { Name = "query", LabelKey = "nodeprop.query", Kind = PropertyKind.KeyValues },
                new()
                {
                    Name = "bodyKind", LabelKey = "nodeprop.bodyKind", Kind = PropertyKind.Choice,
                    Options = ["none", "json", "form", "text", "raw"], Default = "none",
                },
                new()
                {
                    Name = "body", LabelKey = "nodeprop.body", Kind = PropertyKind.LongText,
                    Required = true,
                    VisibleWhen = new("bodyKind", ["json", "form", "text", "raw"]),
                },
                new()
                {
                    Name = "timeoutSeconds", LabelKey = "nodeprop.timeout", Kind = PropertyKind.Duration,
                    HelpKey = "nodeprop.timeout.help",
                },
            ],
        },
        new()
        {
            Key = "http.graphql",
            Group = NodeGroup.Core,
            Icon = "braces",
            Reaches = true,
            Outputs = [Port.Out, Port.Failure, Port.Data("response", DataType.Response)],
            Properties =
            [
                new() { Name = "url", LabelKey = "nodeprop.url", Kind = PropertyKind.Url, Required = true },
                new()
                {
                    Name = "query", LabelKey = "nodeprop.graphqlQuery", Kind = PropertyKind.LongText,
                    Required = true, Placeholder = "query { records { id name } }",
                },
                new() { Name = "variables", LabelKey = "nodeprop.graphqlVariables", Kind = PropertyKind.LongText },
                new() { Name = "headers", LabelKey = "nodeprop.headers", Kind = PropertyKind.KeyValues },
            ],
        },
        new()
        {
            Key = "core.delay",
            Group = NodeGroup.Core,
            Icon = "timer",
            Properties =
            [
                new()
                {
                    Name = "duration", LabelKey = "nodeprop.duration", Kind = PropertyKind.Duration,
                    Required = true, Default = "1s", HelpKey = "node.core.delay.help",
                },
            ],
        },
        new()
        {
            Key = "core.log",
            Group = NodeGroup.Core,
            Icon = "file-text",
            Properties =
            [
                new()
                {
                    Name = "message", LabelKey = "nodeprop.message", Kind = PropertyKind.Text,
                    Required = true, Placeholder = "Logged in as {{steps.login.response.user.name}}",
                },
                new()
                {
                    Name = "level", LabelKey = "nodeprop.level", Kind = PropertyKind.Choice,
                    Options = ["info", "warning", "error"], Default = "info",
                },
            ],
        },
        new()
        {
            // No ports at all: a note pinned to the canvas. It is a node so that it moves, selects
            // and versions with everything else rather than being a second kind of thing.
            Key = "core.comment",
            Group = NodeGroup.Core,
            Icon = "message-square",
            Inputs = [],
            Outputs = [],
            Properties =
            [
                new() { Name = "text", LabelKey = "nodeprop.text", Kind = PropertyKind.LongText, Required = true },
            ],
        },
        new()
        {
            Key = "core.setVariable",
            Group = NodeGroup.Core,
            Icon = "variable",
            Outputs = [Port.Out, Port.Data("value", DataType.Any)],
            Properties =
            [
                new()
                {
                    Name = "name", LabelKey = "nodeprop.variableName", Kind = PropertyKind.Text,
                    Required = true, Placeholder = "token",
                },
                new()
                {
                    Name = "value", LabelKey = "nodeprop.value", Kind = PropertyKind.Text, Required = true,
                    Placeholder = "{{steps.login.response.accessToken}}",
                },
                new()
                {
                    Name = "scope", LabelKey = "nodeprop.scope", Kind = PropertyKind.Choice,
                    Options = ["run", "iteration"], Default = "run", HelpKey = "nodeprop.scope.help",
                },
            ],
        },
        new()
        {
            Key = "core.expression",
            Group = NodeGroup.Core,
            Icon = "sigma",
            Outputs = [Port.Out, Port.Data("result", DataType.Any)],
            Properties =
            [
                new()
                {
                    // An expression, not a script. Arbitrary code in a test tool is a way to run
                    // arbitrary code on whatever runs the tests, and this product runs other
                    // people's tests.
                    Name = "expression", LabelKey = "nodeprop.expression", Kind = PropertyKind.Expression,
                    Required = true, Placeholder = "{{steps.list.response.items}} | count",
                    HelpKey = "node.core.expression.help",
                },
            ],
        },
        new()
        {
            Key = "core.group",
            Group = NodeGroup.Core,
            Icon = "boxes",
            IsContainer = true,
            Properties =
            [
                new() { Name = "title", LabelKey = "nodeprop.title", Kind = PropertyKind.Text, Required = true },
                new() { Name = "collapsed", LabelKey = "nodeprop.collapsed", Kind = PropertyKind.Boolean },
            ],
        },
        new()
        {
            Key = "core.parallel",
            Group = NodeGroup.Core,
            Icon = "git-branch",
            Outputs =
            [
                Port.Control("branch1", "port.branch1"),
                Port.Control("branch2", "port.branch2"),
                Port.Control("branch3", "port.branch3"),
            ],
            Properties =
            [
                new()
                {
                    Name = "maxConcurrent", LabelKey = "nodeprop.maxConcurrent", Kind = PropertyKind.Number,
                    Default = "3", HelpKey = "nodeprop.maxConcurrent.help",
                },
            ],
        },
        new()
        {
            Key = "core.join",
            Group = NodeGroup.Core,
            Icon = "git-merge",
            Inputs = [Port.Control("branch1", "port.branch1"), Port.Control("branch2", "port.branch2"),
                      Port.Control("branch3", "port.branch3")],
            Properties =
            [
                new()
                {
                    Name = "wait", LabelKey = "nodeprop.wait", Kind = PropertyKind.Choice,
                    Options = ["all", "any", "first"], Default = "all", HelpKey = "nodeprop.wait.help",
                },
            ],
        },
        new()
        {
            Key = "core.abort",
            Group = NodeGroup.Core,
            Icon = "octagon-x",
            IsTerminal = true,
            Outputs = [],
            Properties =
            [
                new() { Name = "reason", LabelKey = "nodeprop.reason", Kind = PropertyKind.Text, Required = true },
            ],
        },
        new()
        {
            Key = "core.checkpoint",
            Group = NodeGroup.Core,
            Icon = "flag",
            Properties =
            [
                new() { Name = "name", LabelKey = "nodeprop.name", Kind = PropertyKind.Text, Required = true },
            ],
        },
    ];
}
