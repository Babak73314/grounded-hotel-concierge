using GroundedConcierge.Domain;

namespace GroundedConcierge.Domain.Tests;

/// <summary>
/// The deterministic date/party/room parser behind the concierge's live
/// quotes. Every date here is resolved against a pinned "today", never the
/// machine clock, which is what makes the assertions repeatable.
/// </summary>
public class HotelStayIntentParserTests
{
    // Saturday, 5 September 2026.
    private static readonly DateOnly Today = new(2026, 9, 5);

    [Theory]
    [InlineData("15-18 Ekim arası 2 yetişkin için fiyat nedir?", 2026, 10, 15, 2026, 10, 18, "tr")]
    [InlineData("15 Ekim - 18 Ekim, 2 kişi", 2026, 10, 15, 2026, 10, 18, "tr")]
    [InlineData("How much for October 15 to 18 for two adults?", 2026, 10, 15, 2026, 10, 18, "en")]
    [InlineData("price for 15th of October to 18th of October", 2026, 10, 15, 2026, 10, 18, "en")]
    [InlineData("12.10.2026 - 14.10.2026 arası boş oda var mı?", 2026, 10, 12, 2026, 10, 14, "tr")]
    [InlineData("availability 2026-11-03 to 2026-11-06", 2026, 11, 3, 2026, 11, 6, "en")]
    public void ExplicitDateRangesAreReadInBothLanguages(string text, int iy, int im, int id, int oy, int om, int od, string lang)
    {
        var intent = HotelStayIntentParser.Parse(text, Today);

        Assert.True(intent.IsStayQuery);
        Assert.Equal(new DateOnly(iy, im, id), intent.CheckIn);
        Assert.Equal(new DateOnly(oy, om, od), intent.CheckOut);
        Assert.Equal(lang, intent.Language);
        Assert.Equal(StayIntentGap.None, intent.Gap);
    }

    [Fact]
    public void ACheckInWithALengthOfStayComputesTheCheckOut()
    {
        var intent = HotelStayIntentParser.Parse("20 Ekim'den itibaren 3 gece kalmak istiyoruz", Today);
        Assert.Equal(new DateOnly(2026, 10, 20), intent.CheckIn);
        Assert.Equal(new DateOnly(2026, 10, 23), intent.CheckOut);
        Assert.Equal(3, intent.Nights);
    }

    [Fact]
    public void RelativeDatesResolveAgainstThePropertysToday()
    {
        // Today is a Saturday: "this weekend" is today and tomorrow night.
        var thisWeekend = HotelStayIntentParser.Parse("this weekend for 2 adults, price?", Today);
        Assert.Equal(Today, thisWeekend.CheckIn);
        Assert.Equal(Today.AddDays(2), thisWeekend.CheckOut);

        var nextWeekend = HotelStayIntentParser.Parse("gelecek hafta sonu fiyat nedir", Today);
        Assert.Equal(Today.AddDays(7), nextWeekend.CheckIn);
        Assert.Equal(Today.AddDays(9), nextWeekend.CheckOut);

        var tomorrow = HotelStayIntentParser.Parse("yarın için 2 gece oda var mı", Today);
        Assert.Equal(Today.AddDays(1), tomorrow.CheckIn);
        Assert.Equal(Today.AddDays(3), tomorrow.CheckOut);
    }

    [Fact]
    public void AMonthAlreadyGoneMeansNextYear()
    {
        var intent = HotelStayIntentParser.Parse("3-5 Mart fiyat", Today);
        Assert.Equal(new DateOnly(2027, 3, 3), intent.CheckIn);
        Assert.Equal(new DateOnly(2027, 3, 5), intent.CheckOut);
    }

    [Theory]
    [InlineData("2 yetişkin 1 çocuk", 2, 1)]
    [InlineData("two adults and a child", 2, 1)]
    [InlineData("3 adults, no kids", 3, 0)]
    [InlineData("iki kişilik", 2, null)]
    public void PartySizesAreReadInBothLanguages(string text, int? adults, int? children)
    {
        var intent = HotelStayIntentParser.Parse(text + " 15-16 Ekim", Today);
        Assert.Equal(adults, intent.Adults);
        Assert.Equal(children, intent.Children);
    }

    [Theory]
    [InlineData("Executive oda ne kadar?", "EXEC")]
    [InlineData("Is the Deluxe Bosphorus Suite free?", "BOSSUITE")]
    [InlineData("deluxe oda", "DELUXE")]
    [InlineData("junior suite please", "JRSUITE")]
    [InlineData("bahçe odası fiyatı", "GARDEN")]
    public void RoomTypesAreRecognisedByNameInBothLanguages(string text, string code)
    {
        Assert.Equal(code, HotelStayIntentParser.Parse(text, Today).RoomCode);
    }

    [Fact]
    public void AStayQuestionWithoutDatesIsAGapNotAGuess()
    {
        var intent = HotelStayIntentParser.Parse("Boğaz manzaralı bir oda ne kadar?", Today);
        Assert.True(intent.IsStayQuery);
        Assert.Null(intent.CheckIn);
        Assert.Equal(StayIntentGap.MissingDates, intent.Gap);
    }

    [Fact]
    public void ACheckInAloneAsksForTheCheckOut()
    {
        var intent = HotelStayIntentParser.Parse("15 Ekim için oda var mı?", Today);
        Assert.True(intent.IsStayQuery);
        Assert.Equal(new DateOnly(2026, 10, 15), intent.CheckIn);
        Assert.Null(intent.CheckOut);
        Assert.Equal(StayIntentGap.MissingCheckOut, intent.Gap);
    }

