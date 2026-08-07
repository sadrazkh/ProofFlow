namespace ProofFlow.Application.Common;

/// <summary>
/// How long ago something happened, as a decision rather than a sentence.
///
/// The rounding rules are the whole of it, and they are the part worth testing: 59 minutes is
/// "59 minutes ago" and 61 minutes is "an hour ago", a run that finished 6 days back is still
/// counted in days and one from 8 days back gets a real date. Getting those boundaries wrong is
/// not a formatting bug — "0 minutes ago" and "1 days ago" are the two ways relative time
/// announces that nobody tested it.
///
/// No strings here on purpose. Turning this into words needs a localizer, which needs a request;
/// keeping the arithmetic separate is what lets the boundaries be tested without one.
/// </summary>
public static class RelativeTime
{
    /// <summary>
    /// Beyond a week, "13 days ago" stops helping and a date starts. Someone comparing a run to
    /// last month's wants to know it was the 3rd, not that it was 34 days ago.
    /// </summary>
    public static readonly TimeSpan AbsoluteAfter = TimeSpan.FromDays(7);

    public static RelativeSpan From(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;

        // A timestamp in the future is a clock-skew artefact, not a prediction. Reading it as
        // "in 3 minutes" invites someone to debug their own machine's clock; "just now" is both
        // closer to the truth and less distracting.
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (elapsed >= AbsoluteAfter) return new RelativeSpan(RelativeUnit.Absolute, 0);

        if (elapsed.TotalMinutes < 1) return new RelativeSpan(RelativeUnit.JustNow, 0);

        if (elapsed.TotalHours < 1)
            return new RelativeSpan(RelativeUnit.Minutes, (int)elapsed.TotalMinutes);

        if (elapsed.TotalDays < 1)
            return new RelativeSpan(RelativeUnit.Hours, (int)elapsed.TotalHours);

        return new RelativeSpan(RelativeUnit.Days, (int)elapsed.TotalDays);
    }
}

public enum RelativeUnit
{
    /// <summary>Too long ago to count. Show the date instead.</summary>
    Absolute,
    JustNow,
    Minutes,
    Hours,
    Days,
}

public readonly record struct RelativeSpan(RelativeUnit Unit, int Value);
