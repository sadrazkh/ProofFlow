using FluentAssertions;
using ProofFlow.Application.Common;

namespace ProofFlow.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("Orders API", "orders-api")]
    [InlineData("  Catalog   Service  ", "catalog-service")]
    [InlineData("v2 / Checkout", "v2-checkout")]
    [InlineData("Café Ordering", "cafe-ordering")]
    [InlineData("UPPER lower 123", "upper-lower-123")]
    [InlineData("---dashes---", "dashes")]
    public void Turns_a_name_into_something_a_url_can_hold(string input, string expected) =>
        Slug.From(input).Should().Be(expected);

    [Fact]
    public void A_Persian_name_still_gets_a_usable_slug()
    {
        // Nothing survives the ASCII filter here, and returning an empty string would make the
        // project unreachable. The fallback is a deterministic hash so the same name always maps
        // to the same slug — re-importing an export must not create a second project.
        var first = Slug.From("سامانه سفارش‌ها", "project");
        var second = Slug.From("سامانه سفارش‌ها", "project");

        first.Should().StartWith("project-");
        first.Should().Be(second);
        first.Should().MatchRegex("^project-[0-9a-f]{8}$");
    }

    [Fact]
    public void A_mixed_name_keeps_the_part_that_survives()
    {
        Slug.From("API سفارش‌ها v2").Should().Be("api-v2");
    }

    [Fact]
    public void Long_names_are_cut_without_a_trailing_dash()
    {
        var slug = Slug.From(new string('a', 40) + " " + new string('b', 40));

        slug.Length.Should().BeLessThanOrEqualTo(60);
        slug.Should().NotEndWith("-");
    }

    [Fact]
    public void Uniqueness_appends_a_counter()
    {
        Slug.Unique("orders-api", ["orders-api", "orders-api-2"]).Should().Be("orders-api-3");
        Slug.Unique("orders-api", []).Should().Be("orders-api");
    }

    [Fact]
    public void Uniqueness_ignores_case_when_comparing()
    {
        // The database index is case-insensitive on PostgreSQL under a citext-like collation and
        // case-sensitive on SQLite. Deciding here rather than there keeps both consistent.
        Slug.Unique("orders", ["ORDERS"]).Should().Be("orders-2");
    }
}
