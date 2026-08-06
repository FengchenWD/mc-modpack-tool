using System.Diagnostics;
using System.Text.RegularExpressions;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed partial class ServerCoreService : IDisposable
{
    public const string CardboardProjectId = "MLYQ9VGP";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly IServerCoreJavaRunner _javaRunner;
    private readonly Action<string>? _logWarning;

    public ServerCoreService(
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        IServerCoreJavaRunner? javaRunner = null,
        Action<string>? logWarning = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _javaRunner = javaRunner ?? new ServerCoreJavaRunner();
        _logWarning = logWarning;
    }

    /// <summary>
    /// Queries authoritative provider metadata. Only confirmed, exact Minecraft-version matches
    /// are returned as selectable options; a failed or ambiguous provider stays unavailable.
    /// </summary>
    public async Task<ServerCoreCatalogResult> GetAvailableAsync(
        ServerCoreQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string minecraft = NormalizeVersion(query.MinecraftVersion, nameof(query.MinecraftVersion));
        string loader = SearchMatcher.NormalizeLoaderName(query.LoaderType);
        string loaderVersion = NormalizeOptionalVersion(query.LoaderVersion, nameof(query.LoaderVersion));
        var normalized = query with
        {
            MinecraftVersion = minecraft,
            LoaderType = loader,
            LoaderVersion = loaderVersion,
        };

        var queries = new List<Task<ProviderOutcome>>
        {
            QueryProviderAsync(ServerCoreIds.Vanilla, () => QueryVanillaAsync(normalized, cancellationToken), cancellationToken),
        };

        if (loader == "fabric")
        {
            Task<ServerCoreOption?> fabric = QueryFabricAsync(normalized, cancellationToken);
            queries.Add(QueryProviderAsync(ServerCoreIds.Fabric, () => fabric, cancellationToken));
            queries.Add(QueryProviderAsync(
                ServerCoreIds.Cardboard,
                async () => await QueryCardboardAsync(normalized, await fabric.ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken));
        }
        else if (loader == "forge")
        {
            queries.Add(QueryProviderAsync(ServerCoreIds.Forge, () => QueryForgeAsync(normalized, cancellationToken), cancellationToken));
            queries.Add(QueryProviderAsync(ServerCoreIds.Mohist, () => QueryMohistAsync(normalized, cancellationToken), cancellationToken));
            queries.Add(QueryProviderAsync(ServerCoreIds.CatServer, () => QueryCatServerAsync(normalized, cancellationToken), cancellationToken));
        }
        else if (loader == "neoforge")
        {
            queries.Add(QueryProviderAsync(ServerCoreIds.NeoForge, () => QueryNeoForgeAsync(normalized, cancellationToken), cancellationToken));
        }

        ProviderOutcome[] outcomes = await Task.WhenAll(queries).ConfigureAwait(false);
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [ServerCoreIds.Fabric] = 0,
            [ServerCoreIds.Forge] = 0,
            [ServerCoreIds.NeoForge] = 0,
            [ServerCoreIds.Cardboard] = 1,
            [ServerCoreIds.Mohist] = 1,
            [ServerCoreIds.CatServer] = 2,
            [ServerCoreIds.Vanilla] = 99,
        };
        return new ServerCoreCatalogResult
        {
            Options = outcomes
                .Where(outcome => outcome.Option is not null)
                .OrderBy(outcome => order.GetValueOrDefault(outcome.CoreId, int.MaxValue))
                .Select(outcome => outcome.Option!)
                .ToArray(),
            Unavailable = outcomes
                .Where(outcome => outcome.Option is null)
                .Select(outcome => new ServerCoreUnavailable
                {
                    CoreId = outcome.CoreId,
                    Reason = outcome.Reason,
                })
                .ToArray(),
        };
    }

    /// <summary>
    /// Downloads every artifact with the provider's published size/hash data and runs Java
    /// installers when required. The destination is expected to be a fresh builder staging area.
    /// </summary>
    public async Task<ServerCoreInstallResult> InstallAsync(
        ServerCoreInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Option);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationDirectory);
        ValidateInstallPlan(request.Option);
        if (request.Option.InstallStrategy == ServerCoreInstallStrategy.JavaInstaller
            && string.IsNullOrWhiteSpace(request.JavaExecutable))
        {
            return Failed("A Java executable is required by this server core installer.");
        }

        string root = Path.GetFullPath(request.DestinationDirectory);
        if (Directory.Exists(root)
            && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The server installation directory cannot be a reparse point.");
        }
        Directory.CreateDirectory(root);

        var artifactPaths = request.Option.Artifacts.ToDictionary(
            artifact => artifact,
            artifact => ResolveDestination(root, artifact.RelativePath));
        foreach (string targetPath in artifactPaths.Values)
        {
            EnsureNoReparsePoints(root, targetPath);
        }
        if (artifactPaths.Values.Any(File.Exists))
        {
            return Failed("A server core artifact already exists in the destination directory.");
        }

        var downloaded = new List<string>();
        foreach ((ServerCoreArtifact artifact, string targetPath) in artifactPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.GetDirectoryName(targetPath)!;
            EnsureNoReparsePoints(root, targetPath);
            bool succeeded = await ArchiveSafety.DownloadFileAsync(
                    _httpClient,
                    artifact.DownloadUrl,
                    directory,
                    Path.GetFileName(targetPath),
                    expectedSize: artifact.Size,
                    expectedHashes: artifact.Hashes,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!succeeded)
            {
                DeleteFiles(downloaded);
                return Failed($"Could not download or verify server core artifact '{artifact.RelativePath}'.");
            }
            downloaded.Add(targetPath);
        }

        if (request.Option.InstallStrategy == ServerCoreInstallStrategy.DirectFiles)
        {
            string serverJar = request.Option.Artifacts
                .Where(artifact => artifact.Role == ServerCoreArtifactRole.ServerJar)
                .Select(artifact => artifactPaths[artifact])
                .Single();
            string relativeJar = Path.GetRelativePath(root, serverJar).Replace('\\', '/');
            string jarArgument = relativeJar.Equals("server.jar", StringComparison.OrdinalIgnoreCase)
                ? relativeJar
                : $"\"{relativeJar}\"";
            return Installed(root, $"java -jar {jarArgument} nogui", downloaded);
        }

        ServerCoreJavaInstaller installer = request.Option.JavaInstaller!;
        ServerCoreArtifact installerArtifact = request.Option.Artifacts.Single(artifact =>
            artifact.Role == ServerCoreArtifactRole.Installer
            && PathsEqual(artifact.RelativePath, installer.ArtifactRelativePath));
        string installerPath = artifactPaths[installerArtifact];
        int exitCode;
        try
        {
            exitCode = await _javaRunner.RunAsync(
                    request.JavaExecutable.Trim(),
                    installerPath,
                    installer.Arguments,
                    root,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed($"The server core installer could not start: {exception.Message}", root);
        }
        if (exitCode != 0)
        {
            return Failed($"The server core installer exited with code {exitCode}.", root);
        }

        if (installerArtifact.DeleteAfterInstall)
        {
            TryDeleteFile(installerPath);
        }
        string launchCommand = FindInstalledLaunchCommand(root, installerPath);
        if (launchCommand.Length == 0)
        {
            return Failed("The installer completed but no supported server launch entry was produced.", root);
        }
        return Installed(root, launchCommand);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<ProviderOutcome> QueryProviderAsync(
        string coreId,
        Func<Task<ServerCoreOption?>> query,
        CancellationToken cancellationToken)
    {
        try
        {
            var option = await query().ConfigureAwait(false);
            return option is null
                ? new ProviderOutcome(coreId, null, "no_compatible_build")
                : new ProviderOutcome(coreId, option, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logWarning?.Invoke($"Server core metadata failed ({coreId}): {exception.Message}");
            return new ProviderOutcome(coreId, null, "metadata_unavailable");
        }
    }

    private static void ValidateInstallPlan(ServerCoreOption option)
    {
        if (option.Artifacts.Count == 0)
        {
            throw new InvalidDataException("The server core option has no artifacts.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in option.Artifacts)
        {
            if (!Uri.TryCreate(artifact.DownloadUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Server core artifacts must use absolute HTTPS URLs.");
            }
            if (!OverrideContentClassifier.TryNormalizeRelativeArchivePath(
                    artifact.RelativePath.Replace('\\', '/'),
                    out string normalized,
                    out string? reason)
                || !paths.Add(normalized))
            {
                throw new InvalidDataException(reason ?? "The server core artifact path is duplicated.");
            }
        }
        if (option.InstallStrategy == ServerCoreInstallStrategy.DirectFiles)
        {
            if (option.JavaInstaller is not null
                || option.Artifacts.Count(artifact => artifact.Role == ServerCoreArtifactRole.ServerJar) != 1)
            {
                throw new InvalidDataException("A direct core requires exactly one server JAR and no Java installer.");
            }
            return;
        }
        ServerCoreArtifact[] installerArtifacts = option.Artifacts
            .Where(artifact => artifact.Role == ServerCoreArtifactRole.Installer)
            .ToArray();
        if (option.JavaInstaller is null
            || installerArtifacts.Length != 1
            || !PathsEqual(installerArtifacts[0].RelativePath, option.JavaInstaller.ArtifactRelativePath))
        {
            throw new InvalidDataException("The Java installer plan does not identify one installer artifact.");
        }
    }

    private static string ResolveDestination(string root, string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string destination = Path.GetFullPath(Path.Combine(root, normalized));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A server core artifact resolves outside the destination directory.");
        }
        return destination;
    }

    private static void EnsureNoReparsePoints(string root, string destination)
    {
        string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string destinationPath = Path.GetFullPath(destination);
        string? current = Path.GetDirectoryName(destinationPath);
        while (current is not null && current.Length >= rootPath.Length)
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A server core artifact path contains a reparse point.");
            }
            if (current.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidDataException("A server core artifact path is outside the installation directory.");
    }

    private static string FindInstalledLaunchCommand(string root, string installerPath)
    {
        if (File.Exists(Path.Combine(root, "run.bat")))
        {
            return "call run.bat nogui";
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        string? argsFile = Directory.EnumerateFiles(root, "win_args.txt", options)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (argsFile is not null)
        {
            string relative = Path.GetRelativePath(root, argsFile).Replace('\\', '/');
            string userArgs = File.Exists(Path.Combine(root, "user_jvm_args.txt"))
                ? "@user_jvm_args.txt "
                : string.Empty;
            return $"java {userArgs}@{relative} nogui";
        }

        string installerFullPath = Path.GetFullPath(installerPath);
        string? legacyJar = Directory.EnumerateFiles(root, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFullPath(path), installerFullPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return (name.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
                    && !name.Contains("installer", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("sources", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("javadoc", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return legacyJar is null
            ? string.Empty
            : $"java -jar \"{Path.GetFileName(legacyJar)}\" nogui";
    }

    private static ServerCoreInstallResult Installed(
        string root,
        string launchCommand,
        IEnumerable<string>? knownFiles = null)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        IEnumerable<string> files = Directory.EnumerateFiles(root, "*", options);
        if (knownFiles is not null)
        {
            files = files.Concat(knownFiles);
        }
        return new ServerCoreInstallResult
        {
            InstalledFiles = files
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Errors = [],
            LaunchCommand = launchCommand,
        };
    }

    private static ServerCoreInstallResult Failed(string error, string? root = null)
    {
        string[] files = [];
        if (root is not null && Directory.Exists(root))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };
            try
            {
                files = Directory.EnumerateFiles(root, "*", options)
                    .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                    .ToArray();
            }
            catch (IOException)
            {
                files = [];
            }
            catch (UnauthorizedAccessException)
            {
                files = [];
            }
        }
        return new ServerCoreInstallResult { InstalledFiles = files, Errors = [error] };
    }

    private static void DeleteFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            TryDeleteFile(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            first.Replace('\\', '/').Trim('/'),
            second.Replace('\\', '/').Trim('/'),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string value, string parameterName)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (!SafeVersionRegex().IsMatch(normalized))
        {
            throw new ArgumentException("The version is empty or contains unsupported characters.", parameterName);
        }
        return normalized;
    }

    private static string NormalizeOptionalVersion(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizeVersion(value, parameterName);

    private sealed record ProviderOutcome(string CoreId, ServerCoreOption? Option, string Reason);

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._+\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersionRegex();
}

internal sealed class ServerCoreJavaRunner : IServerCoreJavaRunner
{
    public async Task<int> RunAsync(
        string javaExecutable,
        string installerPath,
        IReadOnlyList<string> installerArguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        foreach (string argument in installerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Java installer process did not start.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        });
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
