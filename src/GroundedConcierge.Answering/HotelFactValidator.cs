using System.Text;
using System.Text.RegularExpressions;

using GroundedConcierge.Domain;

namespace GroundedConcierge.Answering;

/// <summary>What a draft did that a demonstration concierge may not do.</summary>
public enum HotelClaimKind
{
    None,

    /// <summary>"Booked", "reserved", "confirmed", "held" — an inventory action the demo never performs.</summary>
    BookingClaim,

    /// <summary>"Charged", "payment received" — the demo takes no money and holds no card.</summary>
    PaymentClaim,

    /// <summary>"I have e-mailed you", "your request has been sent" — the concierge sends nothing.</summary>
    OutboundMessageClaim,

    /// <summary>"Reception has called you / arranged it" — an act by a person that did not happen.</summary>
    ReceptionActionClaim,

    /// <summary>A commercial figure in text that is not allowed to carry one.</summary>
    CommercialFigure,

    /// <summary>A date or a relative day in text that is not allowed to carry one.</summary>
    CommercialDate,

    /// <summary>An availability or sold-out assertion in text that is not allowed to make one.</summary>
    AvailabilityClaim,

    /// <summary>A sentence could not be checked inside the scanner's time limit.</summary>
    ScannerTimeout
}

/// <summary>What the validator found. Counts and kinds; the offending text stays inside.</summary>
public sealed record HotelFactValidationResult
{
    public static HotelFactValidationResult Safe { get; } = new();

    public bool IsSafe { get; init; } = true;
    public int ViolationCount { get; init; }
    public IReadOnlyList<HotelClaimKind> Kinds { get; init; } = Array.Empty<HotelClaimKind>();
}

public interface IHotelFactValidator
{
    /// <summary>Check model prose that carries the whole answer — a document turn.</summary>
    HotelFactValidationResult ValidateAnswer(string? draft);

    /// <summary>
    /// Check text that sits beside deterministic figures and may therefore carry
    /// no commercial content of its own. Nothing in this repository calls it —
    /// a commercial turn makes no model call at all — but it is the domain's
    /// definition of commercially empty text, and it is tested as such.
    /// </summary>
    HotelFactValidationResult ValidateCommentary(string? draft);

    string BuildRegenerationInstruction(HotelFactValidationResult verdict, bool commentary, bool turkish);
    string BuildWithheldReply(bool turkish, string contactEmail);
}

/// <summary>
/// The deterministic check on everything a model writes for a guest.
///
/// <para>
/// <b>Why it does not check figures.</b> The obvious design is an allowlist
/// over VALUES: the model writes the figures and every money amount, date and
/// count must match one the engine produced. That shape fails for one root
/// reason — an allowlist over values cannot see what a value MEANS or notice a
/// claim that carries no value at all. "€704/night" passes when €704 is the
/// total; "every room type is available" and "check-in is tomorrow" pass with
/// nothing to match; "the total is 999" passes because no currency symbol made
/// 999 look like money. Widening it is an endless list of near-misses.
/// </para>
///
/// <para>
/// The commercial statement is written by <see cref="HotelAnswerComposer"/>
/// in code, so this validator never guards figures a model produced — it
/// guards the two kinds of text a model could write:
/// </para>
///
/// <para>
/// <b>1. Document answers</b> (<see cref="ValidateAnswer"/>) keep their own
/// figures and citations, because a price in a retrieved passage is the
/// document's fact. Only the ACTION rules run.
/// </para>
///
/// <para>
/// <b>2. Commercially empty text</b> (<see cref="ValidateCommentary"/>) — text
/// that sits BESIDE deterministic figures and may therefore carry none of its
/// own: no digit, no currency, no date, no month, no relative day, no room
/// availability claim, no spelled-out count of nights or rooms. A rule with no
/// near-misses. Nothing in this repository calls it: a commercial turn makes
/// no model call at all (<see cref="HotelAnswerGate"/>), so there is no model
/// sentence beside the figures. It is kept because it is the domain's
/// definition of commercially empty text, which is what a caller that DID put
/// model prose beside computed figures would need.
/// </para>
///
/// <para>
/// <b>The action rules run on both</b>, because "your room is confirmed" is
/// forbidden whatever the turn was about. Negation is <b>predicate-local</b>:
/// it is read in a window that ends at the last coordinator and reaches back at
/// most a few words (<see cref="NegatedPredicate"/>), so a denial cannot launder
/// a claim that follows it — "Nothing has been booked and your room is
/// confirmed" is refused on the second predicate, while "Nothing has been
/// booked" stays safe. Every match of every rule is inspected, not the first.
/// A scanner timeout is a violation, never a pass.
/// </para>
/// </summary>
public sealed class HotelFactValidator : IHotelFactValidator
{
    private const RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    private static Regex Rx(string pattern) => new(pattern, Opts, Budget);

