using FluentAssertions;
using ProofFlow.TestEngine.Running;

namespace ProofFlow.Tests;

/// <summary>
/// The small language every branch, loop and poll asks a question in.
///
/// Two things are being defended. That it stays small — a node evaluating arbitrary code would be a
/// way to run arbitrary code on whatever runs somebody else's tests. And that it agrees with what a
/// person writing a condition expects, which is not what a diff engine expects: here "200" and 200
/// are the same, because a human typed one of them.
/// </summary>
public class ExpressionTests
{
    [Theory]
    [InlineData("200 == 200", true)]
    [InlineData("200 == 201", false)]
    [InlineData("\"200\" == 200", true)]
    [InlineData("200 != 404", true)]
    [InlineData("3 > 2", true)]
    [InlineData("2 > 3", false)]
    [InlineData("2 >= 2", true)]
    [InlineData("2 <= 1", false)]
    [InlineData("ready == ready", true)]
    [InlineData("\"ready\" == ready", true)]
    public void Comparisons_read_the_way_somebody_would_say_them(string expression, bool expected)
    {
        Expressions.IsTrue(expression).Should().Be(expected);
    }

    [Fact]
    public void A_greater_than_or_equal_is_not_read_as_a_greater_than()
    {
        // ">=" contains ">", so a naive scan splits "2 >= 3" at the ">" and compares 2 with "= 3".
        Expressions.IsTrue("2 >= 3").Should().BeFalse();
        Expressions.IsTrue("3 >= 3").Should().BeTrue();
    }

    [Fact]
    public void An_operator_inside_quotes_is_part_of_the_text()
    {
        // Otherwise this splits into `"a ` and ` b"` and compares two fragments.
        Expressions.IsTrue("\"a == b\" == \"a == b\"").Should().BeTrue();
    }

    [Theory]
    [InlineData("\"abcdef\" contains cde", true)]
    [InlineData("\"abcdef\" contains xyz", false)]
    [InlineData("\"abcdef\" starts with abc", true)]
    [InlineData("\"abcdef\" ends with def", true)]
    public void Text_operators_do_what_they_say(string expression, bool expected)
    {
        Expressions.IsTrue(expression).Should().Be(expected);
    }

    [Theory]
    [InlineData("[1,2,3] | count == 3", true)]
    [InlineData("[1,2,3] | count > 0", true)]
    [InlineData("[] | count > 0", false)]
    [InlineData("\"hello\" | length == 5", true)]
    [InlineData("\"HeLLo\" | lower == hello", true)]
    public void A_value_can_be_piped_through_a_verb(string expression, bool expected)
    {
        Expressions.IsTrue(expression).Should().Be(expected);
    }

    [Theory]
    [InlineData("\"\" is empty", true)]
    [InlineData("\"x\" is empty", false)]
    [InlineData("\"x\" is not empty", true)]
    [InlineData("[] is empty", true)]
    public void Emptiness_is_asked_about_in_words(string expression, bool expected)
    {
        Expressions.IsTrue(expression).Should().Be(expected);
    }

    [Fact]
    public void An_unresolved_reference_is_false_rather_than_an_error()
    {
        // A condition on a step that did not run should send the run down the "no" branch, not stop
        // it: the branch is how a scenario copes with that in the first place.
        Expressions.IsTrue("").Should().BeFalse();
        Expressions.IsTrue("   ").Should().BeFalse();
    }

    [Fact]
    public void A_bare_value_evaluates_to_itself()
    {
        // Which is what makes this double as the "work something out" node.
        Expressions.Evaluate("42")!.GetValue<double>().Should().Be(42);
        Expressions.Evaluate("\"hello\"")!.GetValue<string>().Should().Be("hello");
        Expressions.Evaluate("[1,2,3] | count")!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void Nothing_here_evaluates_code()
    {
        // Not a proof, but a statement of intent that fails loudly if somebody adds an eval: these
        // are not expressions this language has any way to run.
        Expressions.Evaluate("1+1")!.ToString().Should().Be("1+1");
        Expressions.Evaluate("process.exit()")!.ToString().Should().Be("process.exit()");
    }
}
