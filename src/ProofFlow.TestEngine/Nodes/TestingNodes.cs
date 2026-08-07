namespace ProofFlow.TestEngine.Nodes;

/// <summary>
/// Saying what should be true.
///
/// Every node here has a failure port, and that is the point of the group: an assertion that
/// cannot fail is a comment. The runner takes the failure edge when one does not hold, so a
/// scenario can decide what to do about it rather than simply stopping.
/// </summary>
public static class TestingNodes
{
    private static PortSpec ResponseIn => new()
    {
        Name = "response", LabelKey = "port.response", Kind = PortKind.Data,
        Type = DataType.Response, Required = true,
    };

    /// <summary>Passed and failed, in that order. Failure drawn as a diamond in the failure colour.</summary>
    private static IReadOnlyList<PortSpec> Verdict => [Port.Out, Port.Failure];

    /// <summary>
    /// Every assertion can be softened.
    ///
    /// A soft assertion records the failure and lets the run continue, which is what somebody
    /// wants when a scenario checks fifteen fields and they would rather see all fifteen results
    /// than the first one that broke.
    /// </summary>
    private static PropertySpec Soft => new()
    {
        Name = "soft", LabelKey = "nodeprop.soft", Kind = PropertyKind.Boolean,
        HelpKey = "nodeprop.soft.help",
    };

