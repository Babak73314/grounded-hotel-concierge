using GroundedConcierge.Domain;

namespace GroundedConcierge.Domain.Tests;

/// <summary>
/// The hotel demo's reservation engine: deterministic across processes, and
/// arithmetic a screen can check.
///
/// <para>
/// The golden values below were computed once and written down. They are what
/// makes "the same picture in every process" a measured fact rather than a
/// claim: <c>HashCode.Combine</c> would produce a different number in every
/// test run, and a hand-written FNV-1a produces this one, forever.
/// </para>
/// </summary>
public class HotelEngineTests
{
    private static readonly DateOnly Today = new(2026, 9, 5);

    // ------------------------------------------------------- determinism

    [Theory]
    [InlineData("GARDEN", 2026, 10, 15, 816601178u)]
    [InlineData("garden", 2026, 10, 15, 816601178u)]   // case-folded: same room, same hash
    [InlineData("BOSSUITE", 2026, 12, 24, 3371096985u)]
    [InlineData("EXEC", 2027, 1, 1, 3701248928u)]
    public void TheAvailabilityHashIsStableAcrossProcesses(string room, int y, int m, int d, uint expected)
    {
        // A golden value is a value computed in ANOTHER process. If this test
        // fails, either the algorithm changed (which changes the demo's whole
        // history) or the hash is no longer stable — both are worth a red build.
        Assert.Equal(expected, HotelAvailabilityModel.StableHash(room, new DateOnly(y, m, d).DayNumber));
    }

    [Fact]
    public void TheSameInputsGiveTheSameAvailabilityFromTwoIndependentEngines()
    {
        var a = new HotelDataService();
        var b = new HotelDataService();

        for (var i = 0; i < 120; i++)
        {
            var date = Today.AddDays(i);
            foreach (var room in a.RoomTypes)
            {
                Assert.Equal(a.GetAvailableRooms(room.Code, date), b.GetAvailableRooms(room.Code, date));
                Assert.Equal(a.GetNightlyRate(room.Code, date), b.GetNightlyRate(room.Code, date));
            }
        }
    }

    [Fact]
    public void TheModelCoversBothAvailableAndSoldOutNightsInsideAYear()
    {
        var hotel = new HotelDataService();
        var suite = hotel.GetRoomType("BOSSUITE")!;

        var counts = Enumerable.Range(0, 365)
            .Select(i => hotel.GetAvailableRooms(suite.Code, Today.AddDays(i)))
            .ToList();

        Assert.Contains(counts, c => c == 0);
        Assert.Contains(counts, c => c > 0);
        Assert.All(counts, c => Assert.InRange(c, 0, suite.TotalRooms));
    }

    // ----------------------------------------------------------- rates

    [Fact]
    public void SeasonalRatesFollowTheDocumentedModel()
    {
        // Base 220: a January weekday is the base, a July Saturday carries summer
        // and weekend, a Christmas Friday carries holiday and weekend.
        Assert.Equal(220m, HotelAvailabilityModel.ModelledNightlyRate(220m, new DateOnly(2027, 1, 13)));
        Assert.Equal(Math.Round(220m * 1.25m * 1.10m, 0, MidpointRounding.ToEven),
            HotelAvailabilityModel.ModelledNightlyRate(220m, new DateOnly(2027, 7, 10)));
        Assert.Equal(Math.Round(220m * 1.35m * 1.10m, 0, MidpointRounding.ToEven),
            HotelAvailabilityModel.ModelledNightlyRate(220m, new DateOnly(2026, 12, 25)));
    }

    [Fact]
    public void BankersRoundingIsKeptAsDocumented()
    {
        // €302.50 is €302, not €303 — the rate model documents it, and a screen
        // that rounded the other way would disagree with the engine by a euro.
        // 275 × 1.10 on a plain Friday (15 Jan 2027) is 302.50 → 302 under banker's rounding.
        Assert.Equal(302m, HotelAvailabilityModel.ModelledNightlyRate(275m, new DateOnly(2027, 1, 15)));
        Assert.Equal(378m, HotelAvailabilityModel.ModelledNightlyRate(275m, new DateOnly(2027, 7, 10)));
        Assert.Equal(302m, Math.Round(302.5m, 0, MidpointRounding.ToEven));
    }

    // ----------------------------------------------------------- quotes

    [Fact]
    public void ATotalIsTheSumOfTheNightsAndNeverAnAverageMultipliedBack()
    {
        var hotel = new HotelDataService();
        var result = hotel.Quote(new StayRequest(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18), 2, 0), Today);

