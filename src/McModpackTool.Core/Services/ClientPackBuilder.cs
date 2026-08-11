using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed class ClientPackBuilder : IDisposable
{
    private const int HashConcurrency = 4;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ModrinthClient _modrinth;
    private readonly CurseForgeClient _curseForge;
    private readonly bool _ownsModrinth;
    private readonly bool _ownsCurseForge;

    public ClientPackBuilder(
        ModrinthClient? modrinth = null,
        CurseForgeClient? curseForge = null)
    {
        _ownsModrinth = modrinth is null;
        _ownsCurseForge = curseForge is null;
        _modrinth = modrinth ?? new ModrinthClient();
        _curseForge = curseForge ?? new CurseForgeClient();
    }

    public async Task<ClientBuildResult> BuildAsync(
        ClientBuildRequest request,
        IProgress<ClientBuildPhase>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        string outputPath = Path.GetFullPath(request.OutputPath);
        var result = new ClientBuildResult { OutputPath = outputPath };
        if (File.Exists(outputPath) && !request.Overwrite)
        {
            result.MissingFiles.Add("输出文件已存在，且未允许覆盖。");
            return result;
        }

        string stagingRoot = Path.Combine(Path.GetTempPath(), $"mc-client-pack-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            IReadOnlyList<ClientFilePlan> plans = CreateFilePlans(request, result, cancellationToken);
            if (!result.Succeeded)
            {
                return result;
            }

            progress?.Report(ClientBuildPhase.MatchingPlatformFiles);
            IReadOnlyDictionary<string, RemoteMatch> matches = await MatchPlatformFilesAsync(
                request.Format,
                plans,
                result,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(ClientBuildPhase.CopyingOverrides);
            await CopyEmbeddedFilesAsync(stagingRoot, plans, matches, result, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(ClientBuildPhase.WritingManifest);
            if (request.Format.Equals(ClientPackFormats.Modrinth, StringComparison.OrdinalIgnoreCase))
            {
                await WriteModrinthIndexAsync(stagingRoot, request.Source, plans, matches, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await WriteCurseForgeManifestAsync(stagingRoot, request.Source, plans, matches, cancellationToken)
                    .ConfigureAwait(false);
            }

            progress?.Report(ClientBuildPhase.CompressingArchive);
            await ArchiveSafety.CreateZipAtomicAsync(
                outputPath,
                stagingRoot,
                request.Overwrite,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            result.MissingFiles.Add(exception.Message);
            return result;
        }
        finally
        {
            TryDeleteStagingDirectory(stagingRoot);
        }
    }

    public void Dispose()
    {
        if (_ownsModrinth)
        {
            _modrinth.Dispose();
        }
        if (_ownsCurseForge)
        {
            _curseForge.Dispose();
        }
    }

    private static void ValidateRequest(ClientBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source.MinecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source.ContentRoot);

        bool modrinth = request.Format.Equals(ClientPackFormats.Modrinth, StringComparison.OrdinalIgnoreCase);
        bool curseForge = request.Format.Equals(ClientPackFormats.CurseForge, StringComparison.OrdinalIgnoreCase);
        if (!modrinth && !curseForge)
        {
            throw new ArgumentException("不支持的客户端整合包格式。", nameof(request));
        }

        string extension = Path.GetExtension(request.OutputPath);
        if (modrinth && !extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Modrinth 整合包必须使用 .mrpack 扩展名。", nameof(request));
        }
        if (curseForge && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("CurseForge 整合包必须使用 .zip 扩展名。", nameof(request));
        }

        _ = GetLoaderMetadata(request.Source);
    }

    private static IReadOnlyList<ClientFilePlan> CreateFilePlans(
        ClientBuildRequest request,
        ClientBuildResult result,
        CancellationToken cancellationToken)
    {
        string contentRoot = Path.GetFullPath(request.Source.ContentRoot);
        var root = new DirectoryInfo(contentRoot);
        if (!root.Exists)
        {
            result.MissingFiles.Add($"游戏内容目录不存在：{contentRoot}");
            return [];
        }
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            result.MissingFiles.Add("游戏内容根目录不能是符号链接或重解析点。");
            return [];
        }

        IEnumerable<ClientContentEntry> candidates = request.IncludedItems ?? request.Source.Items;
        ClientContentEntry[] selected = candidates.Where(item => item.Selected).ToArray();
        var plans = new List<ClientFilePlan>();
        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClientContentEntry item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string relativePath = NormalizeRelativePath(item.RelativePath);
                string sourcePath = Path.GetFullPath(item.SourcePath);
                EnsureSourceMatchesRelativePath(contentRoot, sourcePath, relativePath);

                if (item.IsDirectory)
                {
                    var directory = new DirectoryInfo(sourcePath);
                    if (!directory.Exists)
                    {
                        result.MissingFiles.Add($"{item.Name}：目录不存在。");
                        continue;
                    }
                    if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        result.MissingFiles.Add($"{item.Name}：目录是符号链接或重解析点。");
                        continue;
                    }
                    AddDirectoryPlans(directory, relativePath, item, plans, destinations, result, cancellationToken);
                }
                else
                {
                    var file = new FileInfo(sourcePath);
                    if (!file.Exists)
                    {
                        result.MissingFiles.Add($"{item.Name}：文件不存在。");
                        continue;
                    }
                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        result.MissingFiles.Add($"{item.Name}：文件是符号链接或重解析点。");
                        continue;
                    }
                    AddPlan(new ClientFilePlan(
                        file.FullName,
                        relativePath,
                        item.Name,
                        item.Kind,
                        item.Disabled,
                        IsPlatformEligible(item, relativePath)), plans, destinations, result);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
            {
                result.MissingFiles.Add($"{item.Name}：{exception.Message}");
            }
        }
        return plans.OrderBy(plan => plan.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void AddDirectoryPlans(
        DirectoryInfo directory,
        string relativeRoot,
        ClientContentEntry item,
        List<ClientFilePlan> plans,
        Dictionary<string, string> destinations,
        ClientBuildResult result,
        CancellationToken cancellationToken)
    {
        foreach (DirectoryInfo child in directory.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                result.MissingFiles.Add($"{item.Name}：包含符号链接或重解析点目录 {child.FullName}");
                continue;
            }
            ArchiveSafety.ValidateLocalName(child.Name);
            AddDirectoryPlans(
                child,
                CombineRelativePath(relativeRoot, child.Name),
                item,
                plans,
                destinations,
                result,
                cancellationToken);
        }

        foreach (FileInfo file in directory.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                result.MissingFiles.Add($"{item.Name}：包含符号链接或重解析点文件 {file.FullName}");
                continue;
            }
            ArchiveSafety.ValidateLocalName(file.Name);
            AddPlan(new ClientFilePlan(
                file.FullName,
                CombineRelativePath(relativeRoot, file.Name),
                item.Name,
                item.Kind,
                item.Disabled,
                PlatformEligible: false), plans, destinations, result);
        }
    }

    private static void AddPlan(
        ClientFilePlan plan,
        List<ClientFilePlan> plans,
        Dictionary<string, string> destinations,
        ClientBuildResult result)
    {
        if (destinations.TryGetValue(plan.RelativePath, out string? existing))
        {
            result.MissingFiles.Add(
                $"导出相对路径冲突：{plan.RelativePath}（{existing} 与 {plan.SourcePath}）");
            return;
        }
        destinations[plan.RelativePath] = plan.SourcePath;
        plans.Add(plan);
    }

    private async Task<IReadOnlyDictionary<string, RemoteMatch>> MatchPlatformFilesAsync(
        string format,
        IReadOnlyList<ClientFilePlan> plans,
        ClientBuildResult result,
        CancellationToken cancellationToken)
    {
        ClientFilePlan[] candidates = plans.Where(plan => plan.PlatformEligible).ToArray();
        if (candidates.Length == 0)
        {
            return new Dictionary<string, RemoteMatch>(StringComparer.OrdinalIgnoreCase);
        }

        bool curseForge = format.Equals(ClientPackFormats.CurseForge, StringComparison.OrdinalIgnoreCase);
        FileHashInfo[] hashes = await ComputeHashesAsync(candidates, curseForge, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return curseForge
                ? await MatchCurseForgeAsync(hashes, result, cancellationToken).ConfigureAwait(false)
                : await MatchModrinthAsync(hashes, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string platform = curseForge ? "CurseForge" : "Modrinth";
            result.Warnings.Add($"{platform} 精确匹配暂时不可用，相关文件已改为内嵌：{exception.Message}");
            return new Dictionary<string, RemoteMatch>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<IReadOnlyDictionary<string, RemoteMatch>> MatchModrinthAsync(
        IReadOnlyList<FileHashInfo> hashes,
        ClientBuildResult result,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, ModrinthVersion> versions = await _modrinth.LookupByHashesAsync(
            hashes.Select(hash => hash.Sha1),
            "sha1",
            cancellationToken).ConfigureAwait(false);
        var matches = new Dictionary<string, RemoteMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (FileHashInfo hash in hashes)
        {
            if (!versions.TryGetValue(hash.Sha1, out ModrinthVersion? version))
            {
                result.Warnings.Add($"{hash.Plan.DisplayName}：Modrinth 未精确匹配，已内嵌原文件。");
                continue;
            }
            ModrinthFile? file = version.Files?.FirstOrDefault(candidate =>
                TryGetDeclaredHash(candidate.Hashes, "sha1", out string value)
                && value.Equals(hash.Sha1, StringComparison.OrdinalIgnoreCase));
            if (file is null
                || !TryGetDeclaredHash(file.Hashes, "sha512", out string sha512)
                || !sha512.Equals(hash.Sha512, StringComparison.OrdinalIgnoreCase)
                || !IsSafeDownloadUrl(file.Url)
                || file.Size > 0 && file.Size != hash.Length)
            {
                result.Warnings.Add($"{hash.Plan.DisplayName}：Modrinth 返回的数据无法验证，已内嵌原文件。");
                continue;
            }

            var verifiedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sha1"] = hash.Sha1,
                ["sha512"] = hash.Sha512,
            };
            matches[hash.Plan.RelativePath] = new RemoteMatch(
                hash.Plan.RelativePath,
                file.Url,
                file.Size > 0 ? file.Size : hash.Length,
                verifiedHashes,
                version.ProjectId,
                version.Id,
                0,
                0);
        }
        return matches;
    }

    private async Task<IReadOnlyDictionary<string, RemoteMatch>> MatchCurseForgeAsync(
        IReadOnlyList<FileHashInfo> hashes,
        ClientBuildResult result,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<uint, CurseForgeFile> files = await _curseForge.LookupByFingerprintsAsync(
            hashes.Where(hash => hash.CurseForgeFingerprint.HasValue)
                .Select(hash => hash.CurseForgeFingerprint!.Value),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<long, CurseForgeProject> projects = await _curseForge.GetProjectsByIdsAsync(
            files.Values.Where(file => file is not null).Select(file => file.ModId),
            cancellationToken).ConfigureAwait(false);
        var matches = new Dictionary<string, RemoteMatch>(StringComparer.OrdinalIgnoreCase);
        var usedFiles = new HashSet<(long ProjectId, long FileId)>();
        foreach (FileHashInfo hash in hashes)
        {
            if (!hash.CurseForgeFingerprint.HasValue)
            {
                result.Warnings.Add($"{hash.Plan.DisplayName}：文件过大，无法计算 CurseForge 指纹，已内嵌原文件。");
                continue;
            }
            if (!files.TryGetValue(hash.CurseForgeFingerprint.Value, out CurseForgeFile? file)
                || file.ModId <= 0 || file.Id <= 0
                || file.FileLength > 0 && file.FileLength != hash.Length
                || !CurseForgeHashMatches(file, hash.Sha1)
                || !CurseForgePathMatches(file, hash.Plan)
                || !projects.TryGetValue(file.ModId, out CurseForgeProject? project)
                || !CurseForgeClassMatches(project.ClassId, hash.Plan.Kind))
            {
                result.Warnings.Add($"{hash.Plan.DisplayName}：CurseForge 未精确匹配，已内嵌原文件。");
                continue;
            }
            if (!usedFiles.Add((file.ModId, file.Id)))
            {
                result.Warnings.Add($"{hash.Plan.DisplayName}：与另一文件匹配到相同 CurseForge 文件，已内嵌以保留内容。");
                continue;
            }
            matches[hash.Plan.RelativePath] = new RemoteMatch(
                hash.Plan.RelativePath,
                file.DownloadUrl,
                file.FileLength > 0 ? file.FileLength : hash.Length,
                new Dictionary<string, string> { ["sha1"] = hash.Sha1 },
                file.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Empty,
                file.ModId,
                file.Id);
        }
        return matches;
    }

    private static async Task<FileHashInfo[]> ComputeHashesAsync(
        ClientFilePlan[] plans,
        bool includeCurseForgeFingerprint,
        CancellationToken cancellationToken)
    {
        var result = new FileHashInfo[plans.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, plans.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(HashConcurrency, Math.Max(1, Environment.ProcessorCount)),
            },
            async (index, token) =>
            {
                result[index] = await ComputeHashAsync(plans[index], includeCurseForgeFingerprint, token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        return result;
    }

    private static async Task<FileHashInfo> ComputeHashAsync(
        ClientFilePlan plan,
        bool includeCurseForgeFingerprint,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(plan.SourcePath);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long normalizedLength = 0;
        try
        {
            await using var stream = OpenSourceFile(info.FullName);
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                sha1.AppendData(buffer, 0, read);
                sha512.AppendData(buffer, 0, read);
                if (includeCurseForgeFingerprint)
                {
                    for (int index = 0; index < read; index++)
                    {
                        if (!IsCurseForgeWhitespace(buffer[index]))
                        {
                            normalizedLength++;
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        uint? fingerprint = includeCurseForgeFingerprint
            ? await ComputeCurseForgeFingerprintAsync(plan.SourcePath, normalizedLength, cancellationToken)
                .ConfigureAwait(false)
            : 0;
        return new FileHashInfo(
            plan,
            Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(),
            info.Length,
            fingerprint);
    }

    private static async Task<uint?> ComputeCurseForgeFingerprintAsync(
        string sourcePath,
        long normalizedLength,
        CancellationToken cancellationToken)
    {
        if (normalizedLength > uint.MaxValue)
        {
            return null;
        }

        const uint multiplier = 0x5bd1e995;
        uint hash = 1u ^ (uint)normalizedLength;
        byte[] input = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var tail = new byte[4];
        int tailLength = 0;
        try
        {
            await using var stream = OpenSourceFile(sourcePath);
            while (true)
            {
                int read = await stream.ReadAsync(input.AsMemory(0, input.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                for (int index = 0; index < read; index++)
                {
                    byte value = input[index];
                    if (IsCurseForgeWhitespace(value))
                    {
                        continue;
                    }
                    tail[tailLength++] = value;
                    if (tailLength == 4)
                    {
                        uint part = BinaryPrimitives.ReadUInt32LittleEndian(tail);
                        part *= multiplier;
                        part ^= part >> 24;
                        part *= multiplier;
                        hash *= multiplier;
                        hash ^= part;
                        tailLength = 0;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
        }

        switch (tailLength)
        {
            case 3:
                hash ^= (uint)tail[2] << 16;
                goto case 2;
            case 2:
                hash ^= (uint)tail[1] << 8;
                goto case 1;
            case 1:
                hash ^= tail[0];
                hash *= multiplier;
                break;
        }
        hash ^= hash >> 13;
        hash *= multiplier;
        hash ^= hash >> 15;
        return hash;
    }

    private static async Task CopyEmbeddedFilesAsync(
        string stagingRoot,
        IReadOnlyList<ClientFilePlan> plans,
        IReadOnlyDictionary<string, RemoteMatch> matches,
        ClientBuildResult result,
        CancellationToken cancellationToken)
    {
        string overrides = Path.Combine(stagingRoot, "overrides");
        foreach (ClientFilePlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.ContainsKey(plan.RelativePath))
            {
                result.RemoteItems++;
                continue;
            }
            string destination = ResolveUnderRoot(overrides, plan.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (FileStream input = OpenSourceFile(plan.SourcePath))
            await using (var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            }
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(plan.SourcePath));
            result.EmbeddedItems++;
        }
    }

    private static async Task WriteCurseForgeManifestAsync(
        string stagingRoot,
        ClientPackSource source,
        IReadOnlyList<ClientFilePlan> plans,
        IReadOnlyDictionary<string, RemoteMatch> matches,
        CancellationToken cancellationToken)
    {
        LoaderMetadata loader = GetLoaderMetadata(source);
        var modLoaders = new JsonArray();
        if (!loader.IsVanilla)
        {
            modLoaders.Add(new JsonObject
            {
                ["id"] = $"{loader.CurseForgeId}-{source.LoaderVersion}",
                ["primary"] = true,
            });
        }
        var files = new JsonArray();
        foreach (RemoteMatch match in plans
                     .Select(plan => matches.GetValueOrDefault(plan.RelativePath))
                     .Where(match => match is not null)
                     .Cast<RemoteMatch>())
        {
            files.Add(new JsonObject
            {
                ["projectID"] = match.CurseForgeProjectId,
                ["fileID"] = match.CurseForgeFileId,
                ["required"] = true,
            });
        }

        var manifest = new JsonObject
        {
            ["minecraft"] = new JsonObject
            {
                ["version"] = source.MinecraftVersion,
                ["modLoaders"] = modLoaders,
            },
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = PackName(source),
            ["version"] = "1.0.0",
            ["author"] = string.Empty,
            ["files"] = files,
            ["overrides"] = "overrides",
        };
        await WriteJsonAsync(Path.Combine(stagingRoot, "manifest.json"), manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteModrinthIndexAsync(
        string stagingRoot,
        ClientPackSource source,
        IReadOnlyList<ClientFilePlan> plans,
        IReadOnlyDictionary<string, RemoteMatch> matches,
        CancellationToken cancellationToken)
    {
        LoaderMetadata loader = GetLoaderMetadata(source);
        var dependencies = new JsonObject { ["minecraft"] = source.MinecraftVersion };
        if (!loader.IsVanilla)
        {
            dependencies[loader.ModrinthId] = source.LoaderVersion;
        }
        var files = new JsonArray();
        foreach (ClientFilePlan plan in plans)
        {
            if (!matches.TryGetValue(plan.RelativePath, out RemoteMatch? match))
            {
                continue;
            }
            var hashes = new JsonObject();
            foreach ((string name, string value) in match.Hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                hashes[name] = value;
            }
            files.Add(new JsonObject
            {
                ["path"] = match.RelativePath,
                ["hashes"] = hashes,
                ["downloads"] = new JsonArray(match.DownloadUrl),
                ["fileSize"] = match.FileSize,
            });
        }

        var index = new JsonObject
        {
            ["game"] = "minecraft",
            ["formatVersion"] = 1,
            ["versionId"] = "1.0.0",
            ["name"] = PackName(source),
            ["summary"] = string.Empty,
            ["files"] = files,
            ["dependencies"] = dependencies,
        };
        await WriteJsonAsync(Path.Combine(stagingRoot, "modrinth.index.json"), index, cancellationToken)
            .ConfigureAwait(false);
    }

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
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static LoaderMetadata GetLoaderMetadata(ClientPackSource source)
    {
        string normalized = SearchMatcher.NormalizeLoaderName(source.LoaderType);
        if (normalized.Length == 0 || normalized.Equals("vanilla", StringComparison.Ordinal))
        {
            return new LoaderMetadata(true, string.Empty, string.Empty);
        }
        if (string.IsNullOrWhiteSpace(source.LoaderVersion))
        {
            throw new ArgumentException("模组加载器版本不能为空。");
        }
        return normalized switch
        {
            "fabric" => new LoaderMetadata(false, "fabric", "fabric-loader"),
            "forge" => new LoaderMetadata(false, "forge", "forge"),
            "neoforge" => new LoaderMetadata(false, "neoforge", "neoforge"),
            "quilt" => new LoaderMetadata(false, "quilt", "quilt-loader"),
            _ => throw new ArgumentException($"不支持的模组加载器：{source.LoaderType}"),
        };
    }

    private static bool IsPlatformEligible(ClientContentEntry item, string relativePath)
    {
        if (item.Disabled)
        {
            return false;
        }
        string extension = Path.GetExtension(relativePath);
        return item.Kind switch
        {
            ClientContentKinds.Mod => IsUnderDirectory(relativePath, "mods")
                                      && extension.Equals(".jar", StringComparison.OrdinalIgnoreCase),
            ClientContentKinds.ResourcePack => IsUnderDirectory(relativePath, "resourcepacks")
                                               && extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
            ClientContentKinds.ShaderPack => IsUnderDirectory(relativePath, "shaderpacks")
                                             && extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsUnderDirectory(string relativePath, string directory) =>
        relativePath.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase);

    private static bool CurseForgeHashMatches(CurseForgeFile file, string sha1)
    {
        CurseForgeHash? declared = file.Hashes?.FirstOrDefault(hash => hash.Algorithm == 1);
        return declared is null || string.IsNullOrWhiteSpace(declared.Value)
            || declared.Value.Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CurseForgePathMatches(CurseForgeFile file, ClientFilePlan plan)
    {
        if (string.IsNullOrWhiteSpace(file.FileName)
            || !Path.GetFileName(file.FileName).Equals(file.FileName, StringComparison.Ordinal))
        {
            return false;
        }
        string directory = plan.Kind switch
        {
            ClientContentKinds.Mod => "mods",
            ClientContentKinds.ResourcePack => "resourcepacks",
            ClientContentKinds.ShaderPack => "shaderpacks",
            _ => string.Empty,
        };
        return directory.Length > 0
            && plan.RelativePath.Equals($"{directory}/{file.FileName}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CurseForgeClassMatches(int classId, string kind) => kind switch
    {
        ClientContentKinds.Mod => classId == 6,
        ClientContentKinds.ResourcePack => classId == 12,
        ClientContentKinds.ShaderPack => classId == 6552,
        _ => false,
    };

    private static bool TryGetDeclaredHash(
        IReadOnlyDictionary<string, string>? hashes,
        string algorithm,
        out string value)
    {
        if (hashes is not null)
        {
            foreach ((string name, string candidate) in hashes)
            {
                if (name.Equals(algorithm, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(candidate))
                {
                    value = candidate;
                    return true;
                }
            }
        }
        value = string.Empty;
        return false;
    }

    private static bool IsSafeDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static FileStream OpenSourceFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"源文件不存在或是重解析点：{path}");
        }
        return new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void EnsureSourceMatchesRelativePath(
        string contentRoot,
        string sourcePath,
        string relativePath)
    {
        string expected = ResolveUnderRoot(contentRoot, relativePath);
        if (!expected.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("源路径与游戏目录中的相对路径不一致。");
        }
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root);
        string resolved = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(fullRoot, resolved);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("相对路径超出了允许的根目录。");
        }
        return resolved;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        string[] segments = ArchiveSafety.ValidateEntryPath(relativePath.Replace('\\', '/'));
        if (segments.Length == 0)
        {
            throw new InvalidDataException("相对路径不能为空。");
        }
        return string.Join('/', segments);
    }

    private static string CombineRelativePath(string parent, string name) =>
        $"{parent.TrimEnd('/')}/{name}";

    private static bool IsCurseForgeWhitespace(byte value) => value is 9 or 10 or 13 or 32;

    private static string PackName(ClientPackSource source) =>
        string.IsNullOrWhiteSpace(source.DisplayName) ? "Minecraft Client Pack" : source.DisplayName.Trim();

    private static void TryDeleteStagingDirectory(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (Path.GetFileName(fullPath).StartsWith("mc-client-pack-", StringComparison.Ordinal)
                && fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort and must not hide the build result.
        }
    }

    private sealed record ClientFilePlan(
        string SourcePath,
        string RelativePath,
        string DisplayName,
        string Kind,
        bool Disabled,
        bool PlatformEligible);

    private sealed record FileHashInfo(
        ClientFilePlan Plan,
        string Sha1,
        string Sha512,
        long Length,
        uint? CurseForgeFingerprint);

    private sealed record RemoteMatch(
        string RelativePath,
        string DownloadUrl,
        long FileSize,
        IReadOnlyDictionary<string, string> Hashes,
        string ProjectId,
        string VersionId,
        long CurseForgeProjectId,
        long CurseForgeFileId);

    private sealed record LoaderMetadata(bool IsVanilla, string CurseForgeId, string ModrinthId);
}
