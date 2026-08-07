using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Infrastructure.Scenarios;

/// <summary>
/// Loading a graph, saving one, and saying whether it is a test yet.
///
/// The save is the interesting half. The canvas sends the whole graph rather than a list of edits,
/// because a drag that moves nine nodes and deletes an edge is one thought and should be one save —
/// so this works out what changed by comparing, and keeps the ids of everything that survived.
/// Replacing the lot would renumber every node, and a run from yesterday points at those ids.
/// </summary>
public sealed class ScenarioGraphService(
    ProofFlowDbContext db, ICurrentUser me, IClock clock, IProblemText text)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<GraphDto> LoadAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await db.ScenarioVersions
            .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);

        if (version is null) return new GraphDto { Nodes = [], Edges = [] };

        var nodes = await db.WorkflowNodes
            .Where(n => n.ScenarioVersionId == versionId)
            .OrderBy(n => n.SortOrder)
            .ToListAsync(cancellationToken);

        var edges = await db.WorkflowConnections
            .Where(c => c.ScenarioVersionId == versionId)
            .ToListAsync(cancellationToken);

        return new GraphDto
        {
            Nodes = [.. nodes.Select(node => new GraphNodeDto
            {
                Id = node.Id.ToString(),
                Key = node.Key,
                Name = node.Name,
                Note = node.Note,
                X = node.X,
                Y = node.Y,
                ParentId = node.ParentNodeId?.ToString(),
                Disabled = node.Disabled,
                Properties = ReadProperties(node.PropertiesJson),
            })],
            Edges = [.. edges.Select(edge => new GraphEdgeDto
            {
                Id = edge.Id.ToString(),
                FromId = edge.FromNodeId.ToString(),
                FromPort = edge.FromPort,
                ToId = edge.ToNodeId.ToString(),
                ToPort = edge.ToPort,
                Label = edge.Label,
            })],
            CanvasJson = version.CanvasJson,
        };
    }

    /// <summary>
    /// Writes the graph into the draft version, creating one if the scenario has none.
    ///
    /// Only ever the draft. A published version is what a schedule runs tonight, and opening the
    /// canvas to move a node must not change that.
    /// </summary>
    public async Task<SaveGraphResult> SaveAsync(
        TestScenario scenario, GraphDto graph, CancellationToken cancellationToken = default)
    {
        var version = await DraftAsync(scenario, cancellationToken);

        var existing = await db.WorkflowNodes
            .Where(n => n.ScenarioVersionId == version.Id)
            .ToListAsync(cancellationToken);

        var byId = existing.ToDictionary(node => node.Id.ToString(), StringComparer.Ordinal);
        var assigned = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var kept = new HashSet<Guid>();

        var order = 0;

        foreach (var incoming in graph.Nodes)
        {
            var node = byId.TryGetValue(incoming.Id, out var found) ? found : null;

            if (node is null)
            {
                node = new WorkflowNode
                {
                    WorkspaceId = scenario.WorkspaceId,
                    ScenarioVersionId = version.Id,
                    Key = incoming.Key,
                    Name = incoming.Name,
                };

                db.WorkflowNodes.Add(node);
            }

            node.Key = incoming.Key;
            node.Name = incoming.Name;
            node.Note = incoming.Note;
            node.X = incoming.X;
            node.Y = incoming.Y;
            node.Disabled = incoming.Disabled;
            node.SortOrder = order++;
            node.PropertiesJson = JsonSerializer.Serialize(incoming.Properties, Json);

            assigned[incoming.Id] = node.Id;
            kept.Add(node.Id);
        }

        // Parents in a second pass: a node dropped inside a container may be sent before it.
        foreach (var incoming in graph.Nodes.Where(n => n.ParentId is not null))
        {
            if (!assigned.TryGetValue(incoming.Id, out var id)) continue;

            var node = existing.FirstOrDefault(n => n.Id == id)
                       ?? db.WorkflowNodes.Local.First(n => n.Id == id);

            node.ParentNodeId = assigned.TryGetValue(incoming.ParentId!, out var parent) ? parent : null;
        }

        // Edges are replaced wholesale, which is safe in a way nodes are not: nothing refers to an
        // edge by id, so keeping them would buy nothing and the comparison would cost more.
        var oldEdges = await db.WorkflowConnections
            .Where(c => c.ScenarioVersionId == version.Id)
            .ToListAsync(cancellationToken);

        db.WorkflowConnections.RemoveRange(oldEdges);

        foreach (var node in existing.Where(n => !kept.Contains(n.Id)))
        {
            db.WorkflowNodes.Remove(node);
        }

        // Saved before the edges so the removals land first: an edge pointing at a node being
        // deleted in the same batch is a foreign-key violation on the way in.
        await db.SaveChangesAsync(cancellationToken);

        foreach (var edge in graph.Edges)
        {
            if (!assigned.TryGetValue(edge.FromId, out var from)) continue;
            if (!assigned.TryGetValue(edge.ToId, out var to)) continue;

            db.WorkflowConnections.Add(new WorkflowConnection
            {
                WorkspaceId = scenario.WorkspaceId,
                ScenarioVersionId = version.Id,
                FromNodeId = from,
                FromPort = edge.FromPort,
                ToNodeId = to,
                ToPort = edge.ToPort,
                Label = edge.Label,
            });
        }

        var problems = Validate(graph);

        version.CanvasJson = graph.CanvasJson;
        version.IsValid = !problems.Any(p => p.Severity == nameof(GraphSeverity.Error));
        version.ValidationJson = JsonSerializer.Serialize(problems, Json);

        await db.SaveChangesAsync(cancellationToken);

        return new SaveGraphResult
        {
            VersionId = version.Id,
            Number = version.Number,
            IsValid = version.IsValid,
            Problems = problems,
            NodeIds = assigned,
        };
    }

    /// <summary>
    /// Runs the validator over what the canvas sent, without touching the database.
    ///
    /// Used by the save and on its own, because the canvas asks while somebody is still drawing.
    /// The engine returns codes; the sentence is built here, in the reader's language.
    /// </summary>
    public IReadOnlyList<GraphProblemDto> Validate(GraphDto graph)
    {
        var nodes = graph.Nodes
            .Select(node => new GraphNode(
                node.Id, node.Key, node.Name, node.Properties, node.ParentId, node.Disabled))
            .ToList();

        var edges = graph.Edges
            .Select(edge => new GraphEdge(edge.FromId, edge.FromPort, edge.ToId, edge.ToPort))
            .ToList();

        return
        [
            .. GraphValidator.Validate(new Graph(nodes, edges))
                .Select(problem => new GraphProblemDto
                {
                    Severity = problem.Severity.ToString(),
                    Code = problem.Code,
                    Message = text.For(problem),
                    NodeId = problem.NodeId,
                    Port = problem.Port,
                    Property = problem.Property,
                }),
        ];
    }

    /// <summary>
    /// Turns the draft into the published version.
    ///
    /// Refused while the graph has errors, and that refusal is the point: publishing is the moment
    /// a schedule starts running this, and a scenario that cannot run should not be scheduled.
    /// </summary>
    public async Task<ScenarioVersion> PublishAsync(
        TestScenario scenario, CancellationToken cancellationToken = default)
    {
        var draft = await db.ScenarioVersions
            .FirstOrDefaultAsync(v => v.Id == scenario.DraftVersionId, cancellationToken)
            ?? throw new InvalidOperationException("This scenario has no draft to publish.");

        if (!draft.IsValid)
            throw new InvalidOperationException("A scenario with errors cannot be published.");

        var current = await db.ScenarioVersions
            .FirstOrDefaultAsync(v => v.Id == scenario.PublishedVersionId, cancellationToken);

        if (current is not null && current.Id != draft.Id)
        {
            current.Status = ScenarioVersionStatus.Superseded;
        }

        draft.Status = ScenarioVersionStatus.Published;
        draft.PublishedAt = clock.UtcNow;

        scenario.PublishedVersionId = draft.Id;

        // A new draft, copied from what was just published, so the next edit does not change what
        // is now running.
        scenario.DraftVersionId = (await CopyAsync(scenario, draft, cancellationToken)).Id;

        await db.SaveChangesAsync(cancellationToken);
        return draft;
    }

    private async Task<ScenarioVersion> DraftAsync(
        TestScenario scenario, CancellationToken cancellationToken)
    {
        if (scenario.DraftVersionId is { } id)
        {
            var existing = await db.ScenarioVersions.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
            if (existing is not null) return existing;
        }

        var numbers = await db.ScenarioVersions
            .Where(v => v.ScenarioId == scenario.Id)
            .Select(v => v.Number)
            .ToListAsync(cancellationToken);

        var version = new ScenarioVersion
        {
            WorkspaceId = scenario.WorkspaceId,
            ScenarioId = scenario.Id,
            Number = numbers.Count == 0 ? 1 : numbers.Max() + 1,
            Status = ScenarioVersionStatus.Draft,
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.ScenarioVersions.Add(version);
        scenario.DraftVersionId = version.Id;

        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    /// <summary>Copies a version's graph into a new draft, keeping the shape and losing the ids.</summary>
    private async Task<ScenarioVersion> CopyAsync(
        TestScenario scenario, ScenarioVersion source, CancellationToken cancellationToken)
    {
        var numbers = await db.ScenarioVersions
            .Where(v => v.ScenarioId == scenario.Id)
            .Select(v => v.Number)
            .ToListAsync(cancellationToken);

        var copy = new ScenarioVersion
        {
            WorkspaceId = scenario.WorkspaceId,
            ScenarioId = scenario.Id,
            Number = numbers.Count == 0 ? 1 : numbers.Max() + 1,
            Status = ScenarioVersionStatus.Draft,
            CanvasJson = source.CanvasJson,
            IsValid = source.IsValid,
            ValidationJson = source.ValidationJson,
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.ScenarioVersions.Add(copy);

        var nodes = await db.WorkflowNodes
            .Where(n => n.ScenarioVersionId == source.Id)
            .ToListAsync(cancellationToken);

        var edges = await db.WorkflowConnections
            .Where(c => c.ScenarioVersionId == source.Id)
            .ToListAsync(cancellationToken);

        var map = new Dictionary<Guid, Guid>();

        foreach (var node in nodes)
        {
            var fresh = new WorkflowNode
            {
                WorkspaceId = node.WorkspaceId,
                ScenarioVersionId = copy.Id,
                Key = node.Key,
                Name = node.Name,
                Note = node.Note,
                X = node.X,
                Y = node.Y,
                Disabled = node.Disabled,
                SortOrder = node.SortOrder,
                PropertiesJson = node.PropertiesJson,
            };

            map[node.Id] = fresh.Id;
            db.WorkflowNodes.Add(fresh);
        }

        foreach (var node in nodes.Where(n => n.ParentNodeId is not null))
        {
            var fresh = db.WorkflowNodes.Local.First(n => n.Id == map[node.Id]);
            fresh.ParentNodeId = map.TryGetValue(node.ParentNodeId!.Value, out var parent) ? parent : null;
        }

        foreach (var edge in edges)
        {
            if (!map.TryGetValue(edge.FromNodeId, out var from)) continue;
            if (!map.TryGetValue(edge.ToNodeId, out var to)) continue;

            db.WorkflowConnections.Add(new WorkflowConnection
            {
                WorkspaceId = edge.WorkspaceId,
                ScenarioVersionId = copy.Id,
                FromNodeId = from,
                FromPort = edge.FromPort,
                ToNodeId = to,
                ToPort = edge.ToPort,
                Label = edge.Label,
            });
        }

        return copy;
    }

    private static IReadOnlyDictionary<string, string?> ReadProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string?>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Json)
                   ?? new Dictionary<string, string?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>();
        }
    }

    /// <summary>The catalogue, as the palette and inspector need it. Built once per request.</summary>
    public static IReadOnlyList<NodeSpecDto> Catalogue() =>
    [
        .. NodeCatalogue.All.Select(spec => new NodeSpecDto
        {
            Key = spec.Key,
            Group = spec.Group.ToString(),
            Icon = spec.Icon,
            IsStart = spec.IsStart,
            IsTerminal = spec.IsTerminal,
            IsContainer = spec.IsContainer,
            Reaches = spec.Reaches,
            Inputs = [.. spec.Inputs.Select(ToDto)],
            Outputs = [.. spec.Outputs.Select(ToDto)],
            Properties =
            [
                .. spec.Properties.Select(property => new PropertyDto
                {
                    Name = property.Name,
                    LabelKey = property.LabelKey,
                    Kind = property.Kind.ToString(),
                    Required = property.Required,
                    Default = property.Default,
                    HelpKey = property.HelpKey,
                    Placeholder = property.Placeholder,
                    Options = property.Options,
                    VisibleWhen = property.VisibleWhen is { } condition
                        ? new PropertyConditionDto(condition.Property, condition.Values)
                        : null,
                }),
            ],
        }),
    ];

    private static PortDto ToDto(PortSpec port) => new()
    {
        Name = port.Name,
        LabelKey = port.LabelKey,
        Kind = port.Kind.ToString(),
        Type = port.Type.ToString(),
        IsFailure = port.IsFailure,
        Required = port.Required,
    };
}
