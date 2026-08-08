using ProofFlow.Contracts.Scenarios;

namespace ProofFlow.Infrastructure.Portability;

/// <summary>
/// Twelve scenarios somebody can start from.
///
/// These are real graphs, not pictures of graphs. Choosing one writes the same nodes and edges the
/// canvas would have written, so the first thing a new reader does is open a working test and change
/// the address in it — which is a far better introduction to seventy node types than an empty canvas
/// and a palette.
///
/// They are ordered by how much somebody needs to understand to use them: a health check first, a
/// clean-up-afterwards pattern last. Every one of them is validated by a test, because a template
/// that will not run is worse than no template — it teaches the reader that the product is broken
/// before they have built anything of their own.
///
/// The text is entirely resource keys. A template named in English inside a Persian gallery is the
/// exact failure the whole localisation effort exists to prevent.
/// </summary>
public static class TemplateCatalogue
{
    public static IReadOnlyList<ScenarioTemplate> All { get; } =
    [
        Smoke(),
        NotFound(),
        ListThenDetail(),
        SignIn(),
        Crud(),
        Baseline(),
        DataDriven(),
        Pagination(),
        PollJob(),
        Retry(),
        Validation(),
        Cleanup(),
    ];

    public static ScenarioTemplate? Find(string key) =>
        All.FirstOrDefault(template => template.Key == key);

    // ---- the twelve ----------------------------------------------------------------------------

    /// <summary>Does it answer at all. The first test anybody writes and the one that runs hourly.</summary>
    private static ScenarioTemplate Smoke() => new()
    {
        Key = "smoke",
        Icon = "activity",
        Tags = ["basics"],
        Graph = Wire(
        [
            Start(),
            Request("n2", "GET", "{{environment.baseUrl}}/health", 0),
            Node("n3", "assert.status", 1, ("expected", "200")),
            Node("n4", "assert.responseTime", 2, ("under", "2s")),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"), Flow("n3", "n4"),
            Data("n2", "response", "n3", "response"),
            Data("n2", "response", "n4", "response"),
        ]),
    };

    /// <summary>
    /// The negative one, and the one people forget.
    ///
    /// An API that returns 200 and an empty object for something that does not exist is broken in a
    /// way no positive test notices.
    /// </summary>
    private static ScenarioTemplate NotFound() => new()
    {
        Key = "notFound",
        Icon = "circle-slash",
        Tags = ["basics", "negative"],
        Graph = Wire(
        [
            Start(),
            Request("n2", "GET", "{{environment.baseUrl}}/records/does-not-exist", 0),
            Node("n3", "assert.status", 1, ("expected", "404")),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"),
            Data("n2", "response", "n3", "response"),
        ]),
    };

    /// <summary>Read a list, take an identifier out of it, read the thing it names.</summary>
    private static ScenarioTemplate ListThenDetail() => new()
    {
        Key = "listThenDetail",
        Icon = "list-ordered",
        Tags = ["chaining"],
        Graph = Wire(
        [
            Start(),
            Request("n2", "GET", "{{environment.baseUrl}}/records", 0),
            Node("n3", "assert.status", 1, ("expected", "200")),
            Node("n4", "data.extractJsonPath", 2, ("path", "$.items[0].id")),
            Request("n5", "GET", "{{environment.baseUrl}}/records/{{steps.n4.value}}", 3),
            Node("n6", "assert.status", 4, ("expected", "200")),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"), Flow("n3", "n4"), Flow("n4", "n5"), Flow("n5", "n6"),
            Data("n2", "response", "n3", "response"),
            Data("n2", "response", "n4", "response"),
            Data("n5", "response", "n6", "response"),
        ]),
    };

    /// <summary>Sign in, keep the token, use it. The step every other scenario starts with.</summary>
    private static ScenarioTemplate SignIn() => new()
    {
        Key = "signIn",
        Icon = "log-in",
        Tags = ["auth"],
        Graph = Wire(
        [
            Start(),
            Node("n2", "auth.login", 0,
                ("url", "{{environment.baseUrl}}/auth/login"),
                ("username", "{{username}}"),
                ("password", "{{secrets.password}}"),
                ("tokenPath", "$.token")),
            Request("n3", "GET", "{{environment.baseUrl}}/auth/me", 1),
            Node("n4", "assert.status", 2, ("expected", "200")),
            Node("n5", "assert.jsonField", 3,
                ("path", "$.username"), ("matcher", "Exact"), ("value", "{{username}}")),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"), Flow("n3", "n4"), Flow("n4", "n5"),
            Data("n3", "response", "n4", "response"),
            Data("n3", "response", "n5", "response"),
        ]),
    };

