using Shouldly;

namespace IAM.Architecture.Tests;

/// <summary>
/// Negative tests: deliberately bad project graphs must be rejected. These prove the rules
/// are not vacuous (S1-02 acceptance: "Given Module A references Module B's Infrastructure, the test fails").
/// </summary>
public sealed class ArchitectureRulesNegativeTests
{
    private static ProjectInfo Project(string name, string[]? projects = null, string[]? packages = null, string[]? frameworks = null) =>
        new(name, $"src/{name}/{name}.csproj", projects ?? [], packages ?? [], frameworks ?? []);

    [Theory]
    [InlineData("Identity", "Application", "IAM.Modules.Workflow.Infrastructure")]
    [InlineData("Identity", "Application", "IAM.Modules.Workflow.Domain")]
    [InlineData("Identity", "Application", "IAM.Modules.Workflow.Application")]
    [InlineData("Identity", "Endpoints", "IAM.Modules.Policy.Endpoints")]
    [InlineData("Identity", "Domain", "IAM.Modules.Policy.Contracts")]
    [InlineData("Identity", "Contracts", "IAM.Modules.Policy.Contracts")]
    [InlineData("Identity", "Domain", "IAM.Modules.Identity.Infrastructure")]
    [InlineData("Identity", "Domain", "IAM.Modules.Identity.Application")]
    [InlineData("Identity", "Application", "IAM.Modules.Identity.Infrastructure")]
    [InlineData("Identity", "Application", "IAM.Modules.Identity.Endpoints")]
    [InlineData("Identity", "Contracts", "IAM.Modules.Identity.Domain")]
    [InlineData("Identity", "Domain", "IAM.BuildingBlocks.Persistence")]
    [InlineData("Identity", "Endpoints", "IAM.BuildingBlocks.Persistence")]
    [InlineData("Identity", "Application", "IAM.Api")]
    public void ForbiddenModuleReferencesAreRejected(string module, string layer, string reference) =>
        ArchitectureRules.ExplainModuleReference(module, layer, reference).ShouldNotBeNull();

    [Theory]
    [InlineData("Identity", "Application", "IAM.Modules.Workflow.Contracts")]
    [InlineData("Identity", "Application", "IAM.Modules.Identity.Domain")]
    [InlineData("Identity", "Infrastructure", "IAM.Modules.Identity.Application")]
    [InlineData("Identity", "Infrastructure", "IAM.BuildingBlocks.Persistence")]
    [InlineData("Identity", "Endpoints", "IAM.Modules.Identity.Infrastructure")]
    [InlineData("Identity", "Domain", "IAM.BuildingBlocks.Abstractions")]
    public void AllowedModuleReferencesAreAccepted(string module, string layer, string reference) =>
        ArchitectureRules.ExplainModuleReference(module, layer, reference).ShouldBeNull();

    [Fact]
    public void CrossModuleInfrastructureReferenceInAProjectGraphIsReported()
    {
        var violations = ArchitectureRules.CheckModuleReferences(
            [Project("IAM.Modules.Identity.Application", projects: ["IAM.Modules.Workflow.Infrastructure"])]);

        violations.Count.ShouldBe(1);
        violations[0].ShouldContain("only Workflow.Contracts may be referenced");
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("Microsoft.EntityFrameworkCore.InMemory")]
    [InlineData("Microsoft.Data.Sqlite")]
    [InlineData("MediatR")]
    [InlineData("AutoMapper")]
    [InlineData("FluentAssertions")]
    [InlineData("Duende.BFF")]
    public void ForbiddenPackagesAreReportedAnywhere(string package) =>
        ArchitectureRules.CheckPackages([Project("IAM.Architecture.Tests", packages: [package])]).ShouldNotBeEmpty();

    [Theory]
    [InlineData("IAM.Modules.Identity.Infrastructure", "Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("IAM.Modules.Identity.Infrastructure", "Microsoft.Data.SqlClient")]
    [InlineData("IAM.Api", "Microsoft.EntityFrameworkCore.SqlServer")]
    public void DatabaseDriversOutsidePersistenceAreReported(string project, string package) =>
        ArchitectureRules.CheckPackages([Project(project, packages: [package])]).ShouldNotBeEmpty();

    [Fact]
    public void DatabaseDriversInPersistenceProjectsAreAllowed() =>
        ArchitectureRules.CheckPackages([Project("IAM.Persistence.PostgreSql", packages: ["Npgsql.EntityFrameworkCore.PostgreSQL"])]).ShouldBeEmpty();

    [Theory]
    [InlineData("IAM.Modules.Identity.Domain")]
    [InlineData("IAM.Modules.Identity.Contracts")]
    public void DomainAndContractsMayNotReferencePackagesOrFrameworks(string project)
    {
        ArchitectureRules.CheckPurity([Project(project, packages: ["Microsoft.EntityFrameworkCore"])]).ShouldNotBeEmpty();
        ArchitectureRules.CheckPurity([Project(project, frameworks: ["Microsoft.AspNetCore.App"])]).ShouldNotBeEmpty();
    }

    [Fact]
    public void ToolReferencingModuleApplicationIsReported() =>
        ArchitectureRules.CheckHostsWorkersConnectors([Project("IAM.Tools.AccessBootstrap", projects: ["IAM.Modules.Administration.Application"])]).ShouldNotBeEmpty();

    [Fact]
    public void ToolComposingThroughEndpointsIsAllowed() =>
        ArchitectureRules.CheckHostsWorkersConnectors([Project("IAM.Tools.AccessBootstrap", projects: ["IAM.Modules.Administration.Endpoints", "IAM.Persistence.SqlServer", "IAM.BuildingBlocks.Persistence"])]).ShouldBeEmpty();

    [Fact]
    public void HostReferencingModuleInfrastructureIsReported() =>
        ArchitectureRules.CheckHostsWorkersConnectors([Project("IAM.Api", projects: ["IAM.Modules.Identity.Infrastructure"])]).ShouldNotBeEmpty();

    [Fact]
    public void WorkerReferencingEndpointsIsReported() =>
        ArchitectureRules.CheckHostsWorkersConnectors([Project("IAM.Workers.Provisioning", projects: ["IAM.Modules.Provisioning.Endpoints"])]).ShouldNotBeEmpty();

    [Fact]
    public void UnapprovedModuleIsReported() =>
        ArchitectureRules.CheckModuleStructure([Project("IAM.Modules.Billing.Domain")])
            .ShouldContain(v => v.Contains("not in the approved module list", StringComparison.Ordinal));

    [Fact]
    public void ProjectMissingFromSolutionIsReported() =>
        ArchitectureRules.CheckSolutionMembership([Project("IAM.Modules.Identity.Domain")], []).ShouldNotBeEmpty();
}
