using System.Text.Json.Nodes;

namespace McModpackTool.Core.Models;

/// <summary>
/// A mod, resource pack, or shader pack and the target selected for migration.
/// </summary>
public sealed class ContentItem
{
    public string Name { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string FileId { get; set; } = string.Empty;

    public string VersionId { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public List<string> DownloadUrls { get; set; } = [];

    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string OldMinecraftVersion { get; set; } = string.Empty;

    public string OldLoader { get; set; } = string.Empty;

    public string Category { get; set; } = "mod";

    public bool Disabled { get; set; }

    public bool Excluded { get; set; }

    public bool Passthrough { get; set; }

    public bool Required { get; set; } = true;

    public string FilePath { get; set; } = string.Empty;

    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Status { get; set; } = "pending";

    public string TargetFileId { get; set; } = string.Empty;

    public string TargetDownloadUrl { get; set; } = string.Empty;

    public string TargetVersionId { get; set; } = string.Empty;

    /// <summary>
    /// Human/package version used by dependency-range checks. This differs from a platform version ID.
    /// </summary>
    public string TargetVersionNumber { get; set; } = string.Empty;

    public string TargetFileName { get; set; } = string.Empty;

    public long TargetFileSize { get; set; }

    public Dictionary<string, string> TargetHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Note { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string CurseForgeSlug { get; set; } = string.Empty;

    public string ModrinthSlug { get; set; } = string.Empty;

    public List<DependencyReference> TargetDependencies { get; set; } = [];

    public bool DependencyMetadataAvailable { get; set; }

    public JsonObject? OriginalEntry { get; set; }

    public bool IdentityLocked { get; set; }

    public bool PreserveOriginal { get; set; }

    public string OriginalProjectId { get; set; } = string.Empty;

    public string OriginalSource { get; set; } = string.Empty;

    public void ResetTarget()
    {
        Status = "pending";
        TargetFileId = string.Empty;
        TargetDownloadUrl = string.Empty;
        TargetVersionId = string.Empty;
        TargetVersionNumber = string.Empty;
        TargetFileName = string.Empty;
        TargetFileSize = 0;
        TargetHashes.Clear();
        TargetDependencies.Clear();
        DependencyMetadataAvailable = false;
        Note = string.Empty;
        PreserveOriginal = false;
    }
}
