using System.Globalization;

namespace GroundedConcierge.Domain;

/// <summary>
/// The fixed guest-facing sentences a turn needs when it cannot be quoted: a
/// missing date, a request outside the bounds, a search that found nothing.
/// Written by people, in both languages, and never by a model — a clarifying
/// question is part of the commercial flow, so it obeys the same rule.
/// </summary>
public static class GuestMessages
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>
    /// The search found no qualifying stay inside the supported window. Says how
    /// far it looked, so "no availability" is a bounded statement rather than a
    /// shrug.
    /// </summary>
    public static string NoAvailability(EarliestStayResult result, bool turkish, string? roomName = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var r = result.Request;
        var culture = turkish ? Tr : En;
        var from = result.SearchedFrom.ToString("d MMMM yyyy", culture);
        var through = result.SearchedThrough.ToString("d MMMM yyyy", culture);

        if (turkish)
        {
            var party = $"{r.Adults} yetişkin" + (r.Children > 0 ? $" ve {r.Children} çocuk" : string.Empty);
            var room = roomName is null ? string.Empty : $" {roomName} tipinde";
            return $"{from} ile {through} arasında ({result.HorizonDays} gün ileriye kadar) {party} için{room} her gecesi müsait {r.Nights} gecelik bir konaklama bulamadım. Daha kısa bir konaklama deneyebilir veya resepsiyondan başka seçenekler isteyebilirsiniz; hiçbir şey ayrılmadı.";
        }
        else
        {
            var party = HotelPlural.English(r.Adults, "adult", "adults")
                + (r.Children > 0 ? " and " + HotelPlural.English(r.Children, "child", "children") : string.Empty);
            var room = roomName is null ? string.Empty : $" in the {roomName}";
            return $"I searched from {from} through {through} ({result.HorizonDays} days ahead) and found no {r.Nights}-night stay{room} with a room free on every night for {party}. You could try a shorter stay, or ask reception for other options; nothing has been reserved.";
        }
    }

    /// <summary>What to ask when a stay question cannot be quoted yet.</summary>
    public static string ClarificationFor(StayIntentGap gap, bool turkish) => gap switch
    {
        StayIntentGap.MissingDates => turkish
            ? "Fiyat ve müsaitlik için hangi tarihlerde konaklamak istediğinizi söyler misiniz? Örneğin: 15–18 Ekim, 2 yetişkin."
            : "To check rates and availability, which dates would you like to stay? For example: 15–18 October, 2 adults.",
        StayIntentGap.MissingCheckOut => turkish
            ? "Giriş tarihinizi aldım. Çıkış tarihiniz veya kaç gece kalacağınız nedir?"
            : "I have your check-in date. What is your check-out date, or how many nights will you stay?",
        StayIntentGap.InvalidRange => turkish
            ? $"Bu tarih aralığını fiyatlandıramıyorum: giriş tarihi bugünden önce olamaz, çıkış girişten sonra olmalı ve bir konaklama en fazla {HotelServiceCatalog.MaxStayNights} gece olabilir. Tarihleri tekrar paylaşır mısınız?"
            : $"I cannot quote that date range: check-in cannot be in the past, check-out must be after check-in, and a stay is at most {HotelServiceCatalog.MaxStayNights} nights. Could you share the dates again?",
        StayIntentGap.MissingNights => turkish
            ? "En erken müsait tarihi bulabilmem için kaç gece kalmak istediğinizi söyler misiniz? Örneğin: 3 gece."
            : "So that I can find the earliest available dates, how many nights would you like to stay? For example: 3 nights.",
        _ => string.Empty
    };

    /// <summary>Short localized description of a validation problem.</summary>
    public static string Describe(StayRequestProblem problem, bool turkish) => problem switch
    {
        StayRequestProblem.CheckOutNotAfterCheckIn => turkish ? "Çıkış tarihi giriş tarihinden sonra olmalıdır." : "Check-out must be after check-in.",
        StayRequestProblem.CheckInInThePast => turkish ? "Giriş tarihi geçmişte olamaz." : "Check-in cannot be in the past.",
        StayRequestProblem.TooFarAhead => turkish ? "Bu kadar ileri bir tarih için fiyat verilemiyor." : "That date is too far ahead to quote.",
        StayRequestProblem.TooManyNights => turkish ? $"Bir konaklama en fazla {HotelServiceCatalog.MaxStayNights} gece olabilir." : $"A stay is at most {HotelServiceCatalog.MaxStayNights} nights.",
        StayRequestProblem.NoAdults => turkish ? "En az bir yetişkin gereklidir." : "At least one adult is required.",
        StayRequestProblem.TooManyAdults => turkish ? $"Tek talepte en fazla {HotelServiceCatalog.MaxAdultsPerRequest} yetişkin; grup talepleri için resepsiyona yazın." : $"At most {HotelServiceCatalog.MaxAdultsPerRequest} adults per request; larger parties are a group enquiry for reception.",
        StayRequestProblem.TooManyChildren => turkish ? $"Tek talepte en fazla {HotelServiceCatalog.MaxChildrenPerRequest} çocuk." : $"At most {HotelServiceCatalog.MaxChildrenPerRequest} children per request.",
        StayRequestProblem.UnknownRoom => turkish ? "Bilinmeyen oda tipi." : "Unknown room type.",
        StayRequestProblem.RoomInactive => turkish ? "Bu oda tipi şu anda satışta değil." : "This room type is not on sale at the moment.",
        StayRequestProblem.PartyTooLarge => turkish ? "Bu oda tipi bu misafir sayısı için uygun değil." : "This room type does not sleep that many guests.",
        _ => string.Empty
    };
}
