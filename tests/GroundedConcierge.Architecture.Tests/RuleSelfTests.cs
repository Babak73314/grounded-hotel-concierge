namespace GroundedConcierge.Architecture.Tests;

/// <summary>
/// The rules, shown failing. Each case is the real graph with ONE illegal edge
/// added, and each must produce exactly the complaint it should.
/// </summary>
public class RuleSelfTests
{
    private const string Demo = "samples/GroundedConcierge.Demo/GroundedConcierge.Demo.csproj";
    private const string DomainTests = "tests/GroundedConcierge.Domain.Tests/GroundedConcierge.Domain.Tests.csproj";

    private static List<ProjectNode> Legal() => new()
    {
        new(DependencyRules.Domain, Array.Empty<string>(), Array.Empty<string>()),
        new(DependencyRules.Answering, new[] { DependencyRules.Domain }, Array.Empty<string>()),
        new(Demo, new[] { DependencyRules.Domain, DependencyRules.Answering }, Array.Empty<string>()),
        new(DomainTests, new[] { DependencyRules.Domain }, new[] { "xunit", "Microsoft.NET.Test.Sdk" }),
    };

    private static List<ProjectNode> With(string path, Func<ProjectNode, ProjectNode> change) =>
        Legal().Select(p => p.Path == path ? change(p) : p).ToList();

    [Fact]
    public void TheLegalGraphHasNoViolation() =>
        Assert.Empty(DependencyRules.Violations(Legal()));

    [Fact]
    public void TheDomainReferencingTheAnsweringLayerIsRefused()
    {
        var graph = With(DependencyRules.Domain, p => p with { ProjectReferences = new[] { DependencyRules.Answering } });
        Assert.Contains(DependencyRules.Violations(graph), v => v.StartsWith("The domain references", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Some.Model.Sdk")]
    [InlineData("Some.Cloud.Storage")]
    [InlineData("Some.Commercial.Grid")]
    public void AnyPackageInProductionCodeIsRefused(string package)
    {
        var graph = With(DependencyRules.Answering, p => p with { PackageReferences = new[] { package } });
        Assert.Contains(DependencyRules.Violations(graph), v => v.Contains($"the package {package}", StringComparison.Ordinal));
    }

    [Fact]
    public void ATestPackageOffTheAllowListIsRefused()
    {
        var graph = With(DomainTests, p => p with { PackageReferences = p.PackageReferences.Append("Moq").ToArray() });
        Assert.Contains(DependencyRules.Violations(graph), v => v.Contains("Moq", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferencingTheCompositionRootIsRefused()
    {
        var graph = With(DomainTests, p => p with { ProjectReferences = p.ProjectReferences.Append(Demo).ToArray() });
        Assert.Contains(DependencyRules.Violations(graph), v => v.Contains("composition root", StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceToAProjectOutsideTheRepositoryIsRefused()
    {
        var graph = With(DependencyRules.Answering, p => p with { ProjectReferences = new[] { DependencyRules.Domain, "../elsewhere/Other.csproj" } });
        Assert.Contains(DependencyRules.Violations(graph), v => v.Contains("not a project of this repository", StringComparison.Ordinal));
    }

    [Fact]
    public void AConditionalOrComputedReferenceIsRefusedRatherThanGuessed()
    {
        var dir = Directory.CreateTempSubdirectory("arch-rules-");
        try
        {
            var conditional = Path.Combine(dir.FullName, "Conditional.csproj");
            File.WriteAllText(conditional, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                    <ProjectReference Include="..\Other\Other.csproj" />
                  </ItemGroup>
                </Project>
                """);
            Assert.Throws<InvalidOperationException>(() => SolutionGraph.Parse(dir.FullName, conditional));

            var computed = Path.Combine(dir.FullName, "Computed.csproj");
            File.WriteAllText(computed, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="$(SomePackage)" />
                  </ItemGroup>
                </Project>
                """);
            Assert.Throws<InvalidOperationException>(() => SolutionGraph.Parse(dir.FullName, computed));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
