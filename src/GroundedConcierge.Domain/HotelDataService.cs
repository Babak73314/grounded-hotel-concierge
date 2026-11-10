namespace GroundedConcierge.Domain;

/// <summary>
/// The demo's reservation engine: room catalogue, nightly rates, availability
/// and stay quotes. In a real deployment this surface is implemented over the
/// PMS or channel manager; nothing that reads it knows where the numbers came
/// from.
///
/// <para>
/// <b>One snapshot, every reader.</b> The service is meant to be a singleton
/// holding an immutable <see cref="HotelCatalogSnapshot"/> that is swapped
/// atomically by <see cref="Apply"/>. Every reader sees the same value, so a
/// staff edit reaches all of them together. Where the snapshot comes from is an
/// <see cref="IHotelCatalogSource"/>; with none, it is the built-in catalogue.
/// </para>
///
/// <para>
/// <b>Every figure is arithmetic in this class.</b> No language model is ever
/// asked to add nights up or to decide whether a room is free, and no caller
/// should trust a total that arrived from outside — re-quote through this
/// engine instead.
/// </para>
///
/// <para>
/// <b>Deterministic across processes.</b> Availability derives from a hand-written
/// FNV-1a hash (<see cref="HotelAvailabilityModel"/>), not <c>HashCode.Combine</c>,
/// whose per-process salt would make every restart a different hotel.
/// </para>
/// </summary>
public class HotelDataService
{
    private volatile HotelCatalogSnapshot _snapshot;

    public HotelDataService() : this(HotelCatalogSnapshot.BuiltIn()) { }

    public HotelDataService(HotelCatalogSnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _snapshot = initial;
    }

    /// <summary>Start from whatever the catalogue source holds now.</summary>
    public HotelDataService(IHotelCatalogSource source)
        : this((source ?? throw new ArgumentNullException(nameof(source))).Load()) { }

    /// <summary>Re-read the source and apply what it holds — the same atomic swap as <see cref="Apply"/>.</summary>
    public void Reload(IHotelCatalogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Apply(source.Load());
    }

    /// <summary>The catalogue currently in force.</summary>
    public HotelCatalogSnapshot Snapshot => _snapshot;

    /// <summary>Every room type in display order (inactive ones included — a catalogue editor still lists them).</summary>
    public IReadOnlyList<HotelRoomType> RoomTypes => _snapshot.Rooms;

    /// <summary>Only the room types a guest can be quoted.</summary>
    public IReadOnlyList<HotelRoomType> ActiveRoomTypes => _snapshot.ActiveRooms.ToList();

    /// <summary>Version of the snapshot in force; changes on every <see cref="Apply"/>.</summary>
    public int SnapshotVersion => _snapshot.Version;

    /// <summary>Replace the catalogue in force. Atomic: a reader sees the old value or the new one, never a mix.</summary>
    public void Apply(HotelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
    }

