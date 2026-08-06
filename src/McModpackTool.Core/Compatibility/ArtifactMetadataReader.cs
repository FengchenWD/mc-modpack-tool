using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace McModpackTool.Core.Compatibility;

public sealed record ArtifactMetadataReaderOptions
{
    public int MaxArchiveEntries { get; init; } = 100_000;
    public int MaxMetadataBytes { get; init; } = 2 * 1024 * 1024;
    public static ArtifactMetadataReaderOptions Default { get; } = new();
}

public sealed record ArtifactCompatibilityMetadata
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Loader { get; init; } = string.Empty;
    public IReadOnlyCollection<string> ModIds { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CompatibilityRelation> Relations { get; init; }
        = Array.Empty<CompatibilityRelation>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool MetadataFound { get; init; }
}

/// <summary>Reads loader-declared dependency and conflict rules from a selected mod JAR.</summary>
public static partial class ArtifactMetadataReader
{
    private static readonly string[] SupportedEntries =
    [
        "fabric.mod.json",
        "quilt.mod.json",
        "META-INF/mods.toml",
        "META-INF/neoforge.mods.toml",
    ];

    public static ArtifactCompatibilityMetadata Read(
        string artifactPath,
        ArtifactMetadataReaderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        options ??= ArtifactMetadataReaderOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var attributes = File.GetAttributes(artifactPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Symbolic/reparse-point mod artifacts are not inspected.");
        }

        using var file = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        if (archive.Entries.Count > options.MaxArchiveEntries)
        {
            throw new InvalidDataException("The artifact has too many ZIP entries to inspect safely.");
        }

        var builder = new MetadataBuilder();
        foreach (var expectedName in SupportedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = archive.Entries.Where(entry =>
                string.Equals(
                    entry.FullName.Replace('\\', '/').TrimStart('/'),
                    expectedName,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                continue;
            }
            if (matches.Length > 1)
            {
                builder.Warnings.Add($"Multiple '{expectedName}' entries were found; metadata is ambiguous.");
                continue;
            }

            string text;
            try
            {
                text = ReadUtf8Entry(matches[0], options.MaxMetadataBytes, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or DecoderFallbackException)
            {
                builder.Warnings.Add($"Could not read '{expectedName}': {exception.Message}");
                continue;
            }

            try
            {
                if (expectedName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase))
                {
                    ParseFabric(text, builder);
                }
                else if (expectedName.Equals("quilt.mod.json", StringComparison.OrdinalIgnoreCase))
                {
                    ParseQuilt(text, builder);
                }
                else
                {
                    ParseForgeToml(text, builder, expectedName.Contains("neoforge", StringComparison.OrdinalIgnoreCase));
                }
                builder.MetadataFound = true;
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException)
            {
                builder.Warnings.Add($"Could not parse '{expectedName}': {exception.Message}");
            }
        }
        return builder.Build();
    }

    public static CompatibilityContentItem Enrich(
        CompatibilityContentItem item,
        ArtifactCompatibilityMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(metadata);
        var ids = item.ModIds
            .Concat(metadata.ModIds)
            .Append(metadata.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var aliases = item.Aliases
            .Concat(metadata.Aliases)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var relations = item.Relations.Concat(metadata.Relations).Distinct().ToArray();
        return item with
        {
            Version = string.IsNullOrWhiteSpace(item.Version) ? metadata.Version : item.Version,
            DeclaredLoader = string.IsNullOrWhiteSpace(item.DeclaredLoader) ? metadata.Loader : item.DeclaredLoader,
            ModIds = ids,
            Aliases = aliases,
            Relations = relations,
            MetadataWarnings = item.MetadataWarnings.Concat(metadata.Warnings).Distinct(StringComparer.Ordinal).ToArray(),
            DependencyMetadataAvailable = item.DependencyMetadataAvailable || metadata.MetadataFound,
        };
    }

    private static string ReadUtf8Entry(
        ZipArchiveEntry entry,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > maxBytes)
        {
            throw new InvalidDataException($"Metadata exceeds the {maxBytes}-byte safety limit.");
        }
        using var stream = entry.Open();
        using var memory = new MemoryStream((int)Math.Min(entry.Length, maxBytes));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0)
            {
                break;
            }
            if (memory.Length + count > maxBytes)
            {
                throw new InvalidDataException($"Metadata exceeds the {maxBytes}-byte safety limit.");
            }
            memory.Write(buffer, 0, count);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(memory.GetBuffer(), 0, checked((int)memory.Length))
            .TrimStart('\uFEFF');
    }

