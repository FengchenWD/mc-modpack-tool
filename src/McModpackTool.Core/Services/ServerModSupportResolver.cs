using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed class ServerModSupportResolver
{
    private const long MaxConnectorInspectionBytes = 128L * 1024 * 1024;
    private static readonly HttpClient SharedArtifactHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly HashSet<string> KnownClientOnlyIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Maps and waypoints.
        "voxelmap", "minimap", "xaerominimap", "xaerosminimap", "xaerosworldmap",
        "journeymap", "mapwriter", "reiminimap", "reisminimap", "zanminimap", "xaerolib",
        // Renderers, shaders, and purely visual clients.
        "optifine", "iris", "irisshaders", "oculus", "sodium", "embeddium", "rubidium",
        "nvidium", "continuity", "entityculling", "moreculling", "dynamiclights", "shader",
        "shaderloader", "citresewn", "notenoughanimations", "skinlayers3d", "wizoom",
        "cadeditor", "dynamicfps", "splattermod", "watermedia",
        // Inventory, HUD, tooltip, and client-control helpers.
        "jei", "justenoughitems", "rei", "roughlyenoughitems", "emi", "inventorytweaks",
        "inventoryprofilesnext", "mousetweaks", "appleskin", "hwyla", "waila",
        "wailaharvestability", "wthit", "jade", "neat", "overpoweredarmorbar", "betterf3",
        "modmenu", "shulkerboxtooltip", "tooltipfix", "controlling", "lightoverlay", "minihud",
        "litematica", "tweakeroo", "itemscroller", "replaymod", "okzoomer",
        "zoomify", "blur", "customskinloader", "mumblelink", "lanserverproperties",
        "betteradvancements", "notenoughcrashes", "hud",
        "tooltip", "keybind", "keybinding", "crosshair", "overlay",
        // Client bootstrap and UI libraries whose platform-side metadata is incomplete.
        "mcwifipnp", "fancymenu", "konkrete", "melody", "presencefootsteps",
        "soundphysicsremastered", "mcef", "auudio", "ambientsounds", "drippyloadingscreen",
        "probejs", "netmusic", "jecharacters", "ctm", "connectedtexturesmod", "inventoryprofilesnext",
        "particlerain", "sodiumdynamiclights", "entitymodelfeatures",
        "entitytexturefeatures", "tpshooting", "embeddiumextra", "immediatelyfast",
        "shouldersurfing", "i18nupdatemod", "myserveriscompatible", "handheldmoon",
        "conditionalvideos", "epicdeathscreen", "watermediabinaries",
        // Common Chinese labels used by launcher-exported packs.
        "体素地图", "小地图", "世界地图", "光影", "鼠标手势", "苹果皮", "血条显示",
        "挖掘显示", "物品管理器", "r键整理", "万用皮肤补丁", "自定义局域网联机",
    };

    private static readonly HashSet<string> KnownClientOnlyProjectIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "RTWpcTBp", // MC WiFi PnP
        "Wq5SjeWM", // FancyMenu
        "J81TRJWm", // Konkrete
        "CVT4pFB2", // Melody
        "rcTfTZr3", // Presence Footsteps
        "qyVF9oeo", // Sound Physics Remastered
        "ctm",      // ConnectedTexturesMod loader ID
        "TObQ0HxZ", // MCEF
        "O7RBXm3n", // Inventory Profiles Next
        "8shC1gFX", // BetterF3
        "Nv2fQJo5", // ReplayMod
        "nrikgvxm", // Particle Rain
        "PxQSWIcD", // Sodium Dynamic Lights
        "I7k4B65h", // JECharacters
        "FCr31KmZ", // Auudio
        "4I1XuqiY", // Entity Model Features
        "BVzZfTc1", // Entity Texture Features
        "aC3cM3Vq", // Mouse Tweaks
        "yl6ylodU", // TP Shooting
        "oY2B1pjg", // Embeddium Extra
        "sk9rgfiA", // Embeddium
        "5ZwdcRci", // ImmediatelyFast
        "GchcoXML", // Oculus
        "kepjj2sy", // Shoulder Surfing
        "v3CYg2V9", // Drippy Loading Screen
        "PWERr14M", // I18n Update Mod
        "fM515JnW", // AmbientSounds
        "13qq15Cg", // My Server Is Compatible
        "QYxLd8Ry", // ConditionalVideos
        "NBq5BYpp", // Epic Death Screen
        "4997XcoK", // WaterMedia Binaries
        "MJW1h2CF", // CAD Editor
        "LQ3K71Q1", // Dynamic FPS
        "M93DtNU5", // Immersive Blood Splatters
        "G922NeHS", // WaterMedia
    };

    private static readonly string[] KnownClientOnlyNormalizedFragments =
    [
        "wizoom",
    ];

    private static readonly HashSet<string> KnownOptionalClientIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "jei", "justenoughitems", "jade", "appleskin", "clothconfig", "clothconfigapi",
        "yacl", "yetanotherconfiglib", "yetanotherconfiglibv3",
        // A library's client entrypoint alone does not make it unsafe on a server.
        // Explicit environment declarations still take precedence over this fallback.
        "libipn", "malilib",
    };

    private static readonly HashSet<string> KnownOptionalClientProjectIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "u6dRKJwZ", // Just Enough Items
        "nvQzSEkH", // Jade
        "EsAfCjCV", // AppleSkin
        "9s6osm5g", // Cloth Config API
        "1eAoo2KR", // YetAnotherConfigLib
    };

    private static readonly HashSet<string> KnownServerFeatureModIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "voicechat",
        "9eGKb6K1",
        "simple-voice-chat",
    };

    private static readonly HashSet<string> KnownServerLibraryIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric-api",
        "P7dR8mSH",
        "fabric-language-kotlin",
        "Ha28R6CL",
    };

    private readonly ModrinthClient _modrinth;
    private readonly CurseForgeClient? _curseForge;
    private readonly HttpClient _artifactHttpClient;
    private readonly Action<string>? _logWarning;
    private readonly ConditionalWeakTable<ServerPackSource, Dictionary<ServerModEntry, ModEvidence>> _resolvedEvidence = new();

    public ServerModSupportResolver(
        ModrinthClient modrinth,
        Action<string>? logWarning = null,
        HttpClient? artifactHttpClient = null,
        CurseForgeClient? curseForge = null)
    {
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
        _curseForge = curseForge;
        _logWarning = logWarning;
        _artifactHttpClient = artifactHttpClient ?? SharedArtifactHttpClient;
    }

    public async Task ResolveAsync(
        ServerPackSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var evidence = new Dictionary<ServerModEntry, ModEvidence>();
        var hashOwners = new Dictionary<string, List<ServerModEntry>>(StringComparer.OrdinalIgnoreCase);
        var projectIds = new Dictionary<ServerModEntry, string>();

        foreach (var entry in source.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArtifactCompatibilityMetadata? metadata = null;
            if (!entry.Disabled && entry.SourcePath.Length > 0 && File.Exists(entry.SourcePath))
            {
                try
                {
                    metadata = ArtifactMetadataReader.Read(
                        entry.SourcePath,
                        cancellationToken: cancellationToken);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    _logWarning?.Invoke($"Could not inspect '{entry.Name}': {exception.Message}");
                }
            }

            bool needsPlatformEvidence = !entry.Disabled && !HasDefinitiveSide(entry, metadata);
            string sha1 = needsPlatformEvidence ? GetDeclaredSha1(entry) : string.Empty;
            if (sha1.Length == 0 && needsPlatformEvidence &&
                entry.SourcePath.Length > 0 && File.Exists(entry.SourcePath))
            {
                try
                {
                    sha1 = await ComputeSha1Async(entry.SourcePath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logWarning?.Invoke($"Could not hash '{entry.Name}': {exception.Message}");
                }
            }
            if (sha1.Length > 0)
            {
                if (!hashOwners.TryGetValue(sha1, out var owners))
                {
                    owners = [];
                    hashOwners[sha1] = owners;
                }
                owners.Add(entry);
            }

            string directProjectId = needsPlatformEvidence
                ? GetDirectModrinthProjectId(entry)
                : string.Empty;
            if (directProjectId.Length > 0)
            {
                projectIds[entry] = directProjectId;
            }
            evidence[entry] = new ModEvidence(metadata);
        }

        await ResolveCurseForgeEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);

        if (hashOwners.Count > 0)
        {
            try
            {
                var versions = await _modrinth.LookupByHashesAsync(
                    hashOwners.Keys,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var (hash, version) in versions)
                {
                    if (version.ProjectId.Length == 0 || !hashOwners.TryGetValue(hash, out var owners))
                    {
                        continue;
                    }
                    foreach (var owner in owners)
                    {
                        projectIds[owner] = version.ProjectId;
                        evidence[owner].Version = version;
                        if (owner.ContentItem is not null && version.Dependencies is not null)
                        {
                            MergeDependencies(
                                owner.ContentItem,
                                version.Dependencies.Select(DependencyReference.FromModrinth));
                            owner.ContentItem.DependencyMetadataAvailable = true;
                        }
                    }
                }
            }
            catch (PlatformApiException exception)
            {
                _logWarning?.Invoke($"Could not identify server-side metadata by hash: {exception.Message}");
            }
        }

        if (projectIds.Count > 0)
        {
            try
            {
                var projects = await _modrinth.GetProjectsByIdsAsync(
                    projectIds.Values,
                    cancellationToken).ConfigureAwait(false);
                foreach (var (entry, projectId) in projectIds)
                {
                    if (projects.TryGetValue(projectId, out var project))
                    {
                        evidence[entry].Project = project;
                        if (entry.ContentItem is not null && project.Slug.Length > 0)
                        {
                            entry.ContentItem.ModrinthSlug = project.Slug;
                        }
                    }
                }
            }
            catch (PlatformApiException exception)
            {
                _logWarning?.Invoke($"Could not read server-side project metadata: {exception.Message}");
            }
        }

        await InspectConnectorCandidatesAsync(source, evidence, cancellationToken).ConfigureAwait(false);

        foreach (var entry in source.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.JavaVersionRequirements.Clear();
            if (evidence[entry].Metadata is { } metadata)
            {
                entry.JavaVersionRequirements.AddRange(metadata.Relations
                    .Where(relation => relation.Kind == CompatibilityRelationKinds.Required &&
                                       relation.NormalizedReference == "java")
                    .Select(relation => relation.VersionRequirement.Trim())
                    .Where(requirement => requirement.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            }
            var classification = Classify(entry, evidence[entry]);
            entry.ServerSupport = classification.Support;
            entry.SupportReason = classification.Reason;
            entry.Selected = !entry.Disabled && classification.Support is not (
                ServerSupportKinds.Optional or ServerSupportKinds.Unsupported);
            if (entry.ContentItem is not null)
            {
                entry.ContentItem.Excluded = !entry.Selected;
            }
        }

        PromoteRequiredDependencies(source.Mods, evidence);
        _resolvedEvidence.Remove(source);
        _resolvedEvidence.Add(source, evidence);
    }

    /// <summary>Rechecks the user's current selection without repeating network requests or resetting choices.</summary>
    public void RefreshSelectedDependencies(ServerPackSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_resolvedEvidence.TryGetValue(source, out var evidence))
        {
            PromoteRequiredDependencies(source.Mods, evidence);
        }
    }

    private async Task ResolveCurseForgeEvidenceAsync(
        IReadOnlyDictionary<ServerModEntry, ModEvidence> evidence,
        CancellationToken cancellationToken)
    {
        if (_curseForge is null || _curseForge.ApiKey.Length == 0)
        {
            return;
        }

        var entriesByFileId = new Dictionary<long, List<ServerModEntry>>();
        foreach (ServerModEntry entry in evidence.Keys)
        {
            if (entry.Disabled || entry.ContentItem is not { } item ||
                !long.TryParse(item.FileId, NumberStyles.None, CultureInfo.InvariantCulture, out long fileId) ||
                fileId <= 0)
            {
                continue;
            }

            if (!entriesByFileId.TryGetValue(fileId, out List<ServerModEntry>? entries))
            {
                entries = [];
                entriesByFileId[fileId] = entries;
            }
            entries.Add(entry);
        }
        if (entriesByFileId.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<long, CurseForgeFile> files;
        try
        {
            files = await _curseForge.GetFilesByIdsAsync(entriesByFileId.Keys, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PlatformApiException exception)
        {
            _logWarning?.Invoke($"Could not read exact CurseForge file metadata: {exception.Message}");
            return;
        }

        var verified = new List<(ServerModEntry Entry, CurseForgeFile File)>();
        foreach ((long fileId, List<ServerModEntry> entries) in entriesByFileId)
        {
            if (!files.TryGetValue(fileId, out CurseForgeFile? file) ||
                file.Id != fileId || file.ModId <= 0)
            {
                _logWarning?.Invoke($"CurseForge did not return exact file metadata for file {fileId}.");
                continue;
            }

            foreach (ServerModEntry entry in entries)
            {
                if (MatchesExactCurseForgeFile(entry.ContentItem!, file, out string reason))
                {
                    verified.Add((entry, file));
                }
                else
                {
                    _logWarning?.Invoke($"Ignored mismatched CurseForge identity for '{entry.Name}': {reason}");
                }
            }
        }
        if (verified.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<long, CurseForgeProject> projects;
        try
        {
            projects = await _curseForge.GetProjectsByIdsAsync(
                    verified.Select(candidate => candidate.File.ModId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PlatformApiException exception)
        {
            _logWarning?.Invoke($"Could not read exact CurseForge project metadata: {exception.Message}");
            return;
        }

        foreach ((ServerModEntry entry, CurseForgeFile file) in verified)
        {
            if (!projects.TryGetValue(file.ModId, out CurseForgeProject? project) ||
                project.Id != file.ModId || project.ClassId != 6)
            {
                _logWarning?.Invoke(
                    $"Ignored invalid CurseForge project metadata for exact file {file.Id} ('{entry.Name}').");
                continue;
            }

            ModEvidence itemEvidence = evidence[entry];
            itemEvidence.CurseForgeFile = file;
            itemEvidence.CurseForgeProject = project;
            EnrichCurseForgeIdentity(entry.ContentItem!, file, project);
        }
    }

    private static bool MatchesExactCurseForgeFile(
        ContentItem item,
        CurseForgeFile file,
        out string reason)
    {
        if (item.FileSize > 0 && file.FileLength > 0 && item.FileSize != file.FileLength)
        {
            reason = $"declared size {item.FileSize} differs from exact file size {file.FileLength}";
            return false;
        }

        IReadOnlyDictionary<string, string> exactHashes = SearchMatcher.ExtractCurseForgeHashes(file);
        foreach ((string algorithm, string declaredHash) in item.Hashes)
        {
            if (exactHashes.TryGetValue(algorithm, out string? exactHash) &&
                !declaredHash.Trim().Equals(exactHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                reason = $"declared {algorithm} hash differs from the exact file hash";
                return false;
            }
        }

        if (item.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(item.ProjectId, NumberStyles.None, CultureInfo.InvariantCulture, out long projectId) &&
            projectId > 0 && projectId != file.ModId)
        {
            reason = $"declared project {projectId} differs from exact file project {file.ModId}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static void EnrichCurseForgeIdentity(
        ContentItem item,
        CurseForgeFile file,
        CurseForgeProject project)
    {
        string projectId = project.Id.ToString(CultureInfo.InvariantCulture);
        item.CurseForgeSlug = project.Slug.Trim();
        if (item.ProjectId.Length == 0 ||
            item.Source.Length == 0 ||
            item.Source.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            item.ProjectId = projectId;
            item.Source = "curseforge";
            item.IdentityLocked = true;
        }
        if (item.OriginalProjectId.Length == 0)
        {
            item.OriginalProjectId = projectId;
        }
        if (item.OriginalSource.Length == 0 ||
            item.OriginalSource.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            item.OriginalSource = "curseforge";
        }

        MergeDependencies(
            item,
            (file.Dependencies ?? [])
                .Where(dependency => dependency.ModId > 0)
                .Select(DependencyReference.FromCurseForge));
        item.DependencyMetadataAvailable = file.Dependencies is not null;
    }

    private static void MergeDependencies(
        ContentItem item,
        IEnumerable<DependencyReference> dependencies)
    {
        foreach (DependencyReference dependency in dependencies)
        {
            bool exists = item.TargetDependencies.Any(existing =>
                existing.Source.Equals(dependency.Source, StringComparison.OrdinalIgnoreCase) &&
                existing.ProjectId.Equals(dependency.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                existing.VersionId.Equals(dependency.VersionId, StringComparison.OrdinalIgnoreCase) &&
                existing.FileName.Equals(dependency.FileName, StringComparison.OrdinalIgnoreCase) &&
                existing.DependencyType.Equals(dependency.DependencyType, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                item.TargetDependencies.Add(dependency);
            }
        }
    }

    private async Task InspectConnectorCandidatesAsync(
        ServerPackSource source,
        IReadOnlyDictionary<ServerModEntry, ModEvidence> evidence,
        CancellationToken cancellationToken)
    {
        string sourceLoader = SearchMatcher.NormalizeLoaderName(source.LoaderType);
        if (sourceLoader is not ("forge" or "neoforge"))
        {
            return;
        }

        var candidates = source.Mods
            .Where(entry => ShouldInspectConnectorCandidate(entry, evidence[entry]))
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        string inspectionRoot = Path.Combine(
            Path.GetTempPath(),
            $"mc-modpack-tool-connector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(inspectionRoot);
        try
        {
            using var gate = new SemaphoreSlim(4);
            var tasks = candidates.Select(async (entry, index) =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ArtifactCompatibilityMetadata? metadata = await InspectRemoteArtifactAsync(
                        entry,
                        evidence[entry].Version,
                        inspectionRoot,
                        index,
                        cancellationToken).ConfigureAwait(false);
                    return (Entry: entry, Metadata: metadata);
                }
                finally
                {
                    gate.Release();
                }
            });

            foreach (var result in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                if (result.Metadata is not null)
                {
                    evidence[result.Entry].Metadata = result.Metadata;
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(inspectionRoot, recursive: true);
            }
            catch (IOException)
            {
                // A failed temporary cleanup must not invalidate otherwise usable analysis results.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed temporary cleanup must not invalidate otherwise usable analysis results.
            }
        }
    }

    private static bool ShouldInspectConnectorCandidate(ServerModEntry entry, ModEvidence evidence)
    {
        if (entry.Disabled || entry.SourcePath.Length > 0 && File.Exists(entry.SourcePath) ||
            entry.ContentItem is null)
        {
            return false;
        }

        if (evidence.Version?.Loaders is { Count: > 0 } loaders)
        {
            bool fabricFamily = loaders.Any(loader =>
                SearchMatcher.NormalizeLoaderName(loader) is "fabric" or "quilt");
            bool forgeFamily = loaders.Any(loader =>
                SearchMatcher.NormalizeLoaderName(loader) is "forge" or "neoforge");
            return fabricFamily && !forgeFamily;
        }

        string fileName = FirstNonEmpty(
            entry.ContentItem.FileName,
            entry.RelativePath,
            entry.Name).ToLowerInvariant();
        return fileName.Contains("-fabric", StringComparison.Ordinal) ||
               fileName.Contains("_fabric", StringComparison.Ordinal) ||
               fileName.Contains(" fabric", StringComparison.Ordinal) ||
               fileName.Contains("-quilt", StringComparison.Ordinal) ||
               fileName.Contains("_quilt", StringComparison.Ordinal) ||
               fileName.Contains(" quilt", StringComparison.Ordinal);
    }

    private async Task<ArtifactCompatibilityMetadata?> InspectRemoteArtifactAsync(
        ServerModEntry entry,
        ModrinthVersion? version,
        string inspectionRoot,
        int index,
        CancellationToken cancellationToken)
    {
        ContentItem item = entry.ContentItem!;
        ModrinthFile? exactFile = FindExactVersionFile(item, version);
        string[] urls = item.DownloadUrls
            .Prepend(item.DownloadUrl)
            .Append(exactFile?.Url ?? string.Empty)
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
                          uri.Scheme == Uri.UriSchemeHttps)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (urls.Length == 0)
        {
            return null;
        }

        long expectedSize = item.FileSize > 0 ? item.FileSize : exactFile?.Size ?? 0;
        IReadOnlyDictionary<string, string> expectedHashes = item.Hashes.Count > 0
            ? item.Hashes
            : exactFile?.Hashes ?? new Dictionary<string, string>();
        if (expectedSize > MaxConnectorInspectionBytes)
        {
            _logWarning?.Invoke($"Skipped oversized Connector candidate '{entry.Name}'.");
            return null;
        }

        string fileName = $"candidate-{index:D4}.jar";
        var options = new ArchiveSafetyOptions
        {
            MaxDownloadBytes = MaxConnectorInspectionBytes,
            CopyBufferBytes = 128 * 1024,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        foreach (string url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool downloaded = await ArchiveSafety.DownloadFileAsync(
                    _artifactHttpClient,
                    url,
                    inspectionRoot,
                    fileName,
                    expectedSize: expectedSize,
                    expectedHashes: expectedHashes,
                    options: options,
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                if (!downloaded)
                {
                    continue;
                }

                string path = Path.Combine(inspectionRoot, fileName);
                return ArtifactMetadataReader.Read(path, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logWarning?.Invoke($"Timed out while inspecting Connector candidate '{entry.Name}'.");
                return null;
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _logWarning?.Invoke($"Could not inspect Connector candidate '{entry.Name}': {exception.Message}");
            }
        }
        return null;
    }

    private static ModrinthFile? FindExactVersionFile(ContentItem item, ModrinthVersion? version)
    {
        if (version is null)
        {
            return null;
        }

        ModrinthFile? exact = version.Files.FirstOrDefault(file => item.Hashes.Any(pair =>
            file.Hashes.TryGetValue(pair.Key, out string? value) &&
            value.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)));
        return exact ?? version.Files.FirstOrDefault(file => file.Primary == true)
            ?? version.Files.FirstOrDefault();
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static Classification Classify(ServerModEntry entry, ModEvidence evidence)
    {
        if (entry.Disabled)
        {
            return new Classification(ServerSupportKinds.Unknown, "This mod is disabled.");
        }

        string manifestServer = entry.ContentItem?.Environment.GetValueOrDefault("server", string.Empty)
            .Trim().ToLowerInvariant() ?? string.Empty;
        string environment = evidence.Metadata?.ServerEnvironment.Trim().ToLowerInvariant() ?? string.Empty;
        string platformSide = evidence.Project?.ServerSide.Trim().ToLowerInvariant() ?? string.Empty;
        if (environment == "client")
        {
            return new Classification(
                ServerSupportKinds.Unsupported,
                "Loader metadata declares this mod as client-only.");
        }
        if (manifestServer == "unsupported")
        {
            return new Classification(
                ServerSupportKinds.Unsupported,
                "The modpack manifest declares this mod unsupported on a server.");
        }
        if (platformSide == "unsupported")
        {
            return new Classification(
                ServerSupportKinds.Unsupported,
                "The exact Modrinth project marks this mod unsupported on a server.");
        }
        if (evidence.Metadata?.HasUnsafeClientReferencesInCommonEntrypoint == true)
        {
            return new Classification(
                ServerSupportKinds.Unsupported,
                "The Fabric common entrypoint directly references client-only APIs.");
        }
        if (environment is "server" or "dedicated_server")
        {
            return new Classification(
                ServerSupportKinds.Recommended,
                "Loader metadata declares a dedicated-server mod.");
        }
        if (evidence.Metadata?.HasServerEntrypoint == true)
        {
            return new Classification(
                ServerSupportKinds.Recommended,
                "Loader metadata declares a dedicated-server entrypoint.");
        }
        if (IsKnownOptionalClient(entry, evidence))
        {
            return new Classification(
                ServerSupportKinds.Optional,
                "This client convenience or configuration mod can be omitted from the server.");
        }
        if (IsKnownServerLibrary(entry, evidence))
        {
            return new Classification(
                ServerSupportKinds.Recommended,
                "This loader library is required for the server mod environment.");
        }
        if (evidence.Metadata is
            {
                HasClientEntrypoint: true,
                HasCommonEntrypoint: false,
                HasServerEntrypoint: false,
            })
        {
            return new Classification(
                ServerSupportKinds.Unsupported,
                "This mod exposes only client entrypoints and cannot initialize on a dedicated server.");
        }
        if (IsKnownClientOnly(entry, evidence))
        {
            return new Classification(
                ServerSupportKinds.Unsupported,
                "The mod is identified as a client-only map, interface, shader, sound, or visual helper.");
        }
        if (IsKnownServerFeature(entry, evidence))
        {
            return new Classification(
                ServerSupportKinds.Recommended,
                "This mod provides a known server feature and should be included on the server.");
        }
        if (manifestServer == "required")
        {
            return new Classification(
                ServerSupportKinds.Recommended,
                "The modpack manifest requires this mod on a server.");
        }
        if (platformSide == "required")
        {
            return new Classification(
                ServerSupportKinds.Recommended,
                "The exact Modrinth project marks this mod as required on a server.");
        }
        if (platformSide == "optional")
        {
            return new Classification(
                ServerSupportKinds.Optional,
                "The exact Modrinth project marks this mod as optional on a server.");
        }
        if (manifestServer == "optional")
        {
            return new Classification(
                ServerSupportKinds.Optional,
                "The modpack manifest declares this mod optional on a server.");
        }
        if (evidence.Project is not null)
        {
            return new Classification(
                ServerSupportKinds.Optional,
                "The exact public project does not declare a dedicated-server role, so it is excluded by default.");
        }
        return new Classification(
            ServerSupportKinds.Unknown,
            evidence.Metadata?.MetadataFound == true
                ? "The mod can be inspected, but its dedicated-server role is not declared. It is included by default."
                : "No exact public metadata was found. The local mod is included by default.");
    }

    private static void PromoteRequiredDependencies(
        IEnumerable<ServerModEntry> entries,
        IReadOnlyDictionary<ServerModEntry, ModEvidence> evidence)
    {
        var materialized = entries.ToList();
        var byId = new Dictionary<string, ServerModEntry>(StringComparer.OrdinalIgnoreCase);
        var byProjectId = new Dictionary<string, ServerModEntry>(StringComparer.OrdinalIgnoreCase);
        var byVersionId = new Dictionary<string, ServerModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in materialized)
        {
            var metadata = evidence[entry].Metadata;
            if (metadata is not null)
            {
                foreach (var id in metadata.ModIds.Concat(metadata.Aliases).Append(metadata.Id)
                             .Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    byId.TryAdd(id, entry);
                }
            }
            if (evidence[entry].Project is { } project)
            {
                byProjectId.TryAdd(project.EffectiveId, entry);
            }
            if (evidence[entry].CurseForgeProject is { } curseForgeProject)
            {
                byProjectId.TryAdd(
                    curseForgeProject.Id.ToString(CultureInfo.InvariantCulture),
                    entry);
            }
            if (evidence[entry].Version is { } version)
            {
                byProjectId.TryAdd(version.ProjectId, entry);
                if (version.Id.Length > 0)
                {
                    byVersionId.TryAdd(version.Id, entry);
                }
            }
            if (entry.ContentItem is { } item)
            {
                if (item.ProjectId.Length > 0)
                {
                    byProjectId.TryAdd(item.ProjectId, entry);
                }
                if (item.VersionId.Length > 0)
                {
                    byVersionId.TryAdd(item.VersionId, entry);
                }
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var owner in materialized.Where(entry => entry.Selected && !entry.Disabled))
            {
                var metadata = evidence[owner].Metadata;
                if (metadata is not null)
                {
                    foreach (var relation in metadata.Relations.Where(IsServerRequiredModRelation))
                    {
                        string reference = relation.ExactReference.Length > 0
                            ? relation.ExactReference
                            : relation.Reference;
                        if (byId.TryGetValue(reference, out var dependency))
                        {
                            changed |= ApplyRequiredDependency(
                                dependency,
                                owner,
                                evidence[dependency],
                                evidence[owner]);
                            if (!owner.Selected)
                            {
                                break;
                            }
                        }
                    }
                }

                if (!owner.Selected)
                {
                    continue;
                }

                foreach (var dependencyReference in GetRequiredDependencies(owner, evidence[owner]))
                {
                    ServerModEntry? dependency = null;
                    if (dependencyReference.VersionId.Length > 0)
                    {
                        byVersionId.TryGetValue(dependencyReference.VersionId, out dependency);
                    }
                    if (dependency is null && dependencyReference.ProjectId.Length > 0)
                    {
                        byProjectId.TryGetValue(dependencyReference.ProjectId, out dependency);
                    }
                    if (dependency is not null)
                    {
                        changed |= ApplyRequiredDependency(
                            dependency,
                            owner,
                            evidence[dependency],
                            evidence[owner]);
                        if (!owner.Selected)
                        {
                            break;
                        }
                    }
                }
            }
        } while (changed);
    }

    private static bool IsServerRequiredModRelation(CompatibilityRelation relation) =>
        relation.Kind == CompatibilityRelationKinds.Required &&
        relation.ReferenceType == CompatibilityReferenceTypes.ModId &&
        !relation.Side.Equals("client", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<DependencyReference> GetRequiredDependencies(
        ServerModEntry entry,
        ModEvidence evidence)
    {
        IEnumerable<DependencyReference> versionDependencies = evidence.Version?.Dependencies is { } dependencies
            ? dependencies.Select(DependencyReference.FromModrinth)
            : [];
        IEnumerable<DependencyReference> itemDependencies = entry.ContentItem?.TargetDependencies ?? [];
        return versionDependencies.Concat(itemDependencies)
            .Where(dependency => dependency.DependencyType.Equals("required", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ApplyRequiredDependency(
        ServerModEntry dependency,
        ServerModEntry owner,
        ModEvidence dependencyEvidence,
        ModEvidence ownerEvidence)
    {
        if (dependency.ServerSupport == ServerSupportKinds.Unsupported &&
            HasHardServerIncompatibility(dependency, dependencyEvidence))
        {
            return BlockOwnerForDependency(owner, dependency, ownerEvidence);
        }
        if (dependency.Disabled ||
            (dependency.ServerSupport == ServerSupportKinds.Recommended && dependency.Selected))
        {
            return false;
        }
        dependency.ServerSupport = ServerSupportKinds.Recommended;
        dependency.SupportReason = $"Required by selected mod '{owner.Name}'.";
        dependency.Selected = true;
        if (dependency.ContentItem is not null)
        {
            dependency.ContentItem.Excluded = false;
        }
        return true;
    }

    private static bool BlockOwnerForDependency(
        ServerModEntry owner,
        ServerModEntry dependency,
        ModEvidence ownerEvidence)
    {
        if (!owner.Selected && owner.ServerSupport == ServerSupportKinds.Unsupported)
        {
            return false;
        }

        ownerEvidence.BlockedByUnsupportedDependency = true;
        owner.ServerSupport = ServerSupportKinds.Unsupported;
        owner.SupportReason =
            $"Required server dependency '{dependency.Name}' is not supported on a dedicated server.";
        owner.Selected = false;
        if (owner.ContentItem is not null)
        {
            owner.ContentItem.Excluded = true;
        }
        return true;
    }

    private static string GetDeclaredSha1(ServerModEntry entry)
    {
        if (entry.ContentItem?.Hashes.TryGetValue("sha1", out var hash) == true)
        {
            return hash.Trim().ToLowerInvariant();
        }
        return string.Empty;
    }

    private static bool IsKnownServerFeature(ServerModEntry entry, ModEvidence evidence) =>
        GetPrimaryIdentityValues(entry, evidence).Any(KnownServerFeatureModIds.Contains);

    private static bool IsKnownServerLibrary(ServerModEntry entry, ModEvidence evidence) =>
        GetPrimaryIdentityValues(entry, evidence).Any(KnownServerLibraryIds.Contains);

    private static bool IsKnownOptionalClient(ServerModEntry entry, ModEvidence evidence)
    {
        foreach (string value in GetPrimaryIdentityValues(entry, evidence))
        {
            if (KnownOptionalClientProjectIds.Contains(value.Trim()) ||
                KnownOptionalClientIdentifiers.Contains(NormalizeIdentity(value)) ||
                TokenizeIdentity(value).Any(KnownOptionalClientIdentifiers.Contains))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasHardServerIncompatibility(ServerModEntry entry, ModEvidence evidence)
    {
        string manifestServer = entry.ContentItem?.Environment.GetValueOrDefault("server", string.Empty)
            .Trim().ToLowerInvariant() ?? string.Empty;
        string environment = evidence.Metadata?.ServerEnvironment.Trim().ToLowerInvariant() ?? string.Empty;
        string platformSide = evidence.Project?.ServerSide.Trim().ToLowerInvariant() ?? string.Empty;
        return evidence.BlockedByUnsupportedDependency ||
               environment == "client" ||
               manifestServer == "unsupported" ||
               platformSide == "unsupported" ||
               evidence.Metadata?.HasUnsafeClientReferencesInCommonEntrypoint == true ||
               IsKnownClientOnly(entry, evidence);
    }

    private static bool IsKnownClientOnly(ServerModEntry entry, ModEvidence evidence)
    {
        foreach (string value in GetPrimaryIdentityValues(entry, evidence))
        {
            string normalized = NormalizeIdentity(value);
            if (KnownClientOnlyProjectIds.Contains(value.Trim()) ||
                KnownClientOnlyIdentifiers.Contains(normalized) ||
                KnownClientOnlyNormalizedFragments.Any(fragment =>
                    normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase)) ||
                KnownClientOnlyIdentifiers.Any(identifier =>
                    identifier.Length >= 8 && normalized.Contains(identifier, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (TokenizeIdentity(value).Any(KnownClientOnlyIdentifiers.Contains))
            {
                return true;
            }
            string text = value.Trim().ToLowerInvariant();
            if (text.Contains("client-only", StringComparison.Ordinal) ||
                text.Contains("client only", StringComparison.Ordinal) ||
                text.Contains("client-side only", StringComparison.Ordinal) ||
                text.Contains("client side only", StringComparison.Ordinal) ||
                text.Contains("仅客户端", StringComparison.Ordinal) ||
                text.Contains("僅客戶端", StringComparison.Ordinal))
            {
                return true;
            }
        }
        string description = evidence.Metadata?.Description.Trim().ToLowerInvariant() ?? string.Empty;
        if (description.Contains("client-only", StringComparison.Ordinal) ||
            description.Contains("client only", StringComparison.Ordinal) ||
            description.Contains("client-side only", StringComparison.Ordinal) ||
            description.Contains("client side only", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static string NormalizeIdentity(string value)
    {
        var normalized = new System.Text.StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }
        return normalized.ToString();
    }

    private static IEnumerable<string> TokenizeIdentity(string value)
    {
        var token = new System.Text.StringBuilder();
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }
            if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }
        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private static IEnumerable<string> GetPrimaryIdentityValues(ServerModEntry entry, ModEvidence evidence)
    {
        if (evidence.Metadata is { } metadata)
        {
            if (metadata.Id.Length > 0)
            {
                yield return metadata.Id;
            }
            yield return metadata.Name;
        }
        if (evidence.Project is { } project)
        {
            yield return project.EffectiveId;
            yield return project.Slug;
            yield return project.Title;
        }
        if (evidence.CurseForgeProject is { } curseForgeProject)
        {
            yield return curseForgeProject.Id.ToString(CultureInfo.InvariantCulture);
            yield return curseForgeProject.Slug;
            yield return curseForgeProject.Name;
        }
        if (evidence.Version is { } version)
        {
            yield return version.ProjectId;
            yield return version.Name;
        }
        if (entry.ContentItem is { } item)
        {
            yield return item.ProjectId;
            yield return item.ModrinthSlug;
            yield return item.CurseForgeSlug;
            yield return item.Name;
            yield return item.FileName;
        }
        yield return entry.Name;
        yield return entry.RelativePath;
    }

    private static bool HasDefinitiveSide(
        ServerModEntry entry,
        ArtifactCompatibilityMetadata? metadata)
    {
        string manifestServer = entry.ContentItem?.Environment.GetValueOrDefault("server", string.Empty)
            .Trim().ToLowerInvariant() ?? string.Empty;
        if (manifestServer == "unsupported")
        {
            return true;
        }
        string environment = metadata?.ServerEnvironment.Trim().ToLowerInvariant() ?? string.Empty;
        return environment is "client" or "server" or "dedicated_server";
    }

    private static string GetDirectModrinthProjectId(ServerModEntry entry)
    {
        var item = entry.ContentItem;
        if (item is null || item.ProjectId.Length == 0)
        {
            return string.Empty;
        }
        return item.Source.Equals("modrinth", StringComparison.OrdinalIgnoreCase) ||
               item.OriginalSource.Equals("modrinth", StringComparison.OrdinalIgnoreCase)
            ? item.ProjectId
            : string.Empty;
    }

    private static async Task<string> ComputeSha1Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha1 = SHA1.Create();
        byte[] hash = await sha1.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private sealed class ModEvidence(ArtifactCompatibilityMetadata? metadata)
    {
        public ArtifactCompatibilityMetadata? Metadata { get; set; } = metadata;
        public ModrinthVersion? Version { get; set; }
        public ModrinthProject? Project { get; set; }
        public CurseForgeFile? CurseForgeFile { get; set; }
        public CurseForgeProject? CurseForgeProject { get; set; }
        public bool BlockedByUnsupportedDependency { get; set; }
    }

    private readonly record struct Classification(string Support, string Reason);
}