    // ------------------------------------------------------------- the acts

    /// <summary>An inventory action the demonstration never performs.</summary>
    private static readonly Regex BookingClaims = Rx(
        @"\b(?:is|are|was|were|has been|have been)\s+(?:now\s+)?(?:booked|reserved|confirmed|held|secured|blocked)\b"
        + @"|\b(?:i|we)\s+(?:have\s+|had\s+)?(?:booked|reserved|held|blocked|secured|confirmed)\b"
        + @"|\b(?:placed|put|made)\s+(?:a\s+|an\s+)?(?:temporary\s+|provisional\s+)?(?:hold|reservation|booking)\b"
        + @"|\btemporary hold\b|\bheld the room\b|\bon hold for you\b"
        + @"|\brezervasyonunuz\s+(?:onaylandi|yapildi|tamamlandi|olusturuldu|alindi)\b"
        + @"|\bodaniz\s+(?:ayrildi|tutuldu|onaylandi|rezerve edildi|bloke edildi)\b"
        + @"|\bodanizi\s+(?:ayirdim|tuttum|rezerve ettim|bloke ettim)\b"
        + @"|\brezervasyon\w*\s+(?:yaptim|olusturdum|tamamladim|aldim)\b"
        + @"|\brezerve ett?im\b|\brezerve edildi\b"
        + @"|\bgecici olarak\s+(?:tuttum|ayirdim|bloke ettim|rezerve ettim)\b"
        + @"|\bsizin icin\s+(?:tuttum|ayirdim|bloke ettim)\b"
        + @"|\boda\w*\s+(?:sizin icin\s+)?(?:tutuldu|ayrildi|bloke edildi)\b");

    /// <summary>Money moved.</summary>
    private static readonly Regex PaymentClaims = Rx(
        @"\b(?:payment|deposit|card|charge|fee|amount)\s+(?:is|was|has been|have been|were)\s+(?:charged|taken|processed|received|completed|successful|debited|collected)\b"
        + @"|\bpayment\s+(?:succeeded|completed|went through)\b"
        + @"|\byou\s+(?:have\s+been|has been|were)\s+charged\b"
        + @"|\b(?:i|we)\s+(?:have\s+)?charged\b|\byour card was\b"
        + @"|\bodemeniz\s+(?:alindi|tamamlandi|basarili|gerceklesti)\b"
        + @"|\bodeme\s+(?:alindi|tamamlandi|basariyla|gerceklesti)\b"
        + @"|\bucret\w*\s+(?:tahsil edildi|alindi|cekildi)\b"
        + @"|\btahsil edildi\b|\bkartiniz\w*\s+(?:cekildi|tahsil edildi)\b");

