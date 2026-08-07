using FluentAssertions;
using ProofFlow.Infrastructure.Data;

namespace ProofFlow.Tests;

/// <summary>
/// The parser that reads whatever somebody pasted.
///
/// Every test here is about a guess going wrong quietly. A one-per-line list read as a
/// comma-separated file produces a data set that runs perfectly against the wrong inputs, and
/// nothing about the run says so — the requests succeed, the responses are captured, and the
/// baseline records answers to questions nobody asked.
/// </summary>
public class PasteParserTests
{
    [Fact]
    public void Reads_a_plain_list_one_value_per_line()
    {
        var parsed = DataSetService.Parse("12345\n67890\n11111");

        parsed.Format.Should().Be("Lines");
        parsed.Columns.Should().BeEquivalentTo(["value"]);
        parsed.Rows.Should().HaveCount(3);
        parsed.Rows[0]["value"].Should().Be("12345");
    }

    [Fact]
    public void Does_not_read_a_list_with_one_stray_comma_as_a_table()
    {
        // "Smith, John" in a list of names. Read as CSV this becomes a two-column set where every
        // other row is short, and the header is somebody's name.
        var parsed = DataSetService.Parse("Alice\nSmith, John\nCharlie");

        parsed.Format.Should().Be("Lines");
        parsed.Rows.Should().HaveCount(3);
        parsed.Rows[1]["value"].Should().Be("Smith, John");
    }

    [Fact]
    public void Reads_a_comma_separated_table_with_a_header()
    {
        var parsed = DataSetService.Parse("studyId,region\n12345,eu\n67890,us");

        parsed.Format.Should().Be("Csv");
        parsed.Columns.Should().BeEquivalentTo(["studyId", "region"]);
        parsed.Rows.Should().HaveCount(2);
        parsed.Rows[1]["region"].Should().Be("us");
    }

    [Fact]
    public void Keeps_a_comma_that_is_inside_quotes()
    {
        var parsed = DataSetService.Parse("id,name\n1,\"Smith, John\"\n2,\"O\"\"Brien\"");

        parsed.Rows.Should().HaveCount(2);
        parsed.Rows[0]["name"].Should().Be("Smith, John");
        parsed.Rows[1]["name"].Should().Be("O\"Brien");
    }

    [Fact]
    public void Prefers_tabs_over_commas_when_both_appear()
    {
        // A spreadsheet paste is tab-separated and its text is full of commas.
        var parsed = DataSetService.Parse("id\tnote\n1\tone, two, three\n2\tfour, five");

        parsed.Format.Should().Be("Tsv");
        parsed.Columns.Should().BeEquivalentTo(["id", "note"]);
        parsed.Rows[0]["note"].Should().Be("one, two, three");
    }

    [Fact]
    public void Reports_a_row_with_the_wrong_number_of_values_rather_than_padding_it()
    {
        var parsed = DataSetService.Parse("a,b,c\n1,2,3\n4,5\n6,7,8");

        parsed.Rows.Should().HaveCount(2);
        parsed.Problems.Should().ContainSingle()
            .Which.Line.Should().Be(3);
        parsed.Problems[0].Reason.Should().Contain("2 values");
    }

    [Fact]
    public void Reads_an_array_of_objects()
    {
        var parsed = DataSetService.Parse("""[{"studyId":12345,"active":true},{"studyId":67890}]""");

        parsed.Format.Should().Be("Json");
        parsed.Columns.Should().BeEquivalentTo(["studyId", "active"]);
        parsed.Rows.Should().HaveCount(2);
        parsed.Rows[0]["studyId"].Should().Be("12345");
        parsed.Rows[0]["active"].Should().Be("true");
    }

    [Fact]
    public void Keeps_a_nested_value_as_json_rather_than_losing_it()
    {
        var parsed = DataSetService.Parse("""[{"id":1,"filter":{"region":"eu"}}]""");

        parsed.Rows[0]["filter"].Should().Be("""{"region":"eu"}""");
    }

    [Fact]
    public void Says_what_is_wrong_with_malformed_json_instead_of_falling_back_silently()
    {
        // Falling through to the line parser would produce a set whose rows are fragments of JSON.
        var parsed = DataSetService.Parse("""[{"id":1},""");

        parsed.Format.Should().Be("Json");
        parsed.Rows.Should().BeEmpty();
        parsed.Problems.Should().ContainSingle();
    }

    [Fact]
    public void An_honoured_preference_beats_the_guess()
    {
        // The reader looked at the preview and said it is a list. That has to win: the guess is a
        // guess, and the whole point of showing it is that it can be overruled.
        var parsed = DataSetService.Parse("a,b\n1,2", preferredFormat: "Lines");

        parsed.Format.Should().Be("Lines");
        parsed.Rows.Should().HaveCount(2);
        parsed.Rows[0]["value"].Should().Be("a,b");
    }

    [Fact]
    public void Empty_input_is_empty_rather_than_one_blank_row()
    {
        DataSetService.Parse("   \n\n  ").Rows.Should().BeEmpty();
        DataSetService.Parse(null).Format.Should().Be("Empty");
    }

    [Fact]
    public void Names_columns_a_header_left_blank()
    {
        var parsed = DataSetService.Parse("id,,region\n1,x,eu");

        parsed.Columns.Should().BeEquivalentTo(["id", "column2", "region"]);
        parsed.Rows[0]["column2"].Should().Be("x");
    }
}
