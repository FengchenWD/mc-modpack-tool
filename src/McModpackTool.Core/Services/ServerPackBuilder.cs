using System.Text;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed class ServerPackBuilder : IDisposable
{
    private static readonly HashSet<string> OptionalDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "defaultconfigs", "kubejs", "scripts",
    };

    private readonly ServerCoreService _coreService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsCoreService;
    private readonly bool _ownsHttpClient;

    public ServerPackBuilder(ServerCoreService? coreService = null, HttpClient? httpClient = null)
    {
        _ownsCoreService = coreService is null;
        _ownsHttpClient = httpClient is null;
        _coreService = coreService ?? new ServerCoreService();
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<ServerBuildResult> BuildAsync(
        ServerBuildRequest request,
        ServerCoreOption coreOption,
        string javaExecutable = "java",
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(coreOption);
        ValidateRequest(request, coreOption);

        var result = new ServerBuildResult();
        var outputPath = Path.GetFullPath(request.OutputPath);
        if (File.Exists(outputPath) && !request.Overwrite)
        {
            result.MissingFiles.Add("The output ZIP already exists.");
            return result;
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"mc-modpack-tool-server-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            var plans = CreateModPlans(request, result);
            if (!result.Succeeded)
            {
                return result;
            }

            progress?.Report("Installing the selected server core...");
            var coreResult = await _coreService.InstallAsync(
                new ServerCoreInstallRequest
                {
                    Option = coreOption,
                    DestinationDirectory = stagingRoot,
                    JavaExecutable = javaExecutable,
                },
                cancellationToken).ConfigureAwait(false);
            if (!coreResult.Succeeded)
            {
                result.MissingFiles.AddRange(coreResult.Errors.Select(error => $"Server core: {error}"));
                return result;
            }

            progress?.Report("Writing selected mods...");
            await WriteModsAsync(plans, stagingRoot, result, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return result;
            }

            progress?.Report("Copying server configuration and world...");
            await CopyOptionalDirectoriesAsync(request, stagingRoot, cancellationToken).ConfigureAwait(false);
            if (request.World is not null)
            {
                await ArchiveSafety.CopyDirectoryAsync(
                    request.World.SourcePath,
                    Path.Combine(stagingRoot, "world"),
                    cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, "eula.txt"),
                $"eula={request.EulaAccepted.ToString().ToLowerInvariant()}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, "start.bat"),
                CreateStartScript(coreResult.LaunchCommand),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            progress?.Report("Compressing the server ZIP...");
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
            TryDeleteDirectory(stagingRoot);
        }
    }

    public void Dispose()
    {
        if (_ownsCoreService)
        {
            _coreService.Dispose();
        }
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static void ValidateRequest(ServerBuildRequest request, ServerCoreOption option)
    {
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetMinecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        if (!Path.GetExtension(request.OutputPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The server output must be a .zip file.", nameof(request));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CoreId);
        if (!option.Id.Equals(request.CoreId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected server core does not match the build request.");
        }
        if (!option.MinecraftVersion.Equals(request.TargetMinecraftVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected server core does not match the target Minecraft version.");
        }
        string sourceLoader = SearchMatcher.NormalizeLoaderName(request.Source.LoaderType);
        string targetLoader = SearchMatcher.NormalizeLoaderName(request.TargetLoaderType);
        if (!sourceLoader.Equals(targetLoader, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Changing the mod loader is not supported by server packaging.");
        }
        if (request.Source.InputKind == ServerInputKinds.Directory &&
            (!request.Source.MinecraftVersion.Equals(request.TargetMinecraftVersion, StringComparison.Ordinal)
             || !request.Source.LoaderVersion.Equals(request.TargetLoaderVersion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Folder imports cannot change the Minecraft or loader version.");
        }
        if (!option.Id.Equals(ServerCoreIds.Vanilla, StringComparison.OrdinalIgnoreCase) &&
            (!SearchMatcher.NormalizeLoaderName(option.LoaderType).Equals(targetLoader, StringComparison.Ordinal)
             || !option.LoaderVersion.Equals(request.TargetLoaderVersion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The selected server core does not match the target mod loader version.");
        }
    }

    private static IReadOnlyList<ModWritePlan> CreateModPlans(
        ServerBuildRequest request,
        ServerBuildResult result)
    {
        var plans = new List<ModWritePlan>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool migrated = request.Source.InputKind != ServerInputKinds.Directory &&
            (!request.Source.MinecraftVersion.Equals(request.TargetMinecraftVersion, StringComparison.Ordinal)
             || !SearchMatcher.NormalizeLoaderName(request.Source.LoaderType).Equals(
                 SearchMatcher.NormalizeLoaderName(request.TargetLoaderType), StringComparison.Ordinal));

        foreach (ServerModEntry entry in request.Source.Mods.Where(entry => entry.Selected))
        {
            try
            {
                var plan = entry.Origin == ServerModOrigins.Local
                    ? CreateLocalPlan(entry)
                    : CreateManifestPlan(entry, migrated);
                if (!destinations.Add(plan.RelativePath))
                {
                    result.MissingFiles.Add($"Duplicate mod output path: mods/{plan.RelativePath}");
                    continue;
                }
                plans.Add(plan);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                result.MissingFiles.Add($"{entry.Name}: {exception.Message}");
            }
        }
        return plans;
    }

    private static ModWritePlan CreateLocalPlan(ServerModEntry entry)
    {
        if (!File.Exists(entry.SourcePath))
        {
            throw new InvalidOperationException("The local mod file no longer exists.");
        }
        return new ModWritePlan(
            NormalizeModPath(entry.RelativePath),
            entry.SourcePath,
            [],
            0,
            new Dictionary<string, string>());
    }

    private static ModWritePlan CreateManifestPlan(ServerModEntry entry, bool migrated)
    {
        ContentItem item = entry.ContentItem
            ?? throw new InvalidOperationException("The manifest mod has no download metadata.");
        if (migrated && item.Status is not ("found" or "warning"))
        {
            throw new InvalidOperationException("No target version was resolved for this mod.");
        }

        string fileName = migrated ? item.TargetFileName : item.FileName;
        ArchiveSafety.ValidateLocalName(fileName);
        var urls = migrated
            ? new[] { item.TargetDownloadUrl }.Where(url => !string.IsNullOrWhiteSpace(url)).ToArray()
            : item.DownloadUrls.Prepend(item.DownloadUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        urls = urls.Where(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps)
            .ToArray();
        IReadOnlyDictionary<string, string> hashes = migrated ? item.TargetHashes : item.Hashes;
        if (fileName.Length == 0 || urls.Length == 0 || hashes.Count == 0)
        {
            throw new InvalidOperationException("The platform does not provide a secure downloadable file and checksum.");
        }

        string parent = string.Empty;
        if (!string.IsNullOrWhiteSpace(entry.RelativePath))
        {
            string normalizedOriginal = NormalizeModPath(entry.RelativePath);
            parent = Path.GetDirectoryName(normalizedOriginal.Replace('/', Path.DirectorySeparatorChar))?
                .Replace('\\', '/') ?? string.Empty;
        }
        string relativePath = parent.Length == 0 ? fileName : $"{parent}/{fileName}";
        if (entry.Disabled && !relativePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            relativePath += ".disabled";
        }
        return new ModWritePlan(
            NormalizeModPath(relativePath),
            string.Empty,
            urls,
            migrated ? item.TargetFileSize : item.FileSize,
            hashes);
    }

    private async Task WriteModsAsync(
        IReadOnlyList<ModWritePlan> plans,
        string stagingRoot,
        ServerBuildResult result,
        CancellationToken cancellationToken)
    {
        foreach (ModWritePlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(
                stagingRoot,
                "mods",
                plan.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (plan.SourcePath.Length > 0)
            {
                await CopyFileAsync(plan.SourcePath, destination, cancellationToken).ConfigureAwait(false);
                continue;
            }

            bool downloaded = false;
            foreach (string url in plan.DownloadUrls)
            {
                try
                {
                    downloaded = await ArchiveSafety.DownloadFileAsync(
                        _httpClient,
                        url,
                        Path.GetDirectoryName(destination)!,
                        Path.GetFileName(destination),
                        expectedSize: plan.ExpectedSize,
                        expectedHashes: plan.ExpectedHashes,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    downloaded = false;
                }
                if (downloaded)
                {
                    break;
                }
            }
            if (!downloaded)
            {
                result.MissingFiles.Add($"mods/{plan.RelativePath}");
            }
        }
    }

    private static async Task CopyOptionalDirectoriesAsync(
        ServerBuildRequest request,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var selected = new HashSet<string>(request.IncludedOptionalDirectories, StringComparer.OrdinalIgnoreCase);
        if (request.IncludeConfig)
        {
            selected.Add("config");
        }
        foreach (string name in selected)
        {
            if (!OptionalDirectoryNames.Contains(name) ||
                !request.Source.OptionalDirectories.TryGetValue(name, out string? source))
            {
                continue;
            }
            await ArchiveSafety.CopyDirectoryAsync(
                source,
                Path.Combine(stagingRoot, name),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string NormalizeModPath(string relativePath)
    {
        string[] segments = ArchiveSafety.ValidateEntryPath(relativePath.Replace('\\', '/'));
        if (segments.Length == 0)
        {
            throw new InvalidDataException("The mod output path is empty.");
        }
        return string.Join('/', segments);
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A selected mod cannot be a reparse point.");
        }
        await using var input = new FileStream(
            source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            ArchiveSafetyOptions.Default.CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            ArchiveSafetyOptions.Default.CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, ArchiveSafetyOptions.Default.CopyBufferBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string CreateStartScript(string launchCommand) =>
        $"@echo off\r\ntitle Minecraft Server\r\n{launchCommand}\r\npause\r\n";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A failed cleanup must not replace the actionable build result.
        }
    }

    private sealed record ModWritePlan(
        string RelativePath,
        string SourcePath,
        IReadOnlyList<string> DownloadUrls,
        long ExpectedSize,
        IReadOnlyDictionary<string, string> ExpectedHashes);
}
