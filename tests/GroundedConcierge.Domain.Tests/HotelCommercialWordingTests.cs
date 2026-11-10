using GroundedConcierge.Domain;

namespace GroundedConcierge.Domain.Tests;

/// <summary>
/// The deterministic commercial answer in natural English and Turkish, with
/// the facts it carries byte-for-byte unchanged.
///
/// <para>
/// These tests pin the EXACT text produced for hand-built facts, so a singular
/// and a plural case, a child, a sold-out room, a party the room does not
/// sleep, a first candidate that qualified and earlier candidates that were
/// rejected are each asserted in both languages. The Turkish text takes no
/// English plural rule: a numeral is followed by the uninflected noun, as
/// Turkish grammar requires. The no-booking footer is on every answer.
/// </para>
/// </summary>
public class HotelCommercialWordingTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);
    private static readonly IReadOnlyList<HotelRoomType> Rooms = HotelCatalogSnapshot.BuiltInRooms();

    private static HotelRoomType Room(string code) => Rooms.Single(r => r.Code == code);

    private static StayQuote Quote(string code, int nights, int roomsAvailable, decimal total, bool? fits = true) =>
        new(Room(code), nights, roomsAvailable, total, Math.Round(total / nights, 0)) { FitsParty = fits };

    private static HotelAnswerFacts QuoteFacts(StayRequest stay, params StayQuote[] quotes) => new()
    {
        Stay = stay,
        Quotes = quotes,
        Catalogue = Rooms,
        Today = Today
    };

    private const string FooterEn = "Breakfast is included and city tax is excluded. This is a demonstration quote: no room is reserved, nothing is held and nothing is charged.";
    private const string FooterTr = "Fiyatlara kahvaltı dahildir, şehir vergisi hariçtir. Bu bir tanıtım teklifidir: hiçbir oda rezerve edilmedi, tutulmadı ve hiçbir ücret tahsil edilmedi.";

    // ------------------------------------------------------------ singular

    [Fact]
    public void AOneNightStayForOneAdultWithOneRoomLeftReadsInTheSingular()
    {
        var stay = new StayRequest(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 4), 1, 0);
        var facts = QuoteFacts(stay, Quote("GARDEN", 1, 1, 235m));

        Assert.Equal(
            "For 03.11.2026 to 04.11.2026 (1 night, 1 adult):\n" +
            "- Garden Room: 1 room available, €235 total for 1 night (average €235 a night).\n" +
            FooterEn,
            HotelAnswerComposer.Compose(facts, turkish: false));

        Assert.Equal(
            "03.11.2026 girişli, 04.11.2026 çıkışlı 1 gecelik konaklama (1 yetişkin):\n" +
            "- Bahçe Odası: 1 oda müsait, toplam €235 (1 gece, gecelik ortalama €235).\n" +
            FooterTr,
            HotelAnswerComposer.Compose(facts, turkish: true));
    }

    // -------------------------------------------------------------- plural

    [Fact]
    public void AThreeNightStayForTwoAdultsWithEightRoomsLeftReadsInThePlural()
    {
        var stay = new StayRequest(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 6), 2, 0);
        var facts = QuoteFacts(stay, Quote("GARDEN", 3, 8, 704m));

        Assert.Equal(
            "For 03.11.2026 to 06.11.2026 (3 nights, 2 adults):\n" +
            "- Garden Room: 8 rooms available, €704 total for 3 nights (average €235 a night).\n" +
            FooterEn,
            HotelAnswerComposer.Compose(facts, turkish: false));

        Assert.Equal(
            "03.11.2026 girişli, 06.11.2026 çıkışlı 3 gecelik konaklama (2 yetişkin):\n" +
            "- Bahçe Odası: 8 oda müsait, toplam €704 (3 gece, gecelik ortalama €235).\n" +
            FooterTr,
            HotelAnswerComposer.Compose(facts, turkish: true));
    }

    // ------------------------------------------------------------ children

    [Theory]
    [InlineData(1, "2 adults and 1 child", "2 yetişkin ve 1 çocuk")]
    [InlineData(2, "2 adults and 2 children", "2 yetişkin ve 2 çocuk")]
    public void ChildrenAreCountedInTheRightFormInEachLanguage(int children, string expectedEn, string expectedTr)
    {
        var stay = new StayRequest(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 5), 2, children);
        var facts = QuoteFacts(stay, Quote("JRSUITE", 2, 4, 1_040m));

        var en = HotelAnswerComposer.Compose(facts, turkish: false);
        var tr = HotelAnswerComposer.Compose(facts, turkish: true);

        Assert.StartsWith($"For 03.11.2026 to 05.11.2026 (2 nights, {expectedEn}):", en, StringComparison.Ordinal);
        Assert.StartsWith($"03.11.2026 girişli, 05.11.2026 çıkışlı 2 gecelik konaklama ({expectedTr}):", tr, StringComparison.Ordinal);
        Assert.DoesNotContain("child(ren)", en, StringComparison.Ordinal);
        Assert.DoesNotContain("çocuklar", tr, StringComparison.Ordinal);   // no English plural logic in Turkish
    }

    // ------------------------------------------------------------- sold out

    [Fact]
    public void ASoldOutRoomSaysSoAndStillStatesItsFigures()
    {
        var stay = new StayRequest(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 6), 2, 0);
        var facts = QuoteFacts(stay, Quote("GARDEN", 3, 2, 704m), Quote("BOSSUITE", 3, 0, 2_720m));

        var en = HotelAnswerComposer.Compose(facts, turkish: false);
        var tr = HotelAnswerComposer.Compose(facts, turkish: true);

        Assert.Contains("- Garden Room: 2 rooms available, €704 total for 3 nights (average €235 a night).", en, StringComparison.Ordinal);
        Assert.Contains("- Deluxe Bosphorus Suite: sold out for these dates, €2,720 total for 3 nights (average €907 a night).", en, StringComparison.Ordinal);
        Assert.Contains("- Deluxe Boğaz Süiti: bu tarihlerde dolu, toplam €2,720 (3 gece, gecelik ortalama €907).", tr, StringComparison.Ordinal);
        Assert.DoesNotContain("0 rooms available", en, StringComparison.Ordinal);
        Assert.DoesNotContain("0 oda müsait", tr, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ party too large

    [Fact]
    public void ARoomThatDoesNotSleepThePartyStatesItsCapacityInWords()
    {
        var stay = new StayRequest(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 4), 3, 1);
        var facts = QuoteFacts(stay, Quote("GARDEN", 1, 5, 235m, fits: false), Quote("JRSUITE", 1, 2, 520m));

        var en = HotelAnswerComposer.Compose(facts, turkish: false);
        var tr = HotelAnswerComposer.Compose(facts, turkish: true);

        Assert.Contains("- Garden Room: sleeps up to 2 adults / 3 guests, so it does not fit this party, €235 total for 1 night (average €235 a night).", en, StringComparison.Ordinal);
        Assert.Contains("- Palace Junior Suite: 2 rooms available, €520 total for 1 night (average €520 a night).", en, StringComparison.Ordinal);
        Assert.Contains("- Bahçe Odası: bu misafir sayısı için uygun değil (en fazla 2 yetişkin / 3 kişi), toplam €235 (1 gece, gecelik ortalama €235).", tr, StringComparison.Ordinal);
        Assert.Contains("- Saray Junior Süit: 2 oda müsait, toplam €520 (1 gece, gecelik ortalama €520).", tr, StringComparison.Ordinal);
    }

    // --------------------------------------- earliest: first candidate qualified

    [Fact]
    public void AnEarliestStayWhoseFirstCandidateQualifiedSaysNoEarlierDateWasExamined()
    {
        var stay = new StayRequest(new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 20), 2, 0);
        var facts = QuoteFacts(stay, Quote("DELUXE", 3, 6, 928m)) with
        {
            SearchedFrom = new DateOnly(2026, 10, 17),
            SearchedThrough = new DateOnly(2026, 10, 17),
            EarlierCandidatesRejected = false,
            RejectedCandidates = 0
        };

        Assert.Equal(
            "The earliest available 3-night stay is from 17.10.2026 to 20.10.2026, for 2 adults. " +
            "The search started on 17.10.2026 and this first date already had a room free; no earlier date was examined.\n" +
            "- Deluxe Room: 6 rooms available, €928 total for 3 nights (average €309 a night).\n" +
            FooterEn,
            HotelAnswerComposer.Compose(facts, turkish: false));

        Assert.Equal(
            "En erken müsait 3 gecelik konaklama: giriş 17.10.2026, çıkış 20.10.2026 (2 yetişkin). " +
            "Arama 17.10.2026 tarihinden başladı ve incelenen ilk tarih uygun çıktı; daha erken bir tarih incelenmedi.\n" +
            "- Deluxe Oda: 6 oda müsait, toplam €928 (3 gece, gecelik ortalama €309).\n" +
            FooterTr,
            HotelAnswerComposer.Compose(facts, turkish: true));
    }

    // ------------------------------------- earliest: earlier candidates rejected

    [Fact]
    public void AnEarliestStayAfterSeveralRejectedCandidatesNamesTheirCountAndRange()
    {
        var stay = new StayRequest(new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 24), 2, 1);
        var facts = QuoteFacts(stay, Quote("JRSUITE", 2, 1, 1_040m)) with
        {
            SearchedFrom = new DateOnly(2026, 10, 17),
            SearchedThrough = new DateOnly(2026, 10, 22),
            EarlierCandidatesRejected = true,
            RejectedCandidates = 5
        };

        var en = HotelAnswerComposer.Compose(facts, turkish: false);
        var tr = HotelAnswerComposer.Compose(facts, turkish: true);

        Assert.StartsWith(
            "The earliest available 2-night stay is from 22.10.2026 to 24.10.2026, for 2 adults and 1 child. " +
            "The 5 earlier check-in dates from 17.10.2026 to 21.10.2026 were also checked, and none had a room free on every night.\n" +
            "- Palace Junior Suite: 1 room available, €1,040 total for 2 nights (average €520 a night).",
            en, StringComparison.Ordinal);
        Assert.StartsWith(
            "En erken müsait 2 gecelik konaklama: giriş 22.10.2026, çıkış 24.10.2026 (2 yetişkin ve 1 çocuk). " +
            "17.10.2026 ile 21.10.2026 arasındaki 5 giriş tarihi de incelendi; hiçbirinde her gece için boş oda yoktu.\n" +
            "- Saray Junior Süit: 1 oda müsait, toplam €1,040 (2 gece, gecelik ortalama €520).",
            tr, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate check-in date(s)", en, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEarliestStayAfterExactlyOneRejectedCandidateNamesThatDateInTheSingular()
    {
        var stay = new StayRequest(new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 19), 1, 0);
        var facts = QuoteFacts(stay, Quote("GARDEN", 1, 3, 235m)) with
        {
            SearchedFrom = new DateOnly(2026, 10, 17),
            SearchedThrough = new DateOnly(2026, 10, 18),
            EarlierCandidatesRejected = true,
            RejectedCandidates = 1
        };

        var en = HotelAnswerComposer.Compose(facts, turkish: false);
        var tr = HotelAnswerComposer.Compose(facts, turkish: true);

        Assert.Contains("The earliest available 1-night stay is from 18.10.2026 to 19.10.2026, for 1 adult. The earlier check-in date 17.10.2026 was also checked, and it had no room free on every night.", en, StringComparison.Ordinal);
        Assert.Contains("En erken müsait 1 gecelik konaklama: giriş 18.10.2026, çıkış 19.10.2026 (1 yetişkin). 17.10.2026 giriş tarihi de incelendi; o tarihte her gece için boş oda yoktu.", tr, StringComparison.Ordinal);
    }

    // --------------------------------------- the rejection count is metadata

    /// <summary>
    /// The sentence about earlier dates is written from the engine's own search
    /// result, never inferred from the dates: a real search whose first
    /// candidate qualifies gets the "no earlier date" sentence, and one that
    /// skipped sold-out candidates gets exactly their count.
    /// </summary>
    [Fact]
    public void TheEarlierDatesSentenceFollowsTheEnginesSearchMetadata()
    {
        var open = EngineWith((_, _) => 5);
        var first = open.FindEarliestAvailableStay(new EarliestStayRequest(Today, 3, 2, 0), Today);
        Assert.True(first is { Found: true, EarlierCandidatesRejected: false });
        var firstText = HotelAnswerComposer.Compose(Facts(open, first), turkish: false);
        Assert.Contains("no earlier date was examined", firstText, StringComparison.Ordinal);
        Assert.DoesNotContain("were also checked", firstText, StringComparison.Ordinal);

        var blocked = EngineWith((_, date) => date < Today.AddDays(4) ? 0 : 5);
        var later = blocked.FindEarliestAvailableStay(new EarliestStayRequest(Today, 3, 2, 0), Today);
        Assert.True(later is { Found: true, EarlierCandidatesRejected: true, RejectedCandidates: 4 });
        var laterText = HotelAnswerComposer.Compose(Facts(blocked, later), turkish: false);
        Assert.Contains($"The 4 earlier check-in dates from {Today:dd.MM.yyyy} to {Today.AddDays(3):dd.MM.yyyy} were also checked, and none had a room free on every night.", laterText, StringComparison.Ordinal);
    }

    // ------------------------------------- the no-availability fixed answer

    [Fact]
    public void TheNoAvailabilityAnswerCountsThePartyInNaturalEnglish()
    {
        var closed = EngineWith((_, _) => 0, days: 400);
        var none = closed.FindEarliestAvailableStay(new EarliestStayRequest(Today, 2, 1, 1), Today);
        Assert.False(none.Found);

        var en = GuestMessages.NoAvailability(none, turkish: false);
        var tr = GuestMessages.NoAvailability(none, turkish: true);

        Assert.Contains("for 1 adult and 1 child.", en, StringComparison.Ordinal);
        Assert.DoesNotContain("adult(s)", en, StringComparison.Ordinal);
        Assert.DoesNotContain("child(ren)", en, StringComparison.Ordinal);
        Assert.Contains("1 yetişkin ve 1 çocuk için", tr, StringComparison.Ordinal);

        var two = closed.FindEarliestAvailableStay(new EarliestStayRequest(Today, 2, 2, 2), Today);
        Assert.Contains("for 2 adults and 2 children.", GuestMessages.NoAvailability(two, turkish: false), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- helpers

    private static HotelDataService EngineWith(Func<string, DateOnly, int> roomsOn, int days = 40)
    {
        var overrides = new List<HotelDailyOverride>();
        for (var i = 0; i < days; i++)
        {
            var date = Today.AddDays(i);
            foreach (var room in Rooms)
                overrides.Add(new HotelDailyOverride(room.Code, date, null, roomsOn(room.Code, date), "test"));
        }
        return new HotelDataService(new HotelCatalogSnapshot(Rooms, overrides, version: 3, source: "test"));
    }

    private static HotelAnswerFacts Facts(HotelDataService hotel, EarliestStayResult result) => new()
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
