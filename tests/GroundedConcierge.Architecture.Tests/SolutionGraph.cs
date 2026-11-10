using System.Xml;
using System.Xml.Linq;

namespace GroundedConcierge.Architecture.Tests;

/// <summary>One project file, as the architecture rules see it.</summary>
/// <param name="Path">Repository-relative, forward slashes, e.g. <c>src/GroundedConcierge.Domain/GroundedConcierge.Domain.csproj</c>.</param>
/// <param name="ProjectReferences">The referenced projects, each resolved to a repository-relative path.</param>
/// <param name="PackageReferences">The package ids it references.</param>
public sealed record ProjectNode(string Path, IReadOnlyList<string> ProjectReferences, IReadOnlyList<string> PackageReferences)
{
    /// <summary>The project's file name without extension.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The top-level folder: <c>src</c>, <c>samples</c> or <c>tests</c>.</summary>
    public string Layer => Path.Split('/')[0];
}

/// <summary>
/// Reads every project file in the repository into a reference graph — from the
/// files on disk, not from compiled assemblies, because a compiled assembly
/// lists only the references its code happens to use, and an unused reference
/// is still a dependency somebody can start using tomorrow.
///
/// <para>
/// Deliberately strict about what it accepts: a DTD is refused, and a reference
/// that is conditional or built from an MSBuild property is refused rather than
/// guessed at, because a rule checked against a reference it could not resolve
/// is a rule that silently passes.
/// </para>
/// </summary>
public static class SolutionGraph
{
    public const string SolutionFile = "GroundedConcierge.sln";

    /// <summary>The repository root: the nearest directory above the test output that holds the solution file.</summary>
    public static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, SolutionFile)))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"{SolutionFile} was not found above {AppContext.BaseDirectory}.");
    }

    /// <summary>Every project file under the root, outside build output, parsed.</summary>
    public static IReadOnlyList<ProjectNode> Load(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(root, path))
            .Select(path => Parse(root, path))
            .OrderBy(p => p.Path, StringComparer.Ordinal)
            .ToList();

    public static ProjectNode Parse(string root, string projectFile)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        XDocument doc;
        using (var reader = XmlReader.Create(projectFile, settings))
            doc = XDocument.Load(reader);

        var relative = Relative(root, projectFile);
        var directory = System.IO.Path.GetDirectoryName(projectFile)!;

        var projects = new List<string>();
        foreach (var reference in doc.Descendants("ProjectReference"))
        {
            var include = Include(reference, relative);
            projects.Add(Relative(root, System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, include.Replace('\\', '/')))));
        }

        var packages = doc.Descendants("PackageReference").Select(r => Include(r, relative)).ToList();

        return new ProjectNode(relative, projects, packages);
    }

    /// <summary>The literal <c>Include</c> of a reference, refusing anything MSBuild would have to evaluate.</summary>
    private static string Include(XElement reference, string project)
    {
        var include = (string?)reference.Attribute("Include");
        if (string.IsNullOrWhiteSpace(include))
            throw new InvalidOperationException($"{project}: a {reference.Name} without an Include.");
        if (include.Contains("$(", StringComparison.Ordinal) || include.Contains('*'))
            throw new InvalidOperationException($"{project}: '{include}' is computed by MSBuild; the rules only check literal references.");
        if (reference.AncestorsAndSelf().Any(e => e.Attribute("Condition") is not null))
            throw new InvalidOperationException($"{project}: '{include}' is conditional; the rules only check unconditional references.");
        return include;
    }

    public static string Relative(string root, string path) =>
        System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Judged on the RELATIVE path: an absolute one would also match a checkout
    /// that happens to live under a folder called "bin".
    /// </summary>
    public static bool IsBuildOutput(string root, string path)
    {
        var parts = Relative(root, path).Split('/');
        return parts.Contains("bin") || parts.Contains("obj");
    }
}
