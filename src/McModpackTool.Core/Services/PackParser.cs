using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public enum PackArchiveFormat
{
    Unknown,
    CurseForge,
    Modrinth,
}

public static partial class PackParser
{
    private static readonly Dictionary<string, string> ModrinthLoaderKeys = new(StringComparer.Ordinal)
    {
        ["forge"] = "forge",
        ["fabric-loader"] = "fabric",
        ["neoforge"] = "neoforge",
        ["quilt-loader"] = "quilt",
    };

    public static async Task<PackArchiveFormat> DetectFormatAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = OpenArchiveFile(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        var manifest = FindUniqueMetadataEntry(archive, "manifest.json");
        var modrinthIndex = FindUniqueMetadataEntry(archive, "modrinth.index.json");
        if (manifest is not null && modrinthIndex is not null)
        {
            throw new InvalidDataException("整合包同时包含 CurseForge 与 Modrinth 元数据，格式不明确。");
        }

        return manifest is not null
            ? PackArchiveFormat.CurseForge
            : modrinthIndex is not null
                ? PackArchiveFormat.Modrinth
                : PackArchiveFormat.Unknown;
    }

    public static async Task<ModpackInfo> ParseAsync(
        string filePath,
        ArchiveSafetyOptions? safetyOptions = null,
        CancellationToken cancellationToken = default)
    {
        safetyOptions ??= ArchiveSafetyOptions.Default;
        await ArchiveSafety.ValidateArchiveAsync(filePath, safetyOptions, cancellationToken)
            .ConfigureAwait(false);

        await using var stream = OpenArchiveFile(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        var manifest = FindUniqueMetadataEntry(archive, "manifest.json");
        var modrinthIndex = FindUniqueMetadataEntry(archive, "modrinth.index.json");
        if (manifest is not null && modrinthIndex is not null)
        {
            throw new InvalidDataException("整合包同时包含 CurseForge 与 Modrinth 元数据，格式不明确。");
        }

        ModpackInfo result;
        if (manifest is not null)
        {
            var metadata = await ArchiveSafety.ReadJsonObjectAsync(
                    manifest,
                    safetyOptions.MaxMetadataBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            result = ParseCurseForge(metadata, cancellationToken);
        }
        else if (modrinthIndex is not null)
        {
            var metadata = await ArchiveSafety.ReadJsonObjectAsync(
                    modrinthIndex,
                    safetyOptions.MaxMetadataBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            result = ParseModrinth(metadata, cancellationToken);
        }
        else
        {
            throw new InvalidDataException("无法识别整合包格式。");
        }

        result.OverridePaths = CollectOverridePaths(archive, cancellationToken);
        return result;
    }

    public static async Task<string> ExtractOverridesAsync(
        string filePath,
        string destinationDirectory,
        ArchiveSafetyOptions? safetyOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        safetyOptions ??= ArchiveSafetyOptions.Default;
        await ArchiveSafety.ValidateArchiveAsync(filePath, safetyOptions, cancellationToken)
            .ConfigureAwait(false);

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var rootPrefix = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        await using var stream = OpenArchiveFile(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        var members = new List<OverrideMember>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = ArchiveSafety.ValidateEntryPath(entry.FullName);
            var overrideIndex = FindSegment(segments, "overrides");
            if (overrideIndex < 0 || overrideIndex + 1 >= segments.Length || IsDirectory(entry))
            {
                continue;
            }

            var relativeSegments = segments[(overrideIndex + 1)..];
            var destination = Path.GetFullPath(Path.Combine([destinationRoot, .. relativeSegments]));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"整合包路径越界: {entry.FullName}");
            }

            if (!seen.Add(destination))
            {
                throw new InvalidDataException($"整合包包含重复路径: {entry.FullName}");
            }

            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException($"overrides 提取目标已存在: {destination}");
            }

            members.Add(new OverrideMember(entry, destination));
        }

        long totalExtracted = 0;
        var buffer = new byte[safetyOptions.CopyBufferBytes];
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(member.Destination)
                ?? throw new InvalidDataException($"无效的 overrides 目标路径: {member.Destination}");
            Directory.CreateDirectory(parent);
            long memberExtracted = 0;
            try
            {
                await using var input = member.Entry.Open();
                await using var output = new FileStream(
                    member.Destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    safetyOptions.CopyBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                TryDelete(member.Destination);
                throw;
            }
        }

        return destinationDirectory;
    }

