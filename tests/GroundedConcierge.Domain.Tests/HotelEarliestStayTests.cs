using GroundedConcierge.Domain;

namespace GroundedConcierge.Domain.Tests;

/// <summary>
/// The deterministic earliest-availability search: it walks forward over the
/// SAME catalogue, rates, overrides and availability a dated quote uses, stops
/// at the first check-in date where a qualifying room has a room free on EVERY
/// night, is bounded by <see cref="HotelServiceCatalog.MaxAdvanceDays"/>, and
/// says so explicitly when nothing qualifies. No model is anywhere near it.
/// </summary>
public class HotelEarliestStayTests
{
    private static readonly DateOnly Today = new(2026, 9, 5);
    private const int MaxNights = HotelServiceCatalog.MaxStayNights;

    /// <summary>
    /// A catalogue whose availability (and, when stated, rate) for every room
    /// on every night of a window is decided by <paramref name="rule"/> —
    /// staff overrides, exactly as a catalogue source would hold them — so a test
    /// controls the picture instead of depending on the seasonal model.
    /// </summary>
    private static HotelDataService Engine(Func<string, DateOnly, (decimal? Rate, int? Rooms)> rule, int days = 40, IReadOnlyList<HotelRoomType>? rooms = null)
    {
        rooms ??= HotelCatalogSnapshot.BuiltInRooms();
        var overrides = new List<HotelDailyOverride>();
        for (var i = 0; i < days; i++)
        {
            var date = Today.AddDays(i);
            foreach (var room in rooms)
            {
                var (rate, count) = rule(room.Code, date);
                if (rate is not null || count is not null)
                    overrides.Add(new HotelDailyOverride(room.Code, date, rate, count, "test"));
            }
        }
        return new HotelDataService(new HotelCatalogSnapshot(rooms, overrides, version: 7, source: "test"));
    }

    private static EarliestStayRequest Search(int nights, int adults = 2, int children = 0, string? room = null, DateOnly? from = null) =>
        new(from ?? Today, nights, adults, children, room);

    // ------------------------------------------------------ 1. any room, 3 nights

    [Fact]
    public void TheEarliestThreeNightStayIsTheFirstDateWhereARoomIsFreeOnEveryNight()
    {
        var hotel = new HotelDataService();   // the real seasonal/availability model, deterministic
        var result = hotel.FindEarliestAvailableStay(Search(3), Today);

        Assert.True(result.IsValid);
        Assert.True(result.Found);
        Assert.NotNull(result.CheckIn);
        Assert.Equal(result.CheckIn!.Value.AddDays(3), result.CheckOut);
        Assert.True(result.CheckIn >= Today);
        Assert.NotEmpty(result.Quotes);
        Assert.All(result.Quotes, q => Assert.True(q.IsAvailable));
        Assert.All(result.Quotes, q => Assert.NotEqual(false, q.FitsParty));
        Assert.All(result.Quotes, q => Assert.Equal(3, q.Nights));
        Assert.All(result.Quotes, q => Assert.Equal(3, q.NightlyRates.Count));
        Assert.Equal(result.CheckIn.Value.DayNumber - Today.DayNumber + 1, result.CandidatesChecked);

        // Definition check against the dated quote: no earlier date has a
        // bookable room for the same stay, and the found date's bookable rooms
        // are exactly the ones returned.
        for (var d = Today; d < result.CheckIn; d = d.AddDays(1))
            Assert.Empty(hotel.Quote(new StayRequest(d, d.AddDays(3), 2, 0), Today).Bookable);
        var atFound = hotel.Quote(new StayRequest(result.CheckIn.Value, result.CheckOut!.Value, 2, 0), Today).Bookable.Select(q => q.Room.Code).OrderBy(c => c);
        Assert.Equal(atFound, result.Quotes.Select(q => q.Room.Code).OrderBy(c => c));

        // Every figure is the dated quote's figure: the search adds no arithmetic of its own.
        foreach (var q in result.Quotes)
        {
            var dated = hotel.Quote(new StayRequest(result.CheckIn.Value, result.CheckOut.Value, 2, 0, q.Room.Code), Today).Quotes.Single();
            Assert.Equal(dated.TotalEur, q.TotalEur);
            Assert.Equal(dated.RoomsAvailable, q.RoomsAvailable);
            Assert.Equal(dated.NightlyRates.Select(n => n.RateEur), q.NightlyRates.Select(n => n.RateEur));
        }
    }

