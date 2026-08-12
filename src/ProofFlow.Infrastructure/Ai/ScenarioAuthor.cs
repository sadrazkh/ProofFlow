using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Workspaces;
using ProofFlow.TestEngine.Http;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Infrastructure.Ai;

/// <summary>
/// Asks a model to draw a scenario, and refuses to accept one that will not run.
///
/// The model is given the node catalogue and asked for a graph in this product's own shape, which is
/// then put through the same validator the canvas uses. A graph that does not pass never reaches the
/// canvas: the point of this feature is a starting draft somebody edits, and a draft full of steps
/// that cannot run is worse than an empty canvas because it has to be understood before it can be
/// deleted.
///
/// It writes nothing. What comes back goes onto the canvas as unsaved changes, so the person who
/// asked reads it, moves it, and decides — the same as if they had dragged it out of the palette.
/// </summary>
public sealed class ScenarioAuthor(
    IHttpClientFactory clients,
    ISecretCipher cipher,
    ILogger<ScenarioAuthor> logger)
{
    /// <summary>Where a key with no address goes. Whatever most people already have an account for.</summary>
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>A capable model that is cheap enough to press twice.</summary>
    public const string DefaultModel = "anthropic/claude-sonnet-4.5";

    /// <summary>
    /// Long enough for a graph, short enough that a hung provider does not hold a request open.
    ///
    /// A person is watching a spinner while this runs, which is the whole argument for the number.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsConfigured(Workspace workspace) =>
        !string.IsNullOrWhiteSpace(workspace.AiKeyCipher);

    /// <summary>
    /// Draws one, or says why it could not.
    ///
    /// Every failure here is somebody else's service having a bad day, a key that has run out, or a
    /// model that wrote something this cannot read — all three are ordinary and none of them should
    /// look like a crash.
    /// </summary>
    public async Task<AuthoredScenario> DrawAsync(
        Workspace workspace, string request, CancellationToken cancellation = default)
    {
        if (!IsConfigured(workspace)) return AuthoredScenario.Refused("ai.notConfigured");
        if (string.IsNullOrWhiteSpace(request)) return AuthoredScenario.Refused("ai.noRequest");

        var address = (workspace.AiBaseUrl ?? DefaultBaseUrl).TrimEnd('/');

        // The base URL is typed into a settings page, which makes it exactly the kind of address the
        // guard exists for: a workspace admin who can point this at 169.254.169.254 has a server-side
        // request forgery, with a key attached. The same rules the tests run under.
        var url = $"{address}/chat/completions";

        if (new UrlGuard(new UrlPolicy()).Inspect(url) is not null
            || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
        {
            logger.LogWarning("An AI base URL was refused: {Address}", address);
            return AuthoredScenario.Refused("ai.badAddress");
        }

        var key = cipher.Open(new SealedSecret(
            workspace.AiKeyCipher!, workspace.AiKeyNonce!, workspace.AiKeyTag!, workspace.AiKeyVersion));

        using var client = clients.CreateClient(PolicyClientNames.Strict);
        client.Timeout = Patience;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // OpenRouter asks for these and ignores them elsewhere. Naming the product is how a person
        // finds this line item on a bill they did not expect.
        client.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/sadrazkh/ProofFlow");
        client.DefaultRequestHeaders.Add("X-Title", "ProofFlow");

        var body = new ChatRequest
        {
            Model = string.IsNullOrWhiteSpace(workspace.AiModel) ? DefaultModel : workspace.AiModel,
            Temperature = 0,
            Messages =
            [
                new ChatMessage { Role = "system", Content = Instructions() },
                new ChatMessage { Role = "user", Content = request.Trim() },
            ],
        };

        HttpResponseMessage response;

        try
        {
            response = await client.PostAsJsonAsync(endpoint, body, Json, cancellation);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "The model could not be reached.");
            return AuthoredScenario.Refused("ai.unreachable");
        }

        if (!response.IsSuccessStatusCode)
        {
            // The status, because 401 and 402 are two different conversations to have with whoever
            // holds the key. Not the body: it is somebody else's error text, in some other language.
            logger.LogWarning("The model answered {Status}.", (int)response.StatusCode);

            return AuthoredScenario.Refused((int)response.StatusCode switch
            {
                401 or 403 => "ai.keyRefused",
                402 => "ai.outOfCredit",
                429 => "ai.tooFast",
                _ => "ai.providerFailed",
            });
        }

        var answer = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cancellation);
        var text = answer?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text)) return AuthoredScenario.Refused("ai.emptyAnswer");

        var graph = ReadGraph(text);
        if (graph is null) return AuthoredScenario.Refused("ai.unreadable");

        // The same validator the canvas runs, before anybody sees it. A draft that cannot run is
        // worse than an empty canvas: it has to be understood before it can be thrown away.
        var problems = GraphValidator.Validate(ToEngine(graph));
        var errors = problems.Where(problem => problem.Severity == GraphSeverity.Error).ToList();

        if (errors.Count > 0)
        {
            logger.LogInformation("A drawn scenario was rejected: {Problems}",
                string.Join("; ", errors.Select(problem => problem.Code)));

            return AuthoredScenario.Refused("ai.invalidGraph");
        }

        return AuthoredScenario.Drawn(graph);
    }

    /// <summary>
    /// What the model is told, built from the catalogue rather than written out.
    ///
    /// Written out, it would list the node types that existed the day somebody wrote it, and quietly
    /// stop mentioning every one added afterwards.
    /// </summary>
    private static string Instructions()
    {
        var types = string.Join("\n", NodeCatalogue.All.Select(spec =>
        {
            var properties = spec.Properties.Count == 0
                ? "none"
                : string.Join(", ", spec.Properties.Select(property => property.Name));

            var ports = string.Join("/", spec.Outputs.Select(port => port.Name));

            return $"- {spec.Key} — out: {ports}; properties: {properties}";
        }));

        return """
            You draw API test scenarios for ProofFlow as a JSON graph. Answer with JSON only: no
            prose, no markdown fence.

            Shape:
            {"nodes":[{"id":"n1","key":"core.start","name":"Start","x":80,"y":80,"properties":{}}],
             "edges":[{"fromId":"n1","fromPort":"out","toId":"n2","toPort":"in"}]}

            Rules that a graph is rejected for breaking:
            - Exactly one node whose key is core.start, and every other node reachable from it.
            - Every id referenced by an edge exists. Ids are yours to choose; n1, n2, n3 is fine.
            - Every node has a name, and no two nodes share one — names are how steps refer to each
              other, as {{steps.<name>.response}}.
            - Ports must be ones the node actually has, from the list below.
            - x and y lay it out left to right: start at 80,80 and add about 280 to x per step.

            Writing the requests:
            - Addresses begin {{environment.baseUrl}}. Never hard-code a host.
            - A credential is {{secrets.name}}, never a literal token.
            - Something answered per run is {{inputs.name}}; something fixed per environment is
              {{vars.name}}.
            - Read an earlier step with {{steps.<that step's name>.response.body.field}}.
            - Check what you fetched. A scenario with no assert.* node tests nothing.

            The node types available, with their output ports and property names:

            """ + types;
    }

    /// <summary>Reads the answer, forgiving a model that fenced its JSON anyway.</summary>
    private static GraphDto? ReadGraph(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var first = trimmed.IndexOf('\n');
            var last = trimmed.LastIndexOf("```", StringComparison.Ordinal);

            if (first > 0 && last > first) trimmed = trimmed[(first + 1)..last].Trim();
        }

        try
        {
            var graph = JsonSerializer.Deserialize<GraphDto>(trimmed, Json);

            return graph is null || graph.Nodes.Count == 0 ? null : graph;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Graph ToEngine(GraphDto graph) => new(
        [.. graph.Nodes.Select(node => new GraphNode(
            node.Id, node.Key, node.Name, node.Properties, node.ParentId, node.Disabled))],
        [.. graph.Edges.Select(edge => new GraphEdge(
            edge.FromId, edge.FromPort, edge.ToId, edge.ToPort))]);

    // ---- the wire, as OpenAI defined it and everyone else copied ---------------------------------

    private sealed record ChatRequest
    {
        public required string Model { get; init; }
        public required IReadOnlyList<ChatMessage> Messages { get; init; }
        public double Temperature { get; init; }
    }

    private sealed record ChatMessage
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
    }

    private sealed record ChatResponse
    {
        public IReadOnlyList<ChatChoice>? Choices { get; init; }
    }

    private sealed record ChatChoice
    {
        public ChatMessage? Message { get; init; }
    }
}

/// <summary>
/// What came back: a graph, or a reason nobody got one.
///
/// The reason is a key rather than a sentence, so it arrives in the reader's language like every
/// other message in this product.
/// </summary>
public sealed record AuthoredScenario(GraphDto? Graph, string? Problem)
{
    public bool Ok => Graph is not null;

    public static AuthoredScenario Drawn(GraphDto graph) => new(graph, null);

    public static AuthoredScenario Refused(string problem) => new(null, problem);
}