    [Fact]
    public void APastDateIsAnInvalidRange()
    {
        var intent = HotelStayIntentParser.Parse("1-3 Eylül 2026 fiyat", Today);
        Assert.Equal(StayIntentGap.InvalidRange, intent.Gap);
    }

    [Fact]
    public void AFollowUpNamingOnlyARoomReusesTheEarlierDates()
    {
        var state = new HotelConversationState();
        state.Absorb(HotelStayIntentParser.Parse("15-18 Ekim 2 yetişkin fiyat?", Today, state));
        Assert.True(state.HasDates);

        var followUp = HotelStayIntentParser.Parse("Peki Executive oda?", Today, state);
        Assert.True(followUp.IsStayQuery);
        Assert.Equal("EXEC", followUp.RoomCode);
        Assert.Equal(StayIntentGap.None, followUp.Gap);

        state.Absorb(followUp);
        var request = state.ToRequest()!;
        Assert.Equal(new DateOnly(2026, 10, 15), request.CheckIn);
        Assert.Equal(new DateOnly(2026, 10, 18), request.CheckOut);
        Assert.Equal("EXEC", request.RoomCode);
        Assert.Equal(2, request.Adults);
    }

    [Fact]
    public void AskingAboutAllRoomsDropsARememberedRoomPreference()
    {
        var state = new HotelConversationState();
        state.Absorb(HotelStayIntentParser.Parse("15-18 Ekim 2 yetişkin, Executive oda fiyatı?", Today, state));
        Assert.Equal("EXEC", state.RoomCode);

        var all = HotelStayIntentParser.Parse("Peki hangi odalar müsait?", Today, state);
        Assert.True(all.IsStayQuery);
        Assert.True(all.ClearsRoom);
        Assert.Null(all.RoomCode);

        state.Absorb(all);
        Assert.Null(state.RoomCode);
        Assert.True(state.HasDates);   // the dates survive; only the room preference is dropped
    }

    [Theory]
    [InlineData("Kahvaltı saatleri nedir?")]
    [InlineData("Tell me about the spa and hammam.")]
    [InlineData("Do you allow pets?")]
    [InlineData("Merhaba!")]
    public void ADocumentQuestionIsNotAStayQuery(string text)
    {
        var intent = HotelStayIntentParser.Parse(text, Today);
        Assert.False(intent.IsStayQuery);
        Assert.Null(intent.CheckIn);
    }

    // A generic price word ("how much", "ne kadar", "price", "fiyat", "cost",
    // "ücret") is shared by "How much is the airport transfer?" and "How much is
    // a Deluxe room?". On its own it decides nothing: the first is a knowledge
    // question the documents answer, the second a stay question the engine
    // quotes. What tells them apart is a STAY ANCHOR — a room word or type, a
    // stay/reservation/availability word, dates — never the price word.
    [Theory]
    [InlineData("How much is the airport transfer?")]
    [InlineData("How much does the spa cost?")]
    [InlineData("Havalimanı transferi ne kadar?")]
    [InlineData("Spa ücreti nedir?")]
    [InlineData("How much is breakfast?")]
    [InlineData("Otopark ücreti ne kadar?")]
    [InlineData("What is the exchange rate?")]
    [InlineData("Masaj fiyatları nedir?")]
    public void AServicePriceQuestionIsAKnowledgeQuestionNotAStayQuery(string text)
    {
        var intent = HotelStayIntentParser.Parse(text, Today);
        Assert.False(intent.IsStayQuery);
        Assert.Null(intent.CheckIn);
        Assert.Equal(StayIntentGap.None, intent.Gap);
    }

    [Theory]
    [InlineData("How much is a Deluxe room?", "DELUXE")]
    [InlineData("Deluxe oda ne kadar?", "DELUXE")]
    [InlineData("Executive ne kadar?", "EXEC")]
    [InlineData("How much is the Bosphorus Suite?", "BOSSUITE")]
    [InlineData("How much for 15-18 October?", null)]
    [InlineData("15-18 Ekim için fiyat nedir?", null)]
    [InlineData("Is the Junior Suite available this weekend?", "JRSUITE")]
    [InlineData("Bu hafta sonu boş odanız var mı?", null)]
    [InlineData("2 gece kalmak istiyoruz, fiyat nedir?", null)]
    [InlineData("I would like to book a room for tomorrow", null)]
    public void APriceQuestionWithAStayAnchorIsStillAStayQuery(string text, string? room)
    {
        var intent = HotelStayIntentParser.Parse(text, Today);
        Assert.True(intent.IsStayQuery);
        Assert.Equal(room, intent.RoomCode);
    }

    // ------------------------------------------ earliest-availability search

    // 9 + 10. A check-in/check-out TIME question is hotel POLICY — the documents
    // answer it. Neither a stay question nor a search, whatever else it says.
    [Theory]
    [InlineData("What time is check-in?")]
    [InlineData("What is your earliest check-in time?")]
    [InlineData("What time is check-out?")]
    [InlineData("Can I check in early?")]
    [InlineData("Can I have a late check-out?")]
    [InlineData("Check-in saat kaçta?")]
    [InlineData("Giriş saati nedir?")]
    [InlineData("Erken giriş yapabilir miyim?")]
    [InlineData("Geç çıkış mümkün mü?")]
    public void ACheckInOrCheckOutTimeQuestionIsHotelPolicyNotAStayOrASearch(string text)
    {
        var intent = HotelStayIntentParser.Parse(text, Today);
        Assert.False(intent.IsStayQuery);
        Assert.False(intent.SearchEarliestAvailability);
        Assert.Null(intent.CheckIn);
        Assert.Equal(StayIntentGap.None, intent.Gap);
    }

