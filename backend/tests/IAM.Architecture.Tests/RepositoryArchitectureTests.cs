using System.Reflection;
using Shouldly;

namespace IAM.Architecture.Tests;

/// <summary>Checks the real repository against the architecture rules (P0-002, P0-004).</summary>
public sealed class RepositoryArchitectureTests
{
    private static readonly IReadOnlyList<ProjectInfo> Projects = RepositoryProjects.LoadAll();

    [Fact]
    public void RepositoryContainsTheApprovedModules()
    {
        Projects.Count(p => p.Kind == ProjectKind.Module)
            .ShouldBe(ArchitectureRules.ApprovedModules.Count * ArchitectureRules.Layers.Count);
        ArchitectureRules.CheckModuleStructure(Projects).ShouldBeEmpty();
    }

    [Fact]
    public void ModulesReferenceOnlyAllowedProjects() =>
        ArchitectureRules.CheckModuleReferences(Projects).ShouldBeEmpty();

    [Fact]
    public void DomainContractsAndAbstractionsHaveNoFrameworkOrPackageDependencies() =>
        ArchitectureRules.CheckPurity(Projects).ShouldBeEmpty();

    [Fact]
    public void NoForbiddenPackagesAndDriversOnlyInPersistence() =>
        ArchitectureRules.CheckPackages(Projects).ShouldBeEmpty();

    [Fact]
    public void HostsWorkersAndConnectorsRespectComposition() =>
        ArchitectureRules.CheckHostsWorkersConnectors(Projects).ShouldBeEmpty();

    [Fact]
    public void EveryProjectIsInTheSolution() =>
        ArchitectureRules.CheckSolutionMembership(Projects, RepositoryProjects.SolutionProjectPaths()).ShouldBeEmpty();

    /// <summary>
    /// Second line of defence: compiled assembly references must also obey the module rules
    /// (catches references added by other means, e.g. imported props or copied DLLs).
    /// </summary>
    [Fact]
    public void CompiledModuleAssembliesReferenceOnlyAllowedAssemblies()
    {
        var violations = new List<string>();
        var moduleAssemblies = Directory.EnumerateFiles(AppContext.BaseDirectory, "IAM.Modules.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => ProjectInfo.Classify(n!) == ProjectKind.Module)
            .ToList();

        moduleAssemblies.Count.ShouldBe(ArchitectureRules.ApprovedModules.Count * ArchitectureRules.Layers.Count);

        foreach (var name in moduleAssemblies)
        {
            var parts = name!.Split('.');
            var (module, layer) = (parts[2], parts[3]);
            var assembly = Assembly.Load(name);

            foreach (var reference in assembly.GetReferencedAssemblies().Select(r => r.Name!))
            {
                if (reference.StartsWith("IAM.", StringComparison.Ordinal))
                {
                    var problem = ArchitectureRules.ExplainModuleReference(module, layer, reference);
                    if (problem is not null)
                    {
                        violations.Add($"{name} -> {reference}: {problem}");
                    }
                }
                else if (layer is "Domain" or "Contracts"
                    && (reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                        || reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)))
                {
                    violations.Add($"{name} -> {reference}: {layer} may not depend on ASP.NET Core or EF Core.");
                }
            }
        }

        violations.ShouldBeEmpty();
    }
}
