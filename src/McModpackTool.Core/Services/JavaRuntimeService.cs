using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

/// <summary>
/// Finds Java installations without recursively walking the user's disks. The scanner is
/// intentionally limited to environment entries and common vendor folders so that it can run
/// as part of the server source read operation without introducing a long blocking delay.
/// </summary>
public sealed partial class JavaRuntimeService
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _probeTimeout;

    public JavaRuntimeService(TimeSpan? probeTimeout = null)
    {
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
    }

    public async Task<JavaRuntimeDiscoveryResult> DiscoverAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
        => await DiscoverAsync(minecraftVersion, [], cancellationToken).ConfigureAwait(false);

    public async Task<JavaRuntimeDiscoveryResult> DiscoverAsync(
        string minecraftVersion,
        IEnumerable<string> modRequirements,
        CancellationToken cancellationToken = default)
    {
        int recommendedMajor = RecommendedMajorVersion(minecraftVersion, modRequirements);
        string[] candidates = DiscoverCandidatePaths();
        var probes = candidates.Select(path => ProbeAsync(path, cancellationToken)).ToArray();
        JavaRuntimeInfo?[] results = await Task.WhenAll(probes).ConfigureAwait(false);
        JavaRuntimeInfo[] runtimes = results
            .Where(runtime => runtime is not null)
            .Select(runtime => runtime!)
            .OrderBy(runtime => runtime.MajorVersion)
            .ThenBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        JavaRuntimeInfo? recommended = SelectBest(runtimes, recommendedMajor);
        string warning = recommended is null
            ? $"No Java {recommendedMajor} runtime was found for Minecraft {minecraftVersion}."
            : recommended.MajorVersion == recommendedMajor
                ? string.Empty
                : $"Java {recommendedMajor} is recommended for Minecraft {minecraftVersion}, but only Java {recommended.MajorVersion} was found.";
        return new JavaRuntimeDiscoveryResult
        {
            Runtimes = runtimes,
            RecommendedMajorVersion = recommendedMajor,
            Recommended = recommended,
            Warning = warning,
        };
    }

    /// <summary>Probes a user-selected Java launcher, typically from a file picker.</summary>
    public async Task<JavaRuntimeInfo?> ProbeExecutableAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }
        try
        {
            string fullPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
            if (!File.Exists(fullPath))
            {
                return null;
            }
            return await ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Returns the Java feature version normally required by a Minecraft release.</summary>
    public static int RecommendedMajorVersion(string? minecraftVersion)
    {
        if (!TryParseMinecraftVersion(minecraftVersion, out int major, out int minor, out int patch))
        {
            // Current Minecraft releases use Java 21. Unknown values are safer with the
            // modern default than silently selecting an obsolete runtime.
            return 21;
        }

        if (major != 1)
        {
            return major > 1 ? 21 : 8;
        }
        if (minor <= 16)
        {
            return 8;
        }
        if (minor == 17)
        {
            return 16;
        }
        if (minor < 20 || (minor == 20 && patch <= 4))
        {
            return 17;
        }
        return 21;
    }

    /// <summary>
    /// Combines Minecraft's baseline with Java predicates declared by selected mod metadata.
    /// Unrecognized predicates are ignored rather than inventing an incompatible version.
    /// </summary>
    public static int RecommendedMajorVersion(
        string? minecraftVersion,
        IEnumerable<string>? modRequirements)
    {
        int baseline = RecommendedMajorVersion(minecraftVersion);
        string[] requirements = (modRequirements ?? [])
            .Select(requirement => requirement?.Trim() ?? string.Empty)
            .Where(requirement => requirement.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requirements.Length == 0)
        {
            return baseline;
        }

        string[] recognized = requirements.Where(requirement =>
            Enumerable.Range(baseline, 100 - baseline).Any(major =>
                VersionRequirement.Evaluate(
                    requirement,
                    major.ToString(CultureInfo.InvariantCulture)) != VersionRequirementResult.Unknown))
            .ToArray();
        for (int candidate = baseline; candidate < 100; candidate++)
        {
            string version = candidate.ToString(CultureInfo.InvariantCulture);
            if (recognized.All(requirement =>
                    VersionRequirement.Evaluate(requirement, version) == VersionRequirementResult.Satisfied))
            {
                return candidate;
            }
        }

        return baseline;
    }

    /// <summary>
    /// Chooses an exact feature-version match first. If none exists, the nearest installed
    /// runtime is returned so the UI can let the user make an informed decision.
    /// </summary>
    public static JavaRuntimeInfo? SelectBest(
        IEnumerable<JavaRuntimeInfo> runtimes,
        int recommendedMajor)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        JavaRuntimeInfo[] candidates = runtimes.ToArray();
        return candidates
            .OrderBy(runtime => runtime.MajorVersion == recommendedMajor ? 0 : 1)
            .ThenBy(runtime => Math.Abs(runtime.MajorVersion - recommendedMajor))
            // Prefer a runtime not newer than the target when distances tie. This is useful
            // for legacy Forge, whose launcher fails early on newer Java versions.
            .ThenBy(runtime => runtime.MajorVersion > recommendedMajor ? 1 : 0)
            .ThenBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private async Task<JavaRuntimeInfo?> ProbeAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-version");
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Java process did not start.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_probeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return null;
            }
            catch
            {
                TryKill(process);
                throw;
            }

            string output = string.Concat(await stdoutTask.ConfigureAwait(false), "\n", await stderrTask.ConfigureAwait(false));
            if (process.ExitCode != 0 || !TryParseVersion(output, out string version, out int major))
            {
                return null;
            }
            return new JavaRuntimeInfo
            {
                ExecutablePath = executablePath,
                Version = version,
                MajorVersion = major,
                Vendor = DetectVendor(output),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A stale PATH entry or a partially removed JRE should not prevent other runtimes
            // from being offered.
            return null;
        }
    }

    private static string[] DiscoverCandidatePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string full = Path.GetFullPath(path.Trim().Trim('"'));
                if (!File.Exists(full)) return;
                if (!Path.GetFileName(full).Equals("java.exe", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(full).Equals("java", StringComparison.OrdinalIgnoreCase)) return;
                paths.Add(full);
            }
            catch (ArgumentException) { }
            catch (IOException) { }
        }

        string executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            Add(Path.Combine(javaHome.Trim().Trim('"'), "bin", executableName));
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string directory in pathValue.Split(Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try { Add(Path.Combine(directory.Trim().Trim('"'), executableName)); }
                catch (ArgumentException) { }
            }
        }

        foreach (string root in CommonJavaRoots())
        {
            // Some Oracle installations expose a javapath directory whose launcher is
            // directly at the root rather than under root\bin.
            Add(Path.Combine(root, executableName));
            Add(Path.Combine(root, "bin", executableName));
            try
            {
                int maxDepth = root.EndsWith("runtime", StringComparison.OrdinalIgnoreCase) ? 4 : 1;
                foreach (string child in EnumerateDirectories(root, maxDepth))
                {
                    Add(Path.Combine(child, "bin", executableName));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> CommonJavaRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddRoot(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root)) roots.Add(root.Trim().Trim('"'));
        }

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string? basePath in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(basePath)) continue;
            AddRoot(Path.Combine(basePath, "Java"));
            AddRoot(Path.Combine(basePath, "Eclipse Adoptium"));
            AddRoot(Path.Combine(basePath, "Microsoft"));
            AddRoot(Path.Combine(basePath, "Amazon Corretto"));
            AddRoot(Path.Combine(basePath, "Zulu"));
            AddRoot(Path.Combine(basePath, "OpenJDK"));
            AddRoot(Path.Combine(basePath, "Common Files", "Oracle", "Java", "javapath"));
        }

        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            AddRoot(Path.Combine(localAppData, "Programs", "Eclipse Adoptium"));
            AddRoot(Path.Combine(localAppData, "Programs", "Microsoft"));
            AddRoot(Path.Combine(localAppData, ".minecraft", "runtime"));
            AddRoot(Path.Combine(localAppData, "PrismLauncher", "runtime"));
            AddRoot(Path.Combine(localAppData, "HMCL", "java"));
        }

        string? appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            AddRoot(Path.Combine(appData, ".minecraft", "runtime"));
            AddRoot(Path.Combine(appData, "PrismLauncher", "runtime"));
            AddRoot(Path.Combine(appData, "HMCL", "java"));
        }

        string? userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddRoot(Path.Combine(userProfile, ".minecraft", "runtime"));
        }
        foreach (string registryHome in RegistryJavaHomes())
        {
            AddRoot(registryHome);
        }
        return roots;
    }

    private static IEnumerable<string> RegistryJavaHomes()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        string[] keyPaths =
        [
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\JavaSoft\JRE",
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu",
            @"SOFTWARE\Amazon Corretto",
        ];
        RegistryHive[] hives = [RegistryHive.CurrentUser, RegistryHive.LocalMachine];
        RegistryView[] views = [RegistryView.Default, RegistryView.Registry32, RegistryView.Registry64];
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RegistryHive hive in hives)
        foreach (RegistryView view in views)
        {
            RegistryKey? baseKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (string keyPath in keyPaths)
                {
                    using RegistryKey? key = baseKey.OpenSubKey(keyPath);
                    if (key is null)
                    {
                        continue;
                    }

                    AddRegistryHome(key.GetValue("JavaHome") as string, homes);
                    foreach (string subKeyName in key.GetSubKeyNames())
                    {
                        using RegistryKey? subKey = key.OpenSubKey(subKeyName);
                        AddRegistryHome(subKey?.GetValue("JavaHome") as string, homes);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Registry access is optional. A missing or restricted view must not
                // prevent the environment/path scan from completing.
            }
            finally
            {
                baseKey?.Dispose();
            }
        }

        foreach (string home in homes)
        {
            yield return home;
        }
    }

    private static void AddRegistryHome(string? value, ISet<string> homes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        try
        {
            string home = value.Trim().Trim('"');
            if (home.Length > 0 && Directory.Exists(home))
            {
                homes.Add(Path.GetFullPath(home));
            }
        }
        catch (ArgumentException)
        {
            // Ignore malformed registry values.
        }
        catch (IOException)
        {
            // The installation may have been removed after its registry entry was written.
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root, int maxDepth)
    {
        if (maxDepth <= 0 || !Directory.Exists(root))
        {
            yield break;
        }
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string child in children)
        {
            yield return child;
            foreach (string descendant in EnumerateDirectories(child, maxDepth - 1))
            {
                yield return descendant;
            }
        }
    }

    private static bool TryParseVersion(string output, out string version, out int major)
    {
        Match quoted = VersionRegex().Match(output);
        version = quoted.Success ? quoted.Groups["version"].Value.Trim() : string.Empty;
        if (version.Length == 0)
        {
            Match fallback = NumberVersionRegex().Match(output);
            version = fallback.Success ? fallback.Groups["version"].Value.Trim() : string.Empty;
        }
        if (version.Length == 0 || !TryParseJavaMajor(version, out major))
        {
            major = 0;
            return false;
        }
        return true;
    }

    private static bool TryParseJavaMajor(string version, out int major)
    {
        major = 0;
        string value = version.Trim();
        if (value.StartsWith("1.", StringComparison.Ordinal))
        {
            int end = value.IndexOfAny(['.', '_', '-'], 2);
            string legacy = end < 0 ? value[2..] : value[2..end];
            return int.TryParse(legacy, out major) && major > 0;
        }
        int length = 0;
        while (length < value.Length && char.IsDigit(value[length])) length++;
        return length > 0 && int.TryParse(value[..length], out major) && major > 0;
    }

    private static string DetectVendor(string output)
    {
        string lower = output.ToLowerInvariant();
        if (lower.Contains("temurin")) return "Eclipse Temurin";
        if (lower.Contains("adoptium")) return "Eclipse Adoptium";
        if (lower.Contains("corretto")) return "Amazon Corretto";
        if (lower.Contains("microsoft")) return "Microsoft Build of OpenJDK";
        if (lower.Contains("zulu")) return "Azul Zulu";
        if (lower.Contains("oracle")) return "Oracle";
        if (lower.Contains("openjdk")) return "OpenJDK";
        return string.Empty;
    }

    private static bool TryParseMinecraftVersion(
        string? value,
        out int major,
        out int minor,
        out int patch)
    {
        major = minor = patch = 0;
        string[] parts = (value ?? string.Empty).Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor))
        {
            return false;
        }
        return parts.Length < 3 || int.TryParse(parts[2].Split('-', '+')[0], out patch);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    [GeneratedRegex(@"version\s+[""'](?<version>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(?<!\d)(?<version>1\.\d+(?:\.\d+)?(?:[_+][A-Za-z0-9._-]+)?|\d+\.\d+(?:\.\d+){0,2})", RegexOptions.CultureInvariant)]
    private static partial Regex NumberVersionRegex();
}
