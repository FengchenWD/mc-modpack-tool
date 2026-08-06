using System.Collections.ObjectModel;

namespace McModpackTool.Core.Compatibility;

/// <summary>Stable string values consumed by localization and report serialization.</summary>
public static class CompatibilitySeverity
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}

public static class CompatibilityConfidence
{
    public const string Confirmed = "confirmed";
    public const string Heuristic = "heuristic";
    public const string Incomplete = "incomplete";
}

public static class CompatibilityScopes
{
    public const string General = "general";
    public const string Mod = "mod";
    public const string ResourcePack = "resourcepack";
    public const string ShaderPack = "shaderpack";
    public const string Content = "content";
    public const string Dependency = "dependency";
    public const string Output = "output";
}

public static class CompatibilityRelationKinds
{
    public const string Required = "required";
    public const string Incompatible = "incompatible";
    public const string IncompatibleSelf = "incompatible_self";
}

public static class CompatibilityReferenceTypes
{
    public const string ProjectId = "project_id";
    public const string VersionId = "version_id";
    public const string FileName = "file_name";
    public const string Slug = "slug";
    public const string Name = "name";
    public const string ModId = "mod_id";
}

public sealed record CompatibilityRelation
{
    public string Kind { get; init; } = CompatibilityRelationKinds.Required;
    public string Reference { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string ReferenceType { get; init; } = CompatibilityReferenceTypes.ProjectId;
    public string ExactReference { get; init; } = string.Empty;

    /// <summary>
    /// A Fabric/Quilt predicate (for example <c>&gt;=0.6.0 &lt;0.7.0</c>) or a
    /// Forge Maven range (for example <c>[0.6,0.7)</c>). Empty means any version.
    /// </summary>
    public string VersionRequirement { get; init; } = string.Empty;

    public string NormalizedReference => CompatibilityText.NormalizeReference(Reference);
}

/// <summary>
/// A deliberately UI/API-neutral snapshot used by the analyzer. The workflow layer maps its
/// ContentItem model to this type after target lookup and, when available, JAR metadata reading.
/// </summary>
public sealed record CompatibilityContentItem
{
    public int OriginalIndex { get; init; } = -1;
    public string Name { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string TargetVersionId { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string CurseForgeSlug { get; init; } = string.Empty;
    public string ModrinthSlug { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Category { get; init; } = "mod";
    public string Status { get; init; } = "pending";
    public string FileName { get; init; } = string.Empty;
    public string TargetFileName { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string TargetDownloadUrl { get; init; } = string.Empty;

    /// <summary>The actual mod version declared by the selected target artifact, not its API version ID.</summary>
    public string Version { get; init; } = string.Empty;
    public string DeclaredLoader { get; init; } = string.Empty;

    public bool Disabled { get; init; }
    public bool Excluded { get; init; }
    public bool Passthrough { get; init; }
    public bool Required { get; init; } = true;
    public bool DependencyMetadataAvailable { get; init; }
    public bool ExplicitlyIncompatible { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = ReadOnlyDictionary<string, string>.Empty;
    public IReadOnlyList<CompatibilityRelation> Relations { get; init; }
        = Array.Empty<CompatibilityRelation>();

    /// <summary>Loader-level IDs declared inside the artifact, such as <c>iris</c> and <c>sodium</c>.</summary>
    public IReadOnlyCollection<string> ModIds { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MetadataWarnings { get; init; } = Array.Empty<string>();
}

public sealed record CompatibilityIssue
{
    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = CompatibilitySeverity.Info;
    public string Message { get; init; } = string.Empty;
    public string Confidence { get; init; } = CompatibilityConfidence.Confirmed;
    public string Scope { get; init; } = CompatibilityScopes.General;
    public string? Item { get; init; }
    public string? Path { get; init; }
    public IReadOnlyDictionary<string, object?> Evidence { get; init; }
        = ReadOnlyDictionary<string, object?>.Empty;
}

public sealed class CompatibilityReport
{
    private readonly List<CompatibilityIssue> _issues = [];
    private readonly List<string> _limitations = [];
    private readonly Dictionary<string, int> _stats = new(StringComparer.Ordinal);

    public CompatibilityReport(
        string sourceMinecraftVersion,
        string targetMinecraftVersion,
        string sourceLoader = "",
        string targetLoader = "")
    {
        SourceMinecraftVersion = sourceMinecraftVersion ?? string.Empty;
        TargetMinecraftVersion = targetMinecraftVersion ?? string.Empty;
        SourceLoader = sourceLoader ?? string.Empty;
        TargetLoader = targetLoader ?? string.Empty;
    }

    public string SourceMinecraftVersion { get; }
    public string TargetMinecraftVersion { get; }
    public string SourceLoader { get; }
    public string TargetLoader { get; }
    public IReadOnlyList<CompatibilityIssue> Issues => _issues;
    public IReadOnlyList<string> Limitations => _limitations;
    public IReadOnlyDictionary<string, int> Stats => _stats;
    public bool HasErrors => _issues.Any(issue =>
        string.Equals(issue.Severity, CompatibilitySeverity.Error, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, int> Counts
    {
        get
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [CompatibilitySeverity.Error] = 0,
                [CompatibilitySeverity.Warning] = 0,
                [CompatibilitySeverity.Info] = 0,
            };
            foreach (var issue in _issues)
            {
                result[issue.Severity] = result.GetValueOrDefault(issue.Severity) + 1;
            }
            return new ReadOnlyDictionary<string, int>(result);
        }
    }

    public void AddIssue(
        string code,
        string severity,
        string message,
        string confidence = CompatibilityConfidence.Confirmed,
        string scope = CompatibilityScopes.General,
        string? item = null,
        string? path = null,
        IReadOnlyDictionary<string, object?>? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(severity);
        _issues.Add(new CompatibilityIssue
        {
            Code = code,
            Severity = severity,
            Message = message ?? string.Empty,
            Confidence = confidence,
            Scope = scope,
            Item = item,
            Path = path,
            Evidence = evidence is null
                ? ReadOnlyDictionary<string, object?>.Empty
                : new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(evidence)),
        });
    }

    public void AddLimitation(string limitation)
    {
        if (!string.IsNullOrWhiteSpace(limitation) && !_limitations.Contains(limitation, StringComparer.Ordinal))
        {
            _limitations.Add(limitation);
        }
    }

    public void SetStat(string name, int value) => _stats[name] = value;

    public void IncrementStat(string name, int amount = 1) =>
        _stats[name] = _stats.GetValueOrDefault(name) + amount;
}

public sealed record CompatibilityAnalysisRequest
{
    public required IEnumerable<CompatibilityContentItem> Items { get; init; }
    public string SourceMinecraftVersion { get; init; } = string.Empty;
    public string TargetMinecraftVersion { get; init; } = string.Empty;
    public string SourceLoader { get; init; } = string.Empty;
    public string TargetLoader { get; init; } = string.Empty;
    public string SourceLoaderVersion { get; init; } = string.Empty;
    public string TargetLoaderVersion { get; init; } = string.Empty;
    public string TargetFormat { get; init; } = string.Empty;

    /// <summary>Paths relative to the pack's overrides root. Their files are never parsed or migrated here.</summary>
    public IEnumerable<string>? PassthroughPaths { get; init; }
}

internal static class CompatibilityText
{
    public static string NormalizeReference(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static string NormalizeLoader(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray());
        return normalized switch
        {
            "fabricloader" => "fabric",
            "quiltloader" => "quilt",
            "neoforged" or "neo" => "neoforge",
            _ => normalized,
        };
    }
}
