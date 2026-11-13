namespace IAM.Architecture.Tests;

/// <summary>
/// Module boundary and dependency rules (ADR-0001, ADR-0011, ADR-0017, ADR-0020, ADR-0022, ADR-0010).
/// Each check returns human-readable violations; an empty list means the rule holds.
/// The rules are pure functions so they can be tested with deliberately bad inputs.
/// </summary>
public static class ArchitectureRules
{
    /// <summary>Approved module list (ADR-0011, accepted 2026-11-07).</summary>
    public static readonly IReadOnlySet<string> ApprovedModules = new HashSet<string>(StringComparer.Ordinal)
    {
        "Identity", "Organization", "Catalog", "Account", "Policy", "Workflow", "Request",
        "Provisioning", "Governance", "Audit", "Integration", "Notification", "Administration", "Lifecycle",
    };

    public static readonly IReadOnlyList<string> Layers = ["Contracts", "Domain", "Application", "Infrastructure", "Endpoints"];

    /// <summary>Same-module layer references each layer may declare.</summary>
    private static readonly Dictionary<string, string[]> AllowedSameModule = new(StringComparer.Ordinal)
    {
        ["Contracts"] = [],
        ["Domain"] = [],
        ["Application"] = ["Domain", "Contracts"],
        ["Infrastructure"] = ["Application", "Domain", "Contracts"],
        ["Endpoints"] = ["Application", "Contracts", "Infrastructure"],
    };

    /// <summary>Layers that may reference another module's Contracts (and nothing else of that module).</summary>
    private static readonly HashSet<string> MayUseOtherModuleContracts = new(StringComparer.Ordinal)
    {
        "Application", "Infrastructure", "Endpoints",
    };

    /// <summary>BuildingBlocks each layer may reference.</summary>
    private static readonly Dictionary<string, string[]> AllowedBuildingBlocks = new(StringComparer.Ordinal)
    {
        ["Contracts"] = ["Abstractions"],
        ["Domain"] = ["Abstractions"],
        ["Application"] = ["Abstractions", "Messaging"],
        ["Infrastructure"] = ["Abstractions", "Persistence", "Messaging", "Observability", "Security"],
        ["Endpoints"] = ["Abstractions", "Security", "Observability"],
    };

    /// <summary>Packages never allowed: SQLite/InMemory are not provider parity (ADR-0010); commercial libraries not approved (ADR-0020, ADR-0022, DL-018).</summary>
    private static readonly string[] ForbiddenPackagePrefixes =
    [
        "Microsoft.EntityFrameworkCore.Sqlite",
        "Microsoft.EntityFrameworkCore.InMemory",
        "Microsoft.Data.Sqlite",
        "MediatR",
        "AutoMapper",
        "FluentAssertions",
        "Duende.",
    ];

    /// <summary>Database driver/provider packages: only persistence projects may reference them (ADR-0017).</summary>
    private static readonly string[] DatabaseDriverPrefixes =
    [
        "Microsoft.Data.SqlClient",
        "Npgsql",
        "Microsoft.EntityFrameworkCore.SqlServer",
        "System.Data.SqlClient",
    ];

    public static IReadOnlyList<string> CheckModuleStructure(IReadOnlyList<ProjectInfo> projects)
    {
        var violations = new List<string>();
        var modules = projects.Where(p => p.Kind == ProjectKind.Module).ToList();

        foreach (var p in modules)
        {
            if (!ApprovedModules.Contains(p.Module!))
            {
                violations.Add($"{p.Name}: module '{p.Module}' is not in the approved module list (ADR-0011).");
            }

            if (!Layers.Contains(p.Layer!))
            {
                violations.Add($"{p.Name}: layer '{p.Layer}' is not one of {string.Join(", ", Layers)}.");
            }
        }

        foreach (var module in ApprovedModules)
        {
            foreach (var layer in Layers)
            {
                if (!modules.Any(p => p.Module == module && p.Layer == layer))
                {
                    violations.Add($"Module {module} is missing project IAM.Modules.{module}.{layer}.");
                }
            }
        }

        foreach (var p in projects.Where(p => p.Kind == ProjectKind.Unknown))
        {
            violations.Add($"{p.Name}: project name does not match any known kind (module, building block, host, worker, connector, persistence, test).");
        }

        return violations;
    }

    public static IReadOnlyList<string> CheckModuleReferences(IReadOnlyList<ProjectInfo> projects)
    {
        var violations = new List<string>();
        foreach (var p in projects.Where(p => p.Kind == ProjectKind.Module))
        {
            foreach (var reference in p.ProjectReferences)
            {
                var problem = ExplainModuleReference(p.Module!, p.Layer!, reference);
                if (problem is not null)
                {
                    violations.Add($"{p.Name} -> {reference}: {problem}");
                }
            }
        }

        return violations;
    }