    // 11 + 12. "Earliest / first available / en erken / ilk müsait" asks the
    // engine to FIND the date: a search, never a request for dates. With the
    // length of stay given the search can run; without it, the ONE thing asked
    // for is the number of nights.
    [Theory]
    [InlineData("What is the earliest available room for 3 nights?", 3, null, null, StayIntentGap.None)]
    [InlineData("I need a room for three nights as soon as possible.", 3, null, null, StayIntentGap.None)]
    [InlineData("Find the first available date for 2 adults for 4 nights.", 4, 2, null, StayIntentGap.None)]
    [InlineData("When is the earliest Deluxe room available?", null, null, "DELUXE", StayIntentGap.MissingNights)]
    [InlineData("I want to reserve a room for three days, what is the earliest time slot?", 3, null, null, StayIntentGap.None)]
    [InlineData("En erken 3 gecelik müsait oda ne zaman?", 3, null, null, StayIntentGap.None)]
    [InlineData("3 gece için ilk müsait tarihi bul.", 3, null, null, StayIntentGap.None)]
    [InlineData("2 yetişkin için en yakın müsait odayı bul.", null, 2, null, StayIntentGap.MissingNights)]
    [InlineData("Deluxe oda en erken ne zaman müsait?", null, null, "DELUXE", StayIntentGap.MissingNights)]
    [InlineData("Executive oda için en erken müsait tarih?", null, null, "EXEC", StayIntentGap.MissingNights)]
    public void AnEarliestAvailabilityRequestIsASearchNotAQuestionForDates(string text, int? nights, int? adults, string? room, StayIntentGap gap)
    {
        var intent = HotelStayIntentParser.Parse(text, Today);

        Assert.True(intent.IsStayQuery);
        Assert.True(intent.SearchEarliestAvailability);
        Assert.Equal(nights, intent.Nights);
        Assert.Equal(adults, intent.Adults);
        Assert.Equal(room, intent.RoomCode);
        Assert.Equal(gap, intent.Gap);
        Assert.Null(intent.CheckIn);   // the date is what is being searched for
        Assert.Null(intent.CheckOut);
        Assert.NotEqual(StayIntentGap.MissingDates, intent.Gap);
    }

    [Fact]
    public void AnEarliestSearchAboutAPricedServiceIsAKnowledgeQuestion()
    {
        var spa = HotelStayIntentParser.Parse("What is the earliest spa appointment available?", Today);
        Assert.False(spa.SearchEarliestAvailability);
        Assert.False(spa.IsStayQuery);
    }

    // 13 + 14. Follow-ups: search mode is the conversation's until explicit
    // dates end it; a follow-up that names a room or a length RE-RUNS the search
    // (the found dates are never stored), and the language state is untouched.
    [Fact]
    public void AFollowUpNamingARoomReRunsTheEarliestSearchWithTheSameNightsAndParty()
    {
        var state = new HotelConversationState("en");
        var first = HotelStayIntentParser.Parse("Find the earliest room for 3 nights for 2 adults.", Today, state);
        Assert.True(first.SearchEarliestAvailability);
        state.Absorb(first);

        Assert.True(state.SearchEarliest);
        Assert.Equal(3, state.RequestedNights);
        Assert.Equal(2, state.Adults);
        Assert.False(state.HasDates);   // the engine's found dates are shown, not stored
        Assert.Equal(new EarliestStayRequest(Today, 3, 2, 0, null), state.ToEarliestRequest(Today));

        var followUp = HotelStayIntentParser.Parse("What about Executive?", Today, state);
        Assert.True(followUp.IsStayQuery);
        Assert.True(followUp.SearchEarliestAvailability);   // inherited from search mode
        Assert.Equal("EXEC", followUp.RoomCode);
        Assert.Equal(StayIntentGap.None, followUp.Gap);       // the nights are known from the earlier turn
        Assert.Equal("en", followUp.Language);
        state.Absorb(followUp);

        Assert.True(state.SearchEarliest);
        Assert.Equal(new EarliestStayRequest(Today, 3, 2, 0, "EXEC"), state.ToEarliestRequest(Today));

        // Explicit dates end search mode and return to ordinary quoting.
        var dated = HotelStayIntentParser.Parse("Actually, October 15 to 18.", Today, state);
        Assert.False(dated.SearchEarliestAvailability);
        state.Absorb(dated);
        Assert.False(state.SearchEarliest);
        Assert.True(state.HasDates);
        Assert.Null(state.ToEarliestRequest(Today));
        Assert.Equal("EXEC", state.RoomCode);   // the room preference survives the switch
    }

    [Fact]
    public void ALengthGivenLaterReRunsTheEarliestSearchForTheRememberedRoomInTurkish()
    {
        var state = new HotelConversationState();
        var first = HotelStayIntentParser.Parse("Deluxe oda en erken ne zaman müsait?", Today, state);
        Assert.Equal(StayIntentGap.MissingNights, first.Gap);
        state.Absorb(first);
        Assert.True(state.SearchEarliest);
        Assert.Equal("DELUXE", state.RoomCode);
        Assert.Null(state.ToEarliestRequest(Today));   // nothing to search yet

        var nights = HotelStayIntentParser.Parse("4 gece olsun.", Today, state);
        Assert.True(nights.SearchEarliestAvailability);
        Assert.Equal(4, nights.Nights);
        Assert.Equal(StayIntentGap.None, nights.Gap);
        Assert.Equal("tr", nights.Language);
        state.Absorb(nights);

        Assert.Equal(new EarliestStayRequest(Today, 4, 2, 0, "DELUXE"), state.ToEarliestRequest(Today));
        Assert.Equal("tr", state.Language);

        // A turn with no language signal keeps Turkish while in search mode too.
        Assert.Equal("tr", HotelStayIntentParser.Parse("Executive?", Today, state).Language);
    }

