using System.Text.Json;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public static class GameDirectoryScanner
{
    private const long MaxVersionMetadataBytes = 16 * 1024 * 1024;

    private static readonly string[] ContentDirectoryNames =
    [
        "mods",
        "config",
        "saves",
        "defaultconfigs",
        "kubejs",
        "scripts",
    ];

    private static readonly string[] OptionalDirectoryNames =
    [
        "config",
        "defaultconfigs",
        "kubejs",
        "scripts",
    ];

    public static Task<GameDirectoryDiscovery> DiscoverAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Task.Run(() => Discover(directory, cancellationToken), cancellationToken);
    }

    public static Task<ServerPackSource> ReadAsync(
        string directory,
        ServerVersionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(candidate);
        return Task.Run(() => Read(directory, candidate, cancellationToken), cancellationToken);
    }

    private static GameDirectoryDiscovery Discover(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = GetSafeDirectory(directory, "The selected game directory");
        var topLevelDirectories = FindContentDirectories(root, cancellationToken);
        var versionCandidates = DiscoverVersions(root, cancellationToken);

        return new GameDirectoryDiscovery
        {
            ContentRoot = root,
            RequiresInstanceDirectory = topLevelDirectories.Count == 0,
            VersionCandidates = versionCandidates,
        };
    }

    private static ServerPackSource Read(
        string directory,
        ServerVersionCandidate requestedCandidate,
        CancellationToken cancellationToken)
    {
        var discovery = Discover(directory, cancellationToken);
        if (discovery.RequiresInstanceDirectory)
        {
            throw new InvalidDataException(
                "The selected directory has no top-level mods, config, saves, defaultconfigs, kubejs, or scripts directory. " +
                "Select the version-isolated instance directory instead.");
        }

        var candidate = ResolveCandidate(discovery.VersionCandidates, requestedCandidate);
        if (candidate.LoaderType.Equals("quilt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("One-click server packaging does not support Quilt instances.");
        }
        var root = discovery.ContentRoot;
        var source = new ServerPackSource
        {
            InputKind = ServerInputKinds.Directory,
            SourcePath = root,
            DisplayName = new DirectoryInfo(root).Name,
            MinecraftVersion = candidate.MinecraftVersion,
            LoaderType = candidate.LoaderType,
            LoaderVersion = candidate.LoaderVersion,
            ContentRoot = root,
        };

        var modsDirectory = Path.Combine(root, "mods");
        if (Directory.Exists(modsDirectory))
        {
            source.Mods = ReadMods(modsDirectory, candidate, cancellationToken);
        }

        var savesDirectory = Path.Combine(root, "saves");
        if (Directory.Exists(savesDirectory))
        {
            source.Worlds = ReadWorlds(savesDirectory, cancellationToken);
        }

        foreach (var name in OptionalDirectoryNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, name);
            if (!Directory.Exists(path))
            {
                continue;
            }

            EnumerateFiles(path, cancellationToken);
            source.OptionalDirectories[name] = Path.GetFullPath(path);
        }

        return source;
    }

    private static Dictionary<string, string> FindContentDirectories(
        string root,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in EnumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (!ContentDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The content directory '{entry}' is a symbolic link or reparse point and cannot be read safely.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                result[name] = Path.GetFullPath(entry);
            }
        }
        return result;
    }

    private static List<ServerVersionCandidate> DiscoverVersions(
        string root,
        CancellationToken cancellationToken)
    {
        var catalogPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directPaths = GetJsonFiles(root, cancellationToken);
        catalogPaths.UnionWith(directPaths);

        var selectedIsVersionDirectory = string.Equals(
            Directory.GetParent(root)?.Name,
            "versions",
            StringComparison.OrdinalIgnoreCase);
        List<string> candidatePaths;

        if (selectedIsVersionDirectory)
        {
            candidatePaths = directPaths;
            var versionsRoot = Directory.GetParent(root)!.FullName;
            catalogPaths.UnionWith(GetVersionDirectoryJsonFiles(versionsRoot, cancellationToken));
        }
        else
        {
            var directMetadata = directPaths
                .Where(path => TryReadVersionMetadata(path, cancellationToken) is not null)
                .ToList();
            if (directMetadata.Count > 0)
            {
                candidatePaths = directMetadata;
            }
            else
            {
                var versionsRoot = Path.Combine(root, "versions");
                candidatePaths = Directory.Exists(versionsRoot)
                    ? GetVersionDirectoryJsonFiles(versionsRoot, cancellationToken)
                    : [];
                catalogPaths.UnionWith(candidatePaths);
            }
        }

        var metadataByPath = new Dictionary<string, VersionMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in catalogPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = TryReadVersionMetadata(path, cancellationToken);
            if (metadata is not null)
            {
                metadataByPath[Path.GetFullPath(path)] = metadata;
            }
        }

        var metadataById = metadataByPath.Values
            .GroupBy(metadata => metadata.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<ServerVersionCandidate>();
        foreach (var path in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!metadataByPath.TryGetValue(Path.GetFullPath(path), out var metadata))
            {
                continue;
            }

            var chain = ResolveInheritanceChain(metadata, metadataById);
            var minecraftVersion = FindMinecraftVersion(chain);
            var (loaderType, loaderVersion) = FindLoader(chain, minecraftVersion);
            result.Add(new ServerVersionCandidate
            {
                Id = metadata.Id,
                MinecraftVersion = minecraftVersion,
                LoaderType = loaderType,
                LoaderVersion = loaderVersion,
                MetadataPath = metadata.Path,
            });
        }

        return result
            .OrderBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.MetadataPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<VersionMetadata> ResolveInheritanceChain(
        VersionMetadata candidate,
        IReadOnlyDictionary<string, VersionMetadata> metadataById)
    {
        var chain = new List<VersionMetadata>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = candidate;
        while (visited.Add(current.Id))
        {
            chain.Add(current);
            if (current.InheritsFrom.Length == 0 ||
                !metadataById.TryGetValue(current.InheritsFrom, out var parent))
            {
                break;
            }
            current = parent;
        }
        return chain;
    }

    private static string FindMinecraftVersion(IReadOnlyList<VersionMetadata> chain)
    {
        foreach (var metadata in chain)
        {
            if (metadata.ClientVersion.Length > 0)
            {
                return metadata.ClientVersion;
            }
        }

        var last = chain[^1];
        if (last.InheritsFrom.Length > 0)
        {
            return last.InheritsFrom;
        }
        if (last.Jar.Length > 0)
        {
            return last.Jar;
        }

        foreach (var metadata in chain)
        {
            foreach (var library in metadata.Libraries)
            {
                if (TryParseMavenCoordinate(library, out var group, out var artifact, out var version) &&
                    group.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase) &&
                    artifact.Equals("forge", StringComparison.OrdinalIgnoreCase))
                {
                    var separator = version.IndexOf('-');
                    if (separator > 0)
                    {
                        return version[..separator];
                    }
                }
            }
        }

        return last.Id;
    }

    private static (string LoaderType, string LoaderVersion) FindLoader(
        IReadOnlyList<VersionMetadata> chain,
        string minecraftVersion)
    {
        foreach (var metadata in chain)
        {
            foreach (var library in metadata.Libraries)
            {
                if (!TryParseMavenCoordinate(library, out var group, out var artifact, out var version))
                {
                    continue;
                }

                if (group.Equals("net.fabricmc", StringComparison.OrdinalIgnoreCase) &&
                    artifact.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase))
                {
                    return ("fabric", version);
                }
                if (group.Equals("org.quiltmc", StringComparison.OrdinalIgnoreCase) &&
                    artifact.Equals("quilt-loader", StringComparison.OrdinalIgnoreCase))
                {
                    return ("quilt", version);
                }
                if (group.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase) &&
                    artifact.Equals("forge", StringComparison.OrdinalIgnoreCase))
                {
                    return ("forge", RemoveMinecraftPrefix(version, minecraftVersion));
                }
                if (group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase) &&
                    artifact.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
                {
                    return ("neoforge", version);
                }
                if (group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase) &&
                    artifact.Equals("forge", StringComparison.OrdinalIgnoreCase))
                {
                    return ("neoforge", RemoveMinecraftPrefix(version, minecraftVersion));
                }
            }
        }

        foreach (var metadata in chain)
        {
            var fallback = FindLoaderInVersionId(metadata.Id, minecraftVersion);
            if (fallback.LoaderType.Length > 0)
            {
                return fallback;
            }
        }
        return (string.Empty, string.Empty);
    }

    private static (string LoaderType, string LoaderVersion) FindLoaderInVersionId(
        string id,
        string minecraftVersion)
    {
        var markers = new (string Marker, string Loader)[]
        {
            ("fabric-loader-", "fabric"),
            ("quilt-loader-", "quilt"),
            ("-neoforge-", "neoforge"),
            ("neoforge-", "neoforge"),
            ("-forge-", "forge"),
            ("forge-", "forge"),
        };
        foreach (var (marker, loader) in markers)
        {
            var index = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var version = id[(index + marker.Length)..];
            var minecraftSuffix = $"-{minecraftVersion}";
            if (version.EndsWith(minecraftSuffix, StringComparison.OrdinalIgnoreCase))
            {
                version = version[..^minecraftSuffix.Length];
            }
            return (loader, version);
        }
        return (string.Empty, string.Empty);
    }

    private static string RemoveMinecraftPrefix(string version, string minecraftVersion)
    {
        var prefix = minecraftVersion + "-";
        if (version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return version[prefix.Length..];
        }
        var separator = version.IndexOf('-');
        return separator >= 0 ? version[(separator + 1)..] : version;
    }

    private static bool TryParseMavenCoordinate(
        string coordinate,
        out string group,
        out string artifact,
        out string version)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3)
        {
            group = artifact = version = string.Empty;
            return false;
        }
        group = parts[0].Trim();
        artifact = parts[1].Trim();
        version = parts[2].Trim();
        return group.Length > 0 && artifact.Length > 0 && version.Length > 0;
    }

    private static VersionMetadata? TryReadVersionMetadata(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Version metadata '{path}' is a symbolic link or reparse point and cannot be read safely.");
        }

        var file = new FileInfo(path);
        if (file.Length > MaxVersionMetadataBytes)
        {
            throw new InvalidDataException(
                $"Version metadata '{path}' exceeds the {MaxVersionMetadataBytes}-byte safety limit.");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = GetString(root, "id");
            var inheritsFrom = GetString(root, "inheritsFrom");
            if (id.Length == 0 && inheritsFrom.Length == 0)
            {
                return null;
            }
            if (id.Length == 0)
            {
                id = Path.GetFileNameWithoutExtension(path);
            }

            var libraries = new List<string>();
            if (root.TryGetProperty("libraries", out var libraryArray) &&
                libraryArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var library in libraryArray.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (library.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var name = GetString(library, "name");
                    if (name.Length > 0)
                    {
                        libraries.Add(name);
                    }
                }
            }

            return new VersionMetadata(
                id,
                inheritsFrom,
                GetString(root, "clientVersion"),
                GetString(root, "jar"),
                libraries,
                Path.GetFullPath(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetString(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
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

    private static List<string> GetVersionDirectoryJsonFiles(
        string versionsRoot,
        CancellationToken cancellationToken)
    {
        var root = GetSafeDirectory(versionsRoot, "The versions directory");
        var result = new List<string>();
        foreach (var entry in EnumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Version directory '{entry}' is a symbolic link or reparse point and cannot be read safely.");
            }

            var preferred = Path.Combine(entry, Path.GetFileName(entry) + ".json");
            if (File.Exists(preferred))
            {
                EnsureRegularFile(preferred, "Version metadata");
                result.Add(Path.GetFullPath(preferred));
                continue;
            }
            result.AddRange(GetJsonFiles(entry, cancellationToken));
        }
        return result;
    }

    private static List<string> GetJsonFiles(string directory, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var entry in EnumerateEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0 ||
                !Path.GetExtension(entry).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Version metadata '{entry}' is a symbolic link or reparse point and cannot be read safely.");
            }
            result.Add(Path.GetFullPath(entry));
        }
        return result;
    }

    private static List<ServerModEntry> ReadMods(
        string modsDirectory,
        ServerVersionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var result = new List<ServerModEntry>();
        foreach (var file in EnumerateFiles(modsDirectory, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var disabled = file.RelativePath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
            if (!disabled && !file.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ArtifactCompatibilityMetadata? metadata = null;
            string? readError = null;
            try
            {
                metadata = ArtifactMetadataReader.Read(file.FullPath, cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                readError = exception.Message;
            }

            var (support, reason) = ClassifyServerSupport(metadata, readError);
            result.Add(new ServerModEntry
            {
                Name = Path.GetFileName(file.RelativePath),
                RelativePath = file.RelativePath,
                SourcePath = file.FullPath,
                Origin = ServerModOrigins.Local,
                ServerSupport = support,
                SupportReason = reason,
                Selected = !disabled && support != ServerSupportKinds.Unsupported,
                Disabled = disabled,
                ContentItem = new ContentItem
                {
                    Name = Path.GetFileName(file.RelativePath),
                    FileName = Path.GetFileName(file.RelativePath),
                    FilePath = "mods/" + file.RelativePath,
                    OldMinecraftVersion = candidate.MinecraftVersion,
                    OldLoader = candidate.LoaderType,
                    Category = "mod",
                    Disabled = disabled,
                    Passthrough = true,
                    Status = "found",
                    Source = ServerModOrigins.Local,
                },
            });
        }
        return result.OrderBy(mod => mod.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static (string Support, string Reason) ClassifyServerSupport(
        ArtifactCompatibilityMetadata? metadata,
        string? readError)
    {
        if (metadata is null)
        {
            return (
                ServerSupportKinds.Unknown,
                readError is null ? "No loader metadata was found." : $"Could not inspect mod metadata: {readError}");
        }

        var environment = metadata.ServerEnvironment.Trim().ToLowerInvariant();
        if (environment is "client")
        {
            return (ServerSupportKinds.Unsupported, "Loader metadata declares this mod as client-only.");
        }
        if (environment is "server" or "dedicated_server" or "*")
        {
            return (ServerSupportKinds.Recommended, "Loader metadata permits this mod on a dedicated server.");
        }
        if (metadata.Loader is "fabric" or "quilt")
        {
            return (
                ServerSupportKinds.Recommended,
                "Fabric/Quilt metadata has no client-only environment restriction.");
        }
        if (!metadata.MetadataFound)
        {
            return (ServerSupportKinds.Unknown, "No supported loader metadata was found in this file.");
        }
        return (
            ServerSupportKinds.Unknown,
            "Forge/NeoForge metadata does not reliably declare dedicated-server support.");
    }

    private static List<ServerWorldEntry> ReadWorlds(
        string savesDirectory,
        CancellationToken cancellationToken)
    {
        var files = EnumerateFiles(savesDirectory, cancellationToken);
        var worldNames = files
            .Select(file => file.RelativePath.Split('/'))
            .Where(parts => parts.Length == 2 && parts[1].Equals("level.dat", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        return worldNames.Select(name => new ServerWorldEntry
        {
            Name = name,
            SourcePath = Path.GetFullPath(Path.Combine(savesDirectory, name)),
        }).ToList();
    }

    private static List<LocalFile> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var safeRoot = GetSafeDirectory(root, "The content directory");
        var result = new List<LocalFile>();
        var pending = new Stack<string>();
        pending.Push(safeRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Content path '{entry}' is a symbolic link or reparse point and cannot be packaged safely.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                result.Add(new LocalFile(
                    Path.GetFullPath(entry),
                    Path.GetRelativePath(safeRoot, entry).Replace('\\', '/')));
            }
        }
        return result;
    }

    private static ServerVersionCandidate ResolveCandidate(
        IReadOnlyList<ServerVersionCandidate> discovered,
        ServerVersionCandidate requested)
    {
        ServerVersionCandidate? match = null;
        if (!string.IsNullOrWhiteSpace(requested.MetadataPath))
        {
            var requestedPath = Path.GetFullPath(requested.MetadataPath);
            match = discovered.FirstOrDefault(candidate =>
                Path.GetFullPath(candidate.MetadataPath).Equals(requestedPath, StringComparison.OrdinalIgnoreCase));
        }
        if (match is null && requested.Id.Length > 0)
        {
            var idMatches = discovered
                .Where(candidate => candidate.Id.Equals(requested.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (idMatches.Length == 1)
            {
                match = idMatches[0];
            }
        }
        return match ?? throw new InvalidDataException(
            "The selected version candidate does not belong to the selected game directory.");
    }

    private static string GetSafeDirectory(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"{description} does not exist: {fullPath}");
        }
        var attributes = GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{description} is a symbolic link or reparse point and cannot be read safely: {fullPath}");
        }
        return fullPath;
    }

    private static void EnsureRegularFile(string path, string description)
    {
        var attributes = GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{description} '{path}' is a symbolic link or reparse point and cannot be read safely.");
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException($"{description} '{path}' is not a regular file.");
        }
    }

    private static string[] EnumerateEntries(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Could not read directory '{directory}': {exception.Message}", exception);
        }
    }

    private static FileAttributes GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Could not inspect path '{path}': {exception.Message}", exception);
        }
    }

    private sealed record VersionMetadata(
        string Id,
        string InheritsFrom,
        string ClientVersion,
        string Jar,
        IReadOnlyList<string> Libraries,
        string Path);

    private sealed record LocalFile(string FullPath, string RelativePath);
}
