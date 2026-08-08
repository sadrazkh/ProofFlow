using FluentAssertions;
using ProofFlow.Infrastructure.Portability;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Tests;

/// <summary>
/// Every template is a graph that would run.
///
/// This is the test that makes a template gallery worth having. A template somebody chooses and
/// cannot run teaches them the product is broken before they have built anything of their own — and
/// nothing else would catch it, because a graph with a missing edge or a mistyped node key looks
/// perfectly reasonable in a card.
/// </summary>
public class TemplateCatalogueTests
{
    [Fact]
    public void There_are_twelve_of_them_and_their_keys_are_distinct()
    {
        TemplateCatalogue.All.Should().HaveCount(12);
        TemplateCatalogue.All.Select(template => template.Key).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The only thing a template is allowed to be missing is the thing the gallery tells you to
    /// choose — a data set, a baseline. Everything else has to be right, because a template that
    /// will not run teaches somebody the product is broken before they have built anything.
    /// </summary>
    [Theory]
    [MemberData(nameof(Templates))]
    public void It_passes_the_same_validator_the_canvas_uses(string key)
    {
        var template = TemplateCatalogue.Find(key)!;

        var nodes = template.Graph.Nodes
            .Select(node => new GraphNode(
                node.Id, node.Key, node.Name, node.Properties, node.ParentId, node.Disabled))
            .ToList();

        var edges = template.Graph.Edges
            .Select(edge => new GraphEdge(edge.FromId, edge.FromPort, edge.ToId, edge.ToPort))
            .ToList();

        var problems = GraphValidator.Validate(new Graph(nodes, edges));

        var errors = problems
            .Where(problem => problem.Severity == GraphSeverity.Error)
            .Where(problem => problem.Property is not ("dataSet" or "baseline"))
            .Select(problem => $"{problem.Code} on {problem.NodeId} {problem.Property}")
            .ToArray();

        errors.Should().BeEmpty("«{0}» has to be runnable: {1}", key, string.Join("; ", errors));

        // And a template that is missing one of those has to be the one that says so.
        var waiting = problems.Any(problem =>
            problem.Severity == GraphSeverity.Error
            && problem.Property is "dataSet" or "baseline");

        waiting.Should().Be(template.NeedsChoosing,
            "«{0}» must say up front whether something has to be chosen", key);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_node_type_it_names_exists(string key)
    {
        var template = TemplateCatalogue.Find(key)!;

        var unknown = template.Graph.Nodes
            .Select(node => node.Key)
            .Where(node => NodeCatalogue.All.All(spec => spec.Key != node))
            .Distinct()
            .ToArray();

        unknown.Should().BeEmpty("«{0}» names: {1}", key, string.Join(", ", unknown));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_edge_joins_two_nodes_that_are_in_it(string key)
    {
        // A dangling edge does not stop the validator, and it does stop the scenario: the step it
        // was meant to reach simply never runs.
        var template = TemplateCatalogue.Find(key)!;
        var ids = template.Graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in template.Graph.Edges)
        {
            ids.Should().Contain(edge.FromId, "«{0}» has an edge from a node that is not there", key);
            ids.Should().Contain(edge.ToId, "«{0}» has an edge to a node that is not there", key);
        }
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Nothing_is_stranded(string key)
    {
        // Every node except the start has something leading into it, or is inside a container that
        // does. A step nobody can reach is a step that never runs, and a template is supposed to be
        // the thing somebody trusts.
        var template = TemplateCatalogue.Find(key)!;

        var reached = template.Graph.Edges
            .Where(edge => edge.ToPort == "in")
            .Select(edge => edge.ToId)
            .ToHashSet(StringComparer.Ordinal);

        var stranded = template.Graph.Nodes
            .Where(node => node.Key != "core.start")
            .Where(node => !reached.Contains(node.Id))
            .Where(node => node.ParentId is null || !FirstInside(template, node.ParentId, node.Id))
            .Select(node => node.Id)
            .ToArray();

        stranded.Should().BeEmpty("«{0}» cannot reach: {1}", key, string.Join(", ", stranded));

        static bool FirstInside(ScenarioTemplate template, string parent, string id) =>
            template.Graph.Nodes.First(node => node.ParentId == parent).Id == id;
    }

    [Fact]
    public void Every_template_says_what_it_needs_before_it_will_run()
    {
        // Two of them point at something that has to be chosen — a data set, a baseline — and the
        // gallery has to be able to say so rather than handing somebody a scenario that fails.
        TemplateCatalogue.Find("dataDriven")!.NeedsChoosing.Should().BeTrue();
        TemplateCatalogue.Find("baseline")!.NeedsChoosing.Should().BeTrue();

        TemplateCatalogue.Find("smoke")!.NeedsChoosing.Should().BeFalse();
        TemplateCatalogue.Find("crud")!.NeedsChoosing.Should().BeFalse();
    }

    [Fact]
    public void The_step_count_on_the_card_is_the_number_of_steps()
    {
        TemplateCatalogue.Find("smoke")!.Steps.Should().Be(3);
        TemplateCatalogue.Find("notFound")!.Steps.Should().Be(2);
    }

    public static TheoryData<string> Templates()
    {
        var data = new TheoryData<string>();

        foreach (var template in TemplateCatalogue.All) data.Add(template.Key);

        return data;
    }
}
