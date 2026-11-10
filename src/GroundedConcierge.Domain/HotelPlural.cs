namespace GroundedConcierge.Domain;

/// <summary>
/// The one place the hotel domain turns a count into words a guest reads.
///
/// <para>
/// English needs a singular and a plural ("1 night" / "2 nights", "1 child" /
/// "2 children"); Turkish does not inflect a noun after a numeral ("1 gece" /
/// "2 gece", "3 yetişkin"), so the Turkish helper deliberately applies NO
/// plural rule at all — writing "2 geceler" would be wrong, not merely odd.
/// Natural English, untouched Turkish grammar, and no English plural logic
/// leaking into the Turkish text.
/// </para>
///
/// <para>
/// Hotel-scoped on purpose: until a second domain actually needs the same
/// pattern, a shared abstraction would be speculation.
/// </para>
/// </summary>
public static class HotelPlural
{
    /// <summary>"1 night", "2 nights", "1 child", "2 children" — the count followed by the right English form.</summary>
    public static string English(int count, string singular, string plural) =>
        count == 1 ? $"{count} {singular}" : $"{count} {plural}";

    /// <summary>"1 gece", "2 gece" — the count followed by the uninflected Turkish noun.</summary>
    public static string Turkish(int count, string noun) => $"{count} {noun}";
}