    [Fact]
    public void TwoIndependentEnginesFindTheSameEarliestStay()
    {
        var a = new HotelDataService().FindEarliestAvailableStay(Search(3), Today);
        var b = new HotelDataService().FindEarliestAvailableStay(Search(3), Today);
        Assert.Equal(a.CheckIn, b.CheckIn);
        Assert.Equal(a.Quotes.Select(q => (q.Room.Code, q.TotalEur)), b.Quotes.Select(q => (q.Room.Code, q.TotalEur)));
    }

    // ------------------------------------------------------- 2. one room type

    [Fact]
    public void TheEarliestStayForOneRoomTypeReturnsOnlyThatType()
    {
        var hotel = new HotelDataService();
        var any = hotel.FindEarliestAvailableStay(Search(3), Today);
        var garden = hotel.FindEarliestAvailableStay(Search(3, room: "garden"), Today);

        Assert.True(garden.Found);
        Assert.All(garden.Quotes, q => Assert.Equal("GARDEN", q.Room.Code));
        Assert.Single(garden.Quotes);
        Assert.True(garden.CheckIn >= any.CheckIn);   // a narrower search can never start earlier than the wide one
        Assert.Equal("garden", garden.Request.RoomCode);   // the request is echoed as given; the engine normalises the code itself
    }

    // ------------------------------------------------------ 3. party capacity

    [Fact]
    public void PartyCapacityFiltersTheQualifyingRooms()
    {
        var hotel = Engine((_, _) => (null, 5));   // every room free every night: capacity is the only filter
        var family = hotel.FindEarliestAvailableStay(Search(2, adults: 3, children: 1), Today);

        Assert.True(family.Found);
        Assert.Equal(Today, family.CheckIn);
        Assert.NotEmpty(family.Quotes);
        Assert.All(family.Quotes, q => Assert.True(q.Room.MaxAdults >= 3 && q.Room.MaxGuests >= 4, $"{q.Room.Code} does not sleep 3 adults + 1 child"));
        Assert.DoesNotContain(family.Quotes, q => q.Room.Code is "GARDEN" or "DELUXE" or "EXEC");
        Assert.All(family.Quotes, q => Assert.True(q.FitsParty));
    }

    // -------------------------------------- 4. night 2 sold out → candidate skipped

    [Fact]
    public void ACandidateWhoseSecondNightIsSoldOutIsSkippedEvenThoughItsFirstNightIsFree()
    {
        var soldOut = Today.AddDays(1);
        var hotel = Engine((_, date) => (null, date == soldOut ? 0 : 5));

        var result = hotel.FindEarliestAvailableStay(Search(2), Today);

        // Today: night 1 free, night 2 (tomorrow) sold out → skip.
        // Tomorrow: night 1 sold out → skip. The day after: both nights free.
        Assert.True(result.Found);
        Assert.Equal(Today.AddDays(2), result.CheckIn);
        Assert.Equal(Today.AddDays(4), result.CheckOut);
        Assert.Equal(3, result.CandidatesChecked);
        Assert.All(result.Quotes, q => Assert.All(q.NightlyRates, n => Assert.True(n.RoomsAvailable > 0)));

        // And the skipped candidate really was skipped for that reason: quoted
        // directly, its tightest night is zero.
        var skipped = hotel.Quote(new StayRequest(Today, Today.AddDays(2), 2, 0), Today);
        Assert.All(skipped.Quotes, q => Assert.Equal(0, q.RoomsAvailable));
    }

