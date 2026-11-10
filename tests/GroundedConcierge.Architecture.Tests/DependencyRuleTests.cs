namespace GroundedConcierge.Architecture.Tests;

/// <summary>The real solution obeys the dependency rule.</summary>
public class DependencyRuleTests
{
    private static readonly string Root = SolutionGraph.FindRoot();
    private static readonly IReadOnlyList<ProjectNode> Graph = SolutionGraph.Load(Root);

    [Fact]
    public void TheScanFoundExactlyTheSixProjects()
    {
        // A vacuity guard: a rule checked over an empty or partial graph passes
        // for the wrong reason.
        Assert.Equal(new[]
        {
            "samples/GroundedConcierge.Demo/GroundedConcierge.Demo.csproj",
            "src/GroundedConcierge.Answering/GroundedConcierge.Answering.csproj",
            "src/GroundedConcierge.Domain/GroundedConcierge.Domain.csproj",
            "tests/GroundedConcierge.Answering.Tests/GroundedConcierge.Answering.Tests.csproj",
            "tests/GroundedConcierge.Architecture.Tests/GroundedConcierge.Architecture.Tests.csproj",
            "tests/GroundedConcierge.Domain.Tests/GroundedConcierge.Domain.Tests.csproj",
        }, Graph.Select(p => p.Path));
    }

    [Fact]
    public void TheSolutionObeysEveryDependencyRule() =>
        Assert.Empty(DependencyRules.Violations(Graph));

    [Fact]
    public void TheDomainReferencesNothingAtAll()
    {
        var domain = Graph.Single(p => p.Path == DependencyRules.Domain);
        Assert.Empty(domain.ProjectReferences);
        Assert.Empty(domain.PackageReferences);
    }

    [Fact]
    public void TheAnsweringLayerReferencesTheDomainAndNoPackage()
    {
        var answering = Graph.Single(p => p.Path == DependencyRules.Answering);
        Assert.Equal(new[] { DependencyRules.Domain }, answering.ProjectReferences);
        Assert.Empty(answering.PackageReferences);
    }

    [Fact]
    public void NoPackageOutsideTheAllowListAppearsAnywhere()
    {
        var packages = Graph.SelectMany(p => p.PackageReferences).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.NotEmpty(packages);   // the test projects do declare packages, so the scan is reading them
        Assert.All(packages, package => Assert.Contains(package, DependencyRules.TestPackages));
    }

    [Fact]
    public void ThePackageVersionsFileDeclaresOnlyTheAllowList()
    {
        // Central package management: a version declared here is one step from
        // being referenced, so the list is held to the same allow-list.
        var declared = System.Xml.Linq.XDocument.Load(Path.Combine(Root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Select(e => (string)e.Attribute("Include")!)
            .ToList();
        Assert.Equal(DependencyRules.TestPackages.OrderBy(p => p), declared.OrderBy(p => p), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryProjectOnDiskIsInTheSolution()
    {
        var solution = File.ReadAllText(Path.Combine(Root, SolutionGraph.SolutionFile)).Replace('\\', '/');
        Assert.All(Graph, project => Assert.Contains(project.Path, solution, StringComparison.Ordinal));
    }
}
