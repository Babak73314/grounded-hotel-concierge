using GroundedConcierge.Domain;

namespace GroundedConcierge.Answering;

/// <summary>
/// Produces one guest-facing draft. The gate calls it with
/// <paramref name="regenerationInstruction"/> null for the first attempt and
/// with a rewrite instruction for the one permitted retry; the CALLER decides
/// how to compose that into its own prompt, because only the caller knows what
/// its prompt is. The gate never sees a prompt, so it cannot drop a grounding
/// block on the retry — the mistake this shape exists to prevent.
/// </summary>
public delegate Task<string> HotelDraftSource(string? regenerationInstruction, CancellationToken cancellationToken);

/// <summary>
/// What the gate decided. Counts and kind names only — the refused sentences
/// stay inside: anything that travels gets logged, and a log quoting an
/// invented claim is a store of exactly what the gate exists to withhold.
/// </summary>
public sealed record HotelAnswerOutcome
{
    /// <summary>What the guest reads.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// True when the model's prose was refused twice and none of it is in
    /// <see cref="Text"/>. Only a DOCUMENT turn can set it: a commercial turn
    /// asks no model, so it has nothing to withhold.
    /// </summary>
    public bool WasWithheld { get; init; }

    /// <summary>True when the first draft failed, whatever became of the second.</summary>
    public bool WasRegenerated { get; init; }

    /// <summary>True when the commercial half of <see cref="Text"/> was rendered by code rather than by a model.</summary>
    public bool CommercialFactsAreDeterministic { get; init; }

    public int FirstViolations { get; init; }
    public int SecondViolations { get; init; }
    public IReadOnlyList<HotelClaimKind> FirstKinds { get; init; } = Array.Empty<HotelClaimKind>();
    public IReadOnlyList<HotelClaimKind> SecondKinds { get; init; } = Array.Empty<HotelClaimKind>();

    /// <summary>
    /// How many times the draft source was asked: never more than
    /// <see cref="HotelAnswerGate.MaxDrafts"/>, and always ZERO on a commercial
    /// turn. A test reads this to prove no model was called.
    /// </summary>
    public int DraftsRequested { get; init; }
}

/// <summary>
/// What a guest reads, and who is allowed to have written it.
///
/// <para>
/// <b>Two paths, and the difference is the whole design.</b>
/// </para>
///
/// <para>
/// <b>A COMMERCIAL turn</b> — a dated quote or an earliest-availability search —
/// has its answer written by <see cref="HotelAnswerComposer"/>, in code, from
/// the same facts the quote cards are rendered from, and <b>NO MODEL IS ASKED
/// ANYTHING</b>. Not for the figures, and not for a sentence of hospitality
/// beside them either: a model that is
/// asked nothing cannot contradict availability, cannot invent an action claim,
/// and cannot write a sentence a scanner then has to be right about. So there is
/// no path by which a model's statement about a price, a date, availability or a
/// total reaches a guest — not a checked path, an ABSENT one.
/// </para>
///
/// <para>
/// <b>A DOCUMENT turn</b> keeps the model's prose and its citations, because a
/// price in a retrieved passage is the document's fact and rewriting it in code
/// would mean rewriting the documents. There the ACTION rules run — booked,
/// held, charged, sent — and the ordinary draft → check → regenerate once →
/// withhold sequence applies.
/// </para>
///
/// <para>
/// <b>Why the sequence lives here.</b> The order is the guarantee: a caller
/// that checked after showing, retried twice, or fell back to the rejected
/// draft would hold the same validator and none of the safety. One
/// implementation a test can drive end to end beats a shape each caller is
/// trusted to copy.
/// </para>
///
/// <para>
/// <b>No logger, no configuration, no model.</b> Two dependencies, both pure.
/// There is no switch that disables any of it: a gate an operator could turn
/// off is not a gate.
/// </para>
///
/// <para>
/// <b>What it does not cover.</b> Streaming. This gate reads COMPLETED text,
/// so a caller that streams a draft to the guest while it is being written has
/// shown it before the check ran; such an answer must never be described as
/// validated.
/// </para>
/// </summary>
public sealed class HotelAnswerGate
{
    /// <summary>
    /// Two, and a constant rather than a setting. One regeneration, then the
    /// deterministic outcome: a third attempt is a third chance for the model to
    /// find a phrasing the rules happen to miss.
    /// </summary>
    public const int MaxDrafts = 2;

    private readonly IHotelFactValidator _validator;

    public HotelAnswerGate(IHotelFactValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    /// <summary>
    /// Ask for an answer and return only what a guest may safely read. The draft
    /// source is asked at most <see cref="MaxDrafts"/> times on a DOCUMENT turn
    /// and <b>not at all</b> on a turn carrying commercial facts.
    /// </summary>
    public async Task<HotelAnswerOutcome> AnswerAsync(
        HotelDraftSource draft,
        HotelAnswerFacts facts,
        bool turkish,
        string contactEmail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(facts);

        return facts.CarriesCommercialFacts
            ? Commercial(facts, turkish)
            : await DocumentAsync(draft, turkish, contactEmail, cancellationToken);
    }

    /// <summary>
    /// The whole answer, written by code, with <paramref name="facts"/> as its
    /// only input — and <b>the draft source is not called</b>.
    ///
    /// <para>
    /// It is not called on the safe path and not called on any other; there is
    /// no branch here that awaits it: a model that is never asked cannot
    /// contradict availability, invent an action or produce a sentence the
    /// scanner has to be right about. <c>ConciergeEngine</c> does not even reach
    /// this method on a commercial turn — it composes the text itself — so this
    /// is the second line: a future edit that routes a commercial turn back
    /// through the gate with a model behind it still gets no model call.
    /// </para>
    /// </summary>
    private static HotelAnswerOutcome Commercial(HotelAnswerFacts facts, bool turkish) => new()
    {
        Text = HotelAnswerComposer.Compose(facts, turkish),
        CommercialFactsAreDeterministic = true,
        DraftsRequested = 0
    };

    /// <summary>A document answer is the model's prose, checked for the acts it may not claim.</summary>
    private async Task<HotelAnswerOutcome> DocumentAsync(
        HotelDraftSource draft, bool turkish, string contactEmail, CancellationToken ct)
    {
        var first = await draft(null, ct);
        var firstVerdict = _validator.ValidateAnswer(first);

        if (firstVerdict.IsSafe)
        {
            return new HotelAnswerOutcome { Text = first, DraftsRequested = 1 };
        }

        var instruction = _validator.BuildRegenerationInstruction(firstVerdict, commentary: false, turkish);
        var second = await draft(instruction, ct);
        var secondVerdict = _validator.ValidateAnswer(second);

        return new HotelAnswerOutcome
        {
            Text = secondVerdict.IsSafe ? second : _validator.BuildWithheldReply(turkish, contactEmail),
            WasWithheld = !secondVerdict.IsSafe,
            WasRegenerated = true,
            FirstViolations = firstVerdict.ViolationCount,
            SecondViolations = secondVerdict.ViolationCount,
            FirstKinds = firstVerdict.Kinds,
            SecondKinds = secondVerdict.Kinds,
            DraftsRequested = MaxDrafts
        };
    }
}
