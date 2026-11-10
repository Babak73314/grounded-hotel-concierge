namespace GroundedConcierge.Domain;

/// <summary>
/// The demo's rate and availability MODEL — pure functions of (room, date), with
/// no state and no clock.
///
/// <para>
/// <b>Why the hash is written out by hand.</b> The obvious seed,
/// <c>HashCode.Combine(roomCode, dayNumber)</c>, is salted per PROCESS by .NET:
/// the picture would be stable inside one run and different after every restart,
/// which is not what "deterministic pseudo-availability" promises — the calendar
/// a guest saw yesterday and the quote the assistant gives today would disagree.
/// FNV-1a over the folded room code and the day number is the same value in
/// every process, on every machine, forever — and a test pins golden values so
/// a future edit cannot quietly change the demo's history.
/// </para>
///
/// <para>
/// These are MODELLED figures for a fictional property. A real deployment
/// replaces them with the PMS behind the same <see cref="HotelDataService"/>
/// surface; nothing here is an accounting fact, and nothing here is revenue.
/// </para>
/// </summary>
public static class HotelAvailabilityModel
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>
    /// Stable 32-bit FNV-1a over an upper-cased room code and a day number. Only
    /// ASCII letters and digits of the code take part, so "garden", "GARDEN" and
    /// "Garden " hash alike.
    /// </summary>
    public static uint StableHash(string roomCode, int dayNumber)
    {
        ArgumentNullException.ThrowIfNull(roomCode);

        var hash = FnvOffset;
        foreach (var ch in roomCode)
        {
            if (!char.IsAsciiLetterOrDigit(ch)) continue;
            hash ^= char.ToUpperInvariant(ch);
            hash *= FnvPrime;
        }

        // Four bytes of the day number, little-endian, so consecutive days do not
        // land on consecutive buckets.
        for (var i = 0; i < 4; i++)
        {
            hash ^= (byte)(dayNumber >> (8 * i));
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// Modelled occupancy for a room type on a night, in whole percent (55–100).
    /// Summer (June–September) adds fifteen points, capped at full.
    /// </summary>
    public static int OccupancyPercent(string roomCode, DateOnly date)
    {
        var seed = StableHash(roomCode, date.DayNumber);
        var occupancy = 55 + (int)(seed % 46);                    // 55–100 %
        if (date.Month is >= 6 and <= 9) occupancy = Math.Min(100, occupancy + 15);
        return occupancy;
    }

    /// <summary>Rooms of a type left unsold on a night under the model.</summary>
    public static int ModelledAvailableRooms(string roomCode, int totalRooms, DateOnly date)
    {
        var sold = (int)Math.Round(totalRooms * OccupancyPercent(roomCode, date) / 100.0, MidpointRounding.ToEven);
        return Math.Max(0, totalRooms - sold);
    }

    /// <summary>
    /// The seasonal rate: summer (Jun–Sep) +25 %, the holiday period (20 Dec –
    /// 5 Jan) +35 %, Friday and Saturday nights +10 %, rounded to a whole euro
    /// with banker's rounding — €302.50 is €302.
    /// </summary>
    public static decimal ModelledNightlyRate(decimal baseRateEur, DateOnly date)
    {
        var rate = baseRateEur;

        if (date.Month is >= 6 and <= 9) rate *= 1.25m;
        if ((date.Month == 12 && date.Day >= 20) || (date.Month == 1 && date.Day <= 5)) rate *= 1.35m;
        if (date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) rate *= 1.10m;

        return Math.Round(rate, 0, MidpointRounding.ToEven);
    }
}