        Assert.True(result.IsValid);
        foreach (var q in result.Quotes)
        {
            Assert.Equal(3, q.Nights);
            Assert.Equal(3, q.NightlyRates.Count);
            Assert.Equal(q.NightlyRates.Sum(n => n.RateEur), q.TotalEur);
            Assert.Equal(Math.Round(q.TotalEur / 3, 0, MidpointRounding.ToEven), q.AvgNightlyEur);
            Assert.Equal(q.NightlyRates.Min(n => n.RoomsAvailable), q.RoomsAvailable);
        }
    }

    [Fact]
    public void TheLegacyQuoteStayAndTheNewQuoteAgree()
    {
        var hotel = new HotelDataService();
        var checkIn = new DateOnly(2026, 11, 2);
        var checkOut = new DateOnly(2026, 11, 5);

        var legacy = hotel.QuoteStay(checkIn, checkOut);
        var modern = hotel.Quote(new StayRequest(checkIn, checkOut, 2, 0), Today);

        Assert.Equal(legacy.Count, modern.Quotes.Count);
        foreach (var (l, m) in legacy.Zip(modern.Quotes))
        {
            Assert.Equal(l.Room.Code, m.Room.Code);
            Assert.Equal(l.TotalEur, m.TotalEur);
            Assert.Equal(l.RoomsAvailable, m.RoomsAvailable);
        }
    }

    [Fact]
    public void AStaffOverrideWinsForItsNightAndOnlyItsNight()
    {
        var hotel = new HotelDataService();
        var night = new DateOnly(2026, 10, 20);
        var modelledRate = hotel.GetNightlyRate("GARDEN", night);
        var modelledNext = hotel.GetNightlyRate("GARDEN", night.AddDays(1));

        hotel.Apply(new HotelCatalogSnapshot(HotelCatalogSnapshot.BuiltInRooms(),
            new[] { new HotelDailyOverride("garden", night, 199m, 0, "test") }, version: 7, source: "test"));

        Assert.Equal(199m, hotel.GetNightlyRate("GARDEN", night));
        Assert.Equal(0, hotel.GetAvailableRooms("GARDEN", night));
        Assert.Equal(modelledNext, hotel.GetNightlyRate("GARDEN", night.AddDays(1)));
        Assert.NotEqual(199m, modelledRate);

        var quote = hotel.Quote(new StayRequest(night, night.AddDays(2), 2, 0, "GARDEN"), Today);
        var garden = Assert.Single(quote.Quotes);
        Assert.False(garden.IsAvailable);                // the sold-out night decides the stay
        Assert.Equal(199m + modelledNext, garden.TotalEur);
        Assert.True(garden.HasOverrides);
        Assert.Equal(7, garden.SnapshotVersion);
    }

    [Fact]
    public void ApplyingASnapshotIsAtomicAndVersioned()
    {
        var hotel = new HotelDataService();
        var before = hotel.SnapshotVersion;

        var rooms = HotelCatalogSnapshot.BuiltInRooms()
            .Select(r => r.Code == "GARDEN" ? r with { BaseRateEur = 250m } : r)
            .ToList();
        hotel.Apply(new HotelCatalogSnapshot(rooms, version: before + 1, source: "catalogue-source"));

        Assert.Equal(before + 1, hotel.SnapshotVersion);
        Assert.Equal(250m, hotel.GetRoomType("GARDEN")!.BaseRateEur);
        Assert.Equal(250m, hotel.GetNightlyRate("GARDEN", new DateOnly(2027, 2, 3)));   // a plain weekday
    }

    // -------------------------------------------------------- validation

    [Theory]
    [InlineData(2026, 9, 10, 2026, 9, 10, 2, 0, null, StayRequestProblem.CheckOutNotAfterCheckIn)]
    [InlineData(2026, 9, 1, 2026, 9, 3, 2, 0, null, StayRequestProblem.CheckInInThePast)]
    [InlineData(2026, 9, 10, 2026, 10, 15, 2, 0, null, StayRequestProblem.TooManyNights)]
    [InlineData(2026, 9, 10, 2026, 9, 12, 0, 0, null, StayRequestProblem.NoAdults)]
    [InlineData(2026, 9, 10, 2026, 9, 12, 7, 0, null, StayRequestProblem.TooManyAdults)]
    [InlineData(2026, 9, 10, 2026, 9, 12, 2, 0, "PENTHOUSE", StayRequestProblem.UnknownRoom)]
    [InlineData(2026, 9, 10, 2026, 9, 12, 3, 1, "GARDEN", StayRequestProblem.PartyTooLarge)]
    [InlineData(2027, 9, 6, 2027, 9, 8, 2, 0, null, StayRequestProblem.TooFarAhead)]        // 366 days ahead of Today
    [InlineData(2026, 9, 10, 2026, 9, 12, 2, 7, null, StayRequestProblem.TooManyChildren)]
    public void AnInvalidRequestGetsAProblemAndNoQuotes(int iy, int im, int id, int oy, int om, int od, int adults, int children, string? room, StayRequestProblem expected)
    {
        var hotel = new HotelDataService();
        var result = hotel.Quote(new StayRequest(new DateOnly(iy, im, id), new DateOnly(oy, om, od), adults, children, room), Today);

        Assert.False(result.IsValid);
        Assert.Contains(expected, result.Problems);
        Assert.Empty(result.Quotes);
    }

    [Fact]
    public void CapacityIsEnforcedPerRoomTypeRatherThanCollectedForShow()
    {
        var hotel = new HotelDataService();
        var result = hotel.Quote(new StayRequest(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7), 3, 1), Today);

        Assert.True(result.IsValid);
        // Garden and Deluxe sleep 2 adults / 3 guests; four guests with three
        // adults fit only the Junior Suite and the Bosphorus Suite.
        Assert.False(result.Quotes.Single(q => q.Room.Code == "GARDEN").FitsParty);
        Assert.False(result.Quotes.Single(q => q.Room.Code == "DELUXE").FitsParty);
        Assert.False(result.Quotes.Single(q => q.Room.Code == "EXEC").FitsParty);
        Assert.True(result.Quotes.Single(q => q.Room.Code == "JRSUITE").FitsParty);
        Assert.True(result.Quotes.Single(q => q.Room.Code == "BOSSUITE").FitsParty);
        Assert.All(result.Bookable, q => Assert.NotEqual(false, q.FitsParty));
    }

    [Fact]
    public void AnInactiveRoomTypeIsNeitherListedNorQuoted()
    {
        var hotel = new HotelDataService();
        var rooms = HotelCatalogSnapshot.BuiltInRooms()
            .Select(r => r.Code == "EXEC" ? r with { IsActive = false } : r)
            .ToList();
        hotel.Apply(new HotelCatalogSnapshot(rooms, version: 1, source: "test"));

        Assert.DoesNotContain(hotel.ActiveRoomTypes, r => r.Code == "EXEC");
        Assert.Contains(hotel.RoomTypes, r => r.Code == "EXEC");   // the catalogue still lists it

        var all = hotel.Quote(new StayRequest(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7), 2, 0), Today);
        Assert.DoesNotContain(all.Quotes, q => q.Room.Code == "EXEC");

        var direct = hotel.Quote(new StayRequest(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7), 2, 0, "EXEC"), Today);
        Assert.Contains(StayRequestProblem.RoomInactive, direct.Problems);
    }

    // ------------------------------------------------------------ clock

    [Fact]
    public void TheClockCanBePinnedAndAnswersInIstanbul()
    {
        var pinned = new FixedHotelClock(new DateOnly(2026, 10, 1));
        Assert.True(pinned.IsPinned);
        Assert.Equal(new DateOnly(2026, 10, 1), pinned.Today);
        Assert.Equal(TimeSpan.FromHours(3), pinned.Now.Offset);

        var real = new SystemHotelClock(new FixedUtcTime(new DateTimeOffset(2026, 9, 4, 22, 30, 0, TimeSpan.Zero)));
        Assert.False(real.IsPinned);
        // 22:30 UTC is 01:30 the next day in Istanbul (UTC+3, no DST).
        Assert.Equal(new DateOnly(2026, 9, 5), real.Today);
        Assert.Equal(TimeSpan.FromHours(3), real.Now.Offset);
    }

    [Fact]
    public void TheEngineCanStartFromAnInMemoryCatalogueSourceAndReloadIt()
    {
        var source = new InMemoryHotelCatalogSource();
        var hotel = new HotelDataService(source);
        var night = new DateOnly(2026, 10, 15);
        var modelled = hotel.GetNightlyRate("GARDEN", night);

        source.SetOverride(new HotelDailyOverride("garden", night, 199m, 0, "maintenance"));
        Assert.Equal(modelled, hotel.GetNightlyRate("GARDEN", night));   // not applied until reloaded

        hotel.Reload(source);
        Assert.Equal(199m, hotel.GetNightlyRate("GARDEN", night));
        Assert.Equal(0, hotel.GetAvailableRooms("GARDEN", night));
        Assert.Equal(1, hotel.SnapshotVersion);
        Assert.Equal("in-memory", hotel.Snapshot.Source);
    }

    /// <summary>A time source frozen at one instant — enough for a clock test, with no testing package.</summary>
    private sealed class FixedUtcTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