    // ------------------------------------------ clearing a room preference

    // A phrase that itself names rooms clears a remembered room type anywhere
    // in a turn, in either language — and stays inside search mode.
    [Theory]
    [InlineData("Any room is fine.")]
    [InlineData("any room")]
    [InlineData("Any room type.")]
    [InlineData("No room preference.")]
    [InlineData("I don't have a room preference.")]
    [InlineData("I don’t have a room preference.")]
    [InlineData("Whichever room.")]
    [InlineData("Any available room.")]
    [InlineData("Room doesn't matter.")]
    [InlineData("Room type does not matter.")]
    [InlineData("All rooms.")]
    [InlineData("Herhangi bir oda.")]
    [InlineData("Herhangi bir oda olur.")]
    [InlineData("Oda fark etmez.")]
    [InlineData("Oda tipi fark etmez.")]
    [InlineData("Müsait herhangi bir oda.")]
    [InlineData("Tüm odalar.")]
    [InlineData("Bütün odalar.")]
    public void ANoPreferencePhraseThatNamesRoomsClearsTheRememberedRoom(string text)
    {
        var state = new HotelConversationState();
        state.Absorb(HotelStayIntentParser.Parse("Find the earliest Executive room for 3 nights for 2 adults.", Today, state));
        Assert.Equal("EXEC", state.RoomCode);
        Assert.True(state.SearchEarliest);

        var intent = HotelStayIntentParser.Parse(text, Today, state);
        Assert.True(intent.ClearsRoom);
        Assert.Null(intent.RoomCode);
        Assert.True(intent.IsStayQuery);
        Assert.True(intent.SearchEarliestAvailability);
        Assert.Equal(StayIntentGap.None, intent.Gap);

        state.Absorb(intent);
        Assert.Null(state.RoomCode);
        Assert.True(state.SearchEarliest);
        Assert.Equal(new EarliestStayRequest(Today, 3, 2, 0, null), state.ToEarliestRequest(Today));

        // With no conversation at all the phrase still names no room — nothing is invented.
        Assert.True(HotelStayIntentParser.Parse(text, Today).ClearsRoom);
        Assert.Null(HotelStayIntentParser.Parse(text, Today).RoomCode);
    }

    // A bare "doesn't matter" names no room: it clears the preference only when
    // it is the WHOLE turn and a room preference is on the table inside an
    // active stay or search — the guest is answering the room question.
    [Theory]
    [InlineData("It doesn't matter.")]
    [InlineData("Doesn't matter")]
    [InlineData("No preference.")]
    [InlineData("Fark etmez.")]
    [InlineData("Farketmez")]
    [InlineData("Hangisi olursa olsun.")]
    public void ABareNoPreferenceTurnClearsTheRoomOnlyWhenARoomPreferenceIsOnTheTable(string text)
    {
        // No conversation: nothing to clear, and not a stay question either.
        var alone = HotelStayIntentParser.Parse(text, Today);
        Assert.False(alone.ClearsRoom);
        Assert.False(alone.IsStayQuery);

        // A search with no room preference: still nothing to clear.
        var noRoom = new HotelConversationState();
        noRoom.Absorb(HotelStayIntentParser.Parse("Earliest room for 3 nights?", Today, noRoom));
        Assert.Null(noRoom.RoomCode);
        Assert.False(HotelStayIntentParser.Parse(text, Today, noRoom).ClearsRoom);

        // A search WITH a room preference: the bare phrase answers the room question.
        var withRoom = new HotelConversationState();
        withRoom.Absorb(HotelStayIntentParser.Parse("Earliest Executive room for 3 nights?", Today, withRoom));
        Assert.Equal("EXEC", withRoom.RoomCode);
        var answer = HotelStayIntentParser.Parse(text, Today, withRoom);
        Assert.True(answer.ClearsRoom);
        Assert.True(answer.IsStayQuery);
        Assert.True(answer.SearchEarliestAvailability);
        withRoom.Absorb(answer);
        Assert.Null(withRoom.RoomCode);
        Assert.True(withRoom.SearchEarliest);

        // Inside a sentence about something else the phrase is not a room answer.
        var sentence = HotelStayIntentParser.Parse("Breakfast time " + text.TrimEnd('.') + " to us.", Today, withRoom);
        Assert.False(sentence.ClearsRoom);
    }

    [Fact]
    public void AnyRoomIsFineAfterAnExecutiveFollowUpReRunsTheSearchAcrossAllRooms()
    {
        var state = new HotelConversationState("en");

        // 1. earliest any-room search
        state.Absorb(HotelStayIntentParser.Parse("I need a room for 3 nights for 2 adults. What is the earliest available date?", Today, state));
        Assert.True(state.SearchEarliest);
        Assert.Equal(3, state.RequestedNights);
        Assert.Equal(2, state.Adults);
        Assert.Null(state.RoomCode);

        // 2. "What about Executive?" → Executive earliest search
        state.Absorb(HotelStayIntentParser.Parse("What about Executive?", Today, state));
        Assert.Equal("EXEC", state.RoomCode);
        Assert.Equal(new EarliestStayRequest(Today, 3, 2, 0, "EXEC"), state.ToEarliestRequest(Today));

        // 3. "Any room is fine." → the preference is cleared and the search covers every room again
        var any = HotelStayIntentParser.Parse("Any room is fine.", Today, state);
        Assert.True(any.ClearsRoom);
        Assert.True(any.SearchEarliestAvailability);
        Assert.Equal(StayIntentGap.None, any.Gap);
        Assert.Equal("en", any.Language);
        state.Absorb(any);

        Assert.Null(state.RoomCode);
        Assert.True(state.SearchEarliest);
        Assert.Equal(new EarliestStayRequest(Today, 3, 2, 0, null), state.ToEarliestRequest(Today));
        Assert.Equal("en", state.Language);
    }

