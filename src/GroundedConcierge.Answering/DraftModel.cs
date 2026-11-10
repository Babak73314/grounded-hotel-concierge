namespace GroundedConcierge.Answering;

/// <summary>
/// The language-model seam. In production this is an LLM call; in this
/// repository it is <see cref="ScriptedDraftModel"/>, so every test and the demo
/// run with no network, no key and no bill.
///
/// <para>
/// It produces a DRAFT. Nothing it returns reaches a guest without passing
/// <see cref="HotelAnswerGate"/>, and it is never asked anything on a
/// commercial turn — prices, dates and availability are written by code.
/// </para>
/// </summary>
public interface IDraftModel
{
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default);
}

/// <summary>
/// An in-memory stand-in for a model: returns the drafts it was given, in
/// order, and records every prompt it was sent — which is how a test proves a
/// commercial turn made zero calls and a document turn sent fenced passages.
/// </summary>
public sealed class ScriptedDraftModel : IDraftModel
{
    private readonly object _gate = new();
    private readonly Queue<string> _drafts;
    private readonly List<string> _prompts = new();

    public ScriptedDraftModel(params string[] drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        _drafts = new Queue<string>(drafts);
    }

    /// <summary>Every prompt received, in order.</summary>
    public IReadOnlyList<string> Prompts
    {
        get { lock (_gate) return _prompts.ToList(); }
    }

    /// <summary>How many times the model was asked.</summary>
    public int Calls
    {
        get { lock (_gate) return _prompts.Count; }
    }

    public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _prompts.Add(prompt);
            if (_drafts.Count == 0)
                throw new InvalidOperationException("The scripted model has no draft left — a test asked it more often than it expected.");
            return Task.FromResult(_drafts.Dequeue());
        }
    }
}
