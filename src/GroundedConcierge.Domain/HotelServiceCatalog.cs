namespace GroundedConcierge.Domain;

/// <summary>
/// The bounds a stay request has to respect. One place, read by the engine, the
/// parser and the earliest-stay search alike — a limit typed into two places is
/// two limits that will drift.
/// </summary>
public static class HotelServiceCatalog
{
    /// <summary>Longest stay the demo quotes. Long enough for any demonstration, short enough to stop a 400-night loop.</summary>
    public const int MaxStayNights = 21;

    /// <summary>How far ahead a stay may start; also the bound of the earliest-stay search.</summary>
    public const int MaxAdvanceDays = 365;

    /// <summary>Adults on one request. Larger parties are a group enquiry for reception.</summary>
    public const int MaxAdultsPerRequest = 6;

    /// <summary>Children on one request.</summary>
    public const int MaxChildrenPerRequest = 6;
}
