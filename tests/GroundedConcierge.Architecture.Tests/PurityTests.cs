using System.Text.RegularExpressions;

namespace GroundedConcierge.Architecture.Tests;

/// <summary>
/// The domain is arithmetic: no I/O, no network, no ambient clock, no
/// randomness and nothing asynchronous — so the same question gives the same
/// answer on every machine, every run. The answering layer may be asynchronous
/// (a model call is), but it reaches no network itself: the model is an
/// interface, and an in-memory one is all this repository contains.
/// </summary>
public class PurityTests
{
    private static readonly string Root = SolutionGraph.FindRoot();

    private static readonly (string Pattern, string Why)[] AmbientState =
    {
        (@"\bDateTime(Offset)?\s*\.\s*(Now|UtcNow|Today)\b", "reads the machine clock; take today from IHotelClock"),
        (@"\bEnvironment\s*\.", "reads the process environment"),
        (@"\bRandom\b", "is random"),
        (@"\bSystem\.IO\b|\bFile\s*\.|\bDirectory\s*\.", "touches the file system"),
        (@"\bSystem\.Net\b|\bHttpClient\b|\bSocket\b", "reaches the network"),
    };

    private static readonly (string Pattern, string Why)[] Asynchrony =
    {
        (@"\bTask\b|\basync\b|\bawait\b|\bThread\b", "is asynchronous or concurrent"),
    };

    [Fact]
    public void TheDomainHasNoAmbientStateAndNothingAsynchronous() =>
        AssertNone("src/GroundedConcierge.Domain", AmbientState.Concat(Asynchrony));

    [Fact]
    public void TheAnsweringLayerReachesNoNetworkFileOrClock() =>
        AssertNone("src/GroundedConcierge.Answering", AmbientState);

    private static void AssertNone(string projectDirectory, IEnumerable<(string Pattern, string Why)> rules)
    {
        var findings = new List<string>();
        foreach (var file in SourceCode.FilesOf(Root, projectDirectory))
        {
            var code = SourceCode.WithoutComments(File.ReadAllText(file));
            foreach (var (pattern, why) in rules)
            {
                foreach (Match match in Regex.Matches(code, pattern))
                    findings.Add($"{SolutionGraph.Relative(Root, file)}: '{match.Value}' {why}.");
            }
        }
        Assert.Empty(findings);
    }

    // ------------------------------------------------ the scanner itself

    [Theory]
    [InlineData("var a = 1; // DateTime.Now", "DateTime.Now")]
    [InlineData("/* uses HttpClient */ var a = 1;", "HttpClient")]
    [InlineData("/// <summary>Never Task.Run.</summary>\nvar a = 1;", "Task")]
    public void CommentsAreNotCode(string source, string mention) =>
        Assert.DoesNotContain(mention, SourceCode.WithoutComments(source), StringComparison.Ordinal);

    [Theory]
    [InlineData("var url = \"http://example.invalid\"; var t = DateTime.Now;")]
    [InlineData("var p = @\"C:\\\\x\"\"//\"\"y\"; var t = DateTime.Now;")]
    [InlineData("var c = '/'; var d = '/'; var t = DateTime.Now;")]
    [InlineData("var r = \"\"\"a // b\"\"\"; var t = DateTime.Now;")]
    public void ACommentMarkerInsideALiteralDoesNotHideTheCodeAfterIt(string source) =>
        Assert.Contains("DateTime.Now", SourceCode.WithoutComments(source), StringComparison.Ordinal);
}