    // ------------------------------ 5. a staff availability override moves the result

    [Fact]
    public void AStaffAvailabilityOverrideMovesTheEarliestResult()
    {
        var open = Engine((_, _) => (null, 5));
        Assert.Equal(Today, open.FindEarliestAvailableStay(Search(3), Today).CheckIn);

        // Staff sell every room out for the first five nights (per-night overrides).
        var blocked = Engine((_, date) => (null, date < Today.AddDays(5) ? 0 : 5));
        var moved = blocked.FindEarliestAvailableStay(Search(3), Today);

        Assert.True(moved.Found);
        Assert.Equal(Today.AddDays(5), moved.CheckIn);
        Assert.Equal(6, moved.CandidatesChecked);

        // The same change applied to the live engine through Apply() moves the
        // answer for every reader at once — the search reads the snapshot in force.
        open.Apply(blocked.Snapshot);
        Assert.Equal(Today.AddDays(5), open.FindEarliestAvailableStay(Search(3), Today).CheckIn);
    }

    // ------------------------------------- 6. a rate override appears in the quote

    [Fact]
    public void ARateOverrideAppearsInTheReturnedQuoteExactlyAsADatedQuoteWouldShowIt()
    {
        var hotel = Engine((code, date) => (code == "GARDEN" && date == Today ? 199m : null, 5));

        var result = hotel.FindEarliestAvailableStay(Search(2, room: "GARDEN"), Today);

        Assert.True(result.Found);
        Assert.Equal(Today, result.CheckIn);
        var garden = Assert.Single(result.Quotes);
        Assert.Equal(199m, garden.NightlyRates[0].RateEur);
        Assert.True(garden.NightlyRates[0].FromOverride);
        Assert.Equal(garden.NightlyRates.Sum(n => n.RateEur), garden.TotalEur);
        Assert.Equal(hotel.Quote(new StayRequest(Today, Today.AddDays(2), 2, 0, "GARDEN"), Today).Quotes.Single().TotalEur, garden.TotalEur);
        Assert.Equal(7, result.SnapshotVersion);
    }

    // ----------------------------- 7. + 8. nothing inside the horizon, and a hard bound

    [Fact]
    public void NoAvailabilityInsideTheHorizonIsAnExplicitBoundedResult()
    {
        var full = Engine((_, _) => (null, 0), days: HotelServiceCatalog.MaxAdvanceDays + MaxNights + 5);

        var result = full.FindEarliestAvailableStay(Search(3), Today);

        Assert.True(result.IsValid);
        Assert.False(result.Found);
        Assert.Null(result.CheckIn);
        Assert.Null(result.CheckOut);
        Assert.Empty(result.Quotes);
        Assert.Equal(Today, result.SearchedFrom);
        Assert.Equal(Today.AddDays(HotelServiceCatalog.MaxAdvanceDays), result.SearchedThrough);
        Assert.Equal(HotelServiceCatalog.MaxAdvanceDays + 1, result.CandidatesChecked);
        Assert.Equal(HotelServiceCatalog.MaxAdvanceDays, result.HorizonDays);

        // The fixed no-availability text names the window, in both languages, and reserves nothing.
        var en = GuestMessages.NoAvailability(result, turkish: false);
        var tr = GuestMessages.NoAvailability(result, turkish: true);
        Assert.Contains($"{HotelServiceCatalog.MaxAdvanceDays} days ahead", en);
        Assert.Contains("nothing has been reserved", en);
        Assert.Contains($"{HotelServiceCatalog.MaxAdvanceDays} gün ileriye kadar", tr);
        Assert.Contains("hiçbir şey ayrılmadı", tr);
    }

