using Cronos;

namespace ProofFlow.Infrastructure.Scheduling;

/// <summary>
/// Reading a cron expression, and saying when it next fires.
///
/// Its own class because two things about it are easy to get wrong and expensive to get wrong
/// quietly. The first is the time zone: "every day at six" means six where the team is, and a
/// schedule that silently moves by an hour twice a year is one nobody trusts again. The second is
/// what happens to an occurrence that falls in the hour a clock skips — Cronos answers that, and
/// this is the only place that has to know it does.
/// </summary>
public static class CronSchedule
{
    /// <summary>
    /// Whether an expression can be read, and why not when it cannot.
    ///
    /// Returned rather than thrown: a person typing into a box gets the answer as they type, and an
    /// exception per keystroke is a stack trace per keystroke.
    /// </summary>
    public static string? Problem(string? cron, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(cron)) return "cron.empty";

        try
        {
            CronExpression.Parse(cron.Trim(), Format(cron));
        }
        catch (CronFormatException)
        {
            return "cron.unreadable";
        }

        return Zone(timeZoneId) is null ? "cron.zone" : null;
    }

    /// <summary>
    /// When it next fires strictly after <paramref name="after"/>.
    ///
    /// Null when the expression cannot be read or has no future occurrence — which is a real answer
    /// for something like <c>0 0 30 2 *</c>, and better said than approximated.
    /// </summary>
    public static DateTimeOffset? Next(string? cron, string? timeZoneId, DateTimeOffset after)
    {
        if (string.IsNullOrWhiteSpace(cron)) return null;

        var zone = Zone(timeZoneId);
        if (zone is null) return null;

        try
        {
            return CronExpression.Parse(cron.Trim(), Format(cron))
                .GetNextOccurrence(after, zone, inclusive: false);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Six fields means the first one is seconds.
    ///
    /// Cronos needs to be told, and guessing from the count is what everybody who writes cron
    /// expects — five fields is the classic form and six is the one with seconds.
    /// </summary>
    private static CronFormat Format(string cron) =>
        cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6
            ? CronFormat.IncludeSeconds
            : CronFormat.Standard;

    /// <summary>
    /// The zone, by IANA identifier.
    ///
    /// <c>FindSystemTimeZoneById</c> takes IANA names on every platform from .NET 6 on, including
    /// Windows, so "Asia/Tehran" is written once and works everywhere — which matters because the
    /// identifier is stored and the database outlives the machine.
    /// </summary>
    public static TimeZoneInfo? Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// A few well-known expressions, named.
    ///
    /// Not a general cron-to-English translator — those get the common cases right and the
    /// interesting ones subtly wrong, and a schedule described wrongly is worse than one described
    /// not at all. What is not recognised is shown as the expression itself, which is honest.
    /// </summary>
    public static string? Describe(string? cron)
    {
        var text = cron?.Trim();

        return text switch
        {
            "* * * * *" => "cron.everyMinute",
            "*/5 * * * *" => "cron.everyFiveMinutes",
            "*/15 * * * *" => "cron.everyQuarterHour",
            "*/30 * * * *" => "cron.everyHalfHour",
            "0 * * * *" => "cron.hourly",
            "0 0 * * *" => "cron.midnight",
            "0 6 * * *" => "cron.everyMorning",
            "0 6 * * 1-5" => "cron.everyWeekdayMorning",
            "0 0 * * 0" => "cron.weekly",
            "0 0 1 * *" => "cron.monthly",
            _ => null,
        };
    }

    /// <summary>The presets the interface offers, in the order somebody is likely to want them.</summary>
    public static readonly IReadOnlyList<string> Presets =
    [
        "*/15 * * * *",
        "0 * * * *",
        "0 6 * * *",
        "0 6 * * 1-5",
        "0 0 * * *",
        "0 0 * * 0",
    ];
}