    [Fact]
    public void HerhangiBirOdaOlurAfterAnExecutiveFollowUpReRunsTheSearchAcrossAllRoomsInTurkish()
    {
        var state = new HotelConversationState();

        state.Absorb(HotelStayIntentParser.Parse("2 yetişkin için 3 gece kalacağız, en erken müsait tarih nedir?", Today, state));
        Assert.True(state.SearchEarliest);
        Assert.Equal(3, state.RequestedNights);
        Assert.Equal(2, state.Adults);

        state.Absorb(HotelStayIntentParser.Parse("Peki Executive?", Today, state));
        Assert.Equal("EXEC", state.RoomCode);

        var any = HotelStayIntentParser.Parse("Herhangi bir oda olur.", Today, state);
        Assert.True(any.ClearsRoom);
        Assert.True(any.SearchEarliestAvailability);
        Assert.Equal("tr", any.Language);
        state.Absorb(any);

        Assert.Null(state.RoomCode);
        Assert.Equal(new EarliestStayRequest(Today, 3, 2, 0, null), state.ToEarliestRequest(Today));
        Assert.Equal("tr", state.Language);
    }

    [Fact]
    public void AskingForTheEarliestDateAfterStatingDatesKeepsTheLengthAndDropsTheDates()
    {
        var state = new HotelConversationState();
        state.Absorb(HotelStayIntentParser.Parse("15-18 Ekim 2 yetişkin fiyat?", Today, state));
        Assert.True(state.HasDates);

        var earliest = HotelStayIntentParser.Parse("Peki en erken ne zaman müsait?", Today, state);
        Assert.True(earliest.SearchEarliestAvailability);
        Assert.Equal(StayIntentGap.None, earliest.Gap);   // 3 nights are implied by the dates already given
        state.Absorb(earliest);

        Assert.True(state.SearchEarliest);
        Assert.False(state.HasDates);
        Assert.Equal(new EarliestStayRequest(Today, 3, 2, 0, null), state.ToEarliestRequest(Today));
    }

    [Fact]
    public void InsideAStayConversationABarePriceWordReQuotesButAServicePriceStaysAKnowledgeQuestion()
    {
        var state = new HotelConversationState();
        state.Absorb(HotelStayIntentParser.Parse("Bu hafta sonu 2 yetişkin için fiyat nedir?", Today, state));
        Assert.True(state.HasDates);

        // "and the price?" with dates already on the table is the same stay question again …
        Assert.True(HotelStayIntentParser.Parse("Peki fiyat ne kadar?", Today, state).IsStayQuery);
        Assert.True(HotelStayIntentParser.Parse("And how much would that cost?", Today, state).IsStayQuery);

        // … but a priced hotel SERVICE is a knowledge question even now, and it
        // leaves the stay context alone.
        Assert.False(HotelStayIntentParser.Parse("Havalimanı transferi ne kadar?", Today, state).IsStayQuery);
        Assert.False(HotelStayIntentParser.Parse("How much is the airport transfer?", Today, state).IsStayQuery);
        Assert.False(HotelStayIntentParser.Parse("Spa ücreti nedir?", Today, state).IsStayQuery);
        Assert.False(HotelStayIntentParser.Parse("How much does the spa cost?", Today, state).IsStayQuery);
        Assert.True(state.HasDates);
    }

    [Fact]
    public void LanguageIsDetectedFromTurkishLettersAndFunctionWords()
    {
        Assert.Equal("tr", HotelStayIntentParser.Parse("Havalimanı transferi ne kadar?", Today).Language);
        Assert.Equal("tr", HotelStayIntentParser.Parse("Oda fiyatlari nedir", Today).Language);   // no accents, a Turkish word
        Assert.Equal("en", HotelStayIntentParser.Parse("How much is the airport transfer?", Today).Language);
    }

    [Fact]
    public void ATurnWithNoLanguageSignalKeepsTheConversationsLanguage()
    {
        // A bare date says nothing about its language; the answer must not flip
        // a Turkish conversation to English (or an English one to Turkish).
        Assert.Null(HotelStayIntentParser.DetectLanguage("18.10.2026"));
        Assert.Null(HotelStayIntentParser.DetectLanguage("2026-10-18"));
        Assert.Null(HotelStayIntentParser.DetectLanguage("3"));
        Assert.Equal("tr", HotelStayIntentParser.DetectLanguage("18 Ekim"));
        Assert.Equal("en", HotelStayIntentParser.DetectLanguage("October 18"));

        var turkish = new HotelConversationState();
        turkish.Absorb(HotelStayIntentParser.Parse("15 Ekim için oda var mı?", Today, turkish));
        Assert.Equal("tr", HotelStayIntentParser.Parse("18.10.2026", Today, turkish).Language);

        var english = new HotelConversationState("en");
        Assert.Equal("en", HotelStayIntentParser.Parse("18.10.2026", Today, english).Language);
        english.Absorb(HotelStayIntentParser.Parse("Is October 15 available?", Today, english));
        Assert.Equal("en", HotelStayIntentParser.Parse("2026-10-18", Today, english).Language);

        // With no state at all the demo's default language wins.
        Assert.Equal("tr", HotelStayIntentParser.Parse("18.10.2026", Today).Language);
    }

