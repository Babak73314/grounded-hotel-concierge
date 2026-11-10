using GroundedConcierge.Domain;

namespace GroundedConcierge.Answering;

/// <summary>One retrieved passage of hotel policy, with the id a citation names.</summary>
public sealed record PolicyPassage(string Id, string Text);

/// <summary>
/// The retrieval seam: hotel policy passages relevant to a question. In
/// production this is a search index; here it is
/// <see cref="InMemoryPolicyPassages"/>.
/// </summary>
public interface IPolicyPassageSource
{
    IReadOnlyList<PolicyPassage> Find(string question, bool turkish, int max = 3);
}

/// <summary>
/// A handful of invented policy passages for the fictional property, matched by
/// keyword overlap. Deliberately simple: the point of this repository is what
/// happens to a passage AFTER retrieval, not how it is ranked.
/// </summary>
public sealed class InMemoryPolicyPassages : IPolicyPassageSource
{
    private sealed record Entry(string Id, string En, string Tr, string[] Keywords);

    private static readonly Entry[] Entries =
    {
        new("breakfast",
            "Breakfast is served in the garden restaurant from 07:00 to 10:30 and is included in every room rate.",
            "Kahvaltı bahçe restoranında 07:00–10:30 arasında servis edilir ve tüm oda fiyatlarına dahildir.",
            new[] { "breakfast", "kahvalti", "restaurant", "restoran", "morning", "sabah" }),
        new("check-in",
            "Check-in starts at 15:00 and check-out is until 12:00. Early check-in depends on the room being ready.",
            "Giriş 15:00'te başlar, çıkış 12:00'ye kadardır. Erken giriş odanın hazır olmasına bağlıdır.",
            new[] { "check-in", "check in", "checkin", "check-out", "check out", "checkout", "giris", "cikis", "saat", "time" }),
        new("parking",
            "Valet parking is available for guests; the car park is underground and open 24 hours.",
            "Misafirler için vale park hizmeti vardır; otopark yeraltındadır ve 24 saat açıktır.",
            new[] { "parking", "park", "car", "araba", "otopark", "vale", "valet" }),
        new("pets",
            "Small pets are welcome in Garden Rooms on request; please tell reception before arrival.",
            "Küçük evcil hayvanlar talep üzerine Bahçe Odalarında kabul edilir; lütfen gelmeden önce resepsiyona bildirin.",
            new[] { "pet", "pets", "dog", "cat", "evcil", "kopek", "kedi" }),
    };

    public IReadOnlyList<PolicyPassage> Find(string question, bool turkish, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(question) || max <= 0) return Array.Empty<PolicyPassage>();

        var folded = HotelStayIntentParser.Fold(question);
        return Entries
            .Select(e => (Entry: e, Score: e.Keywords.Count(k => folded.Contains(k, StringComparison.Ordinal))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
            .Take(max)
            .Select(x => new PolicyPassage(x.Entry.Id, turkish ? x.Entry.Tr : x.Entry.En))
            .ToList();
    }
}
