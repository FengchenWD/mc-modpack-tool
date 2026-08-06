using McModpackTool.Core.Models;

namespace McModpackTool.Core.Compatibility;

/// <summary>Maps the shared migration model into an immutable analyzer snapshot.</summary>
public static class CompatibilityContentItemAdapter
{
    public static IReadOnlyList<CompatibilityContentItem> FromContentItems(
        IEnumerable<ContentItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var result = new List<CompatibilityContentItem>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(FromContentItem(item, result.Count));
        }
        return result;
    }

    public static CompatibilityContentItem FromContentItem(ContentItem item, int originalIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        var relations = item.TargetDependencies
            .Select(dependency => ConvertRelation(dependency, item.Source))
            .Where(relation => relation is not null)
            .Cast<CompatibilityRelation>()
            .ToArray();
        return new CompatibilityContentItem
        {
            OriginalIndex = originalIndex,
            Name = item.Name,
            ProjectId = item.ProjectId,
            TargetVersionId = item.TargetVersionId,
            CurseForgeSlug = item.CurseForgeSlug,
            ModrinthSlug = item.ModrinthSlug,
            Source = item.Source,
            Category = item.Category,
            Status = item.Status,
            FileName = item.FileName,
            TargetFileName = item.TargetFileName,
            TargetDownloadUrl = item.TargetDownloadUrl,
            Version = item.TargetVersionNumber,
            Disabled = item.Disabled,
            Excluded = item.Excluded,
            Passthrough = item.Passthrough,
            Required = item.Required,
            DependencyMetadataAvailable = item.DependencyMetadataAvailable,
            Environment = new Dictionary<string, string>(item.Environment, StringComparer.OrdinalIgnoreCase),
            Relations = relations,
        };
    }

    private static CompatibilityRelation? ConvertRelation(DependencyReference dependency, string ownerSource)
    {
        var kind = NormalizeKind(dependency.DependencyType);
        if (kind is null)
        {
            return null;
        }

        string reference;
        string referenceType;
        if (!string.IsNullOrWhiteSpace(dependency.VersionId))
        {
            reference = dependency.VersionId;
            referenceType = CompatibilityReferenceTypes.VersionId;
        }
        else if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
        {
            reference = dependency.ProjectId;
            referenceType = CompatibilityReferenceTypes.ProjectId;
        }
        else if (!string.IsNullOrWhiteSpace(dependency.FileName))
        {
            reference = dependency.FileName;
            referenceType = CompatibilityReferenceTypes.FileName;
        }
        else if (!string.IsNullOrWhiteSpace(dependency.Slug))
        {
            reference = dependency.Slug;
            referenceType = CompatibilityReferenceTypes.Slug;
        }
        else if (!string.IsNullOrWhiteSpace(dependency.Name))
        {
            reference = dependency.Name;
            referenceType = CompatibilityReferenceTypes.Name;
        }
        else
        {
            return null;
        }

        return new CompatibilityRelation
        {
            Kind = kind,
            Reference = reference,
            ExactReference = reference,
            ReferenceType = referenceType,
            Source = string.IsNullOrWhiteSpace(dependency.Source) ? ownerSource : dependency.Source,
            VersionRequirement = dependency.VersionRequirement,
        };
    }

    private static string? NormalizeKind(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
            .ToArray()).Trim('_');
        return normalized switch
        {
            "required" or "required_dependency" or "depends" or "dependency" =>
                CompatibilityRelationKinds.Required,
            "incompatible" or "conflict" or "conflicts" or "breaks" =>
                CompatibilityRelationKinds.Incompatible,
            _ => null,
        };
    }
}