    [Fact]
    public void ABareLaterDateAnswersTheCheckOutQuestion()
    {
        // Turn 1 asks for a check-out; turn 2 is the guest's answer, not a new check-in.
        var state = new HotelConversationState();
        var first = HotelStayIntentParser.Parse("15 Ekim için oda var mı?", Today, state);
        Assert.Equal(StayIntentGap.MissingCheckOut, first.Gap);
        state.Absorb(first);
        Assert.False(state.HasDates);

        var answer = HotelStayIntentParser.Parse("18 Ekim", Today, state);
        Assert.True(answer.IsStayQuery);
        Assert.Equal(StayIntentGap.None, answer.Gap);
        Assert.Equal(new DateOnly(2026, 10, 15), answer.CheckIn);
        Assert.Equal(new DateOnly(2026, 10, 18), answer.CheckOut);
        Assert.Equal("tr", answer.Language);

        state.Absorb(answer);
        Assert.True(state.HasDates);
        Assert.Equal(3, state.Nights);
    }

    [Fact]
    public void ALengthOfStayAnswersTheCheckOutQuestion()
    {
        var state = new HotelConversationState();
        state.Absorb(HotelStayIntentParser.Parse("15 Ekim için oda var mı?", Today, state));

        var answer = HotelStayIntentParser.Parse("3 gece", Today, state);
        Assert.Equal(StayIntentGap.None, answer.Gap);
        Assert.Equal(new DateOnly(2026, 10, 15), answer.CheckIn);
        Assert.Equal(new DateOnly(2026, 10, 18), answer.CheckOut);

        state.Absorb(answer);
        Assert.True(state.HasDates);
        Assert.Equal(3, state.Nights);
    }

    [Fact]
    public void ADateEarlierThanTheRememberedCheckInIsANewCheckIn()
    {
        // "12 Ekim" after "15 Ekim …?" cannot be a check-out; it restarts the question.
        var state = new HotelConversationState();
        state.Absorb(HotelStayIntentParser.Parse("15 Ekim için oda var mı?", Today, state));

        var again = HotelStayIntentParser.Parse("12 Ekim", Today, state);
        Assert.Equal(new DateOnly(2026, 10, 12), again.CheckIn);
        Assert.Null(again.CheckOut);
        Assert.Equal(StayIntentGap.MissingCheckOut, again.Gap);
    }

    [Fact]
    public void TurkishDottedCapitalIFoldsToI()
    {
        Assert.Equal("istanbul icin iki gece", HotelStayIntentParser.Fold("İstanbul İÇİN İKİ gece"));
    }

    // ------------------------------------------------ services are not stays

    /// <summary>
    /// A service whose NAME contains a room word is a service. "Room service"
    /// and "oda servisi" used to be read as questions about a room, so the
    /// concierge answered a price question about food by asking which dates the
    /// guest had in mind.
    /// </summary>
    [Theory]
    [InlineData("How much is room service?")]
    [InlineData("Is room service available at night?")]
    [InlineData("Oda servisi ne kadar?")]
    [InlineData("Oda servisi gece de var mı?")]
    [InlineData("How much is a meeting room?")]
    [InlineData("Toplantı odası ne kadar?")]
    // Aliases. "In-room dining" is what a five-star property actually calls
    // room service, and a naive parser reads it as a request to price a room
    // because "room" is inside it.
    [InlineData("How much does in-room dining cost?")]
    [InlineData("How much is in room dining?")]
    [InlineData("Is in-room dining available all night?")]
    [InlineData("What does in-room breakfast cost?")]
    [InlineData("Odaya servis ne kadar?")]
    [InlineData("Oda içi yemek ne kadar?")]
    [InlineData("Odada yemek servisi var mı?")]
    [InlineData("Odaya yemek getiriyor musunuz?")]
    [InlineData("Konferans odası ücreti nedir?")]
    [InlineData("How much is the changing room at the spa?")]
    public void AServiceWhoseNameContainsARoomWordIsNotAStayQuestion(string turn)
    {
        var intent = HotelStayIntentParser.Parse(turn, Today);

        Assert.False(intent.IsStayQuery);
        Assert.Equal(StayIntentGap.None, intent.Gap);
        Assert.Null(intent.RoomCode);
    }

    /// <summary>A duration attached to a SERVICE is the service's, not a room's.</summary>
    [Theory]
    [InlineData("Can I use the spa for 3 days?")]
    [InlineData("Spa'yı 3 gün kullanabilir miyim?")]
    [InlineData("Is the pool open for the 3 days we are here?")]
    [InlineData("Havuz 2 gün açık mı?")]
    [InlineData("We need the spa for two days.")]
    [InlineData("We would like in-room dining for 2 nights.")]
    public void ADurationAttachedToAServiceIsNotAStay(string turn)
    {
        var intent = HotelStayIntentParser.Parse(turn, Today);

        Assert.False(intent.IsStayQuery);
        Assert.Equal(StayIntentGap.None, intent.Gap);
    }

