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
        IProgress<ServerBuildPhase>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<DownloadTransferProgress>? transferProgress = null)
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

            progress?.Report(ServerBuildPhase.DownloadingCore);
            var coreResult = await _coreService.InstallAsync(
                new ServerCoreInstallRequest
                {
                    Option = coreOption,
                    DestinationDirectory = stagingRoot,
                    JavaExecutable = javaExecutable,
                },
                cancellationToken,
                transferProgress).ConfigureAwait(false);
            if (!coreResult.Succeeded)
            {
                result.MissingFiles.AddRange(coreResult.Errors.Select(error => $"Server core: {error}"));
                return result;
            }

            await WriteModsAsync(
                plans,
                stagingRoot,
                result,
                progress,
                transferProgress,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return result;
            }

            bool hasOptionalDirectories =
                request.IncludeConfig && request.Source.OptionalDirectories.ContainsKey("config") ||
                request.IncludedOptionalDirectories.Any(name =>
                    OptionalDirectoryNames.Contains(name) && request.Source.OptionalDirectories.ContainsKey(name));
            if (hasOptionalDirectories)
            {
                progress?.Report(ServerBuildPhase.CopyingConfiguration);
                await CopyOptionalDirectoriesAsync(request, stagingRoot, cancellationToken).ConfigureAwait(false);
            }
            if (request.World is not null)
            {
                progress?.Report(ServerBuildPhase.CopyingWorld);
                await ArchiveSafety.CopyDirectoryAsync(
                    request.World.SourcePath,
                    Path.Combine(stagingRoot, "world"),
                    cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(ServerBuildPhase.WritingLaunchFiles);
            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, "eula.txt"),
                $"eula={request.EulaAccepted.ToString().ToLowerInvariant()}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, "server-console.ps1"),
                CreateServerConsoleScript(coreResult.LaunchCommand, javaExecutable),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, "start.bat"),
                CreateStartScript(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(ServerBuildPhase.CompressingArchive);
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
        if (!option.MinecraftVersion.Equals(request.Source.MinecraftVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected server core does not match the source Minecraft version.");
        }
        string sourceLoader = SearchMatcher.NormalizeLoaderName(request.Source.LoaderType);
        if (!option.Id.Equals(ServerCoreIds.Vanilla, StringComparison.OrdinalIgnoreCase) &&
            (!SearchMatcher.NormalizeLoaderName(option.LoaderType).Equals(sourceLoader, StringComparison.Ordinal)
             || !option.LoaderVersion.Equals(request.Source.LoaderVersion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The selected server core does not match the source mod loader version.");
        }
    }

    private static IReadOnlyList<ModWritePlan> CreateModPlans(
        ServerBuildRequest request,
        ServerBuildResult result)
    {
        var plans = new List<ModWritePlan>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ServerModEntry entry in request.Source.Mods.Where(entry => entry.Selected))
        {
            try
            {
                var plan = entry.Origin == ServerModOrigins.Local
                    ? CreateLocalPlan(entry)
                    : CreateManifestPlan(entry);
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

    private static ModWritePlan CreateManifestPlan(ServerModEntry entry)
    {
        ContentItem item = entry.ContentItem
            ?? throw new InvalidOperationException("The manifest mod has no download metadata.");

        string fileName = item.FileName;
        ArchiveSafety.ValidateLocalName(fileName);
        var urls = item.DownloadUrls.Prepend(item.DownloadUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        urls = urls.Where(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps)
            .ToArray();
        IReadOnlyDictionary<string, string> hashes = item.Hashes;
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
            item.FileSize,
            hashes);
    }

    private async Task WriteModsAsync(
        IReadOnlyList<ModWritePlan> plans,
        string stagingRoot,
        ServerBuildResult result,
        IProgress<ServerBuildPhase>? progress,
        IProgress<DownloadTransferProgress>? transferProgress,
        CancellationToken cancellationToken)
    {
        ModWritePlan[] localPlans = plans.Where(plan => plan.SourcePath.Length > 0).ToArray();
        if (localPlans.Length > 0)
        {
            progress?.Report(ServerBuildPhase.CopyingMods);
            foreach (ModWritePlan plan in localPlans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = ModDestination(stagingRoot, plan);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(plan.SourcePath, destination, cancellationToken).ConfigureAwait(false);
            }
        }

        ModWritePlan[] downloadPlans = plans.Where(plan => plan.SourcePath.Length == 0).ToArray();
        if (downloadPlans.Length == 0)
        {
            return;
        }
        progress?.Report(ServerBuildPhase.DownloadingMods);
        foreach (ModWritePlan plan in downloadPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = ModDestination(stagingRoot, plan);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
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
                        cancellationToken: cancellationToken,
                        transferProgress: transferProgress).ConfigureAwait(false);
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

    private static string ModDestination(string stagingRoot, ModWritePlan plan) => Path.Combine(
        stagingRoot,
        "mods",
        plan.RelativePath.Replace('/', Path.DirectorySeparatorChar));

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

    private static string CreateStartScript() => NormalizeWindowsLineEndings(
        """
        @echo off
        title Minecraft Server Console
        cd /d "%~dp0"
        set "MC_TOOL_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
        if not exist "%MC_TOOL_POWERSHELL%" set "MC_TOOL_POWERSHELL=powershell.exe"
        "%MC_TOOL_POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0server-console.ps1"
        exit /b %ERRORLEVEL%
        """);

    private static string CreateServerConsoleScript(string launchCommand, string? javaExecutable)
    {
        string selectedJava = (javaExecutable ?? string.Empty).Trim().Trim('"');
        string absoluteJava = TryGetAbsoluteJavaPath(selectedJava, out string path) ? path : string.Empty;
        return NormalizeWindowsLineEndings(ServerConsoleTemplate
            .Replace("__MC_TOOL_SELECTED_JAVA__", EscapePowerShellLiteral(absoluteJava), StringComparison.Ordinal)
            .Replace("__MC_TOOL_LAUNCH_COMMAND__", EscapePowerShellLiteral(launchCommand), StringComparison.Ordinal));
    }

    private const string ServerConsoleTemplate = """
        [CmdletBinding()]
        param()

        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'
        Set-Location -LiteralPath $PSScriptRoot
        $script:ServerStarted = $false

        function Set-ServerTitle([string] $Title) {
            try { $Host.UI.RawUI.WindowTitle = $Title } catch { }
        }

        function Wait-BeforeClose {
            Write-Host ''
            Write-Host 'Press any key to close this window...' -ForegroundColor DarkGray
            try { [void] [Console]::ReadKey($true) } catch { [void] (Read-Host) }
        }

        function Write-ServerLine([AllowNull()] [string] $Line) {
            if ($null -eq $Line) { return }
            if ($Line -match '(?i)\bDone \([^)]+\)![^\r\n]*For help') {
                Write-Host $Line -ForegroundColor Green
                if (-not $script:ServerStarted) {
                    $script:ServerStarted = $true
                    Set-ServerTitle 'Minecraft Server - Running'
                    Write-Host ''
                    Write-Host '============================================================' -ForegroundColor Green
                    Write-Host ' SERVER STARTED SUCCESSFULLY - COMMANDS ARE READY' -ForegroundColor Green
                    Write-Host '============================================================' -ForegroundColor Green
                    Write-Host ''
                }
                return
            }
            if ($Line -match '(?i)(\bERROR\b|\bFATAL\b|Exception(?:\s|:|$)|\b[A-Za-z0-9_.$]+Error\b|Caused by:|^\s*at\s+\S+|^\s*Suppressed:|^\s*\.\.\. \d+ more)') {
                Write-Host $Line -ForegroundColor Red
                return
            }
            if ($Line -match '(?i)\bWARN(?:ING)?\b') {
                Write-Host $Line -ForegroundColor Yellow
                return
            }
            Write-Host $Line
        }

        Set-ServerTitle 'Minecraft Server - Starting'
        Write-Host 'Minecraft Server Console' -ForegroundColor Cyan
        Write-Host 'Warnings are yellow; errors are red; successful startup is green.' -ForegroundColor DarkGray
        Write-Host ''

        $selectedJava = '__MC_TOOL_SELECTED_JAVA__'
        $javaExecutable = $null
        if ($selectedJava -and (Test-Path -LiteralPath $selectedJava -PathType Leaf)) {
            $javaExecutable = $selectedJava
        }
        if (-not $javaExecutable -and $env:JAVA_HOME) {
            $candidate = Join-Path $env:JAVA_HOME 'bin\java.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $javaExecutable = $candidate }
        }
        if (-not $javaExecutable) {
            $javaCommand = Get-Command java.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($javaCommand) { $javaExecutable = $javaCommand.Source }
        }
        if (-not $javaExecutable) {
            Write-Host 'No usable Java runtime was found.' -ForegroundColor Red
            Write-Host 'Install the required Java version or configure JAVA_HOME/PATH.' -ForegroundColor Red
            Wait-BeforeClose
            exit 1
        }

        $javaBin = Split-Path -Parent $javaExecutable
        $env:JAVA_HOME = Split-Path -Parent $javaBin
        $env:PATH = $javaBin + ';' + $env:PATH
        Write-Host ('Java: ' + $javaExecutable) -ForegroundColor DarkGray

        $launchCommand = '__MC_TOOL_LAUNCH_COMMAND__'
        $commandProcessor = $env:ComSpec
        if (-not $commandProcessor) { $commandProcessor = Join-Path $env:SystemRoot 'System32\cmd.exe' }
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $commandProcessor
        $startInfo.Arguments = '/D /S /C "' + $launchCommand + '"'
        $startInfo.WorkingDirectory = $PSScriptRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $false
        $startInfo.RedirectStandardInput = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $startInfo
        try {
            if (-not $process.Start()) { throw 'The server process did not start.' }
            $stdoutTask = $process.StandardOutput.ReadLineAsync()
            $stderrTask = $process.StandardError.ReadLineAsync()
            while ($null -ne $stdoutTask -or $null -ne $stderrTask) {
                $handledLine = $false
                if ($null -ne $stdoutTask -and $stdoutTask.IsCompleted) {
                    $line = $stdoutTask.GetAwaiter().GetResult()
                    if ($null -eq $line) { $stdoutTask = $null }
                    else { Write-ServerLine $line; $stdoutTask = $process.StandardOutput.ReadLineAsync() }
                    $handledLine = $true
                }
                if ($null -ne $stderrTask -and $stderrTask.IsCompleted) {
                    $line = $stderrTask.GetAwaiter().GetResult()
                    if ($null -eq $line) { $stderrTask = $null }
                    else { Write-ServerLine $line; $stderrTask = $process.StandardError.ReadLineAsync() }
                    $handledLine = $true
                }
                if (-not $handledLine) { Start-Sleep -Milliseconds 15 }
            }
            $process.WaitForExit()
            $exitCode = $process.ExitCode
        }
        catch {
            Write-Host ('Server console failed: ' + $_.Exception.Message) -ForegroundColor Red
            Set-ServerTitle 'Minecraft Server - Console Error'
            Wait-BeforeClose
            exit 1
        }
        finally {
            $process.Dispose()
        }

        if ($exitCode -ne 0) {
            Set-ServerTitle 'Minecraft Server - Failed'
            Write-Host ''
            Write-Host ('SERVER STOPPED WITH EXIT CODE ' + $exitCode) -ForegroundColor Red
        }
        elseif (-not $script:ServerStarted) {
            Set-ServerTitle 'Minecraft Server - Stopped'
            Write-Host ''
            Write-Host 'The server stopped before a successful startup message was detected.' -ForegroundColor Yellow
        }
        else {
            Set-ServerTitle 'Minecraft Server - Stopped'
            Write-Host ''
            Write-Host 'Server stopped normally.' -ForegroundColor Green
        }
        Wait-BeforeClose
        exit $exitCode
        """;

    private static bool TryGetAbsoluteJavaPath(string value, out string path)
    {
        path = string.Empty;
        if (value.Length == 0 || !Path.IsPathFullyQualified(value))
        {
            return false;
        }
        try
        {
            path = Path.GetFullPath(value);
            return Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("java", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string NormalizeWindowsLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

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
