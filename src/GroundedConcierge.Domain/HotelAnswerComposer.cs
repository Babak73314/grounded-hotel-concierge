using System.Globalization;
using System.Text;

namespace GroundedConcierge.Domain;

/// <summary>
/// The commercial answer, written by CODE.
///
/// <para>
/// <b>Why this exists.</b> The tempting design is to let a language model write
/// the figures and then check them against an allowlist. That is the wrong
/// shape: an allowlist over values cannot see the ROLE a value plays, so
/// "€704/night" passes when €704 is the TOTAL; it cannot see a claim that
/// carries no value at all, so "every room type is available for those dates"
/// passes against a sold-out room; and a bare "the total is 999" passes because
/// no currency symbol made it look like money. Each miss becomes a new rule, and
/// the rules keep losing.
/// </para>
///
/// <para>
/// So the model does not state commercial facts at all. Everything a guest
/// reads about dates, nights, room types, availability, rooms left, nightly
/// rates, averages and totals is rendered here, from the same
/// <see cref="HotelAnswerFacts"/> a quote card would be rendered from — one
/// object, one arithmetic, two views that cannot disagree. A commercial turn
/// makes NO model call, and the wording below is the whole answer.
/// </para>
///
/// <para>
/// The wording is deliberately plain. It is not trying to sound like a
/// concierge — it is trying to be exactly true, in a demonstration where the
/// interesting claim is that the arithmetic reaches the guest unaltered.
/// </para>
/// </summary>
public static class HotelAnswerComposer
{
    // Lines are joined with '\n' explicitly — never AppendLine, whose line end
    // is the operating system's — so the same facts give byte-identical text on
    // every machine.

