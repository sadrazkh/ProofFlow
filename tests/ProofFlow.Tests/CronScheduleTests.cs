using FluentAssertions;
using ProofFlow.Infrastructure.Scheduling;

namespace ProofFlow.Tests;

/// <summary>
/// When a schedule fires.
///
/// The two things worth testing are the two that fail quietly. A cron that cannot be read must say
/// so rather than never firing, and a zone must be honoured — "every day at six" that drifts by an
/// hour twice a year is a schedule nobody trusts again, and nobody would notice for months.
/// </summary>
public class CronScheduleTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0 6 * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 0 1 * *")]
    [InlineData("0 6 * * 1-5")]
    [InlineData("30 2 * * 0")]
    public void An_ordinary_expression_reads(string cron)
    {
        CronSchedule.Problem(cron, "UTC").Should().BeNull();
        CronSchedule.Next(cron, "UTC", Noon).Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cron")]
    [InlineData("0 6 * *")]
    [InlineData("99 * * * *")]
    public void A_nonsense_expression_is_refused_rather_than_silently_never_firing(string cron)
    {
        CronSchedule.Problem(cron, "UTC").Should().NotBeNull();
        CronSchedule.Next(cron, "UTC", Noon).Should().BeNull();
    }

    [Fact]
    public void A_zone_that_does_not_exist_is_a_problem_and_not_a_shrug()
    {
        CronSchedule.Problem("0 6 * * *", "Mars/Olympus").Should().Be("cron.zone");
        CronSchedule.Next("0 6 * * *", "Mars/Olympus", Noon).Should().BeNull();
    }

    [Fact]
    public void Six_oclock_means_six_oclock_where_the_team_is()
    {
        // The whole reason the zone is stored. Tehran is +03:30, so the same expression fires three
        // and a half hours before it would in UTC.
        var utc = CronSchedule.Next("0 6 * * *", "UTC", Noon);
        var tehran = CronSchedule.Next("0 6 * * *", "Asia/Tehran", Noon);

        utc.Should().NotBeNull();
        tehran.Should().NotBeNull();

        utc!.Value.UtcDateTime.Hour.Should().Be(6);

        // 06:00 in Tehran is 02:30 UTC — a different instant entirely, which is the point.
        tehran!.Value.UtcDateTime.Hour.Should().Be(2);
        tehran.Value.UtcDateTime.Minute.Should().Be(30);
    }

    [Fact]
    public void An_IANA_name_works_on_this_machine_whatever_it_is()
    {
        // .NET takes IANA identifiers on every platform from 6 onwards, including Windows. The
        // identifier is stored and the database outlives the machine, so this has to hold.
        CronSchedule.Zone("Asia/Tehran").Should().NotBeNull();
        CronSchedule.Zone("Europe/London").Should().NotBeNull();
        CronSchedule.Zone("America/New_York").Should().NotBeNull();
    }

    [Fact]
    public void The_next_occurrence_is_strictly_after_the_moment_asked_about()
    {
        // Exactly on the hour, asking for the next hourly occurrence. Inclusive would return the
        // same instant, and a scheduler that fires and then finds itself still due loops.
        var onTheHour = new DateTimeOffset(2026, 3, 10, 13, 0, 0, TimeSpan.Zero);
        var next = CronSchedule.Next("0 * * * *", "UTC", onTheHour);

        next.Should().Be(new DateTimeOffset(2026, 3, 10, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Six_fields_are_read_as_including_seconds()
    {
        CronSchedule.Problem("*/30 * * * * *", "UTC").Should().BeNull();

        var next = CronSchedule.Next("*/30 * * * * *", "UTC",
            new DateTimeOffset(2026, 3, 10, 13, 0, 5, TimeSpan.Zero));

        next!.Value.Second.Should().Be(30);
    }

    [Fact]
    public void Only_expressions_it_really_knows_are_described()
    {
        // A general cron-to-English translator gets the common cases right and the interesting ones
        // subtly wrong, and a schedule described wrongly is worse than one not described at all.
        CronSchedule.Describe("0 6 * * *").Should().Be("cron.everyMorning");
        CronSchedule.Describe("0 6 * * 1-5").Should().Be("cron.everyWeekdayMorning");

        CronSchedule.Describe("17 4 */3 * 2").Should().BeNull();
        CronSchedule.Describe(null).Should().BeNull();
    }

    [Fact]
    public void Every_preset_is_readable_and_named()
    {
        // A preset button that produced an unreadable expression, or one with no words on it, would
        // be worse than no button.
        foreach (var preset in CronSchedule.Presets)
        {
            CronSchedule.Problem(preset, "UTC").Should().BeNull($"«{preset}» is offered as a button");
            CronSchedule.Describe(preset).Should().NotBeNull($"«{preset}» is offered as a button");
        }
    }
}