    private static void ParseFabric(string text, MetadataBuilder builder)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("fabric.mod.json root must be an object.");
        }
        builder.Loader = Prefer(builder.Loader, "fabric");
        builder.Id = Prefer(builder.Id, GetScalarText(root, "id"));
        builder.Version = Prefer(builder.Version, GetScalarText(root, "version"));
        AddIdentity(builder.ModIds, builder.Id);

        if (root.TryGetProperty("provides", out var provides))
        {
            ReadIdentityValues(provides, builder.Aliases);
        }
        ReadFabricRelationMap(root, "depends", CompatibilityRelationKinds.Required, builder.Relations);
        ReadFabricRelationMap(root, "breaks", CompatibilityRelationKinds.Incompatible, builder.Relations);
        ReadFabricRelationMap(root, "conflicts", CompatibilityRelationKinds.Incompatible, builder.Relations);
    }

    private static void ReadFabricRelationMap(
        JsonElement root,
        string propertyName,
        string relationKind,
        ICollection<CompatibilityRelation> destination)
    {
        if (!root.TryGetProperty(propertyName, out var relations) || relations.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var relation in relations.EnumerateObject())
        {
            var requirement = ReadRequirement(relation.Value);
            destination.Add(new CompatibilityRelation
            {
                Kind = relationKind,
                Reference = relation.Name,
                ExactReference = relation.Name,
                ReferenceType = CompatibilityReferenceTypes.ModId,
                VersionRequirement = requirement,
            });
        }
    }

    private static void ParseQuilt(string text, MetadataBuilder builder)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64,
        });
        var root = document.RootElement;
        if (!root.TryGetProperty("quilt_loader", out var loader) || loader.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("quilt.mod.json has no quilt_loader object.");
        }
        builder.Loader = Prefer(builder.Loader, "quilt");
        builder.Id = Prefer(builder.Id, GetScalarText(loader, "id"));
        builder.Version = Prefer(builder.Version, GetScalarText(loader, "version"));
        AddIdentity(builder.ModIds, builder.Id);

        if (loader.TryGetProperty("provides", out var provides))
        {
            ReadIdentityValues(provides, builder.Aliases);
        }
        if (loader.TryGetProperty("depends", out var dependencies))
        {
            ReadQuiltRelations(dependencies, CompatibilityRelationKinds.Required, builder.Relations);
        }
        if (loader.TryGetProperty("breaks", out var conflicts))
        {
            ReadQuiltRelations(conflicts, CompatibilityRelationKinds.Incompatible, builder.Relations);
        }
    }

    private static void ReadQuiltRelations(
        JsonElement value,
        string relationKind,
        ICollection<CompatibilityRelation> destination)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                ReadQuiltRelations(child, relationKind, destination);
            }
            return;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var id = value.GetString() ?? string.Empty;
            if (id.Length > 0)
            {
                destination.Add(NewModIdRelation(relationKind, id, string.Empty));
            }
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var idValue = GetScalarText(value, "id");
        if (idValue.Length > 0)
        {
            var optional = value.TryGetProperty("optional", out var optionalElement) &&
                optionalElement.ValueKind is JsonValueKind.True;
            if (!optional)
            {
                var requirement = value.TryGetProperty("versions", out var versions)
                    ? ReadRequirement(versions)
                    : string.Empty;
                destination.Add(NewModIdRelation(relationKind, idValue, requirement));
            }
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("unless") || property.NameEquals("optional"))
            {
                continue;
            }
            destination.Add(NewModIdRelation(relationKind, property.Name, ReadRequirement(property.Value)));
        }
    }

    private static CompatibilityRelation NewModIdRelation(string kind, string id, string requirement) => new()
    {
        Kind = kind,
        Reference = id,
        ExactReference = id,
        ReferenceType = CompatibilityReferenceTypes.ModId,
        VersionRequirement = requirement,
    };

    private static string ReadRequirement(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim() ?? string.Empty;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            return string.Join(" || ", value.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "version", "versions", "versionRange" })
            {
                if (value.TryGetProperty(property, out var nested))
                {
                    return ReadRequirement(nested);
                }
            }
        }
        return string.Empty;
    }

    private static void ReadIdentityValues(JsonElement value, ICollection<string> destination)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            AddIdentity(destination, value.GetString());
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object)
                {
                    AddIdentity(destination, GetScalarText(child, "id"));
                }
                else
                {
                    ReadIdentityValues(child, destination);
                }
            }
        }
    }

    private static string GetScalarText(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static void AddIdentity(ICollection<string> destination, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !destination.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            destination.Add(value.Trim());
        }
    }

    private static string Prefer(string existing, string candidate) =>
        string.IsNullOrWhiteSpace(existing) ? candidate : existing;

    private sealed class MetadataBuilder
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Loader { get; set; } = string.Empty;
        public List<string> ModIds { get; } = [];
        public List<string> Aliases { get; } = [];
        public List<CompatibilityRelation> Relations { get; } = [];
        public List<string> Warnings { get; } = [];
        public bool MetadataFound { get; set; }

        public ArtifactCompatibilityMetadata Build() => new()
        {
            Id = Id,
            Version = Version,
            Loader = Loader,
            ModIds = ModIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Aliases = Aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Relations = Relations.Distinct().ToArray(),
            Warnings = Warnings.ToArray(),
            MetadataFound = MetadataFound,
        };
    }

    private static void ParseForgeToml(string text, MetadataBuilder builder, bool neoForge)
    {
        ParseForgeTomlCore(text, builder, neoForge);
    }

    // Kept in a separate method block below so the narrow TOML reader is easy to audit.
    private static partial void ParseForgeTomlCore(string text, MetadataBuilder builder, bool neoForge);
}
