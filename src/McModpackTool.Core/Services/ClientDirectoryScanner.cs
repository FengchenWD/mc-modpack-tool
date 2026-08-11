using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public static class ClientDirectoryScanner
{
    private static readonly string[] ConfigurationDirectories =
    [
        "config",
        "defaultconfigs",
        "kubejs",
        "scripts",
    ];

    private static readonly HashSet<string> MapAndModDataDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "antiqueatlas",
        "antiqueatlas_data",
        "CustomSkinLoader",
        "data",
        "ftbchunks",
        "journeymap",
        "map exports",
        "map-exports",
        "map_exports",
        "mapdata",
        "map-data",
        "map_data",
        "mapfrontiers",
        "maps",
        "mapwriter",
        "mapwriter_spam",
        "minimap",
        "minimaps",
        "voxelmap",
        "voxelmap_cache",
        "waypoints",
        "worldmap",
        "worldmaps",
        "xaero",
        "xaeroplus",
        "XaeroWaypoints",
        "XaeroWorldMap",
    };

    private static readonly string[] MapAndModDataDirectoryPrefixes =
    [
        "XaeroWaypoints_",
        "XaeroWorldMap_",
    ];

    private static readonly HashSet<string> StructureDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "blueprints",
        "litematica",
        "schematic",
        "schematics",
        "structures",
    };

    private static readonly HashSet<string> ReplayDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "flashback",
        "recordings",
        "replay_recordings",
        "replays",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cache",
        ".fabric",
        ".forge",
        ".hmcl",
        ".launcher",
        ".neoforge",
        ".pcl",
        ".quilt",
        ".replay_cache",
        ".webcache",
        "assets",
        "bin",
        "cache",
        "caches",
        "crash-reports",
        "downloads",
        "hmcl",
        "java",
        "launcher",
        "libraries",
        "logs",
        "natives",
        "PCL",
        "PCL2",
        "runtime",
        "server-resource-packs",
        "temp",
        "tmp",
        "versions",
        "webcache",
        "webcache2",
    };

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.json",
        "client_token.txt",
        "install_profile.json",
        "knownkeys.txt",
        "launcher_accounts.json",
        "launcher_accounts_microsoft_store.json",
        "launcher_cef_log.txt",
        "launcher_log.txt",
        "launcher_profiles.json",
        "launcher_profiles_microsoft_store.json",
        "launcher_settings.json",
        "launcher_ui_state.json",
        "launcher_msa_credentials.bin",
        "PCL.ini",
        "PCL2.ini",
        "realms_persistence.json",
        "session.lock",
        "usercache.json",
        "usernamecache.json",
    };

    private static readonly Dictionary<string, int> KindOrder = new(StringComparer.Ordinal)
    {
        [ClientContentKinds.Mod] = 0,
        [ClientContentKinds.Configuration] = 1,
        [ClientContentKinds.ResourcePack] = 2,
        [ClientContentKinds.ShaderPack] = 3,
        [ClientContentKinds.World] = 4,
        [ClientContentKinds.ModData] = 5,
        [ClientContentKinds.Options] = 6,
        [ClientContentKinds.ServerList] = 7,
        [ClientContentKinds.Screenshot] = 8,
        [ClientContentKinds.Structure] = 9,
        [ClientContentKinds.Replay] = 10,
        [ClientContentKinds.CommandHistory] = 11,
        [ClientContentKinds.Hotbar] = 12,
        [ClientContentKinds.Other] = 13,
    };

    public static async Task<GameDirectoryDiscovery> DiscoverAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var discovery = await GameDirectoryScanner
            .DiscoverAsync(directory, cancellationToken)
            .ConfigureAwait(false);
        if (!discovery.RequiresInstanceDirectory ||
            !HasClientContent(discovery.ContentRoot, cancellationToken))
        {
            return discovery;
        }

        return new GameDirectoryDiscovery
        {
            ContentRoot = discovery.ContentRoot,
            RequiresInstanceDirectory = false,
            VersionCandidates = discovery.VersionCandidates,
        };
    }

    public static async Task<ClientPackSource> ReadAsync(
        string directory,
        ServerVersionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(candidate);
        var discovery = await DiscoverAsync(directory, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => Read(discovery, candidate, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static ClientPackSource Read(
        GameDirectoryDiscovery discovery,
        ServerVersionCandidate requestedCandidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (discovery.RequiresInstanceDirectory)
        {
            throw new InvalidDataException(
                "The selected directory has no client pack content. Select the version-isolated instance directory instead.");
        }

        var candidate = ResolveCandidate(discovery.VersionCandidates, requestedCandidate);
        var root = discovery.ContentRoot;
        EnsureTopLevelPathsAreNotReparsePoints(root, cancellationToken);

        var items = new List<ClientContentEntry>();
        AddMods(root, items, cancellationToken);
        AddTopLevelChildren(root, "resourcepacks", ClientContentKinds.ResourcePack, true, items, cancellationToken);
        AddTopLevelChildren(root, "shaderpacks", ClientContentKinds.ShaderPack, true, items, cancellationToken);
        AddTopLevelChildren(root, "saves", ClientContentKinds.World, false, items, cancellationToken, directoriesOnly: true);

        foreach (var name in ConfigurationDirectories)
        {
            AddRootEntry(root, name, ClientContentKinds.Configuration, true, items, cancellationToken);
        }

        AddMatchingRootDirectories(
            root,
            IsMapAndModDataDirectory,
            ClientContentKinds.ModData,
            false,
            items,
            cancellationToken);
        AddRootEntry(root, "options.txt", ClientContentKinds.Options, true, items, cancellationToken);
        AddMatchingRootFiles(
            root,
            name => name.StartsWith("options", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("options.txt", StringComparison.OrdinalIgnoreCase),
            ClientContentKinds.Configuration,
            true,
            items,
            cancellationToken);
        AddRootEntry(root, "servers.dat", ClientContentKinds.ServerList, false, items, cancellationToken);
        AddRootEntry(root, "servers.dat_old", ClientContentKinds.ServerList, false, items, cancellationToken);
        AddRootEntry(root, "screenshots", ClientContentKinds.Screenshot, false, items, cancellationToken);

        foreach (var name in StructureDirectories)
        {
            AddRootEntry(root, name, ClientContentKinds.Structure, false, items, cancellationToken);
        }
        foreach (var name in ReplayDirectories)
        {
            AddRootEntry(root, name, ClientContentKinds.Replay, false, items, cancellationToken);
        }
        AddRootEntry(root, "command_history.txt", ClientContentKinds.CommandHistory, false, items, cancellationToken);
        AddRootEntry(root, "hotbar.nbt", ClientContentKinds.Hotbar, false, items, cancellationToken);

        AddOtherRootEntries(root, candidate, items, cancellationToken);
        items.Sort(CompareEntries);

        return new ClientPackSource
        {
            SourcePath = root,
            DisplayName = GetDisplayName(root),
            MinecraftVersion = candidate.MinecraftVersion,
            LoaderType = string.IsNullOrWhiteSpace(candidate.LoaderType) ? "vanilla" : candidate.LoaderType,
            LoaderVersion = candidate.LoaderVersion,
            ContentRoot = root,
            Items = items,
        };
    }

    private static bool HasClientContent(string root, CancellationToken cancellationToken)
    {
        foreach (var path in EnumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var attributes = GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isMarker = isDirectory
                ? IsKnownContentDirectory(name)
                : IsKnownContentFile(name);
            if (!isMarker)
            {
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Client content path '{path}' is a symbolic link or reparse point and cannot be read safely.");
            }
            return true;
        }
        return false;
    }

    private static bool IsKnownContentDirectory(string name) =>
        name.Equals("mods", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("saves", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("screenshots", StringComparison.OrdinalIgnoreCase) ||
        ConfigurationDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        IsMapAndModDataDirectory(name) ||
        StructureDirectories.Contains(name) ||
        ReplayDirectories.Contains(name);

    private static bool IsMapAndModDataDirectory(string name) =>
        MapAndModDataDirectories.Contains(name) ||
        MapAndModDataDirectoryPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownContentFile(string name) =>
        name.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("servers.dat", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("servers.dat_old", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("command_history.txt", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("hotbar.nbt", StringComparison.OrdinalIgnoreCase);

    private static void AddMods(
        string root,
        ICollection<ClientContentEntry> items,
        CancellationToken cancellationToken)
    {
        var modsRoot = Path.Combine(root, "mods");
        if (!Directory.Exists(modsRoot))
        {
            return;
        }

        foreach (var file in EnumerateFiles(modsRoot, cancellationToken))
        {
            var disabled = file.RelativePath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
            if (!disabled && !file.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var relativeToRoot = ToRelativePath(root, file.FullPath);
            items.Add(new ClientContentEntry
            {
                Name = Path.GetFileName(file.FullPath),
                RelativePath = relativeToRoot,
                SourcePath = file.FullPath,
                Kind = ClientContentKinds.Mod,
                FileCount = 1,
                TotalBytes = GetFileLength(file.FullPath),
                Selected = !disabled,
                Disabled = disabled,
            });
        }
    }

    private static void AddTopLevelChildren(
        string root,
        string directoryName,
        string kind,
        bool selected,
        ICollection<ClientContentEntry> items,
        CancellationToken cancellationToken,
        bool directoriesOnly = false)
    {
        var directory = Path.Combine(root, directoryName);
        if (!Directory.Exists(directory))
        {
            return;
        }
        EnsureNotReparsePoint(directory);
        foreach (var path in EnumerateEntries(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Client content path '{path}' is a symbolic link or reparse point and cannot be packaged safely.");
            }
            if (directoriesOnly && (attributes & FileAttributes.Directory) == 0)
            {
                continue;
            }
            items.Add(CreateEntry(root, path, kind, selected, cancellationToken));
        }
    }

    private static void AddRootEntry(
        string root,
        string name,
        string kind,
        bool selected,
        ICollection<ClientContentEntry> items,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        if (items.Any(item => item.SourcePath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        items.Add(CreateEntry(root, path, kind, selected, cancellationToken));
    }

    private static void AddMatchingRootFiles(
        string root,
        Func<string, bool> predicate,
        string kind,
        bool selected,
        ICollection<ClientContentEntry> items,
        CancellationToken cancellationToken)
    {
        foreach (var path in EnumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 || !predicate(Path.GetFileName(path)))
            {
                continue;
            }
            AddRootEntry(root, Path.GetFileName(path), kind, selected, items, cancellationToken);
        }
    }

    private static void AddMatchingRootDirectories(
        string root,
        Func<string, bool> predicate,
        string kind,
        bool selected,
        ICollection<ClientContentEntry> items,
        CancellationToken cancellationToken)
    {
        foreach (var path in EnumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || !predicate(Path.GetFileName(path)))
            {
                continue;
            }
            AddRootEntry(root, Path.GetFileName(path), kind, selected, items, cancellationToken);
        }
    }

    private static void AddOtherRootEntries(
        string root,
        ServerVersionCandidate candidate,
        ICollection<ClientContentEntry> items,
        CancellationToken cancellationToken)
    {
        var knownPaths = items
            .Select(item => Path.GetFullPath(item.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var metadataPath = string.IsNullOrWhiteSpace(candidate.MetadataPath)
            ? string.Empty
            : Path.GetFullPath(candidate.MetadataPath);

        foreach (var path in EnumerateEntries(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (knownPaths.Contains(fullPath))
            {
                continue;
            }

            var attributes = GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var name = Path.GetFileName(path);
            if (isDirectory && (IsKnownContentDirectory(name) || IsExcludedDirectory(name)))
            {
                continue;
            }
            if (!isDirectory &&
                (IsKnownContentFile(name) || IsExcludedFile(name, fullPath, metadataPath)))
            {
                continue;
            }

            items.Add(CreateEntry(
                root,
                path,
                ClientContentKinds.Other,
                false,
                cancellationToken));
        }
    }

    private static bool IsExcludedDirectory(string name) =>
        ExcludedDirectories.Contains(name) ||
        name.EndsWith("-natives", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_natives", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedFile(string name, string fullPath, string metadataPath)
    {
        if (ExcludedFiles.Contains(name) ||
            name.StartsWith("launcher_", StringComparison.OrdinalIgnoreCase) ||
            (metadataPath.Length > 0 && fullPath.Equals(metadataPath, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (name.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return Path.GetExtension(name).ToLowerInvariant() is
            ".dll" or ".dylib" or ".exe" or ".jar" or ".lck" or ".lock" or ".log" or ".part" or ".so" or ".tmp";
    }

    private static ClientContentEntry CreateEntry(
        string root,
        string path,
        string kind,
        bool selected,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var attributes = GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Client content path '{fullPath}' is a symbolic link or reparse point and cannot be packaged safely.");
        }
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var (fileCount, totalBytes) = isDirectory
            ? GetDirectoryStatistics(fullPath, cancellationToken)
            : (1, GetFileLength(fullPath));
        return new ClientContentEntry
        {
            Name = Path.GetFileName(fullPath),
            RelativePath = ToRelativePath(root, fullPath),
            SourcePath = fullPath,
            Kind = kind,
            IsDirectory = isDirectory,
            FileCount = fileCount,
            TotalBytes = totalBytes,
            Selected = selected,
        };
    }

    private static (int FileCount, long TotalBytes) GetDirectoryStatistics(
        string directory,
        CancellationToken cancellationToken)
    {
        var files = EnumerateFiles(directory, cancellationToken);
        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes = checked(totalBytes + GetFileLength(file.FullPath));
        }
        return (files.Count, totalBytes);
    }

    private static List<LocalFile> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(root);
        var result = new List<LocalFile>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var path in EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Client content path '{path}' is a symbolic link or reparse point and cannot be packaged safely.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }
                result.Add(new LocalFile(
                    Path.GetFullPath(path),
                    Path.GetRelativePath(root, path).Replace('\\', '/')));
            }
        }
        return result;
    }

    private static void EnsureTopLevelPathsAreNotReparsePoints(
        string root,
        CancellationToken cancellationToken)
    {
        foreach (var path in EnumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Game directory path '{path}' is a symbolic link or reparse point and cannot be packaged safely.");
            }
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Client content path '{path}' is a symbolic link or reparse point and cannot be packaged safely.");
        }
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

    private static int CompareEntries(ClientContentEntry left, ClientContentEntry right)
    {
        var kindComparison = KindOrder.GetValueOrDefault(left.Kind, int.MaxValue)
            .CompareTo(KindOrder.GetValueOrDefault(right.Kind, int.MaxValue));
        return kindComparison != 0
            ? kindComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
    }

    private static string GetDisplayName(string root)
    {
        var directory = new DirectoryInfo(root);
        return directory.Name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase)
            ? directory.Parent?.Name ?? directory.Name
            : directory.Name;
    }

    private static string ToRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static long GetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Could not inspect file '{path}': {exception.Message}", exception);
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

    private sealed record LocalFile(string FullPath, string RelativePath);
}