    /// <summary>
    /// A party size stated in a SERVICE question is the service's too, and it
    /// must not move the remembered stay — a multi-turn shape, in both languages.
    /// </summary>
    [Theory]
    [InlineData("en", "Transfer for six passengers please.")]
    [InlineData("en", "How much is airport transfer for 4 people?")]
    [InlineData("en", "Is in-room dining available for 5 guests?")]
    [InlineData("tr", "6 kişi için transfer lütfen.")]
    [InlineData("tr", "4 kişi için havalimanı transferi ne kadar?")]
    [InlineData("tr", "5 kişi için odaya servis var mı?")]
    public void AServicePartySizeNeverMovesTheRememberedStay(string language, string serviceTurn)
    {
        var state = new HotelConversationState(language);

        var opening = language == "tr"
            ? "Gelecek hafta sonu 2 yetişkin için oda bakıyorum."
            : "Find a room for 2 adults next weekend.";

        state.Absorb(HotelStayIntentParser.Parse(opening, Today, state));
        Assert.Equal(2, state.Adults);
        var checkIn = state.CheckIn;

        var service = HotelStayIntentParser.Parse(serviceTurn, Today, state);
        state.Absorb(service);

        Assert.False(service.IsStayQuery, serviceTurn);
        Assert.Equal(2, state.Adults);
        Assert.Equal(checkIn, state.CheckIn);

        var follow = language == "tr" ? "Peki Executive?" : "What about Executive?";
        state.Absorb(HotelStayIntentParser.Parse(follow, Today, state));

        Assert.Equal("EXEC", state.RoomCode);
        Assert.Equal(2, state.ToRequest()!.Adults);
    }

    /// <summary>
    /// Every priced service the parser knows about, asked as a price question.
    /// Not one of them may become a stay query — the audit the one-off fix for
    /// "room service" would have missed.
    /// </summary>
    [Theory]
    [InlineData("How much is the airport transfer?")]
    [InlineData("How much is the shuttle?")]
    [InlineData("How much is the spa?")]
    [InlineData("How much is the hammam?")]
    [InlineData("How much is a massage?")]
    [InlineData("How much is the sauna?")]
    [InlineData("How much is the pool?")]
    [InlineData("How much is breakfast?")]
    [InlineData("How much is the restaurant?")]
    [InlineData("How much is parking?")]
    [InlineData("How much is the valet?")]
    [InlineData("How much is the yacht tour?")]
    [InlineData("How much is wifi?")]
    [InlineData("How much is the laundry?")]
    [InlineData("How much is the minibar?")]
    [InlineData("How much is the ballroom?")]
    [InlineData("Havalimanı transferi ne kadar?")]
    [InlineData("Kahvaltı ne kadar?")]
    [InlineData("Otopark ne kadar?")]
    [InlineData("Çamaşır ne kadar?")]
    [InlineData("Masaj ne kadar?")]
    public void NoPricedServiceQuestionBecomesAStayQuery(string turn)
    {
        var intent = HotelStayIntentParser.Parse(turn, Today);

        Assert.False(intent.IsStayQuery);
        Assert.Equal(StayIntentGap.None, intent.Gap);
    }

    /// <summary>
    /// A party size stated in a NON-STAY question belongs to that question. It
    /// used to be absorbed into the conversation, so a transfer for four people
    /// silently re-quoted the guest's stay for four adults.
    /// </summary>
    [Fact]
    public void APartyCountInAServiceQuestionDoesNotChangeTheRememberedStay()
    {
        var state = new HotelConversationState("en");

        var first = HotelStayIntentParser.Parse("Find a room for 2 adults next weekend.", Today, state);
        state.Absorb(first);

        Assert.True(first.IsStayQuery);
        Assert.Equal(2, state.Adults);
        var checkIn = state.CheckIn;

        var transfer = HotelStayIntentParser.Parse("How much is airport transfer for 4 people?", Today, state);
        state.Absorb(transfer);

        Assert.False(transfer.IsStayQuery);
        Assert.Equal(2, state.Adults);              // still the stay's party
        Assert.Equal(checkIn, state.CheckIn);       // and still the stay's dates

        var follow = HotelStayIntentParser.Parse("What about Executive?", Today, state);
        state.Absorb(follow);

        Assert.True(follow.IsStayQuery);
        Assert.Equal("EXEC", state.RoomCode);
        Assert.Equal(2, state.Adults);

        var request = state.ToRequest();
        Assert.NotNull(request);
        Assert.Equal(2, request!.Adults);
        Assert.Equal("EXEC", request.RoomCode);
    }

    [Fact]
    public void APartyCountInATurkishServiceQuestionDoesNotChangeTheRememberedStay()
    {
        var state = new HotelConversationState("tr");

        state.Absorb(HotelStayIntentParser.Parse("Gelecek hafta sonu 2 yetişkin için oda bakıyorum.", Today, state));
        Assert.Equal(2, state.Adults);

        var transfer = HotelStayIntentParser.Parse("4 kişi için havalimanı transferi ne kadar?", Today, state);
        state.Absorb(transfer);

        Assert.False(transfer.IsStayQuery);
        Assert.Equal(2, state.Adults);

        state.Absorb(HotelStayIntentParser.Parse("Peki Executive?", Today, state));

        Assert.Equal("EXEC", state.RoomCode);
        Assert.Equal(2, state.ToRequest()!.Adults);
    }

    /// <summary>A room question that also mentions a service is still a room question.</summary>
    [Theory]
    [InlineData("Is there a Deluxe room with airport transfer for 15-18 October?")]
    [InlineData("15-18 Ekim için transfer dahil Deluxe oda var mı?")]
    public void ARoomQuestionThatAlsoMentionsAServiceStaysAStayQuestion(string turn)
    {
        var intent = HotelStayIntentParser.Parse(turn, Today);

        Assert.True(intent.IsStayQuery);
        Assert.Equal("DELUXE", intent.RoomCode);
        Assert.Equal(new DateOnly(2026, 10, 15), intent.CheckIn);
    }

    // --------------------------------------------------- numeric date ranges

