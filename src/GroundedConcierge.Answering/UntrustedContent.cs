using System.Text;

namespace GroundedConcierge.Answering;

/// <summary>
/// The one place untrusted text is fenced before it is placed inside a prompt.
///
/// <para>
/// The injection defence is authority separation, not rewriting: the rules a
/// prompt states outrank retrieved data, which outranks the guest's own text.
/// The fence is what makes that separation legible to a model — a delimiter tag
/// naming the source, preceded by rules that are already in force when the model
/// reads the data. A fence is only worth something if it cannot be broken from
/// inside, and that is what this class guarantees.
/// </para>
///
/// <para>
/// Three holes it closes. A retrieved passage containing the literal text
/// <c>&lt;/retrieved-data&gt;</c> would close its own fence, so everything after
/// it would read as text OUTSIDE the untrusted region; a source named
/// <c>x"&gt;&lt;/retrieved-data&gt;</c> would break the fence from inside the
/// attribute it is interpolated into; and text inside one fence could write a
/// COMPLETE block of another fence's tag, dressing itself as material of a
/// different provenance. Passage content comes from documents and names come
/// from whoever supplied them, so none of these is hypothetical.
/// </para>
///
/// <para>
/// Neutralization is deliberately NARROW. Documents are not rewritten — no
/// phrase list, no "[FILTERED]" substitution, no content policing. The only text
/// that changes is the exact character sequences that would terminate, re-open
/// or forge one of the delimiter tags, and they change to an escaped form
/// (<c>&amp;lt;</c>) a reader still understands. A defence that mutilates the
/// document ("act as" appears in ordinary prose) destroys the evidence the
/// answer is supposed to be grounded in.
/// </para>
/// </summary>
public static class UntrustedContent
{
    /// <summary>
    /// Fence one piece of untrusted text in a named delimiter tag.
    /// Attribute values are sanitized (no quote, no angle bracket, no control
    /// character, bounded length) and the content cannot close, re-open or
    /// forge any of the reserved fences.
    /// </summary>
    /// <param name="tagName">The delimiter tag, e.g. "retrieved-data".</param>
    /// <param name="content">The untrusted text. Null renders as empty.</param>
    /// <param name="attributes">Attribute name/value pairs; values are sanitized.</param>
    public static string Fence(string tagName, string? content, params (string Name, string? Value)[] attributes)
    {
        var sb = new StringBuilder();
        sb.Append('<').Append(tagName);
        foreach (var (name, value) in attributes)
        {
            sb.Append(' ').Append(name).Append("=\"").Append(SanitizeAttributeValue(value)).Append('"');
        }
        sb.Append(">\n");
        sb.Append(NeutralizeFenceBreaks(content ?? string.Empty, tagName));
        sb.Append("\n</").Append(tagName).Append('>');
        return sb.ToString();
    }

    /// <summary>
    /// Make a value safe to sit inside a double-quoted attribute of a fence tag:
    /// quotes, angle brackets and control characters are dropped and the length
    /// is bounded. Used for file names, source names and ids — text a hostile
    /// uploader chooses.
    /// </summary>
    public static string SanitizeAttributeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(Math.Min(value.Length, MaxAttributeLength));
        foreach (var c in value)
        {
            if (c is '"' or '<' or '>' || char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= MaxAttributeLength) break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Neutralize the sequences that could terminate the fence the content sits
    /// inside, or FORGE one of the other reserved fences: for the content's own
    /// tag and for every name in <see cref="ReservedTags"/>, both
    /// <c>&lt;/name</c> and <c>&lt;name</c> have their leading angle bracket
    /// replaced by <c>&amp;lt;</c>, case-insensitively.
    ///
    /// <para>
    /// Its own tag is the ESCAPE; the reserved list is the FORGERY. A hostile
    /// guest message inside <c>&lt;guest-question&gt;</c> cannot leave its region
    /// either way, but without the list it could write a complete
    /// <c>&lt;retrieved-data&gt;…&lt;/retrieved-data&gt;</c> block and dress its
    /// own sentences as retrieved reference material — a claim of provenance it
    /// has no right to make. The list is exactly the delimiter names this
    /// solution emits, so nothing in ordinary prose is touched.
    /// </para>
    /// </summary>
    public static string NeutralizeFenceBreaks(string content, string tagName)
    {
        if (string.IsNullOrEmpty(content)) return content;

        content = NeutralizeOne(content, tagName);
        foreach (var reserved in ReservedTags)
        {
            if (string.Equals(reserved, tagName, StringComparison.OrdinalIgnoreCase)) continue;
            content = NeutralizeOne(content, reserved);
        }
        return content;
    }

    private static string NeutralizeOne(string content, string tagName)
    {
        content = ReplaceOrdinalIgnoreCase(content, "</" + tagName, "&lt;/" + tagName);
        content = ReplaceOrdinalIgnoreCase(content, "<" + tagName, "&lt;" + tagName);
        return content;
    }

    /// <summary>
    /// Every delimiter name this solution fences untrusted text in. Content
    /// inside any fence may write none of them. Adding a fence means adding its
    /// tag here — <c>FenceTests</c> scans the solution for <c>Fence("…"</c> call
    /// sites and fails the build if one names a tag this list does not hold.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedTags = new[]
    {
        "retrieved-data",
        "guest-question",
    };

    private static string ReplaceOrdinalIgnoreCase(string text, string needle, string replacement)
        => text.Replace(needle, replacement, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attribute values longer than this are cut. A file or source name this
    /// long is not a name any more; bounding it also bounds what a hostile
    /// value can spend of the prompt budget.
    /// </summary>
    public const int MaxAttributeLength = 128;
}
