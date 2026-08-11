using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace McModpackTool.Core.Compatibility;

public sealed record ArtifactMetadataReaderOptions
{
    public int MaxArchiveEntries { get; init; } = 100_000;
    public int MaxMetadataBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxNestedArtifacts { get; init; } = 256;
    public long MaxNestedArtifactBytes { get; init; } = 32L * 1024 * 1024;
    public long MaxTotalNestedArtifactBytes { get; init; } = 128L * 1024 * 1024;
    public int MaxCommonEntrypointClasses { get; init; } = 64;
    public int MaxEntrypointClassBytes { get; init; } = 2 * 1024 * 1024;
    public long MaxTotalEntrypointClassBytes { get; init; } = 16L * 1024 * 1024;
    public static ArtifactMetadataReaderOptions Default { get; } = new();
}

public sealed record ArtifactCompatibilityMetadata
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Loader { get; init; } = string.Empty;
    public string ServerEnvironment { get; init; } = string.Empty;
    public bool HasClientEntrypoint { get; init; }
    public bool HasCommonEntrypoint { get; init; }
    public bool HasServerEntrypoint { get; init; }
    public bool HasUnsafeClientReferencesInCommonEntrypoint { get; init; }
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
    private static readonly byte[][] StrongClientApiMarkers =
    [
        Encoding.ASCII.GetBytes("net/fabricmc/fabric/api/client/"),
        Encoding.ASCII.GetBytes("net/minecraft/client/"),
        Encoding.ASCII.GetBytes("net/fabricmc/api/ClientModInitializer"),
        Encoding.ASCII.GetBytes("com/mojang/blaze3d/"),
        Encoding.ASCII.GetBytes("org/lwjgl/"),
    ];

    private static readonly string[] SupportedEntries =
    [
        "fabric.mod.json",
        "quilt.mod.json",
        "META-INF/mods.toml",
        "META-INF/neoforge.mods.toml",
        "mcmod.info",
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
                else if (expectedName.Equals("mcmod.info", StringComparison.OrdinalIgnoreCase))
                {
                    ParseLegacyForge(text, builder);
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
        InspectFabricCommonEntrypoints(archive, builder, options, cancellationToken);
        ReadNestedFabricMetadata(archive, builder, options, cancellationToken);
        return builder.Build();
    }

    private static void InspectFabricCommonEntrypoints(
        ZipArchive archive,
        MetadataBuilder builder,
        ArtifactMetadataReaderOptions options,
        CancellationToken cancellationToken)
    {
        string[] classPaths = builder.CommonEntrypointClassPaths
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (classPaths.Length > options.MaxCommonEntrypointClasses)
        {
            builder.Warnings.Add(
                $"Fabric common entrypoint count exceeds the {options.MaxCommonEntrypointClasses}-class safety limit.");
            classPaths = classPaths[..options.MaxCommonEntrypointClasses];
        }

        long totalBytes = 0;
        foreach (string classPath in classPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry[] matches = archive.Entries.Where(entry => string.Equals(
                NormalizeArchiveEntryName(entry.FullName), classPath, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
            {
                continue;
            }

            ZipArchiveEntry entry = matches[0];
            if (entry.Length < 0 || entry.Length > options.MaxEntrypointClassBytes ||
                totalBytes > options.MaxTotalEntrypointClassBytes - entry.Length)
            {
                builder.Warnings.Add($"Fabric common entrypoint '{classPath}' exceeds the configured safety limits.");
                continue;
            }
            totalBytes += entry.Length;

            try
            {
                using MemoryStream bytes = ReadBoundedEntry(
                    entry, options.MaxEntrypointClassBytes, cancellationToken);
                ReadOnlySpan<byte> content = bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length));
                foreach (byte[] marker in StrongClientApiMarkers)
                {
                    if (content.IndexOf(marker) >= 0)
                    {
                        builder.HasUnsafeClientReferencesInCommonEntrypoint = true;
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                builder.Warnings.Add($"Could not inspect Fabric common entrypoint '{classPath}': {exception.Message}");
            }
        }
    }

    private static void ReadNestedFabricMetadata(
        ZipArchive archive,
        MetadataBuilder builder,
        ArtifactMetadataReaderOptions options,
        CancellationToken cancellationToken)
    {
        var paths = builder.NestedArtifactPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length > options.MaxNestedArtifacts)
        {
            builder.Warnings.Add(
                $"Nested Fabric artifact count exceeds the {options.MaxNestedArtifacts}-artifact safety limit.");
            paths = paths[..options.MaxNestedArtifacts];
        }

        long totalBytes = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = archive.Entries.Where(entry => string.Equals(
                NormalizeArchiveEntryName(entry.FullName), path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                builder.Warnings.Add(matches.Length == 0
                    ? $"Nested Fabric artifact '{path}' was not found."
                    : $"Nested Fabric artifact '{path}' is ambiguous.");
                continue;
            }

            var entry = matches[0];
            if (entry.Length < 0 || entry.Length > options.MaxNestedArtifactBytes ||
                totalBytes > options.MaxTotalNestedArtifactBytes - entry.Length)
            {
                builder.Warnings.Add($"Nested Fabric artifact '{path}' exceeds the configured safety limits.");
                continue;
            }
            totalBytes += entry.Length;

            try
            {
                using var memory = ReadBoundedEntry(entry, checked((int)entry.Length), cancellationToken);
                using var nestedArchive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
                if (nestedArchive.Entries.Count > options.MaxArchiveEntries)
                {
                    builder.Warnings.Add($"Nested Fabric artifact '{path}' has too many ZIP entries.");
                    continue;
                }
                var metadataEntries = nestedArchive.Entries.Where(nested => string.Equals(
                    NormalizeArchiveEntryName(nested.FullName), "fabric.mod.json", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (metadataEntries.Length != 1)
                {
                    continue;
                }

                var nestedBuilder = new MetadataBuilder();
                ParseFabric(
                    ReadUtf8Entry(metadataEntries[0], options.MaxMetadataBytes, cancellationToken),
                    nestedBuilder);
                AddIdentity(builder.ModIds, nestedBuilder.Id);
                foreach (var id in nestedBuilder.ModIds)
                {
                    AddIdentity(builder.ModIds, id);
                }
                foreach (var alias in nestedBuilder.Aliases)
                {
                    AddIdentity(builder.Aliases, alias);
                }
                foreach (var relation in nestedBuilder.Relations.Where(relation =>
                             relation.Kind == CompatibilityRelationKinds.Required &&
                             relation.NormalizedReference == "java"))
                {
                    builder.Relations.Add(relation);
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or DecoderFallbackException or JsonException)
            {
                builder.Warnings.Add($"Could not inspect nested Fabric artifact '{path}': {exception.Message}");
            }
        }
    }

    private static MemoryStream ReadBoundedEntry(
        ZipArchiveEntry entry,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var input = entry.Open();
        var memory = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                memory.Position = 0;
                return memory;
            }
            if (memory.Length + read > maxBytes)
            {
                memory.Dispose();
                throw new InvalidDataException($"Nested artifact exceeds the {maxBytes}-byte safety limit.");
            }
            memory.Write(buffer, 0, read);
        }
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
        var environment = new Dictionary<string, string>(item.Environment, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(metadata.ServerEnvironment))
        {
            string declared = metadata.ServerEnvironment.Trim().ToLowerInvariant();
            switch (declared)
            {
                case "client":
                    environment["client"] = "required";
                    environment["server"] = "unsupported";
                    break;
                case "server":
                case "dedicated_server":
                    environment["client"] = "unsupported";
                    environment["server"] = "required";
                    break;
                case "*":
                    environment["client"] = "required";
                    environment["server"] = "required";
                    break;
            }
        }
        return item with
        {
            Version = string.IsNullOrWhiteSpace(item.Version) ? metadata.Version : item.Version,
            DeclaredLoader = string.IsNullOrWhiteSpace(item.DeclaredLoader) ? metadata.Loader : item.DeclaredLoader,
            ModIds = ids,
            Aliases = aliases,
            Relations = relations,
            Environment = environment,
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
        builder.Name = Prefer(builder.Name, GetScalarText(root, "name"));
        builder.Description = Prefer(builder.Description, GetScalarText(root, "description"));
        builder.Version = Prefer(builder.Version, GetScalarText(root, "version"));
        builder.ServerEnvironment = Prefer(builder.ServerEnvironment, GetScalarText(root, "environment"));
        AddIdentity(builder.ModIds, builder.Id);

        if (root.TryGetProperty("entrypoints", out var entrypoints) &&
            entrypoints.ValueKind == JsonValueKind.Object)
        {
            builder.HasClientEntrypoint |= HasEntrypoint(entrypoints, "client");
            builder.HasCommonEntrypoint |= HasEntrypoint(entrypoints, "main");
            builder.HasServerEntrypoint |= HasEntrypoint(entrypoints, "server");
            ReadFabricEntrypointClasses(entrypoints, "main", builder.CommonEntrypointClassPaths);
        }

        if (root.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
        {
            foreach (var jar in jars.EnumerateArray())
            {
                if (jar.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var path = NormalizeNestedArtifactPath(GetScalarText(jar, "file"));
                if (path.Length > 0)
                {
                    builder.NestedArtifactPaths.Add(path);
                }
            }
        }

        if (root.TryGetProperty("provides", out var provides))
        {
            ReadIdentityValues(provides, builder.Aliases);
        }
        ReadFabricRelationMap(root, "depends", CompatibilityRelationKinds.Required, builder.Relations);
        ReadFabricRelationMap(root, "breaks", CompatibilityRelationKinds.Incompatible, builder.Relations);
        ReadFabricRelationMap(root, "conflicts", CompatibilityRelationKinds.Incompatible, builder.Relations);
    }

    private static void ParseLegacyForge(string text, MetadataBuilder builder)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64,
        });
        JsonElement root = document.RootElement;
        builder.Loader = Prefer(builder.Loader, "forge");

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement mod in root.EnumerateArray())
            {
                ParseLegacyForgeMod(mod, builder);
            }
            return;
        }
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("modList", out JsonElement modList) &&
            modList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement mod in modList.EnumerateArray())
            {
                ParseLegacyForgeMod(mod, builder);
            }
            return;
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            ParseLegacyForgeMod(root, builder);
            return;
        }
        throw new InvalidDataException("mcmod.info root must be an object or array.");
    }

    private static void ParseLegacyForgeMod(JsonElement mod, MetadataBuilder builder)
    {
        if (mod.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        string id = GetScalarText(mod, "modid");
        AddIdentity(builder.ModIds, id);
        builder.Id = Prefer(builder.Id, id);
        builder.Name = Prefer(builder.Name, GetScalarText(mod, "name"));
        builder.Description = Prefer(builder.Description, GetScalarText(mod, "description"));
        string version = GetScalarText(mod, "version");
        if (!version.Contains("${", StringComparison.Ordinal))
        {
            builder.Version = Prefer(builder.Version, version);
        }

        if (GetBoolean(mod, "clientSideOnly"))
        {
            builder.ServerEnvironment = Prefer(builder.ServerEnvironment, "client");
        }
        else if (GetBoolean(mod, "serverSideOnly"))
        {
            builder.ServerEnvironment = Prefer(builder.ServerEnvironment, "server");
        }

        if (mod.TryGetProperty("requiredMods", out JsonElement requiredMods) &&
            requiredMods.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement required in requiredMods.EnumerateArray())
            {
                if (required.ValueKind == JsonValueKind.String)
                {
                    AddLegacyForgeDependency(required.GetString(), builder.Relations);
                }
            }
        }
        string dependencies = GetScalarText(mod, "dependencies");
        foreach (string dependency in dependencies.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dependency.TrimStart().StartsWith("required-", StringComparison.OrdinalIgnoreCase))
            {
                AddLegacyForgeDependency(dependency, builder.Relations);
            }
        }
    }

    private static void AddLegacyForgeDependency(
        string? rawDependency,
        ICollection<CompatibilityRelation> destination)
    {
        string dependency = (rawDependency ?? string.Empty).Trim();
        int separator = dependency.IndexOf(':');
        if (separator >= 0)
        {
            dependency = dependency[(separator + 1)..].Trim();
        }
        int versionStart = dependency.IndexOf('@');
        string version = versionStart >= 0 ? dependency[(versionStart + 1)..].Trim() : string.Empty;
        string id = (versionStart >= 0 ? dependency[..versionStart] : dependency).Trim();
        if (id.Length == 0)
        {
            return;
        }
        destination.Add(NewModIdRelation(CompatibilityRelationKinds.Required, id, version));
    }

    private static bool GetBoolean(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) && parsed,
            _ => false,
        };
    }

    private static bool HasEntrypoint(JsonElement entrypoints, string name)
    {
        if (!entrypoints.TryGetProperty(name, out var value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Object => true,
            _ => false,
        };
    }

    private static void ReadFabricEntrypointClasses(
        JsonElement entrypoints,
        string name,
        ICollection<string> destination)
    {
        if (entrypoints.TryGetProperty(name, out JsonElement value))
        {
            ReadFabricEntrypointClasses(value, destination);
        }
    }

    private static void ReadFabricEntrypointClasses(
        JsonElement value,
        ICollection<string> destination)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                AddFabricEntrypointClass(destination, value.GetString());
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in value.EnumerateArray())
                {
                    ReadFabricEntrypointClasses(child, destination);
                }
                break;
            case JsonValueKind.Object when value.TryGetProperty("value", out JsonElement objectValue):
                ReadFabricEntrypointClasses(objectValue, destination);
                break;
        }
    }

    private static void AddFabricEntrypointClass(ICollection<string> destination, string? value)
    {
        string className = (value ?? string.Empty).Trim();
        int methodSeparator = className.IndexOf("::", StringComparison.Ordinal);
        if (methodSeparator >= 0)
        {
            className = className[..methodSeparator].Trim();
        }

        string classPath = className.Replace('.', '/');
        string[] segments = classPath.Split('/');
        if (classPath.Length == 0 || classPath.StartsWith('/') || classPath.Contains('\\') ||
            segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return;
        }

        string archivePath = $"{classPath}.class";
        if (!destination.Contains(archivePath, StringComparer.Ordinal))
        {
            destination.Add(archivePath);
        }
    }

    private static string NormalizeNestedArtifactPath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized.StartsWith('/') ||
            normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return string.Empty;
        }
        return normalized;
    }

    private static string NormalizeArchiveEntryName(string value) =>
        value.Replace('\\', '/').TrimStart('/');

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
        if (loader.TryGetProperty("metadata", out JsonElement metadata) &&
            metadata.ValueKind == JsonValueKind.Object)
        {
            builder.Name = Prefer(builder.Name, GetScalarText(metadata, "name"));
            builder.Description = Prefer(builder.Description, GetScalarText(metadata, "description"));
        }
        if (root.TryGetProperty("minecraft", out var minecraft) && minecraft.ValueKind == JsonValueKind.Object)
        {
            builder.ServerEnvironment = Prefer(
                builder.ServerEnvironment,
                GetScalarText(minecraft, "environment"));
        }
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
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Loader { get; set; } = string.Empty;
        public string ServerEnvironment { get; set; } = string.Empty;
        public bool HasClientEntrypoint { get; set; }
        public bool HasCommonEntrypoint { get; set; }
        public bool HasServerEntrypoint { get; set; }
        public bool HasUnsafeClientReferencesInCommonEntrypoint { get; set; }
        public List<string> ModIds { get; } = [];
        public List<string> Aliases { get; } = [];
        public List<CompatibilityRelation> Relations { get; } = [];
        public List<string> NestedArtifactPaths { get; } = [];
        public List<string> CommonEntrypointClassPaths { get; } = [];
        public List<string> Warnings { get; } = [];
        public bool MetadataFound { get; set; }

        public ArtifactCompatibilityMetadata Build() => new()
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Version = Version,
            Loader = Loader,
            ServerEnvironment = ServerEnvironment,
            HasClientEntrypoint = HasClientEntrypoint,
            HasCommonEntrypoint = HasCommonEntrypoint,
            HasServerEntrypoint = HasServerEntrypoint,
            HasUnsafeClientReferencesInCommonEntrypoint = HasUnsafeClientReferencesInCommonEntrypoint,
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
