namespace GroundedConcierge.Domain;

/// <summary>
/// The hotel's notion of "today" — one clock, one time zone, for every quote,
/// the parser's relative dates ("tomorrow", "next weekend") and the
/// earliest-stay search.
///
/// <para>
/// A server's <c>DateTime.Today</c> is the SERVER's local date; on a host running
/// in UTC a guest's idea of "tonight" and the rate calendar would disagree late
/// in the evening. Everything asks this clock instead, which answers in the
/// property's own zone. Nothing in the domain reads the machine clock directly —
/// an architecture test fails the build if it ever does.
/// </para>
/// </summary>
public interface IHotelClock
{
    /// <summary>The current date in the property's time zone (or the pinned date).</summary>
    DateOnly Today { get; }

    /// <summary>The current instant in the property's time zone.</summary>
    DateTimeOffset Now { get; }

    /// <summary>The property's time zone.</summary>
    TimeZoneInfo Zone { get; }

    /// <summary>True when the date is pinned rather than read from a time source.</summary>
    bool IsPinned { get; }
}

/// <summary>A clock pinned to one date — for tests, rehearsals and the scripted demo.</summary>
public sealed class FixedHotelClock : IHotelClock
{
    public FixedHotelClock(DateOnly today, TimeZoneInfo? zone = null)
    {
        Today = today;
        Zone = zone ?? HotelTimeZone.Resolve(HotelTimeZone.DefaultId);
    }

    public DateOnly Today { get; }

    public TimeZoneInfo Zone { get; }

    public bool IsPinned => true;

    /// <summary>Noon of the pinned day, in the property's zone.</summary>
    public DateTimeOffset Now
    {
        get
        {
            var local = Today.ToDateTime(new TimeOnly(12, 0));
            return new DateTimeOffset(local, Zone.GetUtcOffset(local));
        }
    }
}

/// <summary>
/// The real clock: a <see cref="TimeProvider"/> read in the property's zone.
/// Injecting the provider is what lets a test say "it is 22:30 UTC" and check
/// that the property already lives in tomorrow.
/// </summary>
public sealed class SystemHotelClock : IHotelClock
{
    private readonly TimeProvider _time;

    public SystemHotelClock(TimeProvider time, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        Zone = zone ?? HotelTimeZone.Resolve(HotelTimeZone.DefaultId);
    }

    public TimeZoneInfo Zone { get; }

    public bool IsPinned => false;

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(_time.GetUtcNow(), Zone);

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}

/// <summary>Resolves the property's time zone on every operating system.</summary>
public static class HotelTimeZone
{
    /// <summary>The fictional property is in Istanbul.</summary>
    public const string DefaultId = "Europe/Istanbul";

    /// <summary>
    /// IANA id first, the Windows id as the fallback, and UTC+3 as the last
    /// resort — Türkiye has had no daylight saving since 2016, so a fixed offset
    /// is not an approximation.
    /// </summary>
    public static TimeZoneInfo Resolve(string? id)
    {
        foreach (var candidate in new[] { id, DefaultId, "Turkey Standard Time" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Istanbul-fixed", TimeSpan.FromHours(3), "Istanbul (UTC+3)", "Istanbul (UTC+3)");
    }
}
