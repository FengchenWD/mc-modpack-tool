using System.Globalization;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed record DependencyRepairCandidate(
    string Source,
    string Reference,
    string ReferenceType,
    ContentItem Item);

/// <summary>
/// Resolves only platform identities explicitly declared by a dependency.
/// It intentionally never guesses a project from a mod id, name, slug, or filename.
/// </summary>
public sealed class DependencyRepairService
{
    private readonly CurseForgeClient _curseForge;
    private readonly ModrinthClient _modrinth;

    public DependencyRepairService(CurseForgeClient curseForge, ModrinthClient modrinth)
    {
        _curseForge = curseForge ?? throw new ArgumentNullException(nameof(curseForge));
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
    }

    public async Task<DependencyRepairCandidate?> ResolveAsync(
        string source,
        string referenceType,
        string reference,
        string targetMinecraft,
        string targetLoader,
        CancellationToken cancellationToken = default)
    {
        source = (source ?? string.Empty).Trim().ToLowerInvariant();
        referenceType = (referenceType ?? string.Empty).Trim().ToLowerInvariant();
        reference = (reference ?? string.Empty).Trim();
        if (reference.Length == 0)
        {
            return null;
        }

        if (source == "modrinth" &&
            referenceType is CompatibilityReferenceTypes.ProjectId or CompatibilityReferenceTypes.VersionId)
        {
            return await ResolveModrinthAsync(
                referenceType,
                reference,
                targetMinecraft,
                targetLoader,
                cancellationToken).ConfigureAwait(false);
        }

        if (source == "curseforge" &&
            referenceType == CompatibilityReferenceTypes.ProjectId &&
            long.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out long projectId) &&
            projectId > 0)
        {
            return await ResolveCurseForgeAsync(
                reference,
                projectId,
                targetMinecraft,
                targetLoader,
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<DependencyRepairCandidate?> ResolveModrinthAsync(
        string referenceType,
        string reference,
        string targetMinecraft,
        string targetLoader,
        CancellationToken cancellationToken)
    {
        ModrinthVersion? version;
        string projectId;
        if (referenceType == CompatibilityReferenceTypes.VersionId)
        {
            ModrinthVersion declared = await _modrinth.GetVersionAsync(reference, cancellationToken)
                .ConfigureAwait(false);
            projectId = declared.ProjectId;
            bool declaredFits = declared.GameVersions.Contains(targetMinecraft, StringComparer.Ordinal)
                && declared.Loaders.Contains(targetLoader, StringComparer.OrdinalIgnoreCase);
            version = declaredFits ? declared : null;
        }
        else
        {
            projectId = reference;
            version = await _modrinth.FindTargetVersionAsync(
                projectId,
                targetMinecraft,
                targetLoader,
                strictMinecraft: true,
                cancellationToken).ConfigureAwait(false);
        }

        ModrinthFile? file = SearchMatcher.SelectUsablePrimaryFile(version?.Files);
        if (version is null || file is null || projectId.Length == 0)
        {
            return null;
        }

        ModrinthProject project = await _modrinth.GetProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        var item = new ContentItem
        {
            Name = project.Title.Length > 0 ? project.Title : projectId,
            ProjectId = projectId,
            OriginalProjectId = projectId,
            Source = "modrinth",
            OriginalSource = "modrinth",
            Category = "mod",
            Required = true,
            IdentityLocked = true,
            Status = version.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase)
                ? "found"
                : "warning",
            TargetVersionId = version.Id,
            TargetVersionNumber = version.VersionNumber,
            TargetFileName = file.FileName,
            TargetDownloadUrl = file.Url,
            TargetFileSize = file.Size,
            TargetHashes = new Dictionary<string, string>(file.Hashes, StringComparer.OrdinalIgnoreCase),
            TargetDependencies = (version.Dependencies ?? [])
                .Select(DependencyReference.FromModrinth)
                .ToList(),
            DependencyMetadataAvailable = version.Dependencies is not null,
            ModrinthSlug = project.Slug,
            Note = version.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $"仅 {version.VersionType} 版",
        };
        return new DependencyRepairCandidate("modrinth", reference, referenceType, item);
    }

    private async Task<DependencyRepairCandidate?> ResolveCurseForgeAsync(
        string reference,
        long projectId,
        string targetMinecraft,
        string targetLoader,
        CancellationToken cancellationToken)
    {
        CurseForgeFile? file = await _curseForge.FindTargetFileAsync(
            projectId,
            targetMinecraft,
            targetLoader,
            strictMinecraft: true,
            cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        CurseForgeProject project = await _curseForge.GetProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        string downloadUrl = file.DownloadUrl;
        if (downloadUrl.Length == 0)
        {
            downloadUrl = await _curseForge.GetDownloadUrlAsync(projectId, file.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        var item = new ContentItem
        {
            Name = project.Name.Length > 0 ? project.Name : file.DisplayName,
            ProjectId = projectId.ToString(CultureInfo.InvariantCulture),
            OriginalProjectId = projectId.ToString(CultureInfo.InvariantCulture),
            Source = "curseforge",
            OriginalSource = "curseforge",
            Category = "mod",
            Required = true,
            IdentityLocked = true,
            Status = file.ReleaseType == 1 ? "found" : "warning",
            TargetFileId = file.Id.ToString(CultureInfo.InvariantCulture),
            TargetFileName = file.FileName,
            TargetDownloadUrl = downloadUrl,
            TargetFileSize = file.FileLength,
            TargetHashes = SearchMatcher.ExtractCurseForgeHashes(file),
            TargetDependencies = (file.Dependencies ?? [])
                .Where(dependency => dependency.ModId > 0)
                .Select(DependencyReference.FromCurseForge)
                .ToList(),
            DependencyMetadataAvailable = file.Dependencies is not null,
            CurseForgeSlug = project.Slug,
            Note = file.ReleaseType == 1 ? string.Empty : "仅 Beta/Alpha 版",
        };
        return new DependencyRepairCandidate(
            "curseforge",
            reference,
            CompatibilityReferenceTypes.ProjectId,
            item);
    }
}
