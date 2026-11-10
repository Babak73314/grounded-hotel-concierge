using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GroundedConcierge.Domain;

/// <summary>What a guest's turn asked for, as far as deterministic parsing can tell.</summary>
public sealed record StayIntent
{
    /// <summary>True when the turn is about dates, prices, availability or booking a room.</summary>
    public bool IsStayQuery { get; init; }

    public DateOnly? CheckIn { get; init; }
    public DateOnly? CheckOut { get; init; }

    /// <summary>A stated length of stay ("3 gece"), used when no check-out date was given.</summary>
    public int? Nights { get; init; }

    public int? Adults { get; init; }
    public int? Children { get; init; }
    public string? RoomCode { get; init; }

    /// <summary>"tr" or "en".</summary>
    public string Language { get; init; } = "tr";

    /// <summary>
    /// The guest asked about every room ("tüm odalar", "which rooms"), so a room
    /// preference remembered from an earlier turn must be dropped rather than
    /// quietly narrowing the answer to one type.
    /// </summary>
    public bool ClearsRoom { get; init; }

    /// <summary>
    /// The guest asked the engine to FIND the earliest available stay ("earliest
    /// room for 3 nights", "en erken 3 gecelik müsait oda") rather than to price
    /// dates they chose. Never set beside explicit dates, and never for a
    /// check-in/check-out TIME question, which is hotel policy.
    /// </summary>
    public bool SearchEarliestAvailability { get; init; }

    /// <summary>Why the turn cannot be quoted yet, if it cannot.</summary>
    public StayIntentGap Gap { get; init; }

    public bool MentionsDates => CheckIn is not null || CheckOut is not null || Nights is not null;
}

public enum StayIntentGap
{
    None,
    /// <summary>A stay question with no dates anywhere (this turn or earlier).</summary>
    MissingDates,
    /// <summary>A check-in date but neither a check-out nor a length of stay.</summary>
    MissingCheckOut,
    /// <summary>The dates could be read but the range is not usable (past, reversed, too long).</summary>
    InvalidRange,
    /// <summary>An earliest-availability search with no length of stay anywhere (this turn or earlier).</summary>
    MissingNights
}