    /// <summary>
    /// The abbreviated numeric range the class comment always claimed to
    /// support. It did not: only the second endpoint matched, so "15-18.10.2026"
    /// silently became a check-in on the 18th with no check-out.
    /// </summary>
    [Theory]
    [InlineData("15-18.10.2026 Deluxe room", 2026, 10, 15, 2026, 10, 18)]
    [InlineData("15–18.10.2026 için fiyat", 2026, 10, 15, 2026, 10, 18)]
    [InlineData("15—18.10.2026", 2026, 10, 15, 2026, 10, 18)]
    [InlineData("15.10-18.10.2026", 2026, 10, 15, 2026, 10, 18)]
    [InlineData("15.10.2026 - 18.10.2026", 2026, 10, 15, 2026, 10, 18)]
    [InlineData("15/10/2026 - 18/10/2026", 2026, 10, 15, 2026, 10, 18)]
    [InlineData("2026-10-15 - 2026-10-18", 2026, 10, 15, 2026, 10, 18)]
    [InlineData("28.10-02.11.2026 oda", 2026, 10, 28, 2026, 11, 2)]
    public void NumericRangesAreReadWholeInEveryCommonShape(
        string turn, int y1, int m1, int d1, int y2, int m2, int d2)
    {
        var intent = HotelStayIntentParser.Parse(turn, Today);

        Assert.Equal(new DateOnly(y1, m1, d1), intent.CheckIn);
        Assert.Equal(new DateOnly(y2, m2, d2), intent.CheckOut);
        Assert.Equal(StayIntentGap.None, intent.Gap);
    }

    /// <summary>
    /// A reversed range is refused, not repaired. Sorting the two dates would
    /// turn an impossible request into a perfectly ordinary three-night stay
    /// the guest never asked for.
    /// </summary>
    [Theory]
    [InlineData("18.10.2026 - 15.10.2026")]
    [InlineData("18.10.2026 - 15.10.2026 arası oda")]
    [InlineData("18/10/2026 - 15/10/2026")]
    [InlineData("2026-10-18 - 2026-10-15")]
    public void AReversedRangeIsInvalidRatherThanQuietlySorted(string turn)
    {
        var intent = HotelStayIntentParser.Parse(turn, Today);

        Assert.Equal(new DateOnly(2026, 10, 18), intent.CheckIn);
        Assert.Equal(new DateOnly(2026, 10, 15), intent.CheckOut);
        Assert.Equal(StayIntentGap.InvalidRange, intent.Gap);
    }

    [Fact]
    public void AYearlessNumericRangeRollsForwardToTheNextOccurrenceOfThatMonth()
    {
        // Today is 5 September 2026; March has gone, October has not.
        var march = HotelStayIntentParser.Parse("3-5.03 oda", Today);
        Assert.Equal(new DateOnly(2027, 3, 3), march.CheckIn);
        Assert.Equal(new DateOnly(2027, 3, 5), march.CheckOut);

        var october = HotelStayIntentParser.Parse("15-18.10 oda", Today);
        Assert.Equal(new DateOnly(2026, 10, 15), october.CheckIn);
        Assert.Equal(new DateOnly(2026, 10, 18), october.CheckOut);
    }

    [Fact]
    public void AYearStatedOnOneEndpointAppliesToBoth()
    {
        var intent = HotelStayIntentParser.Parse("15.10 - 18.10.2026", Today);

        Assert.Equal(new DateOnly(2026, 10, 15), intent.CheckIn);
        Assert.Equal(new DateOnly(2026, 10, 18), intent.CheckOut);
    }

    /// <summary>
    /// A range SHAPE is consumed atomically: if either endpoint is impossible
    /// the whole range is invalid, and the text inside it is not scanned again.
    ///
    /// <para>
    /// Take "31.02-18.10.2026 oda": if the range failed and a fallback scanner
    /// then noticed "18.10.2026" inside the same span and paired it with the
    /// "02" from the impossible endpoint, it would quote 2–18 October — a stay
    /// the guest never asked for, produced from their typo. Repairing a malformed request into a
    /// different valid one is worse than refusing it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("31.02-18.10.2026 oda")]           // 31 February
    [InlineData("31.04-02.05.2026 oda")]           // 31 April
    [InlineData("00.10-03.10.2026 oda")]           // day zero
    [InlineData("15.13-18.10.2026 oda")]           // month 13
    [InlineData("31.02.2026 - 18.10.2026 oda")]    // the fully-written form of the same typo
    [InlineData("15/13/2026 - 18/10/2026")]
    public void AMalformedRangeIsInvalidRatherThanReinterpreted(string turn)
    {
        var intent = HotelStayIntentParser.Parse(turn, Today);

        Assert.Null(intent.CheckIn);
        Assert.Null(intent.CheckOut);
        Assert.Equal(StayIntentGap.InvalidRange, intent.Gap);
    }

    /// <summary>
    /// The same span is not re-scanned for a smaller date, in EITHER direction:
    /// a malformed range must not leave a usable fragment behind.
    /// </summary>
    [Fact]
    public void AMalformedRangeLeavesNoUsableFragment()
    {
        var intent = HotelStayIntentParser.Parse("31.02-18.10.2026 oda", Today);

        Assert.True(intent.IsStayQuery);
        Assert.Equal(StayIntentGap.InvalidRange, intent.Gap);
        Assert.NotEqual(new DateOnly(2026, 10, 18), intent.CheckIn);
        Assert.NotEqual(new DateOnly(2026, 10, 2), intent.CheckIn);
    }

    [Fact]
    public void AnImpossibleDateAloneIsNoDateAtAll()
    {
        var intent = HotelStayIntentParser.Parse("32.13.2026 için oda var mı?", Today);

        Assert.Null(intent.CheckIn);
        Assert.Null(intent.CheckOut);
        Assert.Equal(StayIntentGap.MissingDates, intent.Gap);
    }
}