    /// <summary>Something sent on the guest's behalf.</summary>
    private static readonly Regex OutboundClaims = Rx(
        @"\b(?:i|we)\s+(?:have\s+|had\s+)?(?:sent|emailed|e-mailed|mailed|messaged|forwarded|notified|submitted|passed on)\b"
        + @"|\b(?:your\s+|the\s+|a\s+|an\s+)?(?:request|reservation|booking|message|e-?mail|confirmation|voucher|enquiry|inquiry)\s+"
        + @"(?:has been|have been|was|were|is|are)\s+(?:sent|forwarded|submitted|passed|delivered|shared)\b"
        + @"|\b(?:talebiniz|rezervasyonunuz|mesajiniz|e-?postaniz|basvurunuz)\s+(?:gonderildi|iletildi|yollandi|ulastirildi)\b"
        + @"|\b(?:gonderdim|ilettim|yolladim|gonderdik|ilettik|ulastirdim)\b"
        + @"|\bresepsiyona\s+(?:\w+\s+){0,2}(?:gonderdim|ilettim|yolladim|bildirdim)\b");

    /// <summary>A member of staff already acted.</summary>
    private static readonly Regex ReceptionClaims = Rx(
        @"\b(?:reception|the front desk|front desk|our team|concierge desk|the desk)\s+(?:has\s+|have\s+)?(?:\w+\s+){0,2}"
        + @"(?:confirmed|arranged|booked|called|contacted|approved|reserved|held|sent|processed)\b"
        + @"|\bresepsiyon\w*\s+(?:\w+\s+){0,2}(?:onayladi|ayarladi|aradi|iletti|rezerve etti|tuttu|gonderdi|bildirdi)\b");

    /// <summary>
    /// Read only over a small window BEFORE the match — see
    /// <see cref="NegatedPredicate"/>. The assistant is required to say "nothing
    /// is booked", so without a negation guard the scanner would refuse exactly
    /// the sentences the safety rules demand.
    /// </summary>
    private static readonly Regex NegationBefore = Rx(
        @"\b(?:not|no|never|cannot|can ?not|cant|can t|won t|will not|unable|do not|don t|does not|doesn t|"
        + @"nothing|neither|nor|without|"
        + @"hicbir|hic|degil|yok|yoktur|olmadi|olmaz|yapamam|yapamiyorum|edemem|edemiyorum|"
        + @"gonderemem|tutamam|ayiramam|henuz)\b");

    /// <summary>
    /// Where one clause ends and the next begins. A contrastive conjunction is
    /// the whole point — it is how a draft says the safe thing first and the
    /// unsafe thing second.
    /// </summary>
    private static readonly Regex ClauseBreak = Rx(
        @"\b(?:but|however|although|though|yet|whereas|nevertheless|nonetheless|still)\b"
        + @"|\b(?:ama|ancak|fakat|lakin|yine de)\b|[;:]");

    /// <summary>
    /// Where one PREDICATE ends and the next begins.
    ///
    /// <para>
    /// This is deliberately NOT in <see cref="ClauseBreak"/>, and the difference
    /// is the whole point. A clause break changes what
    /// is SCANNED; this changes only how far back a negation may reach. "and" is
    /// the ordinary way an English sentence joins two predicates, so splitting
    /// clauses on it would quietly change which matches are found at all —
    /// while a negation attached to the predicate before it was never this
    /// predicate's negation in the first place. "Nothing has been booked and
    /// your room is confirmed" asserts a confirmation; the denial belongs to
    /// "booked" and cannot launder it.
    /// </para>
    ///
    /// <para>
    /// "or" is absent on purpose: it joins ITEMS far more often than predicates,
    /// and "No payment, deposit or card fee has been taken" is an honest
    /// sentence whose negation genuinely governs the whole list.
    /// </para>
    /// </summary>
    private static readonly Regex PredicateBreak = Rx(
        @"\b(?:and|then|also|plus|additionally|furthermore|moreover)\b"
        + @"|\b(?:ve|ayrica|ardindan|sonra)\b|&");

    /// <summary>
    /// How many words back a negation may sit and still be read as negating THIS
    /// predicate, once the coordinator cut has been made. The second half of a
    /// bounded window: a denial eight words upstream, with its own subject and
    /// verb in between, is a statement about something else.
    /// </summary>
    private const int NegationWindowWords = 6;

    // ------------------------------------------------ commercial content

