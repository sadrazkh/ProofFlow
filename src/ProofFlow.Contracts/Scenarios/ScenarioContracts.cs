namespace ProofFlow.Contracts.Scenarios;

/// <summary>One node type, as the palette and the inspector need it.</summary>
public sealed record NodeSpecDto
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public required string Icon { get; init; }
    public required IReadOnlyList<PortDto> Inputs { get; init; }
    public required IReadOnlyList<PortDto> Outputs { get; init; }
    public required IReadOnlyList<PropertyDto> Properties { get; init; }
    public bool IsStart { get; init; }
    public bool IsTerminal { get; init; }
    public bool IsContainer { get; init; }

    /// <summary>Makes a real request. The canvas marks these, because it is what a reader asks first.</summary>
    public bool Reaches { get; init; }
}

public sealed record PortDto
{
    public required string Name { get; init; }
    public required string LabelKey { get; init; }
    public required string Kind { get; init; }
    public required string Type { get; init; }
    public bool IsFailure { get; init; }
    public bool Required { get; init; }
}

public sealed record PropertyDto
{
    public required string Name { get; init; }
    public required string LabelKey { get; init; }
    public required string Kind { get; init; }
    public bool Required { get; init; }
    public string? Default { get; init; }
    public string? HelpKey { get; init; }
    public string? Placeholder { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public PropertyConditionDto? VisibleWhen { get; init; }
}

public sealed record PropertyConditionDto(string Property, IReadOnlyList<string> Values);

// ------------------------------------------------------------------------------------------------

/// <summary>The graph, as the canvas holds it and sends it back.</summary>
public sealed record GraphDto
{
    public required IReadOnlyList<GraphNodeDto> Nodes { get; init; }
    public required IReadOnlyList<GraphEdgeDto> Edges { get; init; }

    /// <summary>Viewport and grid. Not the test — panning the canvas is not a change to it.</summary>
    public string? CanvasJson { get; init; }
}

public sealed record GraphNodeDto
{
    /// <summary>
    /// The canvas's own id, which for a node it has just created is not a database id yet.
    ///
    /// Sent as text for that reason: the browser makes one when a node is dropped, and the server
    /// decides whether it becomes a row or matches one.
    /// </summary>
    public required string Id { get; init; }

    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Note { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public string? ParentId { get; init; }
    public bool Disabled { get; init; }
    public IReadOnlyDictionary<string, string?> Properties { get; init; } =
        new Dictionary<string, string?>();
}

public sealed record GraphEdgeDto
{
    public required string Id { get; init; }
    public required string FromId { get; init; }
    public required string FromPort { get; init; }
    public required string ToId { get; init; }
    public required string ToPort { get; init; }
    public string? Label { get; init; }
}

/// <summary>What the validator said, in a shape the canvas can put on the right node.</summary>
public sealed record GraphProblemDto
{
    public required string Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? NodeId { get; init; }
    public string? Port { get; init; }
    public string? Property { get; init; }
}

public sealed record SaveGraphResult
{
    public required Guid VersionId { get; init; }
    public required int Number { get; init; }
    public required bool IsValid { get; init; }
    public required IReadOnlyList<GraphProblemDto> Problems { get; init; }

    /// <summary>
    /// The ids the server assigned, keyed by the canvas's temporary ones.
    ///
    /// Sent back so the canvas can stop treating a saved node as new without reloading and losing
    /// the viewport, the selection and the undo history.
    /// </summary>
    public required IReadOnlyDictionary<string, Guid> NodeIds { get; init; }
}