    /// <summary>Returns null if a module layer may reference <paramref name="reference"/>, otherwise the reason it may not.</summary>
    public static string? ExplainModuleReference(string module, string layer, string reference)
    {
        switch (ProjectInfo.Classify(reference))
        {
            case ProjectKind.BuildingBlock:
                var block = reference["IAM.BuildingBlocks.".Length..];
                return AllowedBuildingBlocks.TryGetValue(layer, out var blocks) && blocks.Contains(block)
                    ? null
                    : $"{layer} may not reference BuildingBlocks.{block}.";

            case ProjectKind.Module:
                var parts = reference.Split('.');
                var (refModule, refLayer) = (parts[2], parts[3]);
                if (refModule == module)
                {
                    return AllowedSameModule.TryGetValue(layer, out var allowed) && allowed.Contains(refLayer)
                        ? null
                        : $"{layer} may not reference its own module's {refLayer}.";
                }

                if (refLayer != "Contracts")
                {
                    return $"cross-module reference to {refModule}.{refLayer}; only {refModule}.Contracts may be referenced.";
                }

                return MayUseOtherModuleContracts.Contains(layer)
                    ? null
                    : $"{layer} may not reference other modules, even their Contracts.";

            default:
                return "modules may reference only BuildingBlocks and module projects.";
        }
    }

    public static IReadOnlyList<string> CheckPurity(IReadOnlyList<ProjectInfo> projects)
    {
        var violations = new List<string>();
        foreach (var p in projects.Where(p => p.Layer is "Domain" or "Contracts"))
        {
            violations.AddRange(p.PackageReferences.Select(pkg => $"{p.Name}: {p.Layer} may not reference packages (found {pkg})."));
            violations.AddRange(p.FrameworkReferences.Select(fw => $"{p.Name}: {p.Layer} may not reference frameworks (found {fw})."));
        }

        foreach (var p in projects.Where(p => p.Kind == ProjectKind.BuildingBlock && p.Name == "IAM.BuildingBlocks.Abstractions"))
        {
            violations.AddRange(p.PackageReferences.Select(pkg => $"{p.Name}: Abstractions may not reference packages (found {pkg})."));
            violations.AddRange(p.FrameworkReferences.Select(fw => $"{p.Name}: Abstractions may not reference frameworks (found {fw})."));
            violations.AddRange(p.ProjectReferences.Select(r => $"{p.Name}: Abstractions may not reference other projects (found {r})."));
        }

        return violations;
    }

    public static IReadOnlyList<string> CheckPackages(IReadOnlyList<ProjectInfo> projects)
    {
        var violations = new List<string>();
        foreach (var p in projects)
        {
            foreach (var pkg in p.PackageReferences)
            {
                if (ForbiddenPackagePrefixes.Any(f => pkg.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{p.Name}: package {pkg} is forbidden (ADR-0010 / ADR-0020 / ADR-0022 / DL-018).");
                }

                var isPersistence = p.Kind == ProjectKind.Persistence || p.Name == "IAM.BuildingBlocks.Persistence";
                if (!isPersistence && DatabaseDriverPrefixes.Any(d => pkg.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{p.Name}: database driver {pkg} is allowed only in persistence projects (ADR-0017).");
                }
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> CheckHostsWorkersConnectors(IReadOnlyList<ProjectInfo> projects)
    {
        var violations = new List<string>();
        foreach (var p in projects.Where(p => p.Kind is ProjectKind.Host or ProjectKind.Tool))
        {
            foreach (var r in p.ProjectReferences)
            {
                var kind = ProjectInfo.Classify(r);
                var ok = kind == ProjectKind.BuildingBlock
                    || kind == ProjectKind.Persistence
                    || (kind == ProjectKind.Module && r.EndsWith(".Endpoints", StringComparison.Ordinal));
                if (!ok)
                {
                    violations.Add($"{p.Name} -> {r}: hosts compose modules through their Endpoints project only (ADR-0001).");
                }
            }
        }

        foreach (var p in projects.Where(p => p.Kind is ProjectKind.Worker or ProjectKind.Connector))
        {
            foreach (var r in p.ProjectReferences.Where(r => r.EndsWith(".Endpoints", StringComparison.Ordinal) || ProjectInfo.Classify(r) == ProjectKind.Host))
            {
                violations.Add($"{p.Name} -> {r}: workers and connectors may not reference Endpoints or hosts (ADR-0001).");
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> CheckSolutionMembership(IReadOnlyList<ProjectInfo> projects, IReadOnlyList<string> solutionPaths)
    {
        var inSolution = new HashSet<string>(solutionPaths, StringComparer.OrdinalIgnoreCase);
        return projects
            .Where(p => !inSolution.Contains(p.RelativePath))
            .Select(p => $"{p.RelativePath}: project is not listed in backend/IAM.slnx, so CI would not build or test it.")
            .ToList();
    }
}