    /// <summary>Any digit at all. Blunt on purpose: commentary needs none, and a bound with no near-misses cannot be argued with.</summary>
    private static readonly Regex AnyDigit = Rx(@"\d");

    private static readonly Regex Currency = Rx(@"€|\beur\b|\beuros?\b|\bavro\b|\btl\b|\blira\b");

    /// <summary>
    /// An availability assertion about the hotel's INVENTORY — the claim that
    /// carries no number, and the one an allowlist over values could never see.
    ///
    /// <para>
    /// <b>Why context is required.</b> Firing on a bare "available" would refuse
    /// "Breakfast details are available in the hotel information" and "I can help
    /// you compare the available options shown above" as room-availability
    /// claims. "Available" on its own says nothing about rooms: information is available, help is
    /// available, in-room dining is available 24 hours. So the word now counts
    /// only in a syntactic relation with a room noun — which is also why "room
    /// SERVICE is available" and "IN-room dining is available" stay safe while
    /// "the Deluxe room is available" and "all rooms are available" do not: the
    /// intervening word must be one that makes the phrase about inventory
    /// ("room TYPE is available"), not one that makes it about a service.
    /// </para>
    ///
    /// <para>
    /// "sold out", "fully booked", "booked up" and "vacancy" keep no context
    /// requirement: they are inventory words whatever they are attached to, and
    /// "Executive is sold out" names a room type this class deliberately does
    /// not have a list of.
    /// </para>
    ///
    /// <para>
    /// "free", "left" and "remaining" were already context-bound for the same
    /// reason and stay so: "feel free to ask" is exactly the hospitality
    /// sentence this text is for.
    /// </para>
    /// </summary>
    private static readonly Regex AvailabilityWords = Rx(
        // 1. inherently about inventory, whatever it is attached to
        @"\b(?:sold ?out|booked up|fully booked|vacan\w*)\b"
        // 2. a room noun in a relation with an availability word — optionally
        //    through one inventory modifier ("room type is available")
        + @"|\b(?:rooms?|suites?)(?:\s+(?:types?|categor\w+|options?))?\s+"
        + @"(?:is\s+|are\s+|was\s+|were\s+)?(?:still\s+|now\s+)?(?:available|unavailable|free|left|remaining|open)\b"
        + @"|\b(?:available|unavailable|free|remaining|open)\s+(?:rooms?|suites?)\b"
        + @"|\bavailability\s+(?:of\s+)?(?:our\s+|the\s+)?(?:rooms?|suites?)\b"
        + @"|\b(?:rooms?|suites?)\s+availability\b"
        // 3. the same in Turkish, where the availability word follows the noun
        + @"|\b(?:oda|odalar|odaniz|odalari|odalarimiz|suit|suitler|suitimiz)\w*\s+"
        + @"(?:\w+\s+){0,2}(?:musait|musaitlik|musaittir|dolu|doludur|doluluk|bos|bosluk|kalan|kaldi|kalmis)\b"
        + @"|\b(?:musait|dolu|bos|kalan|bosalan)\s+(?:oda|odalar|suit|suitler)\w*\b");

    /// <summary>
    /// A date, a month, or a relative day — "check-in is tomorrow" carries no
    /// digit either.
    ///
    /// <para>
    /// English "may" is the one month that is also an everyday word, and "May I
    /// help you with anything else?" is the model's most natural sentence here,
    /// so it counts only in a date-shaped context. "March" is left in: as a verb
    /// it does not appear in hotel prose.
    /// </para>
    /// </summary>
    private static readonly Regex DateWords = Rx(
        @"\b(?:today|tonight|tomorrow|yesterday|overnight|weekend|next week|this week|last week)\b"
        + @"|\b(?:january|february|march|april|june|july|august|september|october|november|december"
        + @"|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec)\b"
        + @"|\b(?:in|on|for|from|until|till|by|of)\s+may\b|\bmay\s+(?:the\s+)?\d"
        + @"|\b(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b"
        + @"|\b(?:bugun|bu gece|yarin|dun|hafta sonu|gelecek hafta|bu hafta|gecen hafta)\b"
        + @"|\b(?:ocak|subat|mart|nisan|mayis|haziran|temmuz|agustos|eylul|ekim|kasim|aralik)\b"
        + @"|\b(?:pazartesi|sali|carsamba|persembe|cuma|cumartesi|pazar)\b");