    /// <summary>Make one, read it, change it, delete it, and check it is gone.</summary>
    private static ScenarioTemplate Crud() => new()
    {
        Key = "crud",
        Icon = "boxes",
        Tags = ["chaining", "lifecycle"],
        Graph = Wire(
        [
            Start(),
            Request("n2", "POST", "{{environment.baseUrl}}/records", 0,
                ("bodyKind", "json"), ("body", "{\n  \"name\": \"from ProofFlow\"\n}")),
            Node("n3", "assert.status", 1, ("expected", "201")),
            Node("n4", "data.extractJsonPath", 2, ("path", "$.id")),
            Request("n5", "GET", "{{environment.baseUrl}}/records/{{steps.n4.value}}", 3),
            Node("n6", "assert.status", 4, ("expected", "200")),
            Request("n7", "DELETE", "{{environment.baseUrl}}/records/{{steps.n4.value}}", 5),
            Request("n8", "GET", "{{environment.baseUrl}}/records/{{steps.n4.value}}", 6),
            Node("n9", "assert.status", 7, ("expected", "404")),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"), Flow("n3", "n4"), Flow("n4", "n5"),
            Flow("n5", "n6"), Flow("n6", "n7"), Flow("n7", "n8"), Flow("n8", "n9"),
            Data("n2", "response", "n3", "response"),
            Data("n2", "response", "n4", "response"),
            Data("n5", "response", "n6", "response"),
            Data("n8", "response", "n9", "response"),
        ]),
    };

    /// <summary>Call it and compare the whole answer against what was approved.</summary>
    private static ScenarioTemplate Baseline() => new()
    {
        Key = "baseline",
        Icon = "target",
        Tags = ["baselines"],
        Graph = Wire(
        [
            Start(),
            Request("n2", "GET", "{{environment.baseUrl}}/records/1", 0),
            Node("n3", "assert.status", 1, ("expected", "200")),
            Node("n4", "baseline.compare", 2, ("baseline", "")),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"), Flow("n3", "n4"),
            Data("n2", "response", "n3", "response"),
            Data("n2", "response", "n4", "response"),
        ]),
    };

    /// <summary>The same request once per row of a data set.</summary>
    private static ScenarioTemplate DataDriven() => new()
    {
        Key = "dataDriven",
        Icon = "table-2",
        Tags = ["data"],
        Graph = Wire(
        [
            Start(),
            Node("n2", "flow.forEachRow", 0, ("dataSet", "")),
            Inside("n2", Request("n3", "GET", "{{environment.baseUrl}}/records/{{row.id}}", 1)),
            Inside("n2", Node("n4", "assert.status", 2, ("expected", "200"))),
        ],
        [
            Flow("n1", "n2"), Flow("n3", "n4"),
            Data("n3", "response", "n4", "response"),
        ]),
    };

    /// <summary>Walk a list page by page and check every page is well formed.</summary>
    private static ScenarioTemplate Pagination() => new()
    {
        Key = "pagination",
        Icon = "rows-3",
        Tags = ["loops"],
        Graph = Wire(
        [
            Start(),
            Node("n2", "core.setVariable", 0, ("name", "page"), ("value", "1")),
            Node("n3", "flow.repeat", 1, ("times", "3")),
            Inside("n3", Request("n4", "GET",
                "{{environment.baseUrl}}/records?page={{vars.page}}&pageSize=10", 2)),
            Inside("n3", Node("n5", "assert.status", 3, ("expected", "200"))),
            Inside("n3", Node("n6", "core.setVariable", 4,
                ("name", "page"), ("value", "{{vars.page + 1}}"))),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"),
            Flow("n4", "n5"), Flow("n5", "n6"),
            Data("n4", "response", "n5", "response"),
        ]),
    };

    /// <summary>Start something slow, then ask until it is ready rather than sleeping and hoping.</summary>
    private static ScenarioTemplate PollJob() => new()
    {
        Key = "pollJob",
        Icon = "history",
        Tags = ["async"],
        Graph = Wire(
        [
            Start(),
            Request("n2", "POST", "{{environment.baseUrl}}/jobs", 0),
            Node("n3", "assert.status", 1, ("expected", "202")),
            Node("n4", "flow.pollUntil", 2,
                ("condition", "{{steps.n5.response.body.status}} == \"ready\""),
                ("interval", "2s"),
                ("timeout", "60s")),
            Inside("n4", Request("n5", "GET", "{{environment.baseUrl}}/jobs/current", 3)),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"), Flow("n3", "n4"),
            Data("n2", "response", "n3", "response"),
        ]),
    };

    /// <summary>A step that is allowed to fail a few times before it counts.</summary>
    private static ScenarioTemplate Retry() => new()
    {
        Key = "retry",
        Icon = "rotate-cw",
        Tags = ["reliability"],
        Graph = Wire(
        [
            Start(),
            Node("n2", "flow.retry", 0, ("attempts", "3"), ("backoff", "exponential")),
            Inside("n2", Request("n3", "GET", "{{environment.baseUrl}}/records", 1)),
            Inside("n2", Node("n4", "assert.status", 2, ("expected", "200"))),
        ],
        [
            Flow("n1", "n2"), Flow("n3", "n4"),
            Data("n3", "response", "n4", "response"),
        ]),
    };

