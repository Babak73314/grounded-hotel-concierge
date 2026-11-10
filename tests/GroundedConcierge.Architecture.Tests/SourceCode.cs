using System.Text;

namespace GroundedConcierge.Architecture.Tests;

/// <summary>
/// C# source as the purity rules read it: with comments removed, so a sentence
/// that EXPLAINS why the domain never reads <c>DateTime.Now</c> is not mistaken
/// for code that does. String and character literals are kept — and skipped over
/// correctly — so a <c>"//"</c> inside a string does not start a comment.
/// </summary>
public static class SourceCode
{
    /// <summary>Every <c>.cs</c> file of one project directory, outside build output. Never empty.</summary>
    public static IReadOnlyList<string> FilesOf(string root, string projectDirectory)
    {
        var files = Directory.EnumerateFiles(Path.Combine(root, projectDirectory), "*.cs", SearchOption.AllDirectories)
            .Where(path => !SolutionGraph.IsBuildOutput(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException($"No source found under {projectDirectory}; a scan of nothing would pass.");
        return files;
    }

    /// <summary>The text with every <c>//</c> and <c>/* */</c> comment replaced by a space.</summary>
    public static string WithoutComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                sb.Append(' ');
            }
            else if (c == '"' && next == '"' && i + 2 < source.Length && source[i + 2] == '"')
            {
                i = CopyRawString(source, i, sb);
            }
            else if (c == '"')
            {
                var verbatim = i > 0 && (source[i - 1] == '@' || (source[i - 1] == '$' && i > 1 && source[i - 2] == '@'));
                i = CopyQuoted(source, i, '"', verbatim, sb);
            }
            else if (c == '\'')
            {
                i = CopyQuoted(source, i, '\'', verbatim: false, sb);
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static int CopyQuoted(string source, int start, char quote, bool verbatim, StringBuilder sb)
    {
        sb.Append(source[start]);
        var i = start + 1;
        while (i < source.Length)
        {
            var c = source[i];
            sb.Append(c);
            i++;
            if (!verbatim && c == '\\' && i < source.Length) { sb.Append(source[i]); i++; continue; }
            if (c == quote)
            {
                if (verbatim && i < source.Length && source[i] == quote) { sb.Append(source[i]); i++; continue; }
                break;
            }
            if (!verbatim && c == '\n') break;   // an unterminated literal ends at the line
        }
        return i;
    }

    private static int CopyRawString(string source, int start, StringBuilder sb)
    {
        var quotes = 0;
        while (start + quotes < source.Length && source[start + quotes] == '"') quotes++;
        var fence = new string('"', quotes);
        var end = source.IndexOf(fence, start + quotes, StringComparison.Ordinal);
        var stop = end < 0 ? source.Length : end + quotes;
        sb.Append(source, start, stop - start);
        return stop;
    }
}
