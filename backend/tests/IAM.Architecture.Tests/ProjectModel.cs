using System.Xml.Linq;

namespace IAM.Architecture.Tests;

/// <summary>Kind of project, derived from its name.</summary>
public enum ProjectKind
{
    Unknown,
    Module,
    BuildingBlock,
    Host,
    Worker,
    Connector,
    Persistence,
    Test,

    /// <summary>Operator command-line tools (for example the access bootstrap): composed like hosts, no HTTP surface.</summary>
    Tool,
}

/// <summary>
/// A project as declared in its .csproj file: name, project references, package references and
/// framework references. Declared references are checked (not only compiled ones), because the C#
/// compiler drops references to assemblies that no code uses yet.
/// </summary>
public sealed record ProjectInfo(
    string Name,
    string RelativePath,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> FrameworkReferences)
{
    public ProjectKind Kind => Classify(Name);

    /// <summary>Module name for module projects (e.g. "Identity"), otherwise null.</summary>
    public string? Module => Kind == ProjectKind.Module ? Name.Split('.')[2] : null;

    /// <summary>Layer for module projects (e.g. "Domain"), otherwise null.</summary>
    public string? Layer => Kind == ProjectKind.Module ? Name.Split('.')[3] : null;

    public static ProjectKind Classify(string name)
    {
        if (name.EndsWith(".Tests", StringComparison.Ordinal))
        {
            return ProjectKind.Test;
        }

        var parts = name.Split('.');
        if (parts.Length == 4 && parts[0] == "IAM" && parts[1] == "Modules")
        {
            return ProjectKind.Module;
        }

        if (name.StartsWith("IAM.BuildingBlocks.", StringComparison.Ordinal))
        {
            return ProjectKind.BuildingBlock;
        }

        if (name.StartsWith("IAM.Workers.", StringComparison.Ordinal))
        {
            return ProjectKind.Worker;
        }

        if (name.StartsWith("IAM.Connectors.", StringComparison.Ordinal))
        {
            return ProjectKind.Connector;
        }

        if (name.StartsWith("IAM.Persistence.", StringComparison.Ordinal))
        {
            return ProjectKind.Persistence;
        }

        if (name.StartsWith("IAM.Tools.", StringComparison.Ordinal))
        {
            return ProjectKind.Tool;
        }

        return name == "IAM.Api" || name == "IAM.Bff" ? ProjectKind.Host : ProjectKind.Unknown;
    }
}

/// <summary>Loads every .csproj under backend/ and the solution file.</summary>
public static class RepositoryProjects
{
    public static string BackendRoot { get; } = FindBackendRoot();

    public static IReadOnlyList<ProjectInfo> LoadAll() =>
        Directory.EnumerateFiles(BackendRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .Select(Load)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<string> SolutionProjectPaths()
    {
        var slnx = XDocument.Load(Path.Combine(BackendRoot, "IAM.slnx"));
        return slnx.Descendants("Project")
            .Select(e => Normalize((string?)e.Attribute("Path") ?? string.Empty))
            .ToList();
    }

    public static ProjectInfo Load(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        static string NameOf(string include) =>
            Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));

        return new ProjectInfo(
            Path.GetFileNameWithoutExtension(csprojPath),
            Normalize(Path.GetRelativePath(BackendRoot, csprojPath)),
            doc.Descendants("ProjectReference").Select(e => NameOf((string?)e.Attribute("Include") ?? string.Empty)).ToList(),
            doc.Descendants("PackageReference").Select(e => (string?)e.Attribute("Include") ?? string.Empty).ToList(),
            doc.Descendants("FrameworkReference").Select(e => (string?)e.Attribute("Include") ?? string.Empty).ToList());
    }

    public static string Normalize(string path) => path.Replace('\\', '/');

    private static bool IsBuildOutput(string path)
    {
        var p = Normalize(path);
        return p.Contains("/bin/", StringComparison.Ordinal) || p.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string FindBackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IAM.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate backend/IAM.slnx above the test output directory.");
    }
}
