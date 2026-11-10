namespace GroundedConcierge.Domain;

/// <summary>One night of a quote: the date and what that night costs.</summary>
public sealed record NightlyRate(DateOnly Date, decimal RateEur, int RoomsAvailable, bool FromOverride);

/// <summary>
/// Availability and price of a stay for one room type.
///
/// <para>
/// <see cref="TotalEur"/> is the SUM of the nightly amounts in
/// <see cref="Nights"/> — never an average multiplied back up.
/// <see cref="AvgNightlyEur"/> is a rounded figure for display, and the screens
/// label it as such; the arithmetic that matters is the sum.
/// </para>
/// </summary>
public record StayQuote(
    HotelRoomType Room, int Nights, int RoomsAvailable,
    decimal TotalEur, decimal AvgNightlyEur)
{
    public bool IsAvailable => RoomsAvailable > 0;

    /// <summary>Per-night breakdown; empty only for a quote built the legacy way.</summary>
    public IReadOnlyList<NightlyRate> NightlyRates { get; init; } = Array.Empty<NightlyRate>();

    /// <summary>Catalogue version this quote was computed under.</summary>
    public int SnapshotVersion { get; init; }

    /// <summary>
    /// Whether the requested party fits this room type. Null when the quote was
    /// asked for without a party size.
    /// </summary>
    public bool? FitsParty { get; init; }

    /// <summary>True when at least one night's rate or availability came from a staff override.</summary>
    public bool HasOverrides => NightlyRates.Any(n => n.FromOverride);
}

/// <summary>What a guest asked for. Dates are inclusive of check-in, exclusive of check-out.</summary>
public sealed record StayRequest(
    DateOnly CheckIn, DateOnly CheckOut, int Adults, int Children, string? RoomCode = null);

/// <summary>Why a stay request could not be quoted, as codes a screen localizes.</summary>
public enum StayRequestProblem
{
    None,
    CheckOutNotAfterCheckIn,
    CheckInInThePast,
    TooFarAhead,
    TooManyNights,
    NoAdults,
    TooManyAdults,
    TooManyChildren,
    UnknownRoom,
    RoomInactive,
    PartyTooLarge
}

/// <summary>
/// The outcome of quoting a stay: the problems found, and — when there are
/// none — one quote per active room type (or the single type asked for).
/// </summary>
public sealed record StayQuoteResult(
    StayRequest Request,
    int Nights,
    IReadOnlyList<StayRequestProblem> Problems,
    IReadOnlyList<StayQuote> Quotes,
    int SnapshotVersion)
{
    public bool IsValid => Problems.Count == 0;

    /// <summary>Quotes the party actually fits and that have a room left.</summary>
    public IEnumerable<StayQuote> Bookable => Quotes.Where(q => q.IsAvailable && q.FitsParty != false);
}

/// <summary>
/// What a guest asked the engine to FIND rather than to price: the earliest
/// stay of a given length, searching forward from a date, for a party,
/// optionally one room type.
/// </summary>
public sealed record EarliestStayRequest(
    DateOnly SearchFrom, int Nights, int Adults, int Children, string? RoomCode = null);

/// <summary>
/// The outcome of an earliest-availability search. When <see cref="Found"/>,
/// <see cref="CheckIn"/> is the FIRST check-in date on or after the search start
/// on which at least one room type is active, fits the party and has a room
/// left on EVERY night of the stay; <see cref="Quotes"/> holds exactly those
/// qualifying room types, quoted by the same arithmetic as <see cref="StayQuoteResult"/>.
/// When not found, <see cref="SearchedThrough"/> is the last check-in date that
/// was examined — the search is bounded, never open-ended.
/// </summary>
public sealed record EarliestStayResult(
    EarliestStayRequest Request,
    bool Found,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    IReadOnlyList<StayQuote> Quotes,
    IReadOnlyList<StayRequestProblem> Problems,
    DateOnly SearchedFrom,
    DateOnly SearchedThrough,
    int CandidatesChecked,
    int SnapshotVersion)
{
    public bool IsValid => Problems.Count == 0;

    /// <summary>How far ahead a check-in may be at most — the same bound <see cref="HotelDataService.Quote"/> enforces.</summary>
    public int HorizonDays => HotelServiceCatalog.MaxAdvanceDays;

    /// <summary>
    /// True when at least one candidate date BEFORE the found check-in was
    /// examined and rejected — the only case in which "earlier dates had no
    /// room free" is a true statement. When the very first candidate (the
    /// search start) qualifies, nothing earlier was examined at all.
    /// </summary>
    public bool EarlierCandidatesRejected => Found && CheckIn is { } found && found > SearchedFrom;

    /// <summary>How many candidate dates before the found one were examined and rejected (0 when the first candidate qualified).</summary>
    public int RejectedCandidates => Found ? Math.Max(0, CandidatesChecked - 1) : CandidatesChecked;
}