    [Fact]
    public void TheSearchNeverExaminesMoreCandidatesThanTheHorizonAllows()
    {
        var full = Engine((_, _) => (null, 0), days: HotelServiceCatalog.MaxAdvanceDays + MaxNights + 5);

        // Starting beyond the horizon examines nothing at all.
        var beyond = full.FindEarliestAvailableStay(Search(3, from: Today.AddDays(HotelServiceCatalog.MaxAdvanceDays + 1)), Today);
        Assert.False(beyond.Found);
        Assert.Equal(0, beyond.CandidatesChecked);

        // Starting in the past is clamped to today, never scanned backwards.
        var past = full.FindEarliestAvailableStay(Search(3, from: Today.AddDays(-30)), Today);
        Assert.Equal(Today, past.SearchedFrom);
        Assert.Equal(HotelServiceCatalog.MaxAdvanceDays + 1, past.CandidatesChecked);

        // And the longest permitted stay still stops at the same bound.
        var longest = full.FindEarliestAvailableStay(Search(MaxNights), Today);
        Assert.True(longest.CandidatesChecked <= HotelServiceCatalog.MaxAdvanceDays + 1);
    }

    // --------------------------- 15. an inactive room or an oversized party is a problem

    [Fact]
    public void AnInactiveRoomAnOversizedPartyOrAnImpossibleRequestIsAProblemNotASearch()
    {
        var rooms = HotelCatalogSnapshot.BuiltInRooms().Select(r => r.Code == "GARDEN" ? r with { IsActive = false } : r).ToList();
        var hotel = Engine((_, _) => (null, 5), rooms: rooms);

        var inactive = hotel.FindEarliestAvailableStay(Search(2, room: "GARDEN"), Today);
        Assert.False(inactive.IsValid);
        Assert.Contains(StayRequestProblem.RoomInactive, inactive.Problems);
        Assert.False(inactive.Found);
        Assert.Equal(0, inactive.CandidatesChecked);

        var tooBig = hotel.FindEarliestAvailableStay(Search(2, adults: 3, room: "DELUXE"), Today);
        Assert.Contains(StayRequestProblem.PartyTooLarge, tooBig.Problems);

        Assert.Contains(StayRequestProblem.UnknownRoom, hotel.FindEarliestAvailableStay(Search(2, room: "PENTHOUSE"), Today).Problems);
        Assert.Contains(StayRequestProblem.CheckOutNotAfterCheckIn, hotel.FindEarliestAvailableStay(Search(0), Today).Problems);
        Assert.Contains(StayRequestProblem.TooManyNights, hotel.FindEarliestAvailableStay(Search(MaxNights + 1), Today).Problems);
        Assert.Contains(StayRequestProblem.NoAdults, hotel.FindEarliestAvailableStay(Search(2, adults: 0), Today).Problems);
        Assert.Contains(StayRequestProblem.TooManyAdults, hotel.FindEarliestAvailableStay(Search(2, adults: HotelServiceCatalog.MaxAdultsPerRequest + 1), Today).Problems);

        // An inactive type simply does not take part in an any-room search.
        var any = hotel.FindEarliestAvailableStay(Search(2), Today);
        Assert.True(any.Found);
        Assert.DoesNotContain(any.Quotes, q => q.Room.Code == "GARDEN");
    }

    // ------------------------------------- "earlier dates" is metadata, not inference