    public static IReadOnlyList<NodeSpec> All =>
    [
        new()
        {
            Key = "assert.status",
            Group = NodeGroup.Testing,
            Icon = "circle-check",
            Inputs = [Port.In, ResponseIn],
            Outputs = Verdict,
            Properties =
            [
                new()
                {
                    Name = "expected", LabelKey = "nodeprop.expectedStatus", Kind = PropertyKind.Text,
                    Required = true, Default = "200", Placeholder = "200, 201, 2xx",
                    HelpKey = "nodeprop.expectedStatus.help",
                },
                Soft,
            ],
        },
        new()
        {
            Key = "assert.header",
            Group = NodeGroup.Testing,
            Icon = "list-checks",
            Inputs = [Port.In, ResponseIn],
            Outputs = Verdict,
            Properties =
            [
                new() { Name = "header", LabelKey = "nodeprop.header", Kind = PropertyKind.Text, Required = true },
                new() { Name = "matcher", LabelKey = "nodeprop.matcher", Kind = PropertyKind.Matcher, Default = "Exact" },
                new() { Name = "value", LabelKey = "nodeprop.value", Kind = PropertyKind.Text },
                Soft,
            ],
        },
        new()
        {
            Key = "assert.jsonField",
            Group = NodeGroup.Testing,
            Icon = "file-json",
            Inputs = [Port.In, ResponseIn],
            Outputs = Verdict,
            Properties =
            [
                new() { Name = "path", LabelKey = "nodeprop.path", Kind = PropertyKind.JsonPath, Required = true },
                new()
                {
                    Name = "matcher", LabelKey = "nodeprop.matcher", Kind = PropertyKind.Matcher,
                    Default = "Exact", Required = true,
                },
                new() { Name = "value", LabelKey = "nodeprop.value", Kind = PropertyKind.Text },
                Soft,
            ],
        },
        new()
        {
            Key = "assert.jsonSchema",
            Group = NodeGroup.Testing,
            Icon = "file-check",
            Inputs = [Port.In, ResponseIn],
            Outputs = Verdict,
            Properties =
            [
                new()
                {
                    Name = "schema", LabelKey = "nodeprop.schema", Kind = PropertyKind.LongText,
                    Required = true, HelpKey = "nodeprop.schema.help",
                },
                Soft,
            ],
        },
        new()
        {
            Key = "assert.responseTime",
            Group = NodeGroup.Testing,
            Icon = "timer",
            Inputs = [Port.In, ResponseIn],
            Outputs = Verdict,
            Properties =
            [
                new()
                {
                    Name = "under", LabelKey = "nodeprop.under", Kind = PropertyKind.Duration,
                    Required = true, Default = "2s",
                },
                Soft,
            ],
        },
        new()
        {
            Key = "assert.bodyContains",
            Group = NodeGroup.Testing,
            Icon = "search",
            Inputs = [Port.In, ResponseIn],
            Outputs = Verdict,
            Properties =
            [
                new() { Name = "text", LabelKey = "nodeprop.text", Kind = PropertyKind.Text, Required = true },
                new() { Name = "ignoreCase", LabelKey = "nodeprop.ignoreCase", Kind = PropertyKind.Boolean },
                Soft,
            ],
        },
        new()
        {
            Key = "assert.listCount",
            Group = NodeGroup.Testing,
            Icon = "list-ordered",
            Inputs = [Port.In, Port.Data("list", DataType.List) with { Required = true }],
            Outputs = Verdict,
            Properties =
            [
                new()
                {
                    Name = "comparison", LabelKey = "nodeprop.comparison", Kind = PropertyKind.Choice,
                    Options = ["exactly", "atLeast", "atMost", "between"], Default = "exactly",
                },
                new() { Name = "count", LabelKey = "nodeprop.count", Kind = PropertyKind.Number, Required = true },
                new()
                {
                    Name = "upper", LabelKey = "nodeprop.upper", Kind = PropertyKind.Number,
                    VisibleWhen = new("comparison", ["between"]),
                },
                Soft,
            ],
        },
        new()
        {
            Key = "assert.listContains",
            Group = NodeGroup.Testing,
            Icon = "list-check",
            Inputs = [Port.In, Port.Data("list", DataType.List) with { Required = true }],
            Outputs = Verdict,
            Properties =
            [
                new() { Name = "path", LabelKey = "nodeprop.path", Kind = PropertyKind.JsonPath },
                new() { Name = "value", LabelKey = "nodeprop.value", Kind = PropertyKind.Text, Required = true },
                Soft,
            ],
        },
        new()
        {
            Key = "assert.notNull",
            Group = NodeGroup.Testing,
            Icon = "circle-dot",
            Inputs = [Port.In, Port.Data("value", DataType.Any) with { Required = true }],
            Outputs = Verdict,
            Properties = [Soft],
        },
        new()
        {
            Key = "assert.matchesRegex",
            Group = NodeGroup.Testing,
            Icon = "regex",
            Inputs = [Port.In, Port.Data("text", DataType.Text) with { Required = true }],
            Outputs = Verdict,
            Properties =
            [
                new() { Name = "pattern", LabelKey = "nodeprop.pattern", Kind = PropertyKind.Text, Required = true },
                Soft,
            ],
        },
        new()
        {
            Key = "baseline.compare",
            Group = NodeGroup.Testing,
            Icon = "git-compare-arrows",
            Inputs = [Port.In, ResponseIn],
            Outputs = [Port.Out, Port.Failure, Port.Data("diff", DataType.Json)],
            Properties =
            [
                new()
                {
                    Name = "baseline", LabelKey = "nodeprop.baseline", Kind = PropertyKind.Reference,
                    Required = true,
                },
                new()
                {
                    Name = "key", LabelKey = "nodeprop.sampleKey", Kind = PropertyKind.Text,
                    HelpKey = "nodeprop.sampleKey.help", Placeholder = "{{dataset.current.id}}",
                },
                Soft,
            ],
        },
        new()
        {
            Key = "baseline.capture",
            Group = NodeGroup.Testing,
            Icon = "camera",
            Inputs = [Port.In, ResponseIn],
            Properties =
            [
                new()
                {
                    Name = "baseline", LabelKey = "nodeprop.baseline", Kind = PropertyKind.Reference,
                    Required = true,
                },
                new()
                {
                    // Never on by default. A capture that approves itself is a test that can never
                    // fail, and it would do it silently.
                    Name = "approve", LabelKey = "nodeprop.approve", Kind = PropertyKind.Boolean,
                    HelpKey = "nodeprop.approve.help",
                },
            ],
        },
        new()
        {
            Key = "test.softFail",
            Group = NodeGroup.Testing,
            Icon = "triangle-alert",
            Properties =
            [
                new() { Name = "message", LabelKey = "nodeprop.message", Kind = PropertyKind.Text, Required = true },
            ],
        },
        new()
        {
            Key = "test.expectFailure",
            Group = NodeGroup.Testing,
            Icon = "shield-alert",
            IsContainer = true,
            Outputs = [Port.Out, Port.Failure],
            Properties =
            [
                new()
                {
                    Name = "reason", LabelKey = "nodeprop.reason", Kind = PropertyKind.Text, Required = true,
                    HelpKey = "node.test.expectFailure.help",
                },
            ],
        },
        new()
        {
            Key = "test.tag",
            Group = NodeGroup.Testing,
            Icon = "tag",
            Properties =
            [
                new() { Name = "tags", LabelKey = "nodeprop.tags", Kind = PropertyKind.Text, Required = true },
            ],
        },
        new()
        {
            Key = "test.attach",
            Group = NodeGroup.Testing,
            Icon = "paperclip",
            Inputs = [Port.In, Port.Data("value", DataType.Any) with { Required = true }],
            Properties =
            [
                new() { Name = "name", LabelKey = "nodeprop.name", Kind = PropertyKind.Text, Required = true },
                new()
                {
                    // Attachments outlive the run and end up in reports somebody forwards, so the
                    // question of whether this one holds a token has to be asked here.
                    Name = "redact", LabelKey = "nodeprop.redact", Kind = PropertyKind.Boolean,
                    Default = "true", HelpKey = "nodeprop.redact.help",
                },
            ],
        },
    ];
}
