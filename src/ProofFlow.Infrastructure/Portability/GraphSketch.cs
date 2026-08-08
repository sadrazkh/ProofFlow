using System.Globalization;
using System.Text;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Infrastructure.Portability;

/// <summary>
/// A small drawing of a graph, made from the graph.
///
/// Generated rather than stored, which is the decision that matters: a picture of a template drifts
/// from the template the first time anybody edits one and nothing notices, because a screenshot has
/// no way of being wrong. This has one — it is built from the same nodes the scenario is built from,
/// so if the shape on the card is wrong, the scenario is wrong.
///
/// It is a sketch, not a canvas. Boxes and lines at ten per cent scale, coloured by node group,
/// with no text: at 200 pixels a label is a smudge, and the thing a reader is actually judging is
/// the shape — three in a row, or a loop with something inside it.
/// </summary>
public static class GraphSketch
{
    public const int Width = 240;
    public const int Height = 96;

    private const int Box = 14;
    private const int Padding = 10;

    /// <summary>
    /// The five group hues, taken from the same tokens the canvas colours its nodes with.
    ///
    /// Semantic tokens rather than colours, because this SVG is inlined into the page: it inherits
    /// the theme and turns over with it. A drawing with #6d28d9 baked into it is a drawing that
    /// glows in dark mode — and, worse, one that stops matching the canvas the moment a token
    /// changes.
    /// </summary>
    private static string Fill(NodeGroup group) => group switch
    {
        NodeGroup.Core => "var(--accent-ink)",
        NodeGroup.Flow => "var(--warn-ink)",
        NodeGroup.Testing => "var(--pass-ink)",
        NodeGroup.Data => "var(--running-ink)",
        NodeGroup.Auth => "var(--diff-type-ink)",
        _ => "var(--ink-subtle)",
    };

    public static string Draw(GraphDto graph)
    {
        if (graph.Nodes.Count == 0) return Empty();

        var minX = graph.Nodes.Min(node => node.X);
        var maxX = graph.Nodes.Max(node => node.X);
        var minY = graph.Nodes.Min(node => node.Y);
        var maxY = graph.Nodes.Max(node => node.Y);

        // Scaled to fit rather than drawn at a fixed zoom, so a scenario of three steps and one of
        // nine both fill the card.
        var scaleX = maxX > minX ? (Width - (2 * Padding) - Box) / (maxX - minX) : 0;
        var scaleY = maxY > minY ? (Height - (2 * Padding) - Box) / (maxY - minY) : 0;

        var places = graph.Nodes.ToDictionary(
            node => node.Id,
            node => (
                X: Padding + ((node.X - minX) * scaleX),
                Y: maxY > minY ? Padding + ((node.Y - minY) * scaleY) : (Height - Box) / 2.0),
            StringComparer.Ordinal);

        var svg = new StringBuilder(1024);

        svg.Append(CultureInfo.InvariantCulture,
            $"""<svg class="sketch" viewBox="0 0 {Width} {Height}" xmlns="http://www.w3.org/2000/svg" aria-hidden="true" focusable="false">""");

        // Edges first, so the boxes sit on top of them.
        foreach (var edge in graph.Edges)
        {
            if (!places.TryGetValue(edge.FromId, out var from)) continue;
            if (!places.TryGetValue(edge.ToId, out var to)) continue;

            var dashed = edge.ToPort != "in";

            svg.Append(CultureInfo.InvariantCulture,
                $"""<line x1="{from.X + Box:0.#}" y1="{from.Y + (Box / 2.0):0.#}" x2="{to.X:0.#}" y2="{to.Y + (Box / 2.0):0.#}" stroke="var(--canvas-edge)" stroke-width="1.5"{(dashed ? """ stroke-dasharray="3 3" """ : " ")}/>""");
        }

        foreach (var node in graph.Nodes)
        {
            var place = places[node.Id];
            var group = NodeCatalogue.All.FirstOrDefault(spec => spec.Key == node.Key)?.Group;

            svg.Append(CultureInfo.InvariantCulture,
                $"""<rect x="{place.X:0.#}" y="{place.Y:0.#}" width="{Box}" height="{Box}" rx="4" fill="{(group is { } known ? Fill(known) : "var(--ink-subtle)")}" />""");
        }

        svg.Append("</svg>");

        return svg.ToString();
    }

    /// <summary>A scenario with nothing in it yet. Drawn rather than left blank, so the card keeps its shape.</summary>
    private static string Empty() =>
        $"""<svg class="sketch" viewBox="0 0 {Width} {Height}" xmlns="http://www.w3.org/2000/svg" aria-hidden="true" focusable="false"><rect x="{(Width - Box) / 2}" y="{(Height - Box) / 2}" width="{Box}" height="{Box}" rx="4" fill="var(--ink-subtle)" opacity="0.4" /></svg>""";
}
