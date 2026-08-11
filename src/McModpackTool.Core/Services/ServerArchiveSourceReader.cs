using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed class ServerArchiveSourceReader
{
    private static readonly HashSet<string> ExtractedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "defaultconfigs", "kubejs", "scripts", "saves",
    };

    private static readonly string[] OptionalRoots =
    [
        "config", "defaultconfigs", "kubejs", "scripts",
    ];

    private readonly CurseForgeClient _curseForge;

    public ServerArchiveSourceReader(CurseForgeClient curseForge)
    {
        _curseForge = curseForge ?? throw new ArgumentNullException(nameof(curseForge));
    }

    public async Task<ServerPackSource> ReadAsync(
        string archivePath,
        string temporaryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePath = Path.GetFullPath(archivePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到整合包文件。", sourcePath);
        }

        var format = await PackParser.DetectFormatAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        if (format == PackArchiveFormat.Unknown)
        {
            throw new InvalidDataException("仅支持标准 CurseForge 或 Modrinth 整合包。");
        }

        var safetyOptions = format == PackArchiveFormat.Modrinth
            ? new ArchiveSafetyOptions
            {
                AllowServerOverrides = true,
                IgnoreClientOverrides = true,
            }
            : ArchiveSafetyOptions.Default;
        var pack = await PackParser.ParseAsync(sourcePath, safetyOptions, cancellationToken)
            .ConfigureAwait(false);
        if (pack.LoaderType.Equals("quilt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("服务端一键打包不支持 Quilt 整合包。");
        }

        if (format == PackArchiveFormat.CurseForge)
        {
            await RetainCurseForgeModsAsync(pack, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            RetainModrinthMods(pack);
        }

        var tempRoot = Path.GetFullPath(temporaryRoot);
        var contentRoot = Path.Combine(tempRoot, "archive-content");
        if (File.Exists(contentRoot) || Directory.Exists(contentRoot))
        {
            throw new IOException($"整合包临时目录已存在: {contentRoot}");
        }

        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(contentRoot);
            await ExtractServerContentAsync(
                    sourcePath,
                    contentRoot,
                    format,
                    pack,
                    safetyOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            var source = new ServerPackSource
            {
                InputKind = format == PackArchiveFormat.CurseForge
                    ? ServerInputKinds.CurseForge
                    : ServerInputKinds.Modrinth,
                SourcePath = sourcePath,
                DisplayName = ReadString(pack.RawData["name"]),
                MinecraftVersion = pack.MinecraftVersion,
                LoaderType = pack.LoaderType,
                LoaderVersion = pack.LoaderVersion,
                ContentRoot = contentRoot,
                TemporaryRoot = tempRoot,
                ManifestPack = pack,
            };
            if (source.DisplayName.Length == 0)
            {
                source.DisplayName = Path.GetFileNameWithoutExtension(sourcePath);
            }

            source.Mods.AddRange(CreateManifestMods(pack, format));
            source.Mods.AddRange(FindLocalMods(contentRoot, cancellationToken));
            source.Worlds.AddRange(FindWorlds(contentRoot, cancellationToken));
            foreach (var rootName in OptionalRoots)
            {
                var path = Path.Combine(contentRoot, rootName);
                if (Directory.Exists(path))
                {
                    source.OptionalDirectories[rootName] = path;
                }
            }

            return source;
        }
        catch
        {
            TryDeleteDirectory(contentRoot);
            throw;
        }
    }

    private async Task RetainCurseForgeModsAsync(
        ModpackInfo pack,
        CancellationToken cancellationToken)
    {
        var idsByItem = new Dictionary<ContentItem, long>();
        var fileIdsByItem = new Dictionary<ContentItem, long>();
        foreach (var item in pack.Items)
        {
            if (!long.TryParse(
                    item.ProjectId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var projectId) || projectId <= 0)
            {
                throw new InvalidDataException($"CurseForge 整合包包含无效项目 ID: {item.ProjectId}");
            }

            idsByItem[item] = projectId;
            if (!long.TryParse(
                    item.FileId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var fileId) || fileId <= 0)
            {
                throw new InvalidDataException($"CurseForge 整合包包含无效文件 ID: {item.FileId}");
            }

            fileIdsByItem[item] = fileId;
        }

        var projectsTask = _curseForge.GetProjectsByIdsAsync(idsByItem.Values, cancellationToken);
        var filesTask = _curseForge.GetFilesByIdsAsync(fileIdsByItem.Values, cancellationToken);
        await Task.WhenAll(projectsTask, filesTask).ConfigureAwait(false);
        var projects = await projectsTask.ConfigureAwait(false);
        var files = await filesTask.ConfigureAwait(false);
        var missingIds = idsByItem.Values.Distinct().Where(id => !projects.ContainsKey(id)).ToArray();
        if (missingIds.Length > 0)
        {
            throw new InvalidDataException(
                $"CurseForge 未返回项目元数据: {string.Join(", ", missingIds)}");
        }

        var retained = new List<ContentItem>();
        foreach (var item in pack.Items)
        {
            var project = projects[idsByItem[item]];
            if (project.ClassId != 6)
            {
                continue;
            }

            item.Name = project.Name.Length > 0 ? project.Name : item.Name;
            item.CurseForgeSlug = project.Slug;
            item.Category = "mod";
            if (files.TryGetValue(fileIdsByItem[item], out var file))
            {
                if (file.ModId > 0 && file.ModId != project.Id)
                {
                    throw new InvalidDataException(
                        $"CurseForge 文件 {file.Id} 不属于清单项目 {project.Id}。");
                }

                item.DownloadUrl = file.DownloadUrl;
                item.DownloadUrls = file.DownloadUrl.Length > 0 ? [file.DownloadUrl] : [];
                item.FileName = file.FileName;
                item.FileSize = file.FileLength;
                item.Hashes = SearchMatcher.ExtractCurseForgeHashes(file);
                item.TargetDependencies = (file.Dependencies ?? [])
                    .Where(dependency => dependency.ModId > 0)
                    .Select(DependencyReference.FromCurseForge)
                    .ToList();
                item.DependencyMetadataAvailable = file.Dependencies is not null;
            }
            retained.Add(item);
        }

        pack.Items = retained;
    }

    private static void RetainModrinthMods(ModpackInfo pack)
    {
        pack.Items = pack.Items
            .Where(item => item.Category.Equals("mod", StringComparison.OrdinalIgnoreCase))
            .Where(item => GetModRelativePath(item.FilePath).Length > 0)
            .ToList();
    }

    private static IEnumerable<ServerModEntry> CreateManifestMods(
        ModpackInfo pack,
        PackArchiveFormat format)
    {
        foreach (var item in pack.Items)
        {
            var relativePath = format == PackArchiveFormat.Modrinth
                ? GetModRelativePath(item.FilePath)
                : string.Empty;
            var serverEnvironment = item.Environment.TryGetValue("server", out var value)
                ? value
                : string.Empty;
            var unsupported = serverEnvironment.Equals("unsupported", StringComparison.OrdinalIgnoreCase);
            var optional = serverEnvironment.Equals("optional", StringComparison.OrdinalIgnoreCase);
            var required = serverEnvironment.Equals("required", StringComparison.OrdinalIgnoreCase);
            yield return new ServerModEntry
            {
                Name = item.Name,
                RelativePath = relativePath,
                Origin = ServerModOrigins.Manifest,
                ServerSupport = unsupported
                    ? ServerSupportKinds.Unsupported
                    : optional
                        ? ServerSupportKinds.Optional
                        : required
                            ? ServerSupportKinds.Recommended
                            : ServerSupportKinds.Unknown,
                SupportReason = unsupported
                    ? "整合包声明该项目不支持服务端。"
                    : optional
                        ? "整合包声明该项目可选安装于服务端。"
                        : required
                            ? "整合包声明该项目为服务端必需。"
                            : string.Empty,
                Selected = !item.Disabled && !unsupported && !optional,
                Disabled = item.Disabled,
                ContentItem = item,
            };
        }
    }

    private static IEnumerable<ServerModEntry> FindLocalMods(
        string contentRoot,
        CancellationToken cancellationToken)
    {
        var modsRoot = Path.Combine(contentRoot, "mods");
        if (!Directory.Exists(modsRoot))
        {
            return [];
        }

        var result = new List<ServerModEntry>();
        foreach (var filePath in Directory.EnumerateFiles(modsRoot, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!filePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !filePath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var disabled = filePath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
            ArtifactCompatibilityMetadata? metadata = null;
            string? readError = null;
            try
            {
                metadata = ArtifactMetadataReader.Read(filePath, cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                readError = exception.Message;
            }
            var (support, reason) = GameDirectoryScanner.ClassifyServerSupport(metadata, readError);
            result.Add(new ServerModEntry
            {
                Name = Path.GetFileName(filePath),
                RelativePath = Path.GetRelativePath(modsRoot, filePath).Replace('\\', '/'),
                SourcePath = filePath,
                Origin = ServerModOrigins.Local,
                ServerSupport = support,
                SupportReason = reason,
                Selected = !disabled && support != ServerSupportKinds.Unsupported,
                Disabled = disabled,
            });
        }

        return result;
    }

    private static IEnumerable<ServerWorldEntry> FindWorlds(
        string contentRoot,
        CancellationToken cancellationToken)
    {
        var savesRoot = Path.Combine(contentRoot, "saves");
        if (!Directory.Exists(savesRoot))
        {
            return [];
        }

        var result = new List<ServerWorldEntry>();
        foreach (var directory in Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, "level.dat")))
            {
                result.Add(new ServerWorldEntry
                {
                    Name = Path.GetFileName(directory),
                    SourcePath = directory,
                });
            }
        }

        return result;
    }

    private static async Task ExtractServerContentAsync(
        string archivePath,
        string contentRoot,
        PackArchiveFormat format,
        ModpackInfo pack,
        ArchiveSafetyOptions safetyOptions,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            safetyOptions.CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        var metadataName = format == PackArchiveFormat.CurseForge
            ? "manifest.json"
            : "modrinth.index.json";
        var metadata = FindMetadataEntry(archive, metadataName);
        var metadataSegments = ArchiveSafety.ValidateEntryPath(metadata.FullName);
        var packageRoot = metadataSegments[..^1];
        var scopes = CreateScopes(format, pack, packageRoot);

        var members = new Dictionary<string, ExtractionMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0)
            {
                continue;
            }

            var segments = ArchiveSafety.ValidateEntryPath(entry.FullName);
            foreach (var scope in scopes)
            {
                if (!TryGetServerRelativePath(segments, scope.Segments, out var relativeSegments))
                {
                    continue;
                }

                var key = string.Join('/', relativeSegments);
                if (members.TryGetValue(key, out var existing))
                {
                    if (existing.Priority == scope.Priority)
                    {
                        throw new InvalidDataException($"整合包包含重复的服务端覆盖路径: {key}");
                    }

                    if (existing.Priority > scope.Priority)
                    {
                        break;
                    }
                }

                members[key] = new ExtractionMember(entry, relativeSegments, scope.Priority);
                break;
            }
        }

        var rootPrefix = contentRoot.EndsWith(Path.DirectorySeparatorChar)
            ? contentRoot
            : contentRoot + Path.DirectorySeparatorChar;
        var buffer = new byte[safetyOptions.CopyBufferBytes];
        long totalExtracted = 0;
        foreach (var member in members.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine([contentRoot, .. member.RelativeSegments]));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"整合包路径越界: {member.Entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            long memberExtracted = 0;
            try
            {
                await using var input = member.Entry.Open();
                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    safetyOptions.CopyBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    memberExtracted += read;
                    totalExtracted += read;
                    if (memberExtracted > safetyOptions.MaxMemberBytes ||
                        totalExtracted > safetyOptions.MaxUncompressedBytes)
                    {
                        throw new InvalidDataException("整合包解压内容超过安全上限。");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                TryDeleteFile(destination);
                throw;
            }
        }
    }

    private static IReadOnlyList<ExtractionScope> CreateScopes(
        PackArchiveFormat format,
        ModpackInfo pack,
        string[] packageRoot)
    {
        if (format == PackArchiveFormat.Modrinth)
        {
            return
            [
                new ExtractionScope([.. packageRoot, "overrides"], 0),
                new ExtractionScope([.. packageRoot, "server-overrides"], 1),
            ];
        }

        var overrideNode = pack.RawData["overrides"];
        var overridePath = overrideNode is null ? "overrides" : ReadString(overrideNode);
        if (overridePath.Length == 0)
        {
            return [];
        }

        var overrideSegments = ArchiveSafety.ValidateEntryPath(overridePath);
        return [new ExtractionScope([.. packageRoot, .. overrideSegments], 0)];
    }

    private static bool TryGetServerRelativePath(
        IReadOnlyList<string> entrySegments,
        IReadOnlyList<string> scopeSegments,
        out string[] relativeSegments)
    {
        relativeSegments = [];
        if (entrySegments.Count <= scopeSegments.Count)
        {
            return false;
        }

        for (var index = 0; index < scopeSegments.Count; index++)
        {
            if (!entrySegments[index].Equals(scopeSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        relativeSegments = entrySegments.Skip(scopeSegments.Count).ToArray();
        return relativeSegments.Length >= 2 && ExtractedRoots.Contains(relativeSegments[0]);
    }

    private static ZipArchiveEntry FindMetadataEntry(ZipArchive archive, string fileName)
    {
        return archive.Entries.Single(entry =>
        {
            var normalized = entry.FullName.Replace('\\', '/').TrimEnd('/');
            return normalized.Count(character => character == '/') <= 1 &&
                normalized.Split('/').LastOrDefault()?.Equals(fileName, StringComparison.Ordinal) == true;
        });
    }

    private static string GetModRelativePath(string path)
    {
        var segments = ArchiveSafety.ValidateEntryPath(path);
        if (segments.Length < 2 || !segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return string.Join('/', segments[1..]);
    }

    private static string ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text?.Trim() ?? string.Empty
            : string.Empty;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the extraction failure.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Preserve the read failure.
        }
    }

    private sealed record ExtractionScope(string[] Segments, int Priority);

    private sealed record ExtractionMember(
        ZipArchiveEntry Entry,
        string[] RelativeSegments,
        int Priority);
}
