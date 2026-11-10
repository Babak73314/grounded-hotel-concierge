namespace GroundedConcierge.Domain;

/// <summary>
/// One room type of the demo property.
///
/// <para>
/// The positional part is the room's identity, size, base rate and stock; the
/// init-only properties default to an ordinary double room.
/// <see cref="MaxAdults"/> and <see cref="MaxGuests"/> are what make a guest
/// count MEAN something: a request that collected them and never enforced them
/// would be collecting fields for show.
/// </para>
/// </summary>
/// <param name="BaseRateEur">Low-season nightly rate in EUR, breakfast included.</param>
public record HotelRoomType(
    string Code,
    string NameEn, string NameTr,
    string DescriptionEn, string DescriptionTr,
    int SizeSqm, decimal BaseRateEur, int TotalRooms)
{
    /// <summary>Adults the room type sleeps at most.</summary>
    public int MaxAdults { get; init; } = 2;

    /// <summary>Adults plus children the room type sleeps at most.</summary>
    public int MaxGuests { get; init; } = 3;

    /// <summary>An inactive type is kept for history but neither quoted nor listed.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>Display order on the site and in quotes.</summary>
    public int SortOrder { get; init; }

    public string Name(bool turkish) => turkish ? NameTr : NameEn;

    public string Description(bool turkish) => turkish ? DescriptionTr : DescriptionEn;
}

/// <summary>
/// A staff-entered exception to the model for ONE room type on ONE night: a
/// different nightly rate, a different number of rooms left, or both. Either
/// field may be null, meaning "the model decides that part". Small on purpose —
/// this is a demonstration override, not a revenue-management system.
/// </summary>
public sealed record HotelDailyOverride(
    string RoomCode,
    DateOnly Date,
    decimal? NightlyRateEur,
    int? RoomsAvailable,
    string? Note)
{
    public bool IsEmpty => NightlyRateEur is null && RoomsAvailable is null;
}

/// <summary>
/// Everything <see cref="HotelDataService"/> reads, as one immutable value that
/// is swapped atomically. Every reader — a rate calendar, a booking form, the
/// assistant — reads the same snapshot, so an edit reaches all of them at once
/// or none of them: never a calendar showing one price while the assistant
/// quotes another.
/// </summary>
public sealed class HotelCatalogSnapshot
{
    private readonly Dictionary<(string Room, DateOnly Date), HotelDailyOverride> _overrides;

    public HotelCatalogSnapshot(
        IReadOnlyList<HotelRoomType> rooms,
        IEnumerable<HotelDailyOverride>? overrides = null,
        int version = 0,
        string source = "built-in")
    {
        ArgumentNullException.ThrowIfNull(rooms);

        Rooms = rooms
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.BaseRateEur)
            .ToList();

        _overrides = new Dictionary<(string, DateOnly), HotelDailyOverride>();
        foreach (var o in overrides ?? Array.Empty<HotelDailyOverride>())
        {
            if (o.IsEmpty) continue;
            _overrides[(Normalize(o.RoomCode), o.Date)] = o with { RoomCode = Normalize(o.RoomCode) };
        }

        Version = version;
        Source = source;
    }

    /// <summary>Every room type, active or not, in display order.</summary>
    public IReadOnlyList<HotelRoomType> Rooms { get; }

    /// <summary>Only the types a guest can be quoted.</summary>
    public IEnumerable<HotelRoomType> ActiveRooms => Rooms.Where(r => r.IsActive);

    /// <summary>
    /// Increments on every swap. A quote carries the version it was computed
    /// under, so a screen can tell that the catalogue moved underneath it.
    /// </summary>
    public int Version { get; }

    /// <summary>Where the snapshot came from, e.g. "built-in" or the name of a catalogue source.</summary>
    public string Source { get; }

    public int OverrideCount => _overrides.Count;

    public IEnumerable<HotelDailyOverride> Overrides => _overrides.Values;

    public HotelDailyOverride? OverrideFor(string roomCode, DateOnly date) =>
        _overrides.TryGetValue((Normalize(roomCode), date), out var o) ? o : null;

    public static string Normalize(string roomCode) => (roomCode ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// The built-in demo catalogue: five room types of a fictional property.
    /// The default for the engine, the in-memory source and the tests.
    /// </summary>
    public static HotelCatalogSnapshot BuiltIn() => new(BuiltInRooms(), version: 0, source: "built-in");

    public static IReadOnlyList<HotelRoomType> BuiltInRooms() => new List<HotelRoomType>
    {
        new("GARDEN",  "Garden Room",           "Bahçe Odası",
            "30 m² of quiet elegance overlooking the palace gardens.",
            "Saray bahçelerine bakan 30 m²'lik sakin bir zarafet.",
            30, 220, 40) { MaxAdults = 2, MaxGuests = 3, SortOrder = 1 },
        new("DELUXE",  "Deluxe Room",           "Deluxe Oda",
            "35 m² with partial sea view and a French balcony.",
            "Kısmi deniz manzaralı, Fransız balkonlu 35 m² oda.",
            35, 290, 60) { MaxAdults = 2, MaxGuests = 3, SortOrder = 2 },
        new("EXEC",    "Executive Room",        "Executive Oda",
            "42 m² Bosphorus view with Executive Lounge access.",
            "Executive Lounge erişimli, 42 m² Boğaz manzaralı oda.",
            42, 380, 45) { MaxAdults = 3, MaxGuests = 3, SortOrder = 3 },
        new("JRSUITE", "Palace Junior Suite",   "Saray Junior Süit",
            "55 m² suite with living corner and marble bathroom, full Bosphorus view.",
            "Oturma köşeli, mermer banyolu, tam Boğaz manzaralı 55 m² süit.",
            55, 520, 25) { MaxAdults = 3, MaxGuests = 4, SortOrder = 4 },
        new("BOSSUITE","Deluxe Bosphorus Suite","Deluxe Boğaz Süiti",
            "85 m² panoramic suite with private terrace and butler service.",
            "Özel teraslı ve butler hizmetli 85 m² panoramik süit.",
            85, 850, 10) { MaxAdults = 4, MaxGuests = 5, SortOrder = 5 },
    };
}