    public HotelRoomType? GetRoomType(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = HotelCatalogSnapshot.Normalize(code);
        return _snapshot.Rooms.FirstOrDefault(r => r.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The staff override for that room and night, if one exists.</summary>
    public HotelDailyOverride? GetOverride(string roomCode, DateOnly date) => _snapshot.OverrideFor(roomCode, date);

    /// <summary>
    /// Nightly rate: a staff override when one names a rate for that night,
    /// otherwise the seasonal model over the room's base rate.
    /// </summary>
    public decimal GetNightlyRate(string roomCode, DateOnly date)
    {
        // ONE read of the snapshot, used for both halves. Reading the field twice
        // would let a staff edit land in between, so the answer could pair the
        // old room's base rate with the new snapshot's override — a figure that
        // never existed in either catalogue.
        var snapshot = _snapshot;
        var room = Find(snapshot, roomCode) ?? throw new ArgumentException($"Unknown room type '{roomCode}'");
        var over = snapshot.OverrideFor(room.Code, date);
        return over?.NightlyRateEur ?? HotelAvailabilityModel.ModelledNightlyRate(room.BaseRateEur, date);
    }

    /// <summary>The seasonal model alone, ignoring overrides — what an editor shows beside an override.</summary>
    public decimal GetModelledNightlyRate(string roomCode, DateOnly date)
    {
        var room = GetRoomType(roomCode) ?? throw new ArgumentException($"Unknown room type '{roomCode}'");
        return HotelAvailabilityModel.ModelledNightlyRate(room.BaseRateEur, date);
    }

    /// <summary>
    /// Rooms of that type left on that night: a staff override when one names a
    /// count, otherwise the deterministic model. Never below zero and never above
    /// the type's total.
    /// </summary>
    public int GetAvailableRooms(string roomCode, DateOnly date)
    {
        var snapshot = _snapshot;
        var room = Find(snapshot, roomCode) ?? throw new ArgumentException($"Unknown room type '{roomCode}'");
        var over = snapshot.OverrideFor(room.Code, date);
        var available = over?.RoomsAvailable
            ?? HotelAvailabilityModel.ModelledAvailableRooms(room.Code, room.TotalRooms, date);
        return Math.Clamp(available, 0, Math.Max(0, room.TotalRooms));
    }

    /// <summary>Room lookup inside a snapshot the caller already holds, so a swap cannot land mid-read.</summary>
    private static HotelRoomType? Find(HotelCatalogSnapshot snapshot, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = HotelCatalogSnapshot.Normalize(code);
        return snapshot.Rooms.FirstOrDefault(r => r.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The model alone, ignoring overrides.</summary>
    public int GetModelledAvailableRooms(string roomCode, DateOnly date)
    {
        var room = GetRoomType(roomCode) ?? throw new ArgumentException($"Unknown room type '{roomCode}'");
        return HotelAvailabilityModel.ModelledAvailableRooms(room.Code, room.TotalRooms, date);
    }

    /// <summary>
    /// Availability + total price for a stay, one result per active room type.
    /// It is <see cref="Quote"/> without a party size and without validation
    /// beyond the date order — for a rate calendar that shows every room type.
    /// </summary>
    public List<StayQuote> QuoteStay(DateOnly checkIn, DateOnly checkOut)
    {
        if (checkOut <= checkIn)
            throw new ArgumentException("Check-out must be after check-in.");

        var snapshot = _snapshot;
        var nights = checkOut.DayNumber - checkIn.DayNumber;

        return snapshot.ActiveRooms
            .Select(room => QuoteRoom(snapshot, room, checkIn, checkOut, nights, party: null))
            .ToList();
    }

    /// <summary>
    /// Quote a stay for a party, validating the request first. Problems come
    /// back as codes; a request with any problem gets no quotes at all, because
    /// a price beside an error is a price somebody will believe.
    /// </summary>
    /// <param name="today">The demo's "today", so a past date is judged against the property's clock.</param>
    public StayQuoteResult Quote(StayRequest request, DateOnly today) => Quote(request, today, _snapshot);

    /// <summary>
    /// Find the EARLIEST stay of <c>request.Nights</c> nights: the first check-in
    /// date on or after <c>request.SearchFrom</c> (never before <paramref name="today"/>)
    /// on which at least one room type is active, fits the party and has a room
    /// left on EVERY night of the stay — one room type only when the request
    /// names one. Each candidate is quoted by the same <see cref="Quote"/> rules
    /// over the same catalogue, rates, overrides and availability, so a staff
    /// override that sells a night out moves the answer exactly as it moves a
    /// quote. Nothing is estimated and nothing is held.
    ///
    /// <para>
    /// Bounded: a check-in may be at most <see cref="HotelServiceCatalog.MaxAdvanceDays"/>
    /// after today — the bound <see cref="Quote"/> already enforces — so the scan
    /// examines at most that many candidates plus one, and a search that starts
    /// beyond the horizon examines none. A search that finds nothing says so
    /// explicitly, with the last date it looked at.
    /// </para>
    /// </summary>
    public EarliestStayResult FindEarliestAvailableStay(EarliestStayRequest request, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = _snapshot;
        var problems = new List<StayRequestProblem>();

        if (request.Nights < 1) problems.Add(StayRequestProblem.CheckOutNotAfterCheckIn);
        if (request.Nights > HotelServiceCatalog.MaxStayNights) problems.Add(StayRequestProblem.TooManyNights);
        if (request.Adults < 1) problems.Add(StayRequestProblem.NoAdults);
        if (request.Adults > HotelServiceCatalog.MaxAdultsPerRequest) problems.Add(StayRequestProblem.TooManyAdults);
        if (request.Children < 0 || request.Children > HotelServiceCatalog.MaxChildrenPerRequest) problems.Add(StayRequestProblem.TooManyChildren);

        if (!string.IsNullOrWhiteSpace(request.RoomCode))
        {
            var only = FindRoom(snapshot, request.RoomCode);
            if (only is null) problems.Add(StayRequestProblem.UnknownRoom);
            else if (!only.IsActive) problems.Add(StayRequestProblem.RoomInactive);
            else if (!Fits(only, new StayRequest(today, today.AddDays(1), request.Adults, request.Children, request.RoomCode)))
                problems.Add(StayRequestProblem.PartyTooLarge);
        }

        var from = request.SearchFrom < today ? today : request.SearchFrom;
        var lastCheckIn = today.AddDays(HotelServiceCatalog.MaxAdvanceDays);

        if (problems.Count > 0)
            return new EarliestStayResult(request, false, null, null, Array.Empty<StayQuote>(), problems, from, from, 0, snapshot.Version);

        var candidates = 0;
        for (var checkIn = from; checkIn <= lastCheckIn; checkIn = checkIn.AddDays(1))
        {
            candidates++;
            var checkOut = checkIn.AddDays(request.Nights);
            var quote = Quote(new StayRequest(checkIn, checkOut, request.Adults, request.Children, request.RoomCode), today, snapshot);
            if (!quote.IsValid) continue;   // cannot happen inside the bound with the checks above; defensive

            // "Available" on a quote is the tightest night of the stay, so one
            // sold-out night anywhere in the range disqualifies the candidate.
            var qualifying = quote.Quotes.Where(q => q.IsAvailable && q.FitsParty != false).ToList();
            if (qualifying.Count > 0)
                return new EarliestStayResult(request, true, checkIn, checkOut, qualifying, Array.Empty<StayRequestProblem>(), from, checkIn, candidates, snapshot.Version);
        }

        return new EarliestStayResult(request, false, null, null, Array.Empty<StayQuote>(), Array.Empty<StayRequestProblem>(), from, lastCheckIn, candidates, snapshot.Version);
    }

    private static HotelRoomType? FindRoom(HotelCatalogSnapshot snapshot, string code)
    {
        var normalized = HotelCatalogSnapshot.Normalize(code);
        return snapshot.Rooms.FirstOrDefault(r => r.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private StayQuoteResult Quote(StayRequest request, DateOnly today, HotelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = new List<StayRequestProblem>();

        if (request.CheckOut <= request.CheckIn) problems.Add(StayRequestProblem.CheckOutNotAfterCheckIn);
        if (request.CheckIn < today) problems.Add(StayRequestProblem.CheckInInThePast);
        if (request.CheckIn.DayNumber - today.DayNumber > HotelServiceCatalog.MaxAdvanceDays) problems.Add(StayRequestProblem.TooFarAhead);

        var nights = Math.Max(0, request.CheckOut.DayNumber - request.CheckIn.DayNumber);
        if (nights > HotelServiceCatalog.MaxStayNights) problems.Add(StayRequestProblem.TooManyNights);

        if (request.Adults < 1) problems.Add(StayRequestProblem.NoAdults);
        if (request.Adults > HotelServiceCatalog.MaxAdultsPerRequest) problems.Add(StayRequestProblem.TooManyAdults);
        if (request.Children < 0 || request.Children > HotelServiceCatalog.MaxChildrenPerRequest) problems.Add(StayRequestProblem.TooManyChildren);

        HotelRoomType? only = null;
        if (!string.IsNullOrWhiteSpace(request.RoomCode))
        {
            only = FindRoom(snapshot, request.RoomCode);
            if (only is null) problems.Add(StayRequestProblem.UnknownRoom);
            else if (!only.IsActive) problems.Add(StayRequestProblem.RoomInactive);
            else if (!Fits(only, request)) problems.Add(StayRequestProblem.PartyTooLarge);
        }

        if (problems.Count > 0)
            return new StayQuoteResult(request, nights, problems, Array.Empty<StayQuote>(), snapshot.Version);

        var rooms = only is not null ? new[] { only } : snapshot.ActiveRooms.ToArray();
        var quotes = rooms
            .Select(room => QuoteRoom(snapshot, room, request.CheckIn, request.CheckOut, nights, request))
            .ToList();

        return new StayQuoteResult(request, nights, problems, quotes, snapshot.Version);
    }

    /// <summary>Whether a party of that size may be quoted this room type.</summary>
    public static bool Fits(HotelRoomType room, StayRequest party) =>
        party.Adults <= room.MaxAdults && party.Adults + party.Children <= room.MaxGuests;

    private static StayQuote QuoteRoom(
        HotelCatalogSnapshot snapshot, HotelRoomType room,
        DateOnly checkIn, DateOnly checkOut, int nights, StayRequest? party)
    {
        var nightly = new List<NightlyRate>(nights);
        var minAvailable = int.MaxValue;
        decimal total = 0;

        for (var d = checkIn; d < checkOut; d = d.AddDays(1))
        {
            var over = snapshot.OverrideFor(room.Code, d);
            var rate = over?.NightlyRateEur ?? HotelAvailabilityModel.ModelledNightlyRate(room.BaseRateEur, d);
            var available = Math.Clamp(
                over?.RoomsAvailable ?? HotelAvailabilityModel.ModelledAvailableRooms(room.Code, room.TotalRooms, d),
                0, Math.Max(0, room.TotalRooms));

            nightly.Add(new NightlyRate(d, rate, available, over is not null && !over.IsEmpty));
            minAvailable = Math.Min(minAvailable, available);
            total += rate;
        }

        if (nights == 0) minAvailable = 0;

        // The total is the SUM of the nights. The average is derived from it for
        // display and rounded, which is why the screens say "avg" next to it.
        var average = nights > 0 ? Math.Round(total / nights, 0, MidpointRounding.ToEven) : 0;

        return new StayQuote(room, nights, minAvailable, total, average)
        {
            NightlyRates = nightly,
            SnapshotVersion = snapshot.Version,
            FitsParty = party is null ? null : Fits(room, party)
        };
    }
}
