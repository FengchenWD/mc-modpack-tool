using System.Text.Json;
using System.Text.Json.Nodes;

namespace McModpackTool.Core.Models;

/// <summary>
/// A platform dependency declaration normalized without losing its original payload.
/// </summary>
public sealed class DependencyReference
{
    public string ProjectId { get; set; } = string.Empty;

    public string VersionId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string DependencyType { get; set; } = "unknown";

    public string VersionRequirement { get; set; } = string.Empty;

    public JsonObject RawData { get; set; } = [];

    public static DependencyReference FromCurseForge(CurseForgeDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        return new DependencyReference
        {
            ProjectId = dependency.ModId > 0
                ? dependency.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
            Source = "curseforge",
            DependencyType = dependency.RelationType switch
            {
                1 => "embedded",
                2 => "optional",
                3 => "required",
                4 => "tool",
                5 => "incompatible",
                _ => "unknown",
            },
            RawData = JsonSerializer.SerializeToNode(dependency)?.AsObject() ?? [],
        };
    }

    public static DependencyReference FromModrinth(ModrinthDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        return new DependencyReference
        {
            ProjectId = dependency.ProjectId ?? string.Empty,
            VersionId = dependency.VersionId ?? string.Empty,
            FileName = dependency.FileName ?? string.Empty,
            Source = "modrinth",
            DependencyType = dependency.DependencyType ?? "unknown",
            VersionRequirement = dependency.VersionRequirement ?? string.Empty,
            RawData = JsonSerializer.SerializeToNode(dependency)?.AsObject() ?? [],
        };
    }
}
