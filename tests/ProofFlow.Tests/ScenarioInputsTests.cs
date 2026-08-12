using FluentAssertions;
using ProofFlow.Contracts.Scenarios;

namespace ProofFlow.Tests;

/// <summary>
/// Settling what a run was given.
///
/// One place decides this, because four callers ask — the form, the run controller, the API a build
/// agent posts to, and the packer that sends a job to an agent. A default applied in three of them
/// and forgotten in the fourth is a test that only fails from a pipeline.
/// </summary>
public class ScenarioInputsTests
{
    private static readonly ScenarioInputDto Order =
        new() { Name = "orderId", Default = "1", Required = true };

    private static readonly ScenarioInputDto Page =
        new() { Name = "page", Default = "25" };

    [Fact]
    public void A_supplied_value_wins_over_the_default()
    {
        var settled = ScenarioInputs.Settle([Order], new Dictionary<string, string?> { ["orderId"] = "8812" });

        settled["orderId"].Should().Be("8812");
    }

    [Fact]
    public void An_empty_box_means_the_default_rather_than_an_empty_string()
    {
        // Somebody who clears a field and presses Run means "the usual one", not "send nothing".
        var settled = ScenarioInputs.Settle([Page], new Dictionary<string, string?> { ["page"] = "  " });

        settled["page"].Should().Be("25");
    }

    [Fact]
    public void Anything_not_defined_is_ignored()
    {
        // A pipeline that sends a stale key from an older version of the test should not be able to
        // introduce a reference the scenario never declared.
        var settled = ScenarioInputs.Settle([Page], new Dictionary<string, string?> { ["nonsense"] = "x" });

        settled.Should().ContainKey("page").And.NotContainKey("nonsense");
    }

    [Fact]
    public void A_required_input_with_no_value_anywhere_is_refused_before_the_run()
    {
        var required = new ScenarioInputDto { Name = "customer", Required = true };

        var settled = ScenarioInputs.Settle([required], null);

        ScenarioInputs.Missing([required], settled).Should().Equal("customer");
    }

    [Fact]
    public void A_required_input_with_a_default_is_not_missing()
    {
        var settled = ScenarioInputs.Settle([Order], null);

        ScenarioInputs.Missing([Order], settled).Should().BeEmpty();
        settled["orderId"].Should().Be("1");
    }

    [Fact]
    public void Definitions_survive_a_round_trip_and_junk_does_not_throw()
    {
        var written = ScenarioInputs.Write([Order, Page]);

        ScenarioInputs.Read(written).Should().HaveCount(2);

        // A column somebody edited by hand should cost a form, not a page.
        ScenarioInputs.Read("{not json").Should().BeEmpty();
        ScenarioInputs.Read(null).Should().BeEmpty();
    }
}
