using FluentAssertions;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Tests;

/// <summary>
/// The catalogue as a contract.
///
/// Node keys are stored in every saved graph, so they are a promise: renaming one silently breaks
/// every scenario that used it, and the break shows up as "this step is of a kind ProofFlow does
/// not know" long after the rename.
/// </summary>
public class NodeCatalogueTests
{
    [Fact]
    public void Every_group_the_brief_names_has_nodes_in_it()
    {
        foreach (var group in Enum.GetValues<NodeGroup>())
        {
            NodeCatalogue.InGroup(group).Should().NotBeEmpty($"«{group}» is one of the five groups");
        }
    }

    [Fact]
    public void There_are_about_seventy_of_them()
    {
        // Not an exact number — the point is the coverage the brief asks for, and a node added on
        // purpose should not fail a test. A collapse to a handful should.
        NodeCatalogue.All.Should().HaveCountGreaterThanOrEqualTo(70);
    }

    [Fact]
    public void Keys_are_unique_and_namespaced()
    {
        NodeCatalogue.All.Select(spec => spec.Key).Should().OnlyHaveUniqueItems();

        // lowercase group, a dot, then camelCase — the shape every existing key already has, and
        // the one a new node has to match so the palette groups it and a reader can guess it.
        var wrong = NodeCatalogue.All
            .Where(spec => !System.Text.RegularExpressions.Regex.IsMatch(
                spec.Key, @"^[a-z]+\.[a-z][A-Za-z0-9]*$"))
            .Select(spec => spec.Key)
            .ToArray();

        wrong.Should().BeEmpty("keys are stored in saved graphs and read by people: {0}",
            string.Join(", ", wrong));
    }

    [Fact]
    public void Exactly_one_node_can_start_a_run()
    {
        NodeCatalogue.All.Count(spec => spec.IsStart).Should().Be(1);
        NodeCatalogue.Start.Inputs.Should().BeEmpty("nothing comes before the start");
    }

    [Fact]
    public void Port_names_are_unique_within_a_node()
    {
        foreach (var spec in NodeCatalogue.All)
        {
            spec.Inputs.Select(p => p.Name).Should().OnlyHaveUniqueItems($"on «{spec.Key}»");
            spec.Outputs.Select(p => p.Name).Should().OnlyHaveUniqueItems($"on «{spec.Key}»");
            spec.Properties.Select(p => p.Name).Should().OnlyHaveUniqueItems($"on «{spec.Key}»");
        }
    }

    [Fact]
    public void Control_ports_carry_no_type_and_data_ports_do()
    {
        foreach (var port in NodeCatalogue.All.SelectMany(spec => spec.Inputs.Concat(spec.Outputs)))
        {
            if (port.Kind == PortKind.Control) port.Type.Should().Be(DataType.None);
            else port.Type.Should().NotBe(DataType.None);
        }
    }

    [Fact]
    public void Every_choice_property_offers_options_and_a_default_that_is_one_of_them()
    {
        foreach (var spec in NodeCatalogue.All)
        {
            foreach (var property in spec.Properties.Where(p => p.Kind == PropertyKind.Choice))
            {
                property.Options.Should().NotBeEmpty($"«{spec.Key}.{property.Name}» is a choice");

                if (property.Default is not null)
                {
                    property.Options.Should().Contain(property.Default,
                        $"«{spec.Key}.{property.Name}» defaults to something it does not offer");
                }
            }
        }
    }

    [Fact]
    public void A_visibility_condition_names_a_property_that_exists()
    {
        // A condition on a misspelled property hides the field forever, and the field is usually a
        // required one — so the node becomes impossible to fill in correctly.
        foreach (var spec in NodeCatalogue.All)
        {
            foreach (var property in spec.Properties.Where(p => p.VisibleWhen is not null))
            {
                var condition = property.VisibleWhen!;

                var other = spec.Properties.FirstOrDefault(p => p.Name == condition.Property);
                other.Should().NotBeNull(
                    $"«{spec.Key}.{property.Name}» is shown when «{condition.Property}» has a value");

                if (other!.Kind == PropertyKind.Choice)
                {
                    condition.Values.Should().BeSubsetOf(other.Options,
                        $"«{spec.Key}.{property.Name}» waits for a value «{condition.Property}» never takes");
                }
            }
        }
    }

    [Fact]
    public void Every_credential_is_a_reference_and_never_a_text_box()
    {
        // A password typed into a text property would be stored in the graph, exported with it,
        // and visible in a screenshot of the canvas.
        var suspicious = NodeCatalogue.All
            .SelectMany(spec => spec.Properties.Select(p => (spec.Key, p)))
            .Where(pair => pair.p.Kind != PropertyKind.SecretRef)
            .Where(pair =>
                pair.p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                pair.p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                pair.p.Name.Equals("token", StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{pair.Key}.{pair.p.Name}")
            .ToArray();

        suspicious.Should().BeEmpty("credentials are named, never typed: {0}", string.Join(", ", suspicious));
    }

    [Fact]
    public void A_terminal_node_has_nowhere_to_go_and_a_start_has_nothing_before_it()
    {
        foreach (var spec in NodeCatalogue.All.Where(s => s.IsTerminal))
        {
            spec.Outputs.Should().BeEmpty($"«{spec.Key}» ends the run");
        }
    }

    [Fact]
    public void A_failure_port_is_always_an_output_and_always_control()
    {
        foreach (var spec in NodeCatalogue.All)
        {
            spec.Inputs.Should().NotContain(p => p.IsFailure, $"on «{spec.Key}»");

            foreach (var failure in spec.Outputs.Where(p => p.IsFailure))
            {
                failure.Kind.Should().Be(PortKind.Control, $"on «{spec.Key}»");
            }
        }
    }

    [Fact]
    public void Every_assertion_can_fail()
    {
        // An assertion with no failure path is a comment: the runner would have nowhere to go when
        // it did not hold, so it could only ever pass.
        var assertions = NodeCatalogue.InGroup(NodeGroup.Testing)
            .Where(spec => spec.Key.StartsWith("assert.", StringComparison.Ordinal));

        assertions.Should().NotBeEmpty();
        assertions.Should().OnlyContain(spec => spec.Outputs.Any(p => p.IsFailure));
    }

    [Theory]
    [InlineData(DataType.Any, DataType.Text, true)]
    [InlineData(DataType.Text, DataType.Any, true)]
    [InlineData(DataType.Text, DataType.Text, true)]
    [InlineData(DataType.Text, DataType.Number, false)]
    [InlineData(DataType.Number, DataType.Text, false)]
    [InlineData(DataType.Json, DataType.Response, false)]
    [InlineData(DataType.Any, DataType.Secret, true)]
    [InlineData(DataType.Secret, DataType.Text, false)]
    [InlineData(DataType.Secret, DataType.Any, false)]
    [InlineData(DataType.Secret, DataType.Secret, true)]
    [InlineData(DataType.None, DataType.Text, false)]
    public void Type_compatibility_is_narrow_on_purpose(DataType to, DataType from, bool accepted)
    {
        // A Number that quietly becomes Text is a test comparing "200" with 200 and passing.
        // A credential that accepts a plain string is a credential that can be forged from one.
        NodeCatalogue.Accepts(to, from).Should().Be(accepted);
    }

    [Fact]
    public void An_unknown_key_is_refused_by_name()
    {
        NodeCatalogue.Find("nope.nothing").Should().BeNull();

        var require = () => NodeCatalogue.Require("nope.nothing");
        require.Should().Throw<KeyNotFoundException>().WithMessage("*nope.nothing*");
    }
}