    [Fact]
    public void TheAnswerClaimsEarlierDatesWereRejectedOnlyWhenSomeActuallyWere()
    {
        // The first candidate — the search start — qualifies: nothing earlier
        // was examined, and the answer must say so rather than invent a rejection.
        var open = Engine((_, _) => (null, 5));
        var first = open.FindEarliestAvailableStay(Search(3), Today);
        Assert.Equal(Today, first.CheckIn);
        Assert.False(first.EarlierCandidatesRejected);
        Assert.Equal(0, first.RejectedCandidates);
        Assert.Equal(1, first.CandidatesChecked);

        var text = HotelAnswerComposer.Compose(FactsOf(open, first), turkish: false);
        Assert.Contains("no earlier date was examined", text, StringComparison.Ordinal);
        Assert.DoesNotContain("were also checked", text, StringComparison.Ordinal);

        // Five sold-out candidates before the found one: the answer names them,
        // with the exact date range, so "earlier dates were not available" is a
        // statement about examined dates and nothing else.
        var blocked = Engine((_, date) => (null, date < Today.AddDays(5) ? 0 : 5));
        var later = blocked.FindEarliestAvailableStay(Search(3), Today);
        Assert.Equal(Today.AddDays(5), later.CheckIn);
        Assert.True(later.EarlierCandidatesRejected);
        Assert.Equal(5, later.RejectedCandidates);

        var laterEn = HotelAnswerComposer.Compose(FactsOf(blocked, later), turkish: false);
        var laterTr = HotelAnswerComposer.Compose(FactsOf(blocked, later), turkish: true);
        Assert.Contains($"The 5 earlier check-in dates from {Today:dd.MM.yyyy} to {Today.AddDays(4):dd.MM.yyyy} were also checked", laterEn, StringComparison.Ordinal);
        Assert.Contains($"{Today:dd.MM.yyyy} ile {Today.AddDays(4):dd.MM.yyyy} arasındaki 5 giriş tarihi de incelendi", laterTr, StringComparison.Ordinal);
        Assert.DoesNotContain("no earlier date was examined", laterEn, StringComparison.Ordinal);

        // A search that starts later than today reports its own start, not today.
        var fromLater = blocked.FindEarliestAvailableStay(Search(3, from: Today.AddDays(5)), Today);
        Assert.Equal(Today.AddDays(5), fromLater.CheckIn);
        Assert.False(fromLater.EarlierCandidatesRejected);
    }

    // ------------------------------------------------ the rendered answer

    [Fact]
    public void TheAnswerStatesTheFoundDatesAndTheSameFiguresAsADatedQuoteInBothLanguages()
    {
        var hotel = new HotelDataService();
        var result = hotel.FindEarliestAvailableStay(Search(3), Today);
        Assert.True(result.Found);

        var en = HotelAnswerComposer.Compose(FactsOf(hotel, result), turkish: false);
        var tr = HotelAnswerComposer.Compose(FactsOf(hotel, result), turkish: true);

        Assert.Contains($"The earliest available 3-night stay is from {result.CheckIn:dd.MM.yyyy} to {result.CheckOut:dd.MM.yyyy}", en, StringComparison.Ordinal);
        Assert.Contains($"En erken müsait 3 gecelik konaklama: giriş {result.CheckIn:dd.MM.yyyy}, çıkış {result.CheckOut:dd.MM.yyyy}", tr, StringComparison.Ordinal);
        Assert.Contains("no room is reserved, nothing is held and nothing is charged", en, StringComparison.Ordinal);

        // A dated quote for the same nights prints the very same totals.
        var dated = hotel.Quote(new StayRequest(result.CheckIn!.Value, result.CheckOut!.Value, 2, 0), Today);
        foreach (var q in result.Quotes)
        {
            var same = dated.Quotes.Single(d => d.Room.Code == q.Room.Code);
            Assert.Equal(q.TotalEur, same.TotalEur);
            Assert.Contains("€" + q.TotalEur.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " total", en, StringComparison.Ordinal);
        }

        // Facts with no stay compose nothing at all: there is no commercial text to invent.
        Assert.Equal(string.Empty, HotelAnswerComposer.Compose(HotelAnswerFacts.None, turkish: false));
    }

    private static HotelAnswerFacts FactsOf(HotelDataService hotel, EarliestStayResult result) => new()
    {
        Stay = new StayRequest(result.CheckIn!.Value, result.CheckOut!.Value, result.Request.Adults, result.Request.Children, result.Request.RoomCode),
        Quotes = result.Quotes,
        Catalogue = hotel.RoomTypes,
        Today = Today,
        SearchedFrom = result.SearchedFrom,
        SearchedThrough = result.SearchedThrough,
        EarlierCandidatesRejected = result.EarlierCandidatesRejected,
        RejectedCandidates = result.RejectedCandidates
    };
}