    public static string ClassifyContentPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return "other";
        }

        return parts[0].ToLowerInvariant() switch
        {
            "mods" => "mod",
            "resourcepacks" or "resourcepack" => "resourcepack",
            "shaderpacks" or "shaderpack" => "shaderpack",
            _ => "other",
        };
    }

    public static string ParseCurseForgeFileId(IEnumerable<string> downloadUrls)
    {
        ArgumentNullException.ThrowIfNull(downloadUrls);
        foreach (var value in downloadUrls)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var host = uri.IdnHost.TrimEnd('.');
            if (!host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = ForgeCdnFilePathRegex().Match(uri.AbsolutePath);
            if (!match.Success ||
                !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
                !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix))
            {
                continue;
            }

            try
            {
                return checked(prefix * 1000 + suffix).ToString(CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                // Ignore a malformed CDN path that cannot represent a CurseForge file ID.
            }
        }

        return string.Empty;
    }

    public static (string ProjectId, string VersionId, string Source) ParseModrinthDownloadUrls(
        IEnumerable<string> downloadUrls)
    {
        ArgumentNullException.ThrowIfNull(downloadUrls);
        foreach (var value in downloadUrls)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var host = uri.IdnHost.TrimEnd('.');
            if (!host.Equals("modrinth.com", StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var dataIndex = Array.FindIndex(parts, static part => part.Equals("data", StringComparison.Ordinal));
            var versionsIndex = Array.FindIndex(parts, static part => part.Equals("versions", StringComparison.Ordinal));
            if (dataIndex >= 0 && versionsIndex >= 0 &&
                dataIndex + 1 < parts.Length && versionsIndex + 1 < parts.Length)
            {
                return (
                    Uri.UnescapeDataString(parts[dataIndex + 1]),
                    Uri.UnescapeDataString(parts[versionsIndex + 1]),
                    "modrinth");
            }
        }

        return (string.Empty, string.Empty, string.Empty);
    }

    private static ModpackInfo ParseCurseForge(JsonObject manifest, CancellationToken cancellationToken)
    {
        var result = new ModpackInfo
        {
            FormatType = "curseforge",
            RawData = manifest.DeepClone().AsObject(),
        };
        var minecraft = manifest["minecraft"] as JsonObject;
        result.MinecraftVersion = NodeAsString(minecraft?["version"]);

        var loaders = minecraft?["modLoaders"] as JsonArray;
        if (loaders is { Count: > 0 })
        {
            var selected = loaders
                .OfType<JsonObject>()
                .FirstOrDefault(loader => NodeAsBoolean(loader["primary"]))
                ?? loaders.OfType<JsonObject>().FirstOrDefault();
            var loaderId = NodeAsString(selected?["id"]);
            var separator = loaderId.IndexOf('-');
            if (separator >= 0)
            {
                result.LoaderType = loaderId[..separator];
                result.LoaderVersion = loaderId[(separator + 1)..];
            }
            else
            {
                result.LoaderType = loaderId;
            }
        }

        if (manifest["files"] is JsonArray files)
        {
            foreach (var entry in files.OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projectId = NodeAsString(entry["projectID"]);
                result.Items.Add(new ContentItem
                {
                    Name = projectId.Length > 0 ? $"Project #{projectId}" : "Project #",
                    ProjectId = projectId,
                    OriginalProjectId = projectId,
                    FileId = NodeAsString(entry["fileID"]),
                    OldMinecraftVersion = result.MinecraftVersion,
                    OldLoader = result.LoaderType,
                    Category = "mod",
                    Status = "pending",
                    Source = "curseforge",
                    OriginalSource = "curseforge",
                    Required = entry["required"] is null || NodeAsBoolean(entry["required"], defaultValue: true),
                    OriginalEntry = entry.DeepClone().AsObject(),
                    IdentityLocked = projectId.Length > 0,
                });
            }
        }

        return result;
    }

    private static ModpackInfo ParseModrinth(JsonObject index, CancellationToken cancellationToken)
    {
        var result = new ModpackInfo
        {
            FormatType = "modrinth",
            RawData = index.DeepClone().AsObject(),
        };
        if (index["dependencies"] is JsonObject dependencies)
        {
            result.MinecraftVersion = NodeAsString(dependencies["minecraft"]);
            foreach (var (key, loader) in ModrinthLoaderKeys)
            {
                if (dependencies[key] is null)
                {
                    continue;
                }

                result.LoaderType = loader;
                result.LoaderVersion = NodeAsString(dependencies[key]);
                break;
            }
        }

        if (index["files"] is not JsonArray files)
        {
            return result;
        }

        foreach (var entry in files.OfType<JsonObject>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NodeAsString(entry["path"]);
            var category = ClassifyContentPath(path);
            if (category == "other")
            {
                result.PassthroughFiles.Add(entry.DeepClone().AsObject());
                continue;
            }

            var downloads = entry["downloads"] is JsonArray downloadNodes
                ? downloadNodes.Select(NodeAsString).Where(static value => value.Length > 0).ToList()
                : [];
            var identity = ParseModrinthDownloadUrls(downloads);
            var curseForgeFileId = ParseCurseForgeFileId(downloads);
            var source = identity.Source == "modrinth" ? "modrinth" : "unknown";
            var fileName = path.Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty;
            var hashes = NodeAsStringDictionary(entry["hashes"] as JsonObject);
            var environment = NodeAsStringDictionary(entry["env"] as JsonObject);
            result.Items.Add(new ContentItem
            {
                Name = fileName.Length > 0 ? fileName : "未知文件",
                ProjectId = identity.ProjectId,
                OriginalProjectId = identity.ProjectId,
                FileId = curseForgeFileId,
                VersionId = identity.VersionId,
                DownloadUrl = downloads.FirstOrDefault() ?? string.Empty,
                DownloadUrls = downloads,
                FileName = fileName,
                FileSize = NodeAsInt64(entry["fileSize"]),
                Hashes = hashes,
                OldMinecraftVersion = result.MinecraftVersion,
                OldLoader = result.LoaderType,
                Category = category,
                Disabled = fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase),
                Status = "pending",
                Source = source,
                OriginalSource = source,
                FilePath = path,
                Environment = environment,
                OriginalEntry = entry.DeepClone().AsObject(),
                IdentityLocked = identity.ProjectId.Length > 0 || curseForgeFileId.Length > 0,
            });
        }

        return result;
    }

    private static HashSet<string> CollectOverridePaths(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDirectory(entry))
            {
                continue;
            }

            var segments = ArchiveSafety.ValidateEntryPath(entry.FullName);
            var overrideIndex = FindSegment(segments, "overrides");
            if (overrideIndex >= 0 && overrideIndex + 1 < segments.Length)
            {
                result.Add(string.Join('/', segments[(overrideIndex + 1)..]));
            }
        }

        return result;
    }

    private static ZipArchiveEntry? FindUniqueMetadataEntry(ZipArchive archive, string fileName)
    {
        var matches = archive.Entries.Where(entry =>
        {
            var normalized = entry.FullName.Replace('\\', '/').TrimEnd('/');
            return normalized.Count(static character => character == '/') <= 1 &&
                normalized.Split('/').LastOrDefault()?.Equals(fileName, StringComparison.Ordinal) == true;
        }).ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException($"整合包包含重复的 {fileName}，格式不明确。"),
        };
    }

    private static Dictionary<string, string> NodeAsStringDictionary(JsonObject? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value is null)
        {
            return result;
        }

        foreach (var (key, node) in value)
        {
            var text = NodeAsString(node);
            if (text.Length > 0)
            {
                result[key] = text;
            }
        }

        return result;
    }

    private static string NodeAsString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text ?? string.Empty;
            }
            if (value.TryGetValue<long>(out var integer))
            {
                return integer.ToString(CultureInfo.InvariantCulture);
            }
            if (value.TryGetValue<double>(out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean ? "true" : "false";
            }
        }

        return node.ToJsonString().Trim('"');
    }

    private static bool NodeAsBoolean(JsonNode? node, bool defaultValue = false)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean))
            {
                return boolean;
            }
        }

        return defaultValue;
    }

    private static long NodeAsInt64(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var integer))
            {
                return integer;
            }
            if (value.TryGetValue<string>(out var text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
            {
                return integer;
            }
        }

        return 0;
    }

    private static int FindSegment(IReadOnlyList<string> segments, string value)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static FileStream OpenArchiveFile(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        ArchiveSafetyOptions.Default.CopyBufferBytes,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The extraction error is more useful than a best-effort cleanup error.
        }
    }

    private sealed record OverrideMember(ZipArchiveEntry Entry, string Destination);

    [GeneratedRegex(@"(?:^|/)files/(\d+)/(\d{1,3})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForgeCdnFilePathRegex();
}
