using GroundedConcierge.Domain;
using GroundedConcierge.Answering;

namespace GroundedConcierge.Answering.Tests;

/// <summary>
/// The boundary between a concierge draft and a guest.
///
/// <para>
/// <b>What is asserted is a count of zero.</b> On a turn carrying computed
/// facts the model is <b>not called at all</b>, so the commercial tests below
/// assert that the scripted model received NO request, rather than trusting a
/// scanner verdict. Every model here is SCRIPTED; nothing calls a live one.
/// </para>
///
/// <para>
/// The scanner tests that remain are the ones that still guard something: the
/// ACTION rules, which run on every document answer a model does write.
/// </para>
/// </summary>
public class HotelOutputGateTests
{
    private static readonly DateOnly Today = new(2026, 9, 5);

    private static HotelAnswerGate Gate() => new(new HotelFactValidator());

    private static (HotelAnswerFacts Facts, StayQuoteResult Quote) QuoteFacts(string? room = null)
    {
        var hotel = new HotelDataService();
        var request = new StayRequest(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18), 2, 0, room);
        var quote = hotel.Quote(request, Today);
        Assert.True(quote.IsValid);
        Assert.NotEmpty(quote.Quotes);

        return (new HotelAnswerFacts
        {
            Stay = request,
            Quotes = quote.Quotes,
            Catalogue = hotel.RoomTypes,
            Today = Today
        }, quote);
    }

    /// <summary>A model that answers with fixed text and records what it was told on the retry.</summary>
    private sealed class ScriptedModel
    {
        private readonly Queue<string> _drafts;
        public ScriptedModel(params string[] drafts) => _drafts = new Queue<string>(drafts);

        public int Calls { get; private set; }
        public List<string?> Instructions { get; } = new();

        public HotelDraftSource Source => (instruction, _) =>
        {
            Calls++;
            Instructions.Add(instruction);
            return Task.FromResult(_drafts.Count > 0 ? _drafts.Dequeue() : "…");
        };
    }

    // ------------------------------------- the figures are the engine's

    /// <summary>
    /// The commercial statement is arithmetic, not prose: every figure in it
    /// comes from the same facts object the quote cards are rendered from, so
    /// the two views cannot disagree.
    /// </summary>
    [Fact]
    public void TheCommercialStatementIsRenderedFromTheEnginesOwnFacts()
    {
        var (facts, quote) = QuoteFacts();

        var text = HotelAnswerComposer.Compose(facts, turkish: false);

        Assert.Contains("15.10.2026", text);
        Assert.Contains("18.10.2026", text);
        Assert.Contains("3 nights", text);
        Assert.Contains("2 adults", text);
        Assert.DoesNotContain("night(s)", text);   // natural English, no engineering plurals

        foreach (var q in quote.Quotes)
        {
            Assert.Contains(q.Room.NameEn, text);
            Assert.Contains("€" + q.TotalEur.ToString("N0"), text);
            Assert.Contains("€" + q.AvgNightlyEur.ToString("N0"), text);

            // Polarity comes from the engine, per room, in the right direction.
            if (q.IsAvailable) Assert.Contains(HotelPlural.English(q.RoomsAvailable, "room", "rooms") + " available", text);
        }

        var soldOut = quote.Quotes.Where(q => !q.IsAvailable).ToList();
        Assert.NotEmpty(soldOut);   // the fixture is chosen so this case is covered
        Assert.Contains("sold out for these dates", text);

        Assert.Contains("nothing is held and nothing is charged", text);
    }

    [Fact]
    public void TheTurkishStatementCarriesTheSameFiguresAndTheSameDisclaimer()
    {
        var (facts, quote) = QuoteFacts("DELUXE");

        var text = HotelAnswerComposer.Compose(facts, turkish: true);

        Assert.Contains("15.10.2026", text);
        Assert.Contains("18.10.2026", text);
        Assert.Contains("3 gece", text);
        Assert.Contains("€" + quote.Quotes[0].TotalEur.ToString("N0"), text);
        Assert.Contains("rezerve edilmedi", text);
        Assert.DoesNotContain("night(s)", text);
    }

    /// <summary>
    /// An earliest-availability answer states the two dates the SEARCH found,
    /// and says whether earlier dates were rejected only when they were —
    /// search metadata, never an inference.
    /// </summary>
    [Fact]
    public void AnEarliestSearchStatesTheFoundDatesAndOnlyClaimsRejectionsThatHappened()
    {
        var hotel = new HotelDataService();
        var search = new EarliestStayRequest(Today, 3, 2, 0, null);
        var result = hotel.FindEarliestAvailableStay(search, Today);
        Assert.True(result is { IsValid: true, Found: true });

        var facts = new HotelAnswerFacts
        {
            Stay = new StayRequest(result.CheckIn!.Value, result.CheckOut!.Value, 2, 0, null),
            Quotes = result.Quotes,
            Catalogue = hotel.RoomTypes,
            Today = Today,
            SearchedFrom = result.SearchedFrom,
            SearchedThrough = result.SearchedThrough,
            EarlierCandidatesRejected = result.EarlierCandidatesRejected,
            RejectedCandidates = result.RejectedCandidates
        };

        Assert.True(facts.IsEarliestSearch);
        var text = HotelAnswerComposer.Compose(facts, turkish: false);

        Assert.Contains("earliest available 3-night stay", text);
        Assert.Contains(result.CheckIn!.Value.ToString("dd.MM.yyyy"), text);
        Assert.Contains(result.CheckOut!.Value.ToString("dd.MM.yyyy"), text);

        if (result.EarlierCandidatesRejected)
            Assert.Contains(result.RejectedCandidates == 1 ? "it had no room free on every night" : "none had a room free on every night", text);
        else
            Assert.Contains("no earlier date was examined", text);
    }

    // ------------------------ the model cannot reach the commercial half

    /// <summary>
    /// Drafts that break an allowlist-over-values design. None of them can
    /// reach a guest, for a reason stronger than "the scanner catches it": on a
    /// turn that carries computed facts the model
    /// <b>is not asked anything at all</b>, so there is no draft to catch. The
    /// scripted model records every call it receives, and it receives none.
    /// </summary>
    [Theory]
    [InlineData("The total is 999.")]
    [InlineData("Every room type is available for those dates.")]
    [InlineData("Check-in is tomorrow.")]
    [InlineData("The Deluxe Bosphorus Suite is available for those dates.")]
    [InlineData("€704/night")]
    [InlineData("€704 per room")]
    [InlineData("That works out at 704 per room.")]
    [InlineData("Your stay runs from 15 October to 18 October.")]
    [InlineData("Three nights in the Garden Room.")]
    [InlineData("Two rooms are left.")]
    [InlineData("Nothing has been booked, but your room is confirmed.")]
    [InlineData("Nothing has been booked and your room is confirmed.")]
    [InlineData("I have placed a temporary hold for you.")]
    [InlineData("Your request has been sent.")]
    [InlineData("Rezervasyon yapılmadı ama odanızı ayırdım.")]
    [InlineData("Ödeme alınmadı ama talebinizi resepsiyona gönderdim.")]
    public async Task NoModelDraftCanPutACommercialClaimInFrontOfAGuest(string draft)
    {
        var (facts, quote) = QuoteFacts();
        var model = new ScriptedModel(draft, draft);

        var outcome = await Gate().AnswerAsync(model.Source, facts, turkish: false, "reception@example.invalid");

        Assert.Equal(0, model.Calls);
        Assert.Equal(0, outcome.DraftsRequested);
        Assert.True(outcome.CommercialFactsAreDeterministic);
        Assert.False(outcome.WasRegenerated);
        Assert.False(outcome.WasWithheld);   // nothing was asked, so nothing was withheld

        // What the guest reads is exactly the deterministic statement — the
        // whole draft is absent, not merely trimmed.
        Assert.Equal(HotelAnswerComposer.Compose(facts, turkish: false), outcome.Text);
        Assert.DoesNotContain(draft, outcome.Text, StringComparison.Ordinal);

        // And the figures the guest DOES read are still the engine's.
        Assert.Contains("€" + quote.Quotes[0].TotalEur.ToString("N0"), outcome.Text);
    }

    /// <summary>
    /// The claims that used to slip through carried no figure at all, which is
    /// why an allowlist over values could not see them. Checked directly against
    /// the commercially-empty rules so the reason each is refused stays visible.
    /// Nothing calls these rules on a commercial turn, because that turn asks no
    /// model; they remain the domain's definition of text that may sit beside
    /// deterministic figures.
    /// </summary>
    [Theory]
    [InlineData("The total is 999.", HotelClaimKind.CommercialFigure)]
    [InlineData("Every room type is available for those dates.", HotelClaimKind.AvailabilityClaim)]
    [InlineData("Check-in is tomorrow.", HotelClaimKind.CommercialDate)]
    [InlineData("The suite is sold out.", HotelClaimKind.AvailabilityClaim)]
    [InlineData("We have two rooms left.", HotelClaimKind.AvailabilityClaim)]
    [InlineData("Your stay starts in October.", HotelClaimKind.CommercialDate)]
    [InlineData("Check-in is on Friday.", HotelClaimKind.CommercialDate)]
    [InlineData("Three nights, then.", HotelClaimKind.CommercialFigure)]
    [InlineData("Girişiniz yarın.", HotelClaimKind.CommercialDate)]
    [InlineData("Bütün oda tipleri müsait.", HotelClaimKind.AvailabilityClaim)]
    [InlineData("Üç gece kalıyorsunuz.", HotelClaimKind.CommercialFigure)]
    public void CommentaryMayNotCarryCommercialContentOfAnyKind(string draft, HotelClaimKind expected)
    {
        var verdict = new HotelFactValidator().ValidateCommentary(draft);

        Assert.False(verdict.IsSafe, draft);
        Assert.Contains(expected, verdict.Kinds);
    }

    /// <summary>
    /// Even a PERFECTLY harmless sentence is not asked for and not appended: the
    /// commercial answer is the composer's text and nothing else. Giving up a
    /// sentence of hospitality is a deliberate cost — a model that is asked
    /// nothing cannot be wrong.
    /// </summary>
    [Theory]
    [InlineData("Would you like me to tell you about the spa?")]
    [InlineData("If you would like to go ahead, the reservation request form on the website reaches reception.")]
    [InlineData("Is there anything else I can help you with?")]
    [InlineData("Size başka nasıl yardımcı olabilirim?")]
    [InlineData("Dilerseniz web sitesindeki rezervasyon talep formunu kullanabilirsiniz; resepsiyon inceleyecektir.")]
    public async Task NotEvenHarmlessHospitalityIsAskedForOnACommercialTurn(string draft)
    {
        var (facts, _) = QuoteFacts();
        var model = new ScriptedModel(draft);

        var outcome = await Gate().AnswerAsync(model.Source, facts, turkish: false, "reception@example.invalid");

        Assert.Equal(0, model.Calls);
        Assert.Equal(HotelAnswerComposer.Compose(facts, turkish: false), outcome.Text);
        Assert.DoesNotContain(draft, outcome.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both commercial shapes — a dated quote and an earliest-availability
    /// search — take the same path, in both languages, with the same silence
    /// towards the model.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEarliestSearchAlsoAsksNoModel(bool turkish)
    {
        var hotel = new HotelDataService();
        var result = hotel.FindEarliestAvailableStay(new EarliestStayRequest(Today, 3, 2, 0, null), Today);
        Assert.True(result is { IsValid: true, Found: true });

        var facts = new HotelAnswerFacts
        {
            Stay = new StayRequest(result.CheckIn!.Value, result.CheckOut!.Value, 2, 0, null),
            Quotes = result.Quotes,
            Catalogue = hotel.RoomTypes,
            Today = Today,
            SearchedFrom = result.SearchedFrom,
            SearchedThrough = result.SearchedThrough,
            EarlierCandidatesRejected = result.EarlierCandidatesRejected,
            RejectedCandidates = result.RejectedCandidates
        };

        var model = new ScriptedModel("I have held the earliest suite for you.");
        var outcome = await Gate().AnswerAsync(model.Source, facts, turkish, "reception@example.invalid");

        Assert.Equal(0, model.Calls);
        Assert.Equal(HotelAnswerComposer.Compose(facts, turkish), outcome.Text);
        Assert.True(outcome.CommercialFactsAreDeterministic);
    }

    /// <summary>
    /// The document path still spends at most two drafts and never rewrites a
    /// clean one — the sequence on the one path that has a model in it.
    /// </summary>
    [Fact]
    public async Task ADocumentAnswerIsAskedTwiceAtMostAndACleanOneIsNeverRewritten()
    {
        var bad = new ScriptedModel("I have booked it.", "It is reserved.", "Third.");
        await Gate().AnswerAsync(bad.Source, HotelAnswerFacts.None, turkish: false, "reception@example.invalid");
        Assert.Equal(HotelAnswerGate.MaxDrafts, bad.Calls);
        Assert.NotNull(bad.Instructions[1]);

        var good = new ScriptedModel("Breakfast is served from 07:00 to 10:30 [1].");
        var outcome = await Gate().AnswerAsync(good.Source, HotelAnswerFacts.None, turkish: false, "reception@example.invalid");
        Assert.Equal(1, good.Calls);
        Assert.False(outcome.WasRegenerated);
    }

    /// <summary>The composer's own text passes the rules the gate applies to everything else.</summary>
    [Fact]
    public void TheDeterministicStatementIsNotItselfARefusableClaim()
    {
        var validator = new HotelFactValidator();

        foreach (var turkish in new[] { false, true })
        {
            var (facts, _) = QuoteFacts();
            Assert.True(validator.ValidateAnswer(HotelAnswerComposer.Compose(facts, turkish)).IsSafe);
        }
    }

    // --------------------------------------------------- the action rules

    /// <summary>Every act the demonstration never performs, in both languages, on any turn.</summary>
    [Theory]
    [InlineData("your room is confirmed")]
    [InlineData("I have confirmed your room")]
    [InlineData("I have reserved it")]
    [InlineData("I reserved it")]
    [InlineData("I have placed a hold")]
    [InlineData("I placed a temporary hold for you")]
    [InlineData("I held the room")]
    [InlineData("your request has been sent")]
    [InlineData("I sent your request")]
    [InlineData("reception confirmed it")]
    [InlineData("payment has been taken")]
    [InlineData("you have been charged")]
    [InlineData("rezervasyonunuz onaylandı")]
    [InlineData("odanız ayrıldı")]
    [InlineData("odanızı ayırdım")]
    [InlineData("rezervasyonu yaptım")]
    [InlineData("geçici olarak tuttum")]
    [InlineData("talebiniz gönderildi")]
    [InlineData("resepsiyona gönderdim")]
    [InlineData("ödeme alındı")]
    [InlineData("ücret tahsil edildi")]
    public void AnActionTheDemonstrationNeverPerformsIsRefusedOnEveryTurn(string draft)
    {
        // ValidateAnswer is the DOCUMENT path — no commercial facts, so only the
        // action rules can be what refuses these.
        Assert.False(new HotelFactValidator().ValidateAnswer(draft).IsSafe, draft);
    }

    /// <summary>
    /// The negation guard is clause-scoped, so a denial cannot launder a claim
    /// that follows it: a safe first clause, an unsafe second.
    /// </summary>
    [Theory]
    [InlineData("Nothing has been booked, but your room is confirmed.")]
    [InlineData("No payment was taken; however, I sent your reservation to reception.")]
    [InlineData("I cannot reserve rooms, although I have placed a temporary hold for you.")]
    [InlineData("Nothing is held yet, yet reception confirmed it this morning.")]
    [InlineData("Rezervasyon yapılmadı ama odanızı ayırdım.")]
    [InlineData("Ödeme alınmadı ancak talebiniz gönderildi.")]
    [InlineData("Hiçbir şey tutulmadı fakat rezervasyonu yaptım.")]
    public void ADenialInOneClauseDoesNotExcuseAClaimInTheNext(string draft)
    {
        Assert.False(new HotelFactValidator().ValidateAnswer(draft).IsSafe, draft);
    }

    /// <summary>
    /// <b>Plain "and" joins a denial to a claim too.</b> A contrastive
    /// conjunction is not the only way: plain "and" — with or without a comma —
    /// is the ordinary one, and a negation guard that read the whole clause
    /// before the match would launder every act in the list. Negation is
    /// PREDICATE-LOCAL, so a denial that belongs to the previous predicate does
    /// not reach this one.
    ///
    /// <para>
    /// The two Turkish lines were already refused before the fix, but only by
    /// accident: "yapılmadı" and "alınmadı" are not in the negation vocabulary at
    /// all, so no negation was found to excuse them. They are here to keep them
    /// refused BY DESIGN.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Nothing has been booked and your room is confirmed.")]
    [InlineData("Nothing has been booked, and your room is confirmed.")]
    [InlineData("No payment has been taken and I sent your request.")]
    [InlineData("No payment has been taken, and I sent your request.")]
    [InlineData("We cannot reserve it and reception has confirmed the room.")]
    [InlineData("Nothing is held and I have placed a temporary hold for you.")]
    [InlineData("No payment has been taken; also, your request has been sent.")]
    [InlineData("Rezervasyon yapılmadı ve odanızı ayırdım.")]
    [InlineData("Ödeme alınmadı ve talebinizi resepsiyona gönderdim.")]
    [InlineData("Hiçbir şey tutulmadı ve rezervasyonu yaptım.")]
    public void ADenialCannotLaunderAClaimJoinedToItByACoordinator(string draft)
    {
        Assert.False(new HotelFactValidator().ValidateAnswer(draft).IsSafe, "REACHES THE GUEST: " + draft);
    }

    /// <summary>
    /// The other half, and the expensive one: the honest denials the assistant
    /// is REQUIRED to write must stay safe under a short negation window. Every
    /// coordinator is exercised in both languages, on sentences that deny and
    /// assert nothing.
    /// </summary>
    [Theory]
    [InlineData("Nothing has been booked.")]
    [InlineData("Your room has not been confirmed.")]
    [InlineData("No payment has been taken.")]
    [InlineData("I cannot reserve it.")]
    [InlineData("The request has not been sent.")]
    [InlineData("Nothing has been booked and nothing is held.")]
    [InlineData("No payment has been taken, and no request has been sent.")]
    [InlineData("No payment, deposit or card fee has been taken.")]
    [InlineData("Nothing has been booked, but nothing is lost either — reception can help.")]
    [InlineData("Nothing is booked; however, nothing is charged either.")]
    [InlineData("Rezervasyon yapılmadı.")]
    [InlineData("Odanız onaylanmadı.")]
    [InlineData("Ödeme alınmadı.")]
    [InlineData("Talep gönderilmedi.")]
    [InlineData("Rezervasyon yapılmadı ve ödeme alınmadı.")]
    [InlineData("Ödeme alınmadı ama rezervasyon da yapılmadı.")]
    public void ADenialOfItsOwnPredicateStaysSafeUnderEveryConjunction(string draft)
    {
        Assert.True(new HotelFactValidator().ValidateAnswer(draft).IsSafe, "REFUSED AN HONEST SENTENCE: " + draft);
    }

    /// <summary>The negation window is pinned directly, because everything above rests on it.</summary>
    [Fact]
    public void TheNegationWindowEndsAtTheLastCoordinator()
    {
        const string laundered = "nothing has been booked and your room is confirmed";
        Assert.False(HotelFactValidator.NegatedPredicate(laundered, laundered.IndexOf("is confirmed", StringComparison.Ordinal)));

        const string honest = "nothing has been booked";
        Assert.True(HotelFactValidator.NegatedPredicate(honest, honest.IndexOf("has been booked", StringComparison.Ordinal)));

        const string turkish = "rezervasyon yapilmadi ve odanizi ayirdim";
        Assert.False(HotelFactValidator.NegatedPredicate(turkish, turkish.IndexOf("odanizi ayirdim", StringComparison.Ordinal)));
    }

    // ------------------------------------ availability needs room context

    /// <summary>
    /// <b>No false positive on "available".</b> "Available" on its own says
    /// nothing about rooms, and refusing these sentences would make the one job
    /// this text has impossible. An availability claim needs a room noun in a
    /// syntactic relation with the availability word — which is also what keeps
    /// "room service" and "in-room dining" out of it.
    /// </summary>
    [Theory]
    [InlineData("Breakfast details are available in the hotel information.")]
    [InlineData("I can help you compare the available options shown above.")]
    [InlineData("Information is available.")]
    [InlineData("Help is available.")]
    [InlineData("The available options shown on the page answer that.")]
    [InlineData("In-room dining is available around the clock.")]
    [InlineData("Room service is available at any hour.")]
    [InlineData("Please feel free to ask about the spa.")]
    [InlineData("Oda servisi her saat hizmet vermektedir.")]
    public void OrdinaryUsesOfAvailableAreNotRoomAvailabilityClaims(string draft)
    {
        var verdict = new HotelFactValidator().ValidateCommentary(draft);

        Assert.DoesNotContain(HotelClaimKind.AvailabilityClaim, verdict.Kinds);
    }

    /// <summary>And an assertion about the hotel's inventory still is one.</summary>
    [Theory]
    [InlineData("The Deluxe room is available.")]
    [InlineData("All rooms are available.")]
    [InlineData("We have 4 rooms available.")]
    [InlineData("Executive is sold out.")]
    [InlineData("There are no rooms left.")]
    [InlineData("Every room type is available for those dates.")]
    [InlineData("Two rooms are left.")]
    [InlineData("The suite is sold out.")]
    [InlineData("Bütün oda tipleri müsait.")]
    [InlineData("Odalar dolu.")]
    public void AnAssertionAboutRoomInventoryIsStillAnAvailabilityClaim(string draft)
    {
        var verdict = new HotelFactValidator().ValidateCommentary(draft);

        Assert.Contains(HotelClaimKind.AvailabilityClaim, verdict.Kinds);
    }

    /// <summary>
    /// The honest disclaimers the assistant is REQUIRED to write stay safe. This
    /// is the expensive error on this path: the common case is an honest answer,
    /// and a scanner that refused these would refuse the truth every turn.
    /// </summary>
    [Theory]
    [InlineData("Nothing has been booked.")]
    [InlineData("I cannot reserve it.")]
    [InlineData("No payment has been taken.")]
    [InlineData("The request has not been sent.")]
    [InlineData("Nothing is booked, held or charged.")]
    [InlineData("I cannot book a room; reception does that.")]
    [InlineData("Your room is not confirmed until reception replies.")]
    [InlineData("Rezervasyon yapılmadı.")]
    [InlineData("Oda ayrılmadı.")]
    [InlineData("Ödeme alınmadı.")]
    [InlineData("Hiçbir şey rezerve edilmedi; bu bir tanıtım teklifidir.")]
    [InlineData("Rezervasyonunuz onaylanmadı, talebiniz resepsiyona iletilecek.")]
    [InlineData("You can book through reception, and they will confirm it themselves.")]
    public void TheHonestDisclaimersTheAssistantMustWriteAreNotRefused(string draft)
    {
        Assert.True(new HotelFactValidator().ValidateAnswer(draft).IsSafe, draft);
    }

    // ------------------------------------------------ the document path

    /// <summary>
    /// A document answer keeps its own figures and citations, in every format a
    /// model writes money in: the price of a transfer comes from a retrieved
    /// passage and is the DOCUMENT's fact, not the engine's.
    /// </summary>
    [Theory]
    [InlineData("The airport transfer is €1,273 one way [1].")]
    [InlineData("The airport transfer is €1273 one way [1].")]
    [InlineData("Havalimanı transferi tek yön 1.273 € [1].")]
    [InlineData("Havalimanı transferi tek yön 1 273 € [1].")]
    [InlineData("Havalimanı transferi tek yön 1.273,00 € [1].")]
    [InlineData("The airport transfer is EUR 1,273 one way [1].")]
    [InlineData("Breakfast is served from 07:00 to 10:30 [2], and check-in is at 15:00 [3].")]
    public void ADocumentAnswerKeepsItsOwnFiguresInEveryHonestFormat(string draft)
    {
        Assert.True(new HotelFactValidator().ValidateAnswer(draft).IsSafe, draft);
    }

    [Fact]
    public async Task ADocumentAnswerThatClaimsAnActionIsRegeneratedAndThenWithheld()
    {
        var model = new ScriptedModel(
            "Breakfast is €35 [1]. I have booked it for you.",
            "Breakfast is €35 [1]. Your table is reserved.");

        var outcome = await Gate().AnswerAsync(
            model.Source, HotelAnswerFacts.None, turkish: false, "reception@example.invalid");

        Assert.True(outcome.WasWithheld);
        Assert.False(outcome.CommercialFactsAreDeterministic);
        Assert.Equal(2, model.Calls);
        Assert.DoesNotContain("booked", outcome.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reception@example.invalid", outcome.Text);
    }

    [Fact]
    public void AnEmptyDraftIsNotAViolation()
    {
        var validator = new HotelFactValidator();

        Assert.True(validator.ValidateAnswer(null).IsSafe);
        Assert.True(validator.ValidateAnswer("   ").IsSafe);
        Assert.True(validator.ValidateCommentary(null).IsSafe);
    }

    /// <summary>Clause splitting is the mechanism the negation guard rests on, so it is pinned directly.</summary>
    [Fact]
    public void ClausesSplitAtContrastiveConjunctionsInBothLanguages()
    {
        var clauses = HotelFactValidator.Clauses("nothing is booked but your room is confirmed").ToList();
        Assert.Equal(2, clauses.Count);
        Assert.Contains("nothing is booked", clauses[0]);
        Assert.Contains("your room is confirmed", clauses[1]);

        var turkish = HotelFactValidator.Clauses("rezervasyon yapilmadi ama odanizi ayirdim").ToList();
        Assert.Equal(2, turkish.Count);

        var semicolon = HotelFactValidator.Clauses("no payment was taken; i sent it").ToList();
        Assert.Equal(2, semicolon.Count);
    }
}
