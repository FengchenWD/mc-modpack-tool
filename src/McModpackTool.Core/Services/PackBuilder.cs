using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public static class PackBuilder
{
    private static readonly Dictionary<string, string> CategoryDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mod"] = "mods",
        ["resourcepack"] = "resourcepacks",
        ["shaderpack"] = "shaderpacks",
    };

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    public static async Task<BuildResult> BuildCurseForgeAsync(
        string outputPath,
        ModpackInfo pack,
        string targetMinecraftVersion,
        string targetLoaderType,
        string targetLoaderVersion,
        string overridesDirectory,
        bool downloadFiles = false,
        string packName = "",
        bool overwrite = false,
        HttpClient? httpClient = null,
        ArchiveSafetyOptions? safetyOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ValidateTarget(targetMinecraftVersion, targetLoaderType, targetLoaderVersion);
        cancellationToken.ThrowIfCancellationRequested();
        safetyOptions ??= ArchiveSafetyOptions.Default;
        httpClient ??= SharedHttpClient;

        var files = pack.PassthroughFiles.Select(CloneObject).ToList();
        var manifest = new JsonObject
        {
            ["minecraft"] = new JsonObject
            {
                ["version"] = targetMinecraftVersion,
                ["modLoaders"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = $"{targetLoaderType}-{targetLoaderVersion}",
                        ["primary"] = true,
                    },
                },
            },
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = string.IsNullOrWhiteSpace(packName)
                ? RawString(pack.RawData, "name", "Migrated Modpack")
                : packName,
            ["version"] = RawString(pack.RawData, "version", "1.0.0"),
            ["author"] = RawString(pack.RawData, "author", string.Empty),
            ["overrides"] = "overrides",
        };

        var result = new BuildResult();
        var disabledItems = new List<ContentItem>();
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var outputOverrides = Path.Combine(temporaryRoot, "overrides");
            if (!string.IsNullOrWhiteSpace(overridesDirectory) && Directory.Exists(overridesDirectory))
            {
                await ArchiveSafety.CopyDirectoryAsync(overridesDirectory, outputOverrides, cancellationToken)
                    .ConfigureAwait(false);
            }

            var protectedPaths = CollectProtectedOverridePaths(pack, outputOverrides);
            foreach (var item in pack.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Passthrough || item.Excluded)
                {
                    continue;
                }

                if (item.PreserveOriginal && item.OriginalEntry is not null)
                {
                    files.Add(CloneObject(item.OriginalEntry));
                    continue;
                }

                if (item.Disabled)
                {
                    disabledItems.Add(item);
                    continue;
                }

                if (!IsResolved(item))
                {
                    result.MissingFiles.Add(DescribeItem(item));
                    continue;
                }

                var manifestEntry = CreateCurseForgeEntry(item);
                if (manifestEntry is null)
                {
                    result.MissingFiles.Add(DescribeItem(item));
                    continue;
                }

                var embedded = false;
                var contentPath = GetContentOutputPath(item, disabled: false);
                var collision = contentPath.Length > 0 && protectedPaths.Contains(contentPath);
                if (collision)
                {
                    result.Warnings.Add(
                        $"{item.Name}：目标路径与 overrides 现有文件同名，已保留原文件并使用联网安装引用。");
                }
                else if (downloadFiles)
                {
                    if (!string.IsNullOrWhiteSpace(item.TargetDownloadUrl))
                    {
                        embedded = await ArchiveSafety.DownloadFileAsync(
                                httpClient,
                                item.TargetDownloadUrl,
                                GetCategoryDirectory(outputOverrides, item.Category),
                                FirstNonEmpty(item.TargetFileName, item.FileName),
                                expectedSize: item.TargetFileSize,
                                expectedHashes: item.TargetHashes,
                                options: safetyOptions,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        if (!embedded)
                        {
                            result.Warnings.Add(
                                $"{item.Name}：下载失败，已回退为 CurseForge 联网安装引用。");
                        }
                    }
                    else
                    {
                        result.Warnings.Add(
                            $"{item.Name}：平台未提供下载地址，已保留 CurseForge 联网安装引用。");
                    }
                }

                if (!embedded)
                {
                    files.Add(manifestEntry);
                }
                else if (contentPath.Length > 0)
                {
                    protectedPaths.Add(contentPath);
                }
            }

            await HandleDisabledItemsAsync(
                    disabledItems,
                    outputOverrides,
                    result,
                    httpClient,
                    safetyOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return result;
            }

            files = OrderEntriesLikeSource(pack.RawData["files"] as JsonArray, files);
            manifest["files"] = ToJsonArray(files);
            await WriteJsonAsync(Path.Combine(temporaryRoot, "manifest.json"), manifest, cancellationToken)
                .ConfigureAwait(false);
            await ArchiveSafety.CreateZipAtomicAsync(
                    outputPath,
                    temporaryRoot,
                    overwrite,
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    public static async Task<BuildResult> BuildModrinthAsync(
        string outputPath,
        ModpackInfo pack,
        string targetMinecraftVersion,
        string targetLoaderType,
        string targetLoaderVersion,
        string overridesDirectory,
        bool downloadFiles = false,
        string packName = "",
        bool overwrite = false,
        HttpClient? httpClient = null,
        ArchiveSafetyOptions? safetyOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ValidateTarget(targetMinecraftVersion, targetLoaderType, targetLoaderVersion);
        cancellationToken.ThrowIfCancellationRequested();
        safetyOptions ??= ArchiveSafetyOptions.Default;
        httpClient ??= SharedHttpClient;

        var loaderKey = NormalizeLoader(targetLoaderType) switch
        {
            "fabric" => "fabric-loader",
            "quilt" => "quilt-loader",
            "forge" => "forge",
            "neoforge" => "neoforge",
            var other => other,
        };
        var dependencies = new JsonObject
        {
            ["minecraft"] = targetMinecraftVersion,
            [loaderKey] = targetLoaderVersion,
        };
        var files = pack.PassthroughFiles.Select(CloneObject).ToList();
        var index = new JsonObject
        {
            ["game"] = "minecraft",
            ["formatVersion"] = 1,
            ["versionId"] = RawString(pack.RawData, "versionId", "1.0.0"),
            ["name"] = string.IsNullOrWhiteSpace(packName)
                ? RawString(pack.RawData, "name", "Migrated Modpack")
                : packName,
            ["summary"] = RawString(pack.RawData, "summary", string.Empty),
            ["dependencies"] = dependencies,
        };

        var result = new BuildResult();
        var disabledItems = new List<ContentItem>();
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var outputOverrides = Path.Combine(temporaryRoot, "overrides");
            if (!string.IsNullOrWhiteSpace(overridesDirectory) && Directory.Exists(overridesDirectory))
            {
                await ArchiveSafety.CopyDirectoryAsync(overridesDirectory, outputOverrides, cancellationToken)
                    .ConfigureAwait(false);
            }

            var protectedPaths = CollectProtectedOverridePaths(pack, outputOverrides);
            foreach (var item in pack.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Passthrough || item.Excluded)
                {
                    continue;
                }

                if (item.PreserveOriginal && item.OriginalEntry is not null)
                {
                    files.Add(CloneObject(item.OriginalEntry));
                    continue;
                }

                if (item.Disabled)
                {
                    disabledItems.Add(item);
                    continue;
                }

                if (!IsResolved(item))
                {
                    result.MissingFiles.Add(DescribeItem(item));
                    continue;
                }

                var remoteEntry = CreateModrinthEntry(item);
                var mustEmbed = item.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase);
                var scopedEnvironment = RequiresScopedEnvironment(item.Environment);
                var contentPath = GetContentOutputPath(item, disabled: false);
                var collision = contentPath.Length > 0 && protectedPaths.Contains(contentPath);
                if (collision)
                {
                    if (remoteEntry is not null && !mustEmbed)
                    {
                        files.Add(remoteEntry);
                        result.Warnings.Add(
                            $"{item.Name}：目标路径与 overrides 现有文件同名，已保留原文件和联网安装引用。");
                    }
                    else
                    {
                        result.MissingFiles.Add(
                            $"{DescribeItem(item)}（与 overrides 现有文件同名，未覆盖原文件）");
                    }
                    continue;
                }

                if (mustEmbed && scopedEnvironment)
                {
                    result.MissingFiles.Add($"{DescribeItem(item)}（无法保留 env 作用域）");
                    continue;
                }

                var embedded = false;
                var attemptedDownload = (downloadFiles || mustEmbed) && !scopedEnvironment;
                if (attemptedDownload && !string.IsNullOrWhiteSpace(item.TargetDownloadUrl))
                {
                    embedded = await ArchiveSafety.DownloadFileAsync(
                            httpClient,
                            item.TargetDownloadUrl,
                            GetCategoryDirectory(outputOverrides, item.Category),
                            FirstNonEmpty(item.TargetFileName, item.FileName),
                            expectedSize: item.TargetFileSize,
                            expectedHashes: item.TargetHashes,
                            options: safetyOptions,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                if (embedded)
                {
                    if (contentPath.Length > 0)
                    {
                        protectedPaths.Add(contentPath);
                    }
                    continue;
                }

                if (remoteEntry is not null && !mustEmbed)
                {
                    files.Add(remoteEntry);
                    if (downloadFiles && scopedEnvironment)
                    {
                        result.Warnings.Add(
                            $"{item.Name}：为保留 Modrinth env 作用域，已保留联网安装引用。");
                    }
                    else if (downloadFiles)
                    {
                        result.Warnings.Add(
                            $"{item.Name}：下载失败，已回退为 Modrinth 联网安装引用。");
                    }
                }
                else
                {
                    result.MissingFiles.Add(DescribeItem(item));
                }
            }

            await HandleDisabledItemsAsync(
                    disabledItems,
                    outputOverrides,
                    result,
                    httpClient,
                    safetyOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return result;
            }

            files = OrderEntriesLikeSource(pack.RawData["files"] as JsonArray, files);
            index["files"] = ToJsonArray(files);
            await WriteJsonAsync(Path.Combine(temporaryRoot, "modrinth.index.json"), index, cancellationToken)
                .ConfigureAwait(false);
            await ArchiveSafety.CreateZipAtomicAsync(
                    outputPath,
                    temporaryRoot,
                    overwrite,
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static async Task HandleDisabledItemsAsync(
        IEnumerable<ContentItem> items,
        string outputOverrides,
        BuildResult result,
        HttpClient httpClient,
        ArchiveSafetyOptions safetyOptions,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var oldPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(item.FileName))
            {
                try
                {
                    ArchiveSafety.ValidateLocalName(item.FileName);
                    oldPath = Path.Combine(GetCategoryDirectory(outputOverrides, "mod"), item.FileName);
                }
                catch (InvalidDataException)
                {
                    // An untrusted archive/API filename must never be resolved outside overrides.
                }
            }
            var oldExists = oldPath.Length > 0 && File.Exists(oldPath);
            if (IsResolved(item) && !string.IsNullOrWhiteSpace(item.TargetDownloadUrl))
            {
                var downloaded = await ArchiveSafety.DownloadFileAsync(
                        httpClient,
                        item.TargetDownloadUrl,
                        GetCategoryDirectory(outputOverrides, "mod"),
                        FirstNonEmpty(item.TargetFileName, item.FileName),
                        suffix: ".disabled",
                        expectedSize: item.TargetFileSize,
                        expectedHashes: item.TargetHashes,
                        options: safetyOptions,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!downloaded)
                {
                    if (oldExists)
                    {
                        result.Warnings.Add($"[禁用] {item.Name}：目标下载失败，已保留旧禁用版本。");
                    }
                    else
                    {
                        result.MissingFiles.Add($"[禁用] {item.Name}");
                    }
                }
            }
            else if (oldExists)
            {
                result.Warnings.Add($"[禁用] {item.Name}：未找到目标版本，已保留旧禁用版本。");
            }
            else
            {
                result.MissingFiles.Add($"[禁用] {item.Name}");
            }
        }
    }

    private static JsonObject? CreateCurseForgeEntry(ContentItem item)
    {
        if (!long.TryParse(item.ProjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var projectId) ||
            !long.TryParse(item.TargetFileId, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId))
        {
            return null;
        }

        return new JsonObject
        {
            ["projectID"] = projectId,
            ["fileID"] = fileId,
            ["required"] = item.Required,
        };
    }

    private static JsonObject? CreateModrinthEntry(ContentItem item)
    {
        var fileName = FirstNonEmpty(item.TargetFileName, item.FileName);
        if (string.IsNullOrWhiteSpace(item.TargetDownloadUrl) ||
            string.IsNullOrWhiteSpace(fileName) ||
            item.TargetHashes.Count == 0)
        {
            return null;
        }

        try
        {
            ArchiveSafety.ValidateLocalName(fileName);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var directory = GetCategoryName(item.Category);
        var hashes = new JsonObject();
        foreach (var (name, value) in item.TargetHashes)
        {
            hashes[name] = value;
        }

        var entry = new JsonObject
        {
            ["path"] = $"{directory}/{fileName}",
            ["downloads"] = new JsonArray { item.TargetDownloadUrl },
            ["hashes"] = hashes,
            ["fileSize"] = item.TargetFileSize,
        };
        if (item.Environment.Count > 0)
        {
            var environment = new JsonObject();
            foreach (var (name, value) in item.Environment)
            {
                environment[name] = value;
            }
            entry["env"] = environment;
        }

        return entry;
    }

    private static List<JsonObject> OrderEntriesLikeSource(JsonArray? sourceEntries, List<JsonObject> entries)
    {
        if (sourceEntries is null || sourceEntries.Count == 0 || entries.Count < 2)
        {
            return entries;
        }

        var remaining = new List<JsonObject>(entries);
        var ordered = new List<JsonObject>(entries.Count);
        foreach (var source in sourceEntries.OfType<JsonObject>())
        {
            var index = remaining.FindIndex(entry => JsonNode.DeepEquals(entry, source));
            if (index >= 0)
            {
                ordered.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
        }

        ordered.AddRange(remaining);
        return ordered;
    }

    private static HashSet<string> CollectProtectedOverridePaths(ModpackInfo pack, string outputOverrides)
    {
        var paths = new HashSet<string>(
            pack.OverridePaths.Select(NormalizeContentPath),
            StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(outputOverrides))
        {
            return paths;
        }

        foreach (var file in Directory.EnumerateFiles(outputOverrides, "*", SearchOption.AllDirectories))
        {
            paths.Add(NormalizeContentPath(Path.GetRelativePath(outputOverrides, file)));
        }
        return paths;
    }

    private static string GetContentOutputPath(ContentItem item, bool disabled)
    {
        var fileName = FirstNonEmpty(item.TargetFileName, item.FileName);
        if (fileName.Length == 0)
        {
            return string.Empty;
        }

        if (disabled && !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".disabled";
        }

        try
        {
            ArchiveSafety.ValidateLocalName(fileName);
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }

        return NormalizeContentPath($"{GetCategoryName(item.Category)}/{fileName}");
    }

    private static string GetCategoryDirectory(string overridesRoot, string category) =>
        Path.Combine(overridesRoot, GetCategoryName(category));

    private static string GetCategoryName(string category) =>
        CategoryDirectories.GetValueOrDefault(category, "mods");

    private static string NormalizeContentPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool RequiresScopedEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        if (environment.Count == 0)
        {
            return false;
        }

        var client = environment.GetValueOrDefault("client", "required");
        var server = environment.GetValueOrDefault("server", "required");
        return !client.Equals("required", StringComparison.OrdinalIgnoreCase) ||
            !server.Equals("required", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResolved(ContentItem item) =>
        item.Status.Equals("found", StringComparison.OrdinalIgnoreCase) ||
        item.Status.Equals("warning", StringComparison.OrdinalIgnoreCase);

    private static string DescribeItem(ContentItem item) => $"{item.Name} [{item.Category}]";

    private static string FirstNonEmpty(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second ?? string.Empty;

    private static void ValidateTarget(string minecraft, string loader, string loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraft) ||
            string.IsNullOrWhiteSpace(loader) ||
            string.IsNullOrWhiteSpace(loaderVersion))
        {
            throw new ArgumentException("目标 MC、加载器类型和加载器版本均不能为空。");
        }
    }

    private static string NormalizeLoader(string loader)
    {
        var normalized = new string(loader.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return normalized switch
        {
            "fabricloader" => "fabric",
            "quiltloader" => "quilt",
            "neoforged" or "neo" => "neoforge",
            _ => normalized,
        };
    }

    private static string RawString(JsonObject source, string propertyName, string fallback)
    {
        var node = source[propertyName];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text ?? fallback;
            }
            if (value.TryGetValue<long>(out var integer))
            {
                return integer.ToString(CultureInfo.InvariantCulture);
            }
        }
        return fallback;
    }

    private static JsonArray ToJsonArray(IEnumerable<JsonObject> entries)
    {
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            array.Add(entry);
        }
        return array;
    }

    private static JsonObject CloneObject(JsonObject source) => source.DeepClone().AsObject();

    private static async Task WriteJsonAsync(
        string path,
        JsonObject value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        value.WriteTo(writer);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcpack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (Path.GetFileName(fullPath).StartsWith("mcpack_", StringComparison.Ordinal) &&
                fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // Temporary cleanup is best effort and must not hide the build result.
        }
    }
}