/// <summary>
/// Turns a guest's sentence into a <see cref="StayIntent"/> — dates, party,
/// room — in Turkish and English, without a model.
///
/// <para>
/// Deliberately narrow: it recognises the shapes a reservation desk actually
/// hears ("15-18 Ekim", "bu hafta sonu", "next weekend for 3 nights", "2 adults
/// and a child", "Executive oda") and returns null-ish for everything else, so a
/// question about the spa never gets a price table. Relative dates are resolved
/// against the property's own clock, never the server's. A turn it half
/// understands is reported as a GAP the concierge asks about once, rather than
/// guessed.
/// </para>
/// </summary>
public static class HotelStayIntentParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // A room, by word ("oda", "room", "suite"); room TYPES are matched separately
    // by RoomPatterns. A room word makes a stay question on its own.
    private static readonly Regex RoomWords = new(@"\boda\w*|\broom\w*|\bsuite?s?\b", Opts);

    // Stay verbs and nouns: availability, reservation, staying, nights, a
    // weekend. A stay verb makes a stay question on its own — unless the subject
    // is a priced hotel SERVICE ("can I book the spa?"). Check-in/check-out words
    // are deliberately NOT here: they carry a stay only beside dates, and beside
    // a time word they are a policy question.
    private static readonly Regex StayVerbs = new(
        @"\b(?:musait|bos oda|bos\b|doluluk|rezerv|kal(?:mak|acag|ir|abilir)|gece|gecelik|konakla|availab|book|reserv|stay|night|weekend|hafta sonu)\w*", Opts);

    // "Find me the earliest …": the guest wants the engine to FIND a date.
    // Folded forms (no diacritics): "en yakın müsait" → "en yakin musait".
    private static readonly Regex EarliestPattern = new(
        @"\b(?:earliest|first available|soonest|as soon as possible|asap|next available|en erken|ilk musait|en yakin musait|musait olan ilk|en yakin tarih|ilk uygun|ilk bos)\b", Opts);

    // A check-in/check-out TIME question is hotel POLICY ("what time is
    // check-in?", "what is your earliest check-in time?", "giriş saati nedir?",
    // "erken giriş yapabilir miyim?"): the documents answer it. Both a
    // check-in/out word and a time word must be present, or one of the fixed
    // early/late phrases — "earliest time slot" alone has no check-in word and
    // stays an availability search.
    private static readonly Regex CheckInOutWords = new(@"\b(?:check-?in|check ?in|check-?out|check ?out|giris|cikis)\w*", Opts);
    private static readonly Regex TimeWords = new(@"\b(?:what time|time|times|saat\w*|kacta|hour|hours|o'?clock|early|late|erken|gec)\b", Opts);
    private static readonly Regex PolicyPhrases = new(@"\b(?:erken giris|gec cikis|early check-?in|late check-?out|early arrival|late departure)\b", Opts);

    // Generic price wording. "How much is the airport transfer?" and "How much
    // is a Deluxe room?" share every one of these, so on their own they decide
    // nothing: a price word becomes a stay question only beside a stay anchor,
    // a room type, dates, or an active stay context (see Parse).
    private static readonly Regex PriceWords = new(
        @"\b(?:fiyat|ucret|tarife|ne kadar|kac(?:a)?\b|price|prices|pricing|rate|rates|cost|costs|how much|fee|fees)\w*", Opts);

    // The services the hotel sells beside the room. A price question whose
    // subject is one of these is a KNOWLEDGE question even in the middle of a
    // stay conversation ("… and how much is the transfer?"), so the active
    // stay context must not pull it into the quote flow.
    private static readonly Regex ServiceSubjects = new(
        @"\b(?:transfer|havaliman|airport|shuttle|servis\b|spa|hamam|hammam|masaj|massage|sauna|havuz|pool|kahvalti|breakfast"
        + @"|restoran|restaurant|otopark|parking|valet|vale\b|yat\b|yacht|tour|wifi|wi-fi|internet|evcil|pets?\b"
        + @"|toplanti|meeting|ballroom|balo|laundry|camasir|minibar|dining|ikram|kuafor|berber|butler|concierge desk)\w*", Opts);

    /// <summary>
    /// Guest services whose ordinary NAME contains a room word, in either
    /// language — the PHRASE-level half of the service vocabulary. It is
    /// matched BEFORE the generic room vocabulary and masked out of it, which is
    /// why "how much is room service?" and "how much does in-room dining cost?"
    /// are service questions and not requests to price a room.
    ///
    /// <para>
    /// A list rather than a rule, and deliberately: this is the set of things a
    /// hotel sells whose name happens to contain "room", "oda" or "suite", and
    /// the only honest way to keep it right is to name them. "In-room dining"
    /// is what a five-star property actually calls room service — so aliases matter as
    /// much as the canonical name, and the Turkish forms ("odaya servis", "oda
    /// içi yemek", "odada yemek servisi") each decline differently.
    /// </para>
    ///
    /// <para>
    /// Ordered longest-first where two entries overlap, because the masking
    /// replaces what it matches: "in-room dining" must win over the "room" that
    /// sits inside it.
    /// </para>
    /// </summary>
    private static readonly Regex ServiceCompounds = new(
        // In-room dining and its aliases — the canonical case.
        @"\bin[- ]?room dining\b|\bin[- ]?room (?:breakfast|meal|meals|service|catering)\b"
        + @"|\broom service\b|\broom[- ]service menu\b"
        + @"|\boda servisi\w*|\bodaya servis\w*|\boda ici yemek\w*|\bodada yemek servisi\w*|\bodaya yemek\w*|\boda ici ikram\w*"
        // Rooms of the building that are facilities, not room types.
        + @"|\bmeeting rooms?\b|\bconference rooms?\b|\bboard rooms?\b|\bbanquet rooms?\b|\bdining rooms?\b"
        + @"|\bbreakfast rooms?\b|\bsteam rooms?\b|\bchanging rooms?\b|\bgames? rooms?\b|\bfitness rooms?\b"
        + @"|\btreatment rooms?\b|\blaundry rooms?\b|\bcloak ?rooms?\b"
        + @"|\btoplanti oda\w*|\btoplanti salon\w*|\bkonferans oda\w*|\bkonferans salon\w*|\byemek oda\w*|\bkahvalti oda\w*|\bsoyunma oda\w*"
        + @"|\bbuhar oda\w*|\boyun oda\w*|\bcamasir oda\w*|\bbalo salon\w*|\bvestiyer\w*", Opts);

    private static readonly Regex NightsPattern = new(
        @"\b(\d{1,2}|bir|iki|uc|dort|bes|alti|yedi|one|two|three|four|five|six|seven|a)\s*(?:gece(?:lik)?|gun(?:luk)?|nights?|days?)\b", Opts);

    private static readonly Regex AdultsPattern = new(
        @"\b(\d{1,2}|bir|iki|uc|dort|bes|alti|one|two|three|four|five|six|a)\s*(?:yetiskin|buyuk|kisi(?:lik)?|adult|adults|people|person|persons|guests?|pax)\b", Opts);

    private static readonly Regex ChildrenPattern = new(
        @"\b(\d{1,2}|bir|iki|uc|dort|bes|alti|one|two|three|four|five|six|a)\s*(?:cocuk|cocuklu|bebek|child|children|kid|kids|baby)\b", Opts);

    private static readonly Regex NoChildrenPattern = new(@"\b(?:cocuk(?:suz)?\s+yok|cocuksuz|no (?:kids|children))\b", Opts);

    // dd.MM(.yyyy) / dd/MM(/yyyy) / yyyy-MM-dd, one date at a time.
    private static readonly Regex NumericDate = new(
        @"\b(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})\b|\b(?<d2>\d{1,2})[./](?<m2>\d{1,2})(?:[./](?<y2>\d{2,4}))?\b", Opts);

    // A numeric RANGE, matched before single dates because the abbreviated form
    // is not two dates: "15-18.10.2026" is one range whose first endpoint has no
    // month of its own. Recognising only single dates silently dropped the "15"
    // and moved the check-in to the 18th.
    //
    //   15-18.10.2026   15–18.10   15.10-18.10.2026   15.10.2026 - 18.10.2026
    //   15/10/2026 - 18/10/2026    2026-10-15 - 2026-10-18
    private static readonly Regex AbbreviatedRange = new(
        @"\b(?<d1>\d{1,2})\s*[-–—]\s*(?<d2>\d{1,2})[./](?<m>\d{1,2})(?:[./](?<y>\d{2,4}))?\b", Opts);

    private static readonly Regex FullNumericRange = new(
        @"\b(?<d1>\d{1,2})[./](?<m1>\d{1,2})(?:[./](?<y1>\d{2,4}))?\s*[-–—]\s*(?<d2>\d{1,2})[./](?<m2>\d{1,2})(?:[./](?<y2>\d{2,4}))?\b"
        + @"|\b(?<iy1>\d{4})-(?<im1>\d{1,2})-(?<id1>\d{1,2})\s*[-–—]\s*(?<iy2>\d{4})-(?<im2>\d{1,2})-(?<id2>\d{1,2})\b", Opts);

    // "15 Ekim", "15-18 Ekim", "15 ile 18 Ekim arası", "October 15", "Oct 15 to 18", "15th of October"
    private static readonly Regex MonthWord = new(
        @"\b(?<d>\d{1,2})(?:st|nd|rd|th)?\s*(?:-|–|—|ile|to|and|ve)?\s*(?<d2>\d{1,2})?(?:st|nd|rd|th)?\s*(?:of\s+)?(?<mon>ocak|subat|mart|nisan|mayis|haziran|temmuz|agustos|eylul|ekim|kasim|aralik|january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec)\b(?:\s*(?<y>\d{4}))?" +
        @"|\b(?<mon2>january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec)\s+(?<dd>\d{1,2})(?:st|nd|rd|th)?(?:\s*(?:-|–|—|to|and)\s*(?<dd2>\d{1,2})(?:st|nd|rd|th)?)?(?:,?\s*(?<yy>\d{4}))?", Opts);

    private static readonly Regex Today = new(@"\b(?:bugun|tonight|bu gece|today)\b", Opts);
    private static readonly Regex Tomorrow = new(@"\b(?:yarin|tomorrow)\b", Opts);
    private static readonly Regex DayAfter = new(@"\b(?:obur gun|day after tomorrow)\b", Opts);
    private static readonly Regex ThisWeekend = new(@"\b(?:bu hafta ?sonu|this weekend)\b", Opts);
    private static readonly Regex NextWeekend = new(@"\b(?:gelecek hafta ?sonu|onumuzdeki hafta ?sonu|haftaya hafta ?sonu|next weekend)\b", Opts);
    private static readonly Regex NextWeek = new(@"\b(?:gelecek hafta|onumuzdeki hafta|haftaya|next week)\b", Opts);

    private static readonly (Regex Pattern, string Code)[] RoomPatterns =
    {
        (new Regex(@"\b(?:bogaz\s+suit|bosphorus\s+suite|deluxe\s+bogaz|deluxe\s+bosphorus|bossuite|buyuk\s+suit|panoramik)\w*", Opts), "BOSSUITE"),
        (new Regex(@"\b(?:junior|saray\s+junior|jrsuite|palace\s+junior)\w*", Opts), "JRSUITE"),
        (new Regex(@"\b(?:executive|exec\b|yonetici)\w*", Opts), "EXEC"),
        (new Regex(@"\b(?:deluxe)\b(?!\s+(?:bogaz|bosphorus))", Opts), "DELUXE"),
        (new Regex(@"\b(?:garden|bahce)\w*", Opts), "GARDEN"),
    };

    // "All rooms / any room / no preference": drop a remembered room type. A
    // phrase that itself names rooms ("herhangi bir oda", "any room is fine",
    // "room doesn't matter", "tüm odalar") is unambiguous anywhere in a turn.
    // Folded input: no diacritics, apostrophes kept (both ' and ’).
    private static readonly Regex AllRooms = new(
        @"\b(?:tum odalar|butun odalar|hangi odalar|diger odalar|odalarin hepsi|herhangi bir oda\w*|oda(?: tipi)? fark ?etmez|hangi oda olursa"
        + @"|all rooms|all room types|which rooms|every room|other rooms|any(?: available)? rooms?(?: types?)?|any type of room|no room preference"
        + @"|(?:don['’]?t|do not) have a room preference|whichever room|room(?: type)? (?:doesn['’]?t|does not) matter)\b", Opts);

    // A bare "doesn't matter / fark etmez / no preference" turn names no room,
    // so it clears a remembered room ONLY when the whole turn is that phrase
    // and a room preference is on the table inside an active stay or search —
    // the guest is answering the concierge's own room question, and nothing
    // else in the turn could be what "it" refers to.
    private static readonly Regex NoPreferenceOnly = new(
        @"^\W*(?:fark ?etmez|farketmez|hicbir tercihim yok|tercihim yok|hangisi olursa(?: olsun)?|(?:it |that )?(?:doesn['’]?t|does not) matter(?: to me)?|no preference|whichever|any(?:thing)?(?: is fine| one)?)\W*$", Opts);

    private static readonly Regex TurkishHint = new(
        @"[çğıöşüÇĞİÖŞÜ]|\b(?:ve|bir|için|nedir|ne|kadar|kaç|kaça|var|yok|saat|mi|mı|mu|mü|nasıl|hangi|oda|odalar|gece|fiyat|müsait|rezervasyon|tarih|misafir|yetişkin|çocuk|hafta|sonu|yarın|bugün|ile|arası|arasi|kişi|kişilik|lütfen|merhaba|evet|hayır|istiyorum|istiyoruz|ocak|şubat|subat|mart|nisan|mayıs|mayis|haziran|temmuz|ağustos|agustos|eylül|eylul|ekim|kasım|kasim|aralık|aralik)\b", Opts);

    // Unambiguously English words. Deliberately no "a", "on", "at", "in", "to"
    // and no word the two languages share ("transfer", "spa", "suit"): "on" is
    // Turkish for ten and "at" for horse, and a bare fragment has to be allowed
    // to say nothing about its language.
    private static readonly Regex EnglishHint = new(
        @"\b(?:the|and|is|are|do|does|for|with|from|what|how|when|where|which|please|you|your|we|our|my|room|rooms|night|nights|adult|adults|child|children|price|rate|rates|book|booking|stay|weekend|week|tomorrow|today|there|any|have|has|can|could|would|want|need|much|many|available|availability|yes|thank|thanks|hello|hi|january|february|march|april|june|july|august|september|october|november|december)\b", Opts);

    /// <summary>
    /// Parse one turn. <paramref name="today"/> anchors relative dates;
    /// <paramref name="state"/> supplies what earlier turns already said, so a
    /// follow-up that names only a room is still a quotable stay query, and a
    /// bare "18 Ekim" or "3 gece" after the concierge asked for a check-out is
    /// read as the answer to that question.
    /// </summary>
    public static StayIntent Parse(string? text, DateOnly today, HotelConversationState? state = null)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length > 1000) raw = raw[..1000];

        // A turn that says nothing about its language ("18 Ekim", "3") keeps the
        // conversation's language rather than flipping it to English.
        var language = DetectLanguage(raw) ?? state?.Language ?? "tr";
        var folded = Fold(raw);

        var (checkIn, checkOut, malformedRange) = ParseDates(folded, today);
        var datesInTurn = checkIn is not null || checkOut is not null;
        int? nights = ParseCount(NightsPattern, folded);
        if (nights is > HotelServiceCatalog.MaxStayNights) nights = HotelServiceCatalog.MaxStayNights + 1;

        var policyTime = IsCheckInOutTimeQuestion(folded);
        var earliestAsked = !policyTime && EarliestPattern.IsMatch(folded);

        // "2 kişilik" / "2 people" style: a party count with a people word and no
        // explicit word for adults is read as adults (AdultsPattern).
        int? adults = ParseCount(AdultsPattern, folded);
        int? children = NoChildrenPattern.IsMatch(folded) ? 0 : ParseCount(ChildrenPattern, folded);

        var clearsRoom = AllRooms.IsMatch(folded)
                         || (NoPreferenceOnly.IsMatch(folded) && state is { RoomCode: not null } && (state.SearchEarliest || state.HasDates));
        var room = clearsRoom ? null : RoomPatterns.FirstOrDefault(p => p.Pattern.IsMatch(folded)).Code;

        // Is this a STAY question at all?
        //
        // PRECEDENCE, and it is the whole of the fix: a compound service subject
        // is recognised FIRST and masked out of the text the room vocabulary
        // reads, so the "room" inside "room service" and the "oda" inside "oda
        // servisi" belong to the service and are not also a room. Without the
        // mask, "how much is room service?" was a room question with no dates
        // and the concierge asked which dates the guest had in mind.
        //
        // After that: a room word, a stay verb or a date decides yes on its own.
        // A generic price word decides yes only beside a room type ("Deluxe ne
        // kadar?") or inside an active stay conversation ("peki fiyat?"). A
        // priced hotel SERVICE as the subject cancels a stay verb, a price word,
        // a DURATION and a date — "can I use the spa for 3 days?" is a question
        // about the spa, and the three days are the spa's, not a room's.
        var withoutServices = MaskServices(folded);
        var roomish = RoomWords.IsMatch(withoutServices) || room is not null || clearsRoom;
        var service = (ServiceCompounds.IsMatch(folded) || ServiceSubjects.IsMatch(folded)) && !roomish;
        var activeStay = state is { CheckIn: not null } || state?.SearchEarliest == true;
        var isStay = !policyTime && (
                         roomish
                         // A date range the guest WROTE is a stay anchor even when
                         // it does not resolve: "15/13/2026 - 18/10/2026" is a
                         // request with a typo, and treating it as a turn about
                         // nothing would answer a stay question with silence.
                         || ((datesInTurn || nights is not null || malformedRange) && !service)
                         || (StayVerbs.IsMatch(withoutServices) && !service)
                         || (PriceWords.IsMatch(folded) && (room is not null || (activeStay && !service)))
                         || (earliestAsked && !service && activeStay));

        // "Earliest / first available / en erken / ilk müsait": the guest is
        // asking the engine to FIND the check-in date, so no date is completed
        // from an earlier turn and none is asked for. A conversation already in
        // search mode stays in it for a follow-up that names no dates ("what
        // about Executive?", "4 gece olsun"); explicit dates end it.
        var earliest = isStay && !datesInTurn && ((earliestAsked && !service) || state?.SearchEarliest == true);

        // The concierge asked "check-out date, or how many nights?" and the guest
        // answered with ONE later date: that is the check-out, not a new check-in.
        if (!earliest && checkIn is not null && checkOut is null && nights is null
            && state is { CheckIn: not null, CheckOut: null } && checkIn > state.CheckIn)
        {
            checkOut = checkIn;
            checkIn = state.CheckIn;
        }

        // A length of stay completes whichever check-in is known — this turn's
        // ("yarın 3 gece") or the one stated earlier ("3 gece" as the answer).
        if (!earliest && checkOut is null && nights is { } n && n >= 1 && n <= HotelServiceCatalog.MaxStayNights
            && (checkIn ?? state?.CheckIn) is { } anchor)
        {
            checkIn ??= anchor;
            checkOut = anchor.AddDays(n);
        }

        // A room named on its own, after dates were given earlier, is a stay
        // question ("peki Executive?"): it is answered for the SAME nights.
        var effectiveIn = checkIn ?? state?.CheckIn;
        var effectiveOut = checkOut ?? state?.CheckOut;
        if (checkIn is not null && checkOut is null && state?.CheckOut is not null && nights is null)
        {
            // New check-in only; keep the previously stated length if it still fits.
            var previousNights = state.Nights;
            effectiveOut = previousNights > 0 ? checkIn.Value.AddDays(previousNights) : null;
        }

        if (!isStay && (room is not null || clearsRoom) && state?.HasDates == true) isStay = true;
        if (!isStay && !service && (checkIn is not null || nights is not null)) isStay = true;

        var gap = StayIntentGap.None;
        if (earliest)
        {
            // A search needs a length of stay and nothing else: the party has the
            // flow's existing default, the date is what is being searched for.
            var knownNights = nights ?? state?.RequestedNights ?? (state?.HasDates == true ? state.Nights : null);
            if (knownNights is null or < 1) gap = StayIntentGap.MissingNights;
        }
        else if (isStay)
        {
            // A range the guest WROTE but that does not resolve is invalid, not
            // missing: "31.02-18.10.2026" is a request with a typo in it, and
            // answering "which dates did you have in mind?" pretends they said
            // nothing. It is checked first, because it is the more specific
            // fact about the same turn.
            if (malformedRange) gap = StayIntentGap.InvalidRange;
            else if (effectiveIn is null) gap = StayIntentGap.MissingDates;
            else if (effectiveOut is null) gap = StayIntentGap.MissingCheckOut;
            else if (effectiveOut <= effectiveIn || effectiveIn < today
                     || effectiveOut.Value.DayNumber - effectiveIn.Value.DayNumber > HotelServiceCatalog.MaxStayNights)
                gap = StayIntentGap.InvalidRange;
        }

        return new StayIntent
        {
            IsStayQuery = isStay,
            CheckIn = earliest ? null : checkIn,
            CheckOut = earliest ? null : checkOut ?? (checkIn is not null ? effectiveOut : null),
            Nights = nights,
            Adults = adults,
            Children = children,
            RoomCode = room,
            Language = language,
            ClearsRoom = clearsRoom,
            SearchEarliestAvailability = earliest,
            Gap = gap
        };
    }

    /// <summary>
    /// The folded turn with every hotel-service phrase blanked out, so the room
    /// and stay vocabularies read only what is left. Replaced by spaces of the
    /// same length rather than removed, so nothing on either side of a service
    /// phrase is joined into a word that was never written.
    /// </summary>
    private static string MaskServices(string folded)
    {
        var masked = folded;
        foreach (var pattern in new[] { ServiceCompounds, ServiceSubjects })
            masked = pattern.Replace(masked, m => new string(' ', m.Length));

        return masked;
    }

    /// <summary>"What time is check-in?", "earliest check-in time?", "giriş saati?", "erken giriş?": hotel policy, not a stay or a search.</summary>
    public static bool IsCheckInOutTimeQuestion(string folded) =>
        PolicyPhrases.IsMatch(folded) || (CheckInOutWords.IsMatch(folded) && TimeWords.IsMatch(folded));

    /// <summary>Crude but cheap: Turkish letters, or common Turkish function words.</summary>
    public static bool LooksTurkish(string? text) =>
        !string.IsNullOrWhiteSpace(text) && TurkishHint.IsMatch(text);

    /// <summary>
    /// "tr", "en", or null when the text carries no language signal at all (a
    /// bare date, a number). Turkish is checked first, so a sentence mixing both
    /// resolves to the language the demo defaults to.
    /// </summary>
    public static string? DetectLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (TurkishHint.IsMatch(text)) return "tr";
        if (EnglishHint.IsMatch(text)) return "en";
        return null;
    }

    /// <summary>The turn's dates, plus whether a range SHAPE was present but did not resolve.</summary>
    private static (DateOnly? In, DateOnly? Out, bool Malformed) ParseDates(string folded, DateOnly today)
    {
        // Relative phrases first: they are unambiguous and cheap.
        if (NextWeekend.IsMatch(folded))
        {
            var sat = NextSaturday(today).AddDays(7);
            return (sat, sat.AddDays(2), false);
        }
        if (ThisWeekend.IsMatch(folded))
        {
            var sat = NextSaturday(today);
            return (sat, sat.AddDays(2), false);
        }
        if (DayAfter.IsMatch(folded)) return (today.AddDays(2), null, false);
        if (Tomorrow.IsMatch(folded)) return (today.AddDays(1), null, false);
        if (Today.IsMatch(folded)) return (today, null, false);
        if (NextWeek.IsMatch(folded))
        {
            var monday = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7 is 0 ? 7 : ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7);
            return (monday, null, false);
        }

        var dates = new List<DateOnly>();

        foreach (Match m in MonthWord.Matches(folded))
        {
            var monthName = m.Groups["mon"].Success ? m.Groups["mon"].Value : m.Groups["mon2"].Value;
            var month = MonthNumber(monthName);
            if (month == 0) continue;

            var year = ParseYear(m.Groups["y"].Success ? m.Groups["y"].Value : m.Groups["yy"].Value, today, month);

            var d1 = m.Groups["d"].Success ? m.Groups["d"].Value : m.Groups["dd"].Value;
            var d2 = m.Groups["d2"].Success ? m.Groups["d2"].Value : m.Groups["dd2"].Value;

            if (TryDay(year, month, d1, out var first)) dates.Add(first);
            if (!string.IsNullOrEmpty(d2) && TryDay(year, month, d2, out var second)) dates.Add(second);
        }

        // A numeric RANGE before single numeric dates: the abbreviated form
        // "15-18.10.2026" is one range, and reading it as the single date it
        // contains dropped an endpoint the guest actually stated.
        // A numeric RANGE is consumed ATOMICALLY. If the text has a range SHAPE,
        // that span is this turn's dates and nothing else in it is scanned:
        // "31.02-18.10.2026" is a range whose first endpoint does not exist, so
        // it is an INVALID RANGE, not an invitation to notice that "18.10.2026"
        // on its own is a perfectly good date. Falling through used to turn a
        // typo into a different, valid stay the guest never asked for.
        if (dates.Count == 0)
        {
            var range = ReadNumericRange(folded, today);
            if (range.Shape)
            {
                return range.From is { } from && range.To is { } to
                    ? (from, to, false)
                    : (null, null, true);   // a range that does not resolve is MALFORMED, not "no dates"
            }
        }

        if (dates.Count == 0)
        {
            foreach (Match m in NumericDate.Matches(folded))
            {
                if (m.Groups["y"].Success)
                {
                    if (TryDay(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                            int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture), m.Groups["d"].Value, out var iso))
                        dates.Add(iso);
                    continue;
                }

                var month = int.Parse(m.Groups["m2"].Value, CultureInfo.InvariantCulture);
                if (month is < 1 or > 12) continue;
                var year = ParseYear(m.Groups["y2"].Success ? m.Groups["y2"].Value : null, today, month);
                if (TryDay(year, month, m.Groups["d2"].Value, out var parsed)) dates.Add(parsed);
            }
        }

        if (dates.Count == 0) return (null, null, false);

        // The guest's ORDER is preserved. Sorting would turn
        // "18.10.2026 - 15.10.2026" into a valid three-night stay starting on
        // the 15th — a range nobody asked for, produced silently. A reversed
        // range reaches the gap check as it was written and is reported as invalid.
        var checkIn = dates[0];
        DateOnly? checkOut = dates.Count > 1 ? dates[^1] : null;
        return (checkIn, checkOut, false);
    }

    /// <summary>
    /// The ONE numeric range in the turn, read as a whole.
    ///
    /// <para>
    /// <b>Shape first, validity second.</b> If the text looks like a range —
    /// two numeric endpoints joined by a dash — that span IS the turn's dates,
    /// and whether it resolves is a separate question. So `31.02-18.10.2026`
    /// reports a shape with no dates, which becomes `InvalidRange`; it does not
    /// fall through to a scanner that would notice `18.10.2026` inside it and
    /// quote a stay the guest never asked for. A typo must not be repaired into
    /// a different, valid request.
    /// </para>
    ///
    /// <para>
    /// <see cref="RangeRead.Shape"/> is therefore the load-bearing value: it
    /// says "this text is a range, stop looking", independently of
    /// <see cref="RangeRead.From"/> and <see cref="RangeRead.To"/>.
    /// </para>
    /// </summary>
    private readonly record struct RangeRead(bool Shape, DateOnly? From, DateOnly? To);

    private static RangeRead ReadNumericRange(string folded, DateOnly today)
    {
        // The fully-written forms first: "15.10.2026 - 18.10.2026",
        // "15.10-18.10.2026", "2026-10-15 - 2026-10-18".
        var full = FullNumericRange.Match(folded);
        if (full.Success)
        {
            if (full.Groups["iy1"].Success)
            {
                var okIso = TryDay(Number(full, "iy1"), Number(full, "im1"), full.Groups["id1"].Value, out var isoFrom)
                            & TryDay(Number(full, "iy2"), Number(full, "im2"), full.Groups["id2"].Value, out var isoTo);
                return okIso ? new RangeRead(true, isoFrom, isoTo) : new RangeRead(true, null, null);
            }

            var month1 = Number(full, "m1");
            var month2 = Number(full, "m2");
            if (month1 is < 1 or > 12 || month2 is < 1 or > 12) return new RangeRead(true, null, null);

            // A year stated on either end applies to both: "15.10 - 18.10.2026"
            // names one range in one year, and inferring a different year for
            // the first endpoint would invent a stay across a year boundary.
            var stated = full.Groups["y2"].Success ? full.Groups["y2"].Value
                : full.Groups["y1"].Success ? full.Groups["y1"].Value : null;
            var year1 = ParseYear(full.Groups["y1"].Success ? full.Groups["y1"].Value : stated, today, month1);
            var year2 = ParseYear(full.Groups["y2"].Success ? full.Groups["y2"].Value : stated, today, month2);

            var ok = TryDay(year1, month1, full.Groups["d1"].Value, out var from)
                     & TryDay(year2, month2, full.Groups["d2"].Value, out var to);
            return ok ? new RangeRead(true, from, to) : new RangeRead(true, null, null);
        }

        // The abbreviated form: "15-18.10.2026", where the month and the year
        // belong to both endpoints.
        var abbreviated = AbbreviatedRange.Match(folded);
        if (!abbreviated.Success) return new RangeRead(false, null, null);

        var month = Number(abbreviated, "m");
        if (month is < 1 or > 12) return new RangeRead(true, null, null);

        var year = ParseYear(abbreviated.Groups["y"].Success ? abbreviated.Groups["y"].Value : null, today, month);
        var bothDays = TryDay(year, month, abbreviated.Groups["d1"].Value, out var first)
                       & TryDay(year, month, abbreviated.Groups["d2"].Value, out var second);

        return bothDays ? new RangeRead(true, first, second) : new RangeRead(true, null, null);
    }

    private static int Number(Match match, string group) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static DateOnly NextSaturday(DateOnly today)
    {
        // On a Saturday "this weekend" is today (delta 0); every other day it is
        // the coming Saturday.
        var delta = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(delta);
    }

    private static int ParseYear(string? value, DateOnly today, int month)
    {
        if (!string.IsNullOrEmpty(value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var y))
            return y < 100 ? 2000 + y : y;

        // No year: the next occurrence of that month, so "Ekim" in November means
        // next year's October rather than a month already gone.
        return month >= today.Month ? today.Year : today.Year + 1;
    }

    private static bool TryDay(int year, int month, string dayText, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out var day)) return false;
        if (year is < 2000 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    private static int MonthNumber(string name) => name.ToLowerInvariant() switch
    {
        "ocak" or "january" or "jan" => 1,
        "subat" or "february" or "feb" => 2,
        "mart" or "march" or "mar" => 3,
        "nisan" or "april" or "apr" => 4,
        "mayis" or "may" => 5,
        "haziran" or "june" or "jun" => 6,
        "temmuz" or "july" or "jul" => 7,
        "agustos" or "august" or "aug" => 8,
        "eylul" or "september" or "sep" or "sept" => 9,
        "ekim" or "october" or "oct" => 10,
        "kasim" or "november" or "nov" => 11,
        "aralik" or "december" or "dec" => 12,
        _ => 0
    };

    private static int? ParseCount(Regex pattern, string folded)
    {
        var m = pattern.Match(folded);
        if (!m.Success) return null;
        return WordNumber(m.Groups[1].Value);
    }

    private static int? WordNumber(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        return value.ToLowerInvariant() switch
        {
            "bir" or "one" or "a" => 1,
            "iki" or "two" => 2,
            "uc" or "three" => 3,
            "dort" or "four" => 4,
            "bes" or "five" => 5,
            "alti" or "six" => 6,
            "yedi" or "seven" => 7,
            _ => null
        };
    }

    /// <summary>Accent- and case-folded, with the Turkish dotted/dotless i handled explicitly.</summary>
    public static string Fold(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                'ç' or 'Ç' => 'c',
                'ğ' or 'Ğ' => 'g',
                'ı' or 'I' => 'i',
                'İ' => 'i',
                'ö' or 'Ö' => 'o',
                'ş' or 'Ş' => 's',
                'ü' or 'Ü' => 'u',
                'â' or 'Â' => 'a',
                'î' or 'Î' => 'i',
                'û' or 'Û' => 'u',
                _ => char.ToLowerInvariant(ch)
            });
        }
        return sb.ToString();
    }
}