    /// <summary>The full factual answer for a turn that carries computed facts. Empty when it carries none.</summary>
    public static string Compose(HotelAnswerFacts facts, bool turkish)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.CarriesCommercialFacts) return string.Empty;

        return facts.IsEarliestSearch ? ComposeEarliest(facts, turkish) : ComposeQuote(facts, turkish);
    }

    private static string ComposeQuote(HotelAnswerFacts facts, bool turkish)
    {
        var sb = new StringBuilder();
        var stay = facts.Stay!;
        var nights = facts.Nights ?? 0;

        // "For 15.10.2026 to 18.10.2026 (3 nights, 2 adults):" — the dates in
        // the same dd.MM.yyyy shape everywhere,
        // the counts in proper singular/plural English, and in Turkish the
        // uninflected noun a numeral takes.
        sb.Append(turkish
            ? $"{Date(stay.CheckIn)} girişli, {Date(stay.CheckOut)} çıkışlı {HotelPlural.Turkish(nights, "gecelik")} konaklama ({Party(stay, true)}):"
            : $"For {Date(stay.CheckIn)} to {Date(stay.CheckOut)} ({Nights(nights, false)}, {Party(stay, false)}):");

        AppendRooms(sb, facts, turkish);
        AppendFooter(sb, turkish);
        return sb.ToString();
    }

    private static string ComposeEarliest(HotelAnswerFacts facts, bool turkish)
    {
        var sb = new StringBuilder();
        var stay = facts.Stay!;
        var nights = facts.Nights ?? 0;

        sb.Append(turkish
            ? $"En erken müsait {HotelPlural.Turkish(nights, "gecelik")} konaklama: giriş {Date(stay.CheckIn)}, çıkış {Date(stay.CheckOut)} ({Party(stay, true)})."
            : $"The earliest available {nights}-night stay is from {Date(stay.CheckIn)} to {Date(stay.CheckOut)}, for {Party(stay, false)}.");

        // Whether earlier dates were rejected is SEARCH METADATA, never an
        // inference: a stay that starts on the search start had nothing earlier
        // examined at all, and saying otherwise would be a false statement about
        // the property's availability. The count and the date range come from
        // the result; only the sentence around them is written here.
        if (facts.EarlierCandidatesRejected && facts.SearchedFrom is { } from)
        {
            var lastRejected = stay.CheckIn.AddDays(-1);
            if (facts.RejectedCandidates == 1)
            {
                sb.Append(turkish
                    ? $" {Date(from)} giriş tarihi de incelendi; o tarihte her gece için boş oda yoktu."
                    : $" The earlier check-in date {Date(from)} was also checked, and it had no room free on every night.");
            }
            else
            {
                sb.Append(turkish
                    ? $" {Date(from)} ile {Date(lastRejected)} arasındaki {HotelPlural.Turkish(facts.RejectedCandidates, "giriş tarihi")} de incelendi; hiçbirinde her gece için boş oda yoktu."
                    : $" The {facts.RejectedCandidates} earlier check-in dates from {Date(from)} to {Date(lastRejected)} were also checked, and none had a room free on every night.");
            }
        }
        else if (facts.SearchedFrom is { } start)
        {
            sb.Append(turkish
                ? $" Arama {Date(start)} tarihinden başladı ve incelenen ilk tarih uygun çıktı; daha erken bir tarih incelenmedi."
                : $" The search started on {Date(start)} and this first date already had a room free; no earlier date was examined.");
        }

        AppendRooms(sb, facts, turkish);
        AppendFooter(sb, turkish);
        return sb.ToString();
    }

    private static void AppendRooms(StringBuilder sb, HotelAnswerFacts facts, bool turkish)
    {
        foreach (var quote in facts.Quotes)
        {
            var name = quote.Room.Name(turkish);
            sb.Append('\n');
            sb.Append("- ").Append(name).Append(": ");

            if (!quote.IsAvailable)
            {
                sb.Append(turkish ? "bu tarihlerde dolu" : "sold out for these dates");
            }
            else if (quote.FitsParty == false)
            {
                sb.Append(turkish
                    ? $"bu misafir sayısı için uygun değil (en fazla {HotelPlural.Turkish(quote.Room.MaxAdults, "yetişkin")} / {HotelPlural.Turkish(quote.Room.MaxGuests, "kişi")})"
                    : $"sleeps up to {HotelPlural.English(quote.Room.MaxAdults, "adult", "adults")} / {HotelPlural.English(quote.Room.MaxGuests, "guest", "guests")}, so it does not fit this party");
            }
            else
            {
                sb.Append(turkish
                    ? $"{HotelPlural.Turkish(quote.RoomsAvailable, "oda")} müsait"
                    : $"{HotelPlural.English(quote.RoomsAvailable, "room", "rooms")} available");
            }

            sb.Append(turkish
                ? $", toplam {Money(quote.TotalEur)} ({HotelPlural.Turkish(quote.Nights, "gece")}, gecelik ortalama {Money(quote.AvgNightlyEur)})."
                : $", {Money(quote.TotalEur)} total for {Nights(quote.Nights, false)} (average {Money(quote.AvgNightlyEur)} a night).");
        }
    }

    private static void AppendFooter(StringBuilder sb, bool turkish)
    {
        sb.Append('\n');
        sb.Append(turkish
            ? "Fiyatlara kahvaltı dahildir, şehir vergisi hariçtir. Bu bir tanıtım teklifidir: hiçbir oda rezerve edilmedi, tutulmadı ve hiçbir ücret tahsil edilmedi."
            : "Breakfast is included and city tax is excluded. This is a demonstration quote: no room is reserved, nothing is held and nothing is charged.");
    }

    /// <summary>dd.MM.yyyy in both languages, so a date reads the same whichever language the guest writes in.</summary>
    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>Whole euros with a thousands separator, exactly as the cards render them.</summary>
    private static string Money(decimal amount) => "€" + amount.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"1 night" / "3 nights" in English; "1 gece" / "3 gece" in Turkish.</summary>
    private static string Nights(int nights, bool turkish) =>
        turkish ? HotelPlural.Turkish(nights, "gece") : HotelPlural.English(nights, "night", "nights");

    /// <summary>"2 adults and 1 child" / "1 adult" in English; "2 yetişkin ve 1 çocuk" / "1 yetişkin" in Turkish.</summary>
    private static string Party(StayRequest stay, bool turkish)
    {
        var adults = turkish
            ? HotelPlural.Turkish(stay.Adults, "yetişkin")
            : HotelPlural.English(stay.Adults, "adult", "adults");
        if (stay.Children <= 0) return adults;

        var children = turkish
            ? HotelPlural.Turkish(stay.Children, "çocuk")
            : HotelPlural.English(stay.Children, "child", "children");
        return turkish ? $"{adults} ve {children}" : $"{adults} and {children}";
    }
}