    /// <summary>Send something wrong on purpose and check the refusal is the documented one.</summary>
    private static ScenarioTemplate Validation() => new()
    {
        Key = "validation",
        Icon = "triangle-alert",
        Tags = ["negative"],
        Graph = Wire(
        [
            Start(),
            Request("n2", "POST", "{{environment.baseUrl}}/records", 0,
                ("bodyKind", "json"), ("body", "{\n  \"name\": \"\"\n}")),
            Node("n3", "assert.status", 1, ("expected", "400")),
            Node("n4", "assert.jsonField", 2,
                ("path", "$.errors[0].field"), ("matcher", "Exact"), ("value", "name")),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"), Flow("n3", "n4"),
            Data("n2", "response", "n3", "response"),
            Data("n2", "response", "n4", "response"),
        ]),
    };

    /// <summary>
    /// Make something, test it, and take it away again whatever happened.
    ///
    /// The pattern that keeps a test suite from filling somebody's staging database with three
    /// thousand records called "from ProofFlow".
    /// </summary>
    private static ScenarioTemplate Cleanup() => new()
    {
        Key = "cleanup",
        Icon = "brush-cleaning",
        Tags = ["lifecycle"],
        Graph = Wire(
        [
            Start(),
            Request("n2", "POST", "{{environment.baseUrl}}/records", 0,
                ("bodyKind", "json"), ("body", "{\n  \"name\": \"temporary\"\n}")),
            Node("n3", "data.extractJsonPath", 1, ("path", "$.id")),
            Request("n4", "GET", "{{environment.baseUrl}}/records/{{steps.n3.value}}", 2),
            Node("n5", "assert.status", 3, ("expected", "200")),
            Node("n6", "flow.cleanup", 4, ("always", "true")),
            Inside("n6", Request("n7", "DELETE",
                "{{environment.baseUrl}}/records/{{steps.n3.value}}", 5)),
        ],
        [
            Flow("n1", "n2"), Flow("n2", "n3"), Flow("n3", "n4"), Flow("n4", "n5"), Flow("n5", "n6"),
            Data("n2", "response", "n3", "response"),
            Data("n4", "response", "n5", "response"),
        ]),
    };

    // ---- building blocks -----------------------------------------------------------------------

    private static GraphDto Wire(GraphNodeDto[] nodes, GraphEdgeDto[] edges) =>
        new() { Nodes = nodes, Edges = edges };

    private static GraphNodeDto Start() =>
        new() { Id = "n1", Key = "core.start", Name = "start", X = 0, Y = 0 };

    /// <summary>
    /// Laid out left to right at a fixed spacing.
    ///
    /// Positions are part of the template because a graph that arrives in a heap on top of itself
    /// is one somebody has to untangle before they can read it, and the point of a template is that
    /// it can be read.
    /// </summary>
    private static GraphNodeDto Node(
        string id, string key, int column, params (string Name, string Value)[] properties) =>
        new()
        {
            Id = id,
            Key = key,
            Name = id,
            X = (column + 1) * 220,
            Y = 0,
            Properties = properties.ToDictionary(pair => pair.Name, pair => (string?)pair.Value),
        };

    private static GraphNodeDto Request(
        string id, string method, string url, int column,
        params (string Name, string Value)[] extra) =>
        Node(id, "http.request", column,
            [("method", method), ("url", url), .. extra]);

    /// <summary>Inside a container, and offset so the child does not sit on its parent's title.</summary>
    private static GraphNodeDto Inside(string parent, GraphNodeDto node) =>
        node with { ParentId = parent, Y = 120 };

    private static GraphEdgeDto Flow(string from, string to) =>
        new() { Id = $"{from}-{to}", FromId = from, FromPort = "out", ToId = to, ToPort = "in" };

    private static GraphEdgeDto Data(string from, string fromPort, string to, string toPort) =>
        new()
        {
            Id = $"{from}-{fromPort}-{to}-{toPort}",
            FromId = from,
            FromPort = fromPort,
            ToId = to,
            ToPort = toPort,
        };
}

/// <summary>
/// One template: a graph, and the keys that name it.
///
/// No prose here at all. <see cref="TitleKey"/> and <see cref="SummaryKey"/> are resource keys, so
/// the gallery reads in the reader's language rather than in the language of whoever wrote the
/// catalogue.
/// </summary>
public sealed record ScenarioTemplate
{
    public required string Key { get; init; }

    /// <summary>The lucide name the card draws. Registered in <c>Scripts/lib/icons.ts</c> like any other.</summary>
    public required string Icon { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public required GraphDto Graph { get; init; }

    public string TitleKey => $"template.{Key}.title";

    public string SummaryKey => $"template.{Key}.summary";

    /// <summary>How many steps it has, for the card. Counted rather than written down twice.</summary>
    public int Steps => Graph.Nodes.Count(node => node.Key != "core.start");

    /// <summary>Whether it needs something chosen before it will run — a data set, a baseline.</summary>
    public bool NeedsChoosing => Graph.Nodes.Any(node =>
        node.Properties.Any(property =>
            property.Key is "dataSet" or "baseline" && string.IsNullOrEmpty(property.Value)));
}
