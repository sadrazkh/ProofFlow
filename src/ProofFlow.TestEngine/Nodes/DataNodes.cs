namespace ProofFlow.TestEngine.Nodes;

/// <summary>
/// Getting values out of responses, and shaping them.
///
/// Almost every one of these exists because the alternative is typing a JSON path by hand, which
/// section 9 of the brief rules out for the person this is built for. They are the nodes the
/// response viewer's "use this field" menu drops onto the canvas.
/// </summary>
public static class DataNodes
{
    /// <summary>The response every extractor reads from. Its own port, so the edge says where it came from.</summary>
    private static PortSpec ResponseIn => new()
    {
        Name = "response", LabelKey = "port.response", Kind = PortKind.Data,
        Type = DataType.Response, Required = true,
    };

    public static IReadOnlyList<NodeSpec> All =>
    [
        new()
        {
            Key = "data.extractJsonPath",
            Group = NodeGroup.Data,
            Icon = "file-json",
            Inputs = [Port.In, ResponseIn],
            Outputs = [Port.Out, Port.Data("value", DataType.Any)],
            Properties =
            [
                new()
                {
                    Name = "path", LabelKey = "nodeprop.path", Kind = PropertyKind.JsonPath, Required = true,
                    Placeholder = "$.data.items[0].id", HelpKey = "nodeprop.path.help",
                },
                new()
                {
                    Name = "onMissing", LabelKey = "nodeprop.onMissing", Kind = PropertyKind.Choice,
                    Options = ["fail", "null", "default"], Default = "fail",
                },
                new()
                {
                    Name = "default", LabelKey = "nodeprop.default", Kind = PropertyKind.Text,
                    VisibleWhen = new("onMissing", ["default"]),
                },
            ],
        },
        new()
        {
            Key = "data.extractHeader",
            Group = NodeGroup.Data,
            Icon = "list",
            Inputs = [Port.In, ResponseIn],
            Outputs = [Port.Out, Port.Data("value", DataType.Text)],
            Properties =
            [
                new()
                {
                    Name = "header", LabelKey = "nodeprop.header", Kind = PropertyKind.Text, Required = true,
                    Placeholder = "X-Request-Id",
                },
            ],
        },
        new()
        {
            Key = "data.extractCookie",
            Group = NodeGroup.Data,
            Icon = "cookie",
            Inputs = [Port.In, ResponseIn],
            Outputs = [Port.Out, Port.Data("value", DataType.Text)],
            Properties =
            [
                new() { Name = "cookie", LabelKey = "nodeprop.cookie", Kind = PropertyKind.Text, Required = true },
            ],
        },
        new()
        {
            Key = "data.extractRegex",
            Group = NodeGroup.Data,
            Icon = "regex",
            Inputs = [Port.In, Port.Data("text", DataType.Text) with { Required = true }],
            Outputs = [Port.Out, Port.Data("value", DataType.Text)],
            Properties =
            [
                new()
                {
                    Name = "pattern", LabelKey = "nodeprop.pattern", Kind = PropertyKind.Text, Required = true,
                    HelpKey = "nodeprop.pattern.help",
                },
                new() { Name = "group", LabelKey = "nodeprop.group", Kind = PropertyKind.Number, Default = "1" },
            ],
        },
        new()
        {
            Key = "data.extractStatus",
            Group = NodeGroup.Data,
            Icon = "hash",
            Inputs = [Port.In, ResponseIn],
            Outputs = [Port.Out, Port.Data("value", DataType.Number)],
        },
        new()
        {
            Key = "data.jsonParse",
            Group = NodeGroup.Data,
            Icon = "braces",
            Inputs = [Port.In, Port.Data("text", DataType.Text) with { Required = true }],
            Outputs = [Port.Out, Port.Failure, Port.Data("json", DataType.Json)],
        },
        new()
        {
            Key = "data.jsonStringify",
            Group = NodeGroup.Data,
            Icon = "quote",
            Inputs = [Port.In, Port.Data("json", DataType.Json) with { Required = true }],
            Outputs = [Port.Out, Port.Data("text", DataType.Text)],
            Properties =
            [
                new() { Name = "indent", LabelKey = "nodeprop.indent", Kind = PropertyKind.Boolean },
            ],
        },
        new()
        {
            Key = "data.mapFields",
            Group = NodeGroup.Data,
            Icon = "shuffle",
            Inputs = [Port.In, Port.Data("json", DataType.Json) with { Required = true }],
            Outputs = [Port.Out, Port.Data("json", DataType.Json)],
            Properties =
            [
                new()
                {
                    Name = "mapping", LabelKey = "nodeprop.mapping", Kind = PropertyKind.KeyValues,
                    Required = true, HelpKey = "nodeprop.mapping.help",
                },
            ],
        },
        new()
        {
            Key = "data.filterList",
            Group = NodeGroup.Data,
            Icon = "filter",
            Inputs = [Port.In, Port.Data("list", DataType.List) with { Required = true }],
            Outputs = [Port.Out, Port.Data("list", DataType.List)],
            Properties =
            [
                new()
                {
                    Name = "condition", LabelKey = "nodeprop.condition", Kind = PropertyKind.Expression,
                    Required = true, Placeholder = "item.active == true",
                },
            ],
        },
        new()
        {
            Key = "data.sortList",
            Group = NodeGroup.Data,
            Icon = "arrow-down-up",
            Inputs = [Port.In, Port.Data("list", DataType.List) with { Required = true }],
            Outputs = [Port.Out, Port.Data("list", DataType.List)],
            Properties =
            [
                new() { Name = "by", LabelKey = "nodeprop.by", Kind = PropertyKind.JsonPath, Required = true },
                new()
                {
                    Name = "direction", LabelKey = "nodeprop.direction", Kind = PropertyKind.Choice,
                    Options = ["ascending", "descending"], Default = "ascending",
                },
            ],
        },
        new()
        {
            Key = "data.pickIndex",
            Group = NodeGroup.Data,
            Icon = "target",
            Inputs = [Port.In, Port.Data("list", DataType.List) with { Required = true }],
            Outputs = [Port.Out, Port.Data("item", DataType.Any)],
            Properties =
            [
                new()
                {
                    Name = "index", LabelKey = "nodeprop.index", Kind = PropertyKind.Number, Default = "0",
                    HelpKey = "nodeprop.index.help",
                },
            ],
        },
        new()
        {
            Key = "data.count",
            Group = NodeGroup.Data,
            Icon = "hash",
            Inputs = [Port.In, Port.Data("list", DataType.List) with { Required = true }],
            Outputs = [Port.Out, Port.Data("count", DataType.Number)],
        },
        new()
        {
            Key = "data.merge",
            Group = NodeGroup.Data,
            Icon = "git-merge",
            Inputs =
            [
                Port.In,
                Port.Data("first", DataType.Json) with { Required = true },
                Port.Data("second", DataType.Json) with { Required = true },
            ],
            Outputs = [Port.Out, Port.Data("json", DataType.Json)],
            Properties =
            [
                new()
                {
                    Name = "onConflict", LabelKey = "nodeprop.onConflict", Kind = PropertyKind.Choice,
                    Options = ["second", "first", "fail"], Default = "second",
                },
            ],
        },
        new()
        {
            Key = "data.template",
            Group = NodeGroup.Data,
            Icon = "type",
            Outputs = [Port.Out, Port.Data("text", DataType.Text)],
            Properties =
            [
                new()
                {
                    Name = "template", LabelKey = "nodeprop.template", Kind = PropertyKind.LongText,
                    Required = true, Placeholder = "Bearer {{steps.login.response.accessToken}}",
                },
            ],
        },
        new()
        {
            Key = "data.datasetRow",
            Group = NodeGroup.Data,
            Icon = "table-2",
            Outputs = [Port.Out, Port.Data("row", DataType.Json), Port.Data("key", DataType.Text)],
            Properties =
            [
                new()
                {
                    Name = "dataSet", LabelKey = "nodeprop.dataSet", Kind = PropertyKind.Reference,
                    HelpKey = "node.data.datasetRow.help",
                },
            ],
        },
        new()
        {
            Key = "data.generate",
            Group = NodeGroup.Data,
            Icon = "sparkles",
            Outputs = [Port.Out, Port.Data("value", DataType.Text)],
            Properties =
            [
                new()
                {
                    Name = "kind", LabelKey = "nodeprop.generateKind", Kind = PropertyKind.Choice,
                    Options = ["uuid", "email", "name", "number", "date", "word", "sentence"],
                    Default = "uuid", Required = true,
                },
                new()
                {
                    // A generated value that differs per run is a value a baseline cannot hold, so
                    // the seed is offered: same seed, same value, every run.
                    Name = "seed", LabelKey = "nodeprop.seed", Kind = PropertyKind.Text,
                    HelpKey = "nodeprop.seed.help",
                },
            ],
        },
        new()
        {
            Key = "data.base64",
            Group = NodeGroup.Data,
            Icon = "binary",
            Inputs = [Port.In, Port.Data("text", DataType.Text) with { Required = true }],
            Outputs = [Port.Out, Port.Data("text", DataType.Text)],
            Properties =
            [
                new()
                {
                    Name = "direction", LabelKey = "nodeprop.direction", Kind = PropertyKind.Choice,
                    Options = ["encode", "decode"], Default = "encode",
                },
            ],
        },
        new()
        {
            Key = "data.hash",
            Group = NodeGroup.Data,
            Icon = "fingerprint",
            Inputs = [Port.In, Port.Data("text", DataType.Text) with { Required = true }],
            Outputs = [Port.Out, Port.Data("hash", DataType.Text)],
            Properties =
            [
                new()
                {
                    Name = "algorithm", LabelKey = "nodeprop.algorithm", Kind = PropertyKind.Choice,
                    Options = ["sha256", "sha512", "md5"], Default = "sha256",
                },
            ],
        },
    ];
}
