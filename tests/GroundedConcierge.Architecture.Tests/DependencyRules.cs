namespace GroundedConcierge.Architecture.Tests;

/// <summary>
/// The dependency rule of this repository, as data. A rule is checked over a
/// graph, so the same code that guards the real solution can be shown failing
/// on a hand-built one — a test of a rule that has never been seen to fail
/// proves nothing.
/// </summary>
public static class DependencyRules
{
    public const string Domain = "src/GroundedConcierge.Domain/GroundedConcierge.Domain.csproj";
    public const string Answering = "src/GroundedConcierge.Answering/GroundedConcierge.Answering.csproj";

    /// <summary>
    /// The only packages allowed anywhere, and only in test projects. No model
    /// SDK, no cloud SDK and no commercial component can arrive without this
    /// list — and this test — changing in the same commit.
    /// </summary>
    public static readonly IReadOnlySet<string> TestPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk",
        "xunit",
        "xunit.runner.visualstudio",
    };

    /// <summary>Every rule the graph breaks, as one readable line each. Empty means the graph is legal.</summary>
    public static IReadOnlyList<string> Violations(IReadOnlyList<ProjectNode> graph)
    {
        var known = graph.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var project in graph)
        {
            foreach (var target in project.ProjectReferences)
            {
                if (!known.Contains(target))
                    violations.Add($"{project.Name} references {target}, which is not a project of this repository.");

                if (target.StartsWith("samples/", StringComparison.Ordinal))
                    violations.Add($"{project.Name} references the composition root {target}; nothing may.");
            }

            switch (project.Path)
            {
                case Domain:
                    foreach (var target in project.ProjectReferences)
                        violations.Add($"The domain references {target}; it may reference no project.");
                    break;

                case Answering:
                    foreach (var target in project.ProjectReferences.Where(t => t != Domain))
                        violations.Add($"The answering layer references {target}; it may reference the domain only.");
                    break;
            }

            if (project.Layer is "src" or "samples")
            {
                foreach (var package in project.PackageReferences)
                    violations.Add($"{project.Name} references the package {package}; production code may reference no package.");
            }

            if (project.Layer is "samples")
            {
                foreach (var target in project.ProjectReferences.Where(t => !t.StartsWith("src/", StringComparison.Ordinal)))
                    violations.Add($"The demo {project.Name} references {target}; it may compose src/ projects only.");
            }

            if (project.Layer is "tests")
            {
                foreach (var target in project.ProjectReferences.Where(t => !t.StartsWith("src/", StringComparison.Ordinal)))
                    violations.Add($"The test project {project.Name} references {target}; tests may reference src/ projects only.");
                foreach (var package in project.PackageReferences.Where(p => !TestPackages.Contains(p)))
                    violations.Add($"The test project {project.Name} references the package {package}, which is not on the allow-list.");
            }

            if (project.Layer is not ("src" or "samples" or "tests"))
                violations.Add($"{project.Path} is outside src/, samples/ and tests/.");
        }

        return violations;
    }
}
