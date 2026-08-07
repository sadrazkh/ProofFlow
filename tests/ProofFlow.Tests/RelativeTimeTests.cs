using FluentAssertions;
using ProofFlow.Application.Common;

namespace ProofFlow.Tests;

/// <summary>
/// The boundaries, which is the only part of relative time that goes wrong.
///
/// "0 minutes ago" and "1 days ago" are the two ways this feature announces that nobody tested it,
/// and both come from an off-by-one at a threshold rather than from the formatting.
/// </summary>
public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static RelativeSpan Ago(TimeSpan elapsed) => RelativeTime.From(Now - elapsed, Now);

    [Fact]
    public void Under_a_minute_is_just_now() =>
        Ago(TimeSpan.FromSeconds(59)).Unit.Should().Be(RelativeUnit.JustNow);

    [Fact]
    public void Exactly_a_minute_starts_counting_minutes()
    {
        // The alternative is "0 minutes ago", which is what a floor without a floor-check produces.
        var span = Ago(TimeSpan.FromMinutes(1));

        span.Unit.Should().Be(RelativeUnit.Minutes);
        span.Value.Should().Be(1);
    }

    [Fact]
    public void Fifty_nine_minutes_is_still_minutes() =>
        Ago(TimeSpan.FromMinutes(59)).Should().Be(new RelativeSpan(RelativeUnit.Minutes, 59));

    [Fact]
    public void An_hour_becomes_hours() =>
        Ago(TimeSpan.FromMinutes(60)).Should().Be(new RelativeSpan(RelativeUnit.Hours, 1));

    [Fact]
    public void Ninety_minutes_rounds_down_to_one_hour() =>
        // Down, not to the nearest: "2 hours ago" for something 90 minutes old overstates the gap,
        // and for a run that just failed that difference matters.
        Ago(TimeSpan.FromMinutes(90)).Should().Be(new RelativeSpan(RelativeUnit.Hours, 1));

    [Fact]
    public void Twenty_three_hours_is_still_hours() =>
        Ago(TimeSpan.FromHours(23)).Should().Be(new RelativeSpan(RelativeUnit.Hours, 23));

    [Fact]
    public void A_day_becomes_days() =>
        Ago(TimeSpan.FromHours(24)).Should().Be(new RelativeSpan(RelativeUnit.Days, 1));

    [Fact]
    public void Six_days_is_still_relative() =>
        Ago(TimeSpan.FromDays(6)).Unit.Should().Be(RelativeUnit.Days);

    [Fact]
    public void Seven_days_switches_to_a_date() =>
        // Past a week the phrase stops helping: nobody reads "13 days ago" and knows the date.
        Ago(TimeSpan.FromDays(7)).Unit.Should().Be(RelativeUnit.Absolute);

    [Fact]
    public void A_year_ago_is_a_date() =>
        Ago(TimeSpan.FromDays(365)).Unit.Should().Be(RelativeUnit.Absolute);

    [Fact]
    public void A_future_timestamp_reads_as_just_now()
    {
        // Clock skew between a runner and the panel produces these. "In 3 minutes" invites someone
        // to debug their own machine's clock instead of the failure they came to look at.
        RelativeTime.From(Now.AddMinutes(5), Now).Unit.Should().Be(RelativeUnit.JustNow);
    }

    [Fact]
    public void The_same_instant_reads_as_just_now() =>
        RelativeTime.From(Now, Now).Unit.Should().Be(RelativeUnit.JustNow);
}
