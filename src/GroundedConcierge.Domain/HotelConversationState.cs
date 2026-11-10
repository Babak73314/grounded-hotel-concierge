namespace GroundedConcierge.Domain;

/// <summary>
/// What the concierge remembers about ONE conversation between turns: the
/// dates, the party and the room a guest has already told it, the language
/// they are writing in — and whether they are asking the engine to FIND a date
/// rather than pricing one they chose.
///
/// <para>
/// A model given the whole history can usually recover "the same dates" from
/// three turns ago; a deterministic quote engine cannot, and the quote is what
/// has to be right. So the dates a guest states are kept here, in code, and a
/// follow-up like "and the Executive room?" is quoted for the SAME nights without
/// the model being asked to remember them. One instance per chat circuit; nothing
/// in it is stored unless the guest consented to the conversation being kept.
/// </para>
///
/// <para>
/// <b>Search mode.</b> An earliest-availability request ("earliest room for 3
/// nights") puts the conversation into search mode: the length of stay, the
/// party and the room preference are remembered, the dates the engine FOUND are
/// shown but deliberately NOT stored, and every follow-up that names no dates
/// ("what about Executive?", "4 gece olsun") re-runs the search — the earliest
/// Executive stay may well start on a different day than the earliest Garden
/// stay. Stating explicit dates ends search mode and returns to ordinary quoting.
/// </para>
/// </summary>
public sealed class HotelConversationState
{
    public HotelConversationState()
    {
    }

    /// <summary>Start in a given language, so a first turn with no language signal answers in it.</summary>
    public HotelConversationState(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language)) Language = language;
    }

    public DateOnly? CheckIn { get; private set; }
    public DateOnly? CheckOut { get; private set; }
    public int? Adults { get; private set; }
    public int? Children { get; private set; }
    public string? RoomCode { get; private set; }

    /// <summary>"tr" or "en" — the language of the last guest turn that carried a language signal.</summary>
    public string Language { get; private set; } = "tr";

    /// <summary>True while the guest is asking the engine to FIND the earliest stay rather than pricing dates they chose.</summary>
    public bool SearchEarliest { get; private set; }

    /// <summary>The length of stay the guest asked for without dates ("3 gece", "for three nights"); what an earliest search needs.</summary>
    public int? RequestedNights { get; private set; }

    public bool HasDates => CheckIn is not null && CheckOut is not null;

    public int Nights => HasDates ? CheckOut!.Value.DayNumber - CheckIn!.Value.DayNumber : 0;

    /// <summary>
    /// Merge what a new turn said over what is already known. Only stated values
    /// move — and only from a turn that was ABOUT a stay.
    ///
    /// <para>
    /// The second half is not a refinement, it is the rule. "How much is the
    /// airport transfer for 4 people?" is a question about a transfer; the four
    /// people are the transfer's. Absorbing them used to silently change the
    /// party of the stay the guest was in the middle of arranging, so the next
    /// quote came back for four adults and nothing on screen said why. A
    /// non-stay turn now moves exactly one thing: the language it was written
    /// in, which is a fact about the conversation rather than about a stay.
    /// </para>
    /// </summary>
    public void Absorb(StayIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (!intent.IsStayQuery)
        {
            if (!string.IsNullOrWhiteSpace(intent.Language)) Language = intent.Language;
            return;
        }

        if (intent.SearchEarliestAvailability)
        {
            // Entering (or staying in) search mode. A length the guest had
            // implied through dates ("15–18 Ekim … en erken ne zaman?") is kept
            // as the requested length; the dates themselves are dropped, because
            // the whole point is to find new ones.
            if (intent.Nights is null && RequestedNights is null && HasDates) RequestedNights = Nights;
            SearchEarliest = true;
            CheckIn = CheckOut = null;
        }
        else if (intent.CheckIn is not null || intent.CheckOut is not null)
        {
            SearchEarliest = false;
        }

        if (intent.CheckIn is not null) CheckIn = intent.CheckIn;
        if (intent.CheckOut is not null) CheckOut = intent.CheckOut;
        if (intent.CheckIn is not null && intent.CheckOut is null && intent.Nights is { } n && n > 0)
            CheckOut = intent.CheckIn.Value.AddDays(n);
        if (intent.Nights is { } requested && requested > 0) RequestedNights = requested;
        if (intent.Adults is not null) Adults = intent.Adults;
        if (intent.Children is not null) Children = intent.Children;
        if (intent.ClearsRoom) RoomCode = null;
        if (!string.IsNullOrWhiteSpace(intent.RoomCode)) RoomCode = intent.RoomCode;
        if (!string.IsNullOrWhiteSpace(intent.Language)) Language = intent.Language;
    }

    public void ClearRoom() => RoomCode = null;

    public void Clear()
    {
        CheckIn = CheckOut = null;
        Adults = Children = null;
        RoomCode = null;
        SearchEarliest = false;
        RequestedNights = null;
    }

    /// <summary>The stay request this state describes, when it has dates.</summary>
    public StayRequest? ToRequest() => HasDates
        ? new StayRequest(CheckIn!.Value, CheckOut!.Value, Adults ?? 2, Children ?? 0, RoomCode)
        : null;

    /// <summary>
    /// The earliest-availability search this state describes, when it is in
    /// search mode and knows the length of stay. The party falls back to the
    /// flow's existing quoting default (2 adults, no children) — the same
    /// default <see cref="ToRequest"/> applies; nothing new is assumed.
    /// </summary>
    public EarliestStayRequest? ToEarliestRequest(DateOnly today) => SearchEarliest && RequestedNights is { } nights
        ? new EarliestStayRequest(today, nights, Adults ?? 2, Children ?? 0, RoomCode)
        : null;
}
