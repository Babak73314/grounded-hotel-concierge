namespace GroundedConcierge.Domain;

/// <summary>
/// The authoritative commercial facts of ONE concierge turn — everything the
/// reservation engine computed, and the only input <see cref="HotelAnswerComposer"/>
/// writes a commercial answer from.
///
/// <para>
/// It is built by the code that ran the engine, from the engine's own result —
/// never parsed back out of prose, and never supplied by a client. The
/// answering layer's <c>HotelFactValidator</c> reads the same object, so a
/// figure in a model's draft either is one of these figures or it is not, and
/// no wording can make an invented €999 into €580.
/// </para>
///
/// <para>
/// A turn with no stay and no quotes (a document answer, a no-evidence turn)
/// carries <see cref="None"/>: there is nothing commercial to compose, and the
/// answering layer's ACTION rules — booked, held, charged, sent — still run on
/// any model prose, because those are forbidden on every turn.
/// </para>
/// </summary>
public sealed record HotelAnswerFacts
{
    /// <summary>A turn that carries no computed commercial fact.</summary>
    public static HotelAnswerFacts None { get; } = new();

    /// <summary>The stay the engine priced, or the one it FOUND in an earliest-availability search.</summary>
    public StayRequest? Stay { get; init; }

    /// <summary>One quote per room type the engine returned for that stay.</summary>
    public IReadOnlyList<StayQuote> Quotes { get; init; } = Array.Empty<StayQuote>();

    /// <summary>
    /// Every room type in the catalogue, active or not — the authority on what
    /// a room type is CALLED. A draft may legitimately name a type it is not
    /// quoting ("the Garden Room is our smallest"); it may not name one that
    /// does not exist.
    /// </summary>
    public IReadOnlyList<HotelRoomType> Catalogue { get; init; } = Array.Empty<HotelRoomType>();

    /// <summary>
    /// The property's own today. A trusted date, because an answer may
    /// legitimately say what the date is now — and the demo clock, not the
    /// server's, is what every other part of the flow reads.
    /// </summary>
    public DateOnly? Today { get; init; }

    /// <summary>Where an earliest-availability search started, when this turn was one.</summary>
    public DateOnly? SearchedFrom { get; init; }

    /// <summary>The last check-in date such a search examined.</summary>
    public DateOnly? SearchedThrough { get; init; }

    /// <summary>
    /// Whether candidate dates BEFORE the found check-in were examined and
    /// rejected — the only case in which "earlier dates had no room" is true.
    /// Search metadata, never an inference from the dates.
    /// </summary>
    public bool EarlierCandidatesRejected { get; init; }

    /// <summary>How many earlier candidates were examined and rejected.</summary>
    public int RejectedCandidates { get; init; }

    /// <summary>True when these facts came from an earliest-availability SEARCH rather than a dated quote.</summary>
    public bool IsEarliestSearch => SearchedFrom is not null;

    /// <summary>
    /// Whether the numeric rules apply at all. False for a document answer,
    /// where a price in a retrieved passage is the document's fact and not the
    /// engine's, and checking it against a quote would refuse the truth.
    /// </summary>
    public bool CarriesCommercialFacts => Stay is not null || Quotes.Count > 0;

    /// <summary>Nights of the priced stay, or null when there is no stay.</summary>
    public int? Nights => Stay is { } stay ? stay.CheckOut.DayNumber - stay.CheckIn.DayNumber : null;
}