    /// <summary>A spelled-out count of the things only the engine may count.</summary>
    private static readonly Regex SpelledCounts = Rx(
        @"\b(?:one|two|three|four|five|six|seven|eight|nine|ten|a|an)\s+(?:nights?|rooms?|suites?|adults?|guests?|children)\b"
        + @"|\b(?:bir|iki|uc|dort|bes|alti|yedi|sekiz|dokuz|on)\s+(?:gece|gecelik|oda|odalik|suit|yetiskin|kisi|cocuk)\b");

    // --------------------------------------------------------------- the scan

    public HotelFactValidationResult ValidateAnswer(string? draft) => Scan(draft, commentary: false);

    public HotelFactValidationResult ValidateCommentary(string? draft) => Scan(draft, commentary: true);

    private static HotelFactValidationResult Scan(string? draft, bool commentary)
    {
        if (string.IsNullOrWhiteSpace(draft)) return HotelFactValidationResult.Safe;

        var kinds = new List<HotelClaimKind>();
        var violations = 0;

        void Record(HotelClaimKind kind)
        {
            violations++;
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }

        try
        {
            var folded = HotelStayIntentParser.Fold(draft);

            // The acts, clause by clause, EVERY match of EVERY rule.
            foreach (var clause in Clauses(folded))
            {
                foreach (var (rule, kind) in new[]
                         {
                             (BookingClaims, HotelClaimKind.BookingClaim),
                             (PaymentClaims, HotelClaimKind.PaymentClaim),
                             (OutboundClaims, HotelClaimKind.OutboundMessageClaim),
                             (ReceptionClaims, HotelClaimKind.ReceptionActionClaim)
                         })
                {
                    for (var match = rule.Match(clause); match.Success; match = match.NextMatch())
                    {
                        if (NegatedPredicate(clause, match.Index)) continue;
                        Record(kind);
                    }
                }
            }

            // Commercial content, in text that is not allowed to carry any.
            if (commentary)
            {
                if (AnyDigit.IsMatch(draft) || Currency.IsMatch(folded)) Record(HotelClaimKind.CommercialFigure);
                if (DateWords.IsMatch(folded)) Record(HotelClaimKind.CommercialDate);
                if (AvailabilityWords.IsMatch(folded)) Record(HotelClaimKind.AvailabilityClaim);
                if (SpelledCounts.IsMatch(folded)) Record(HotelClaimKind.CommercialFigure);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A draft that could not be checked is not a draft that passed.
            Record(HotelClaimKind.ScannerTimeout);
        }

        return violations == 0
            ? HotelFactValidationResult.Safe
            : new HotelFactValidationResult { IsSafe = false, ViolationCount = violations, Kinds = kinds };
    }

    /// <summary>
    /// Whether the action matched at <paramref name="matchIndex"/> is negated by
    /// ITS OWN predicate — and the reason this is not a search over everything
    /// before the match.
    ///
    /// <para>
    /// The window is bounded twice. First it is cut at the last
    /// <see cref="PredicateBreak"/>, because a denial that belongs to an earlier
    /// predicate never governed this one: "Nothing has been booked and your room
    /// is confirmed" still asserts a confirmation. Then it is cut again at
    /// <see cref="NegationWindowWords"/> words, because a denial far upstream
    /// with its own subject and verb in between is a statement about something
    /// else. Both cuts are needed: the first misses "Nothing has been booked, so
    /// as I say your room is confirmed", the second misses nothing but is what
    /// makes the window genuinely local.
    /// </para>
    ///
    /// <para>
    /// The cheap direction is deliberate. Missing a negation refuses an honest
    /// sentence and costs one regeneration; finding one that is not there
    /// delivers "your room is confirmed" to a guest of a hotel that booked
    /// nothing.
    /// </para>
    /// </summary>
    internal static bool NegatedPredicate(string clause, int matchIndex)
    {
        var before = clause[..matchIndex];

        var cut = 0;
        for (var m = PredicateBreak.Match(before); m.Success; m = m.NextMatch())
            cut = m.Index + m.Length;

        return NegationBefore.IsMatch(LastWords(before[cut..], NegationWindowWords));
    }

    /// <summary>The tail of <paramref name="text"/> holding at most <paramref name="count"/> words.</summary>
    private static string LastWords(string text, int count)
    {
        var words = 0;
        var inWord = false;

        for (var i = text.Length - 1; i >= 0; i--)
        {
            var isWord = char.IsLetterOrDigit(text[i]);
            if (isWord && !inWord && ++words > count) return text[(i + 1)..];
            inWord = isWord;
        }

        return text;
    }

    /// <summary>
    /// The folded text split into clauses at sentence ends and contrastive
    /// conjunctions, so a negation governs only what it actually negates.
    /// </summary>
    internal static IEnumerable<string> Clauses(string folded)
    {
        foreach (var sentence in folded.Split('.', '!', '?', '\n'))
        {
            var start = 0;
            for (var match = ClauseBreak.Match(sentence); match.Success; match = match.NextMatch())
            {
                var piece = sentence[start..match.Index];
                if (piece.Trim().Length > 0) yield return piece;
                start = match.Index + match.Length;
            }

            var tail = sentence[start..];
            if (tail.Trim().Length > 0) yield return tail;
        }
    }

    // ------------------------------------------------------------- the texts

    /// <summary>
    /// The retry note handed back to the draft source after a refusal. It names
    /// the KINDS of claim that were refused — never the refused sentence, which
    /// stays inside this class — so the second attempt knows what to leave out.
    /// How the caller folds it into its own prompt is the caller's business.
    /// </summary>
    public string BuildRegenerationInstruction(HotelFactValidationResult verdict, bool commentary, bool turkish)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var sb = new StringBuilder();
        sb.Append("The previous draft was rejected by a deterministic check and was not shown. Write a new draft.").Append('\n');

        foreach (var kind in verdict.Kinds.Distinct())
        {
            var rule = kind switch
            {
                HotelClaimKind.BookingClaim => "Do not state or imply that anything is booked, reserved, confirmed or held.",
                HotelClaimKind.PaymentClaim => "Do not state or imply that any payment was taken or charged.",
                HotelClaimKind.OutboundMessageClaim => "Do not state that anything was sent or forwarded on the guest's behalf.",
                HotelClaimKind.ReceptionActionClaim => "Do not state that staff have already done anything.",
                HotelClaimKind.CommercialFigure => "Include no prices, amounts or counts.",
                HotelClaimKind.CommercialDate => "Include no dates or relative days.",
                HotelClaimKind.AvailabilityClaim => "Do not state whether rooms are available or sold out.",
                HotelClaimKind.ScannerTimeout => "Use shorter, simpler sentences.",
                _ => null
            };
            if (rule is not null) sb.Append("- ").Append(rule).Append('\n');
        }

        sb.Append(commentary
            ? "Write one short, neutral sentence with no figures, dates or availability."
            : "Use only the reference passages provided.").Append('\n');
        sb.Append(turkish ? "Answer in Turkish." : "Answer in English.");

        return sb.ToString();
    }

    public string BuildWithheldReply(bool turkish, string contactEmail)
    {
        var email = string.IsNullOrWhiteSpace(contactEmail) ? "reception" : contactEmail;
        return turkish
            ? $"Bu soruya arkasında durabileceğim bir yanıt veremedim. Lütfen resepsiyona yazın: {email}."
            : $"I could not give you an answer I can stand behind. Please write to reception at {email}.";
    }
}
