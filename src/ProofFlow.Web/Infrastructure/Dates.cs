using System.Globalization;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Every moment shown to a person passes through here.
///
/// Three things were wrong before this existed, and all three are the kind that only the affected
/// reader notices.
///
/// **The calendar.** A Persian interface showing 2026-08-07 is asking its reader to convert in
/// their head. .NET already knows: <c>fa-IR</c> carries <see cref="PersianCalendar"/>, so the same
/// instant formats as 1405/05/16 with no extra dependency and no arithmetic of ours to get wrong.
///
/// **The time zone.** The old code called <c>ToLocalTime()</c>, which is the *server's* zone. On a
/// hosted installation that is UTC, so a run at 09:00 Tehran read as 05:30 to the person who
/// started it — and there is no worse place for that than an audit log, where the whole point is
/// establishing when something happened.
///
/// **The scale.** "2026-08-06 18:53" for something that happened four minutes ago makes the reader
/// do subtraction. Recent things get a relative phrase; anything older than a week gets a date,
/// because past that the phrase stops helping.
///
/// The machine-readable value never moves: every rendered timestamp carries an ISO-8601
/// <c>datetime</c> attribute in UTC, so copying one into a bug report or a support ticket produces
/// something unambiguous regardless of what the reader saw.
/// </summary>
public sealed class Dates(IHttpContextAccessor accessor, IStringLocalizer localizer, IClock clock)
{
    /// <summary>
    /// Written once by the browser, read on every request.
    ///
    /// The first page load of a session has no cookie yet and falls back to UTC. That is a real
    /// gap and it is the right trade: the alternative is guessing a zone from an IP address, which
    /// is wrong for exactly the people who travel and impossible to notice when it is.
    /// </summary>
    public const string TimeZoneCookie = "proofflow.tz";

    private TimeZoneInfo? _viewer;

    public TimeZoneInfo Viewer => _viewer ??= ResolveViewerZone();

    /// <summary>UTC, ISO-8601, round-trippable. What goes in <c>datetime</c> and in every export.</summary>
    public static string Iso(DateTimeOffset when) =>
        when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A full date and time in the reader's calendar and zone.</summary>
    public string Absolute(DateTimeOffset when)
    {
        var local = TimeZoneInfo.ConvertTime(when, Viewer);
        return local.ToString(IsPersian ? "yyyy/MM/dd HH:mm" : "yyyy-MM-dd HH:mm", Format);
    }

    /// <summary>Date only — for grouping headers and anywhere the clock time is noise.</summary>
    public string AbsoluteDate(DateTimeOffset when)
    {
        var local = TimeZoneInfo.ConvertTime(when, Viewer);
        return local.ToString("d MMMM yyyy", Format);
    }

    /// <summary>
    /// "just now", "12 minutes ago", … falling back to a date once the phrase stops being useful.
    /// </summary>
    public string Relative(DateTimeOffset when)
    {
        var span = RelativeTime.From(when, clock.UtcNow);

        return span.Unit switch
        {
            RelativeUnit.JustNow => localizer["time.justNow"].Value,
            RelativeUnit.Minutes => localizer["time.minutesAgo", span.Value].Value,
            RelativeUnit.Hours => localizer["time.hoursAgo", span.Value].Value,
            RelativeUnit.Days => localizer["time.daysAgo", span.Value].Value,
            _ => Absolute(when),
        };
    }

    /// <summary>
    /// The name of the zone the reader is being shown times in.
    ///
    /// Surfaced beside a timestamp when the zone is not the reader's own — otherwise "09:00" is a
    /// number with a missing half.
    /// </summary>
    public string ZoneLabel => Viewer.Id;

    /// <summary>
    /// True when no zone was reported and UTC is being assumed — the first page load of a session,
    /// or a browser that blocks the cookie. Worth saying beside a timestamp rather than letting a
    /// reader believe a time that is up to half a day out.
    /// </summary>
    public bool ZoneIsAssumed => string.IsNullOrWhiteSpace(ReadCookie());

    private bool IsPersian =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fa", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The culture used for formatting.
    ///
    /// Constructed as the specific <c>fa-IR</c> rather than reusing <c>CurrentCulture</c>, which is
    /// the neutral <c>fa</c> the request localizer selected. A neutral culture's calendar is not
    /// something to rely on, and the whole point of this class is which calendar gets used.
    /// </summary>
    private static CultureInfo Calendar { get; } = BuildPersianCulture();

    private static CultureInfo BuildPersianCulture()
    {
        var culture = new CultureInfo("fa-IR");

        // Latin digits, deliberately, matching the digit policy in base.css: a duration or a run
        // date written in Persian numerals cannot be pasted into a ticket, and the technical
        // reader of an audit log is comparing these against timestamps from elsewhere.
        culture.NumberFormat.DigitSubstitution = DigitShapes.None;
        return culture;
    }

    private CultureInfo Format => IsPersian ? Calendar : CultureInfo.GetCultureInfo("en-GB");

    private TimeZoneInfo ResolveViewerZone()
    {
        var id = ReadCookie();
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;

        try
        {
            // IANA identifiers resolve on Windows too from .NET 6 onwards, so the browser's own
            // "Asia/Tehran" needs no translation table of ours.
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A cookie is user input. An unknown zone falls back rather than throwing — a bad
            // value in a cookie must not be able to take down every page that shows a date.
            return TimeZoneInfo.Utc;
        }
    }

    private string? ReadCookie() =>
        accessor.HttpContext?.Request.Cookies.TryGetValue(TimeZoneCookie, out var value) == true
            ? Uri.UnescapeDataString(value)
            : null;
}

/// <summary>
/// What <c>Pieces/_Timestamp</c> renders. <paramref name="PreferRelative"/> is off for anything
/// being compared against another timestamp — in a table of run start times, "3 hours ago" beside
/// "4 hours ago" is harder to read than two clock times.
/// </summary>
public sealed record TimestampView(DateTimeOffset When, bool PreferRelative = true);
