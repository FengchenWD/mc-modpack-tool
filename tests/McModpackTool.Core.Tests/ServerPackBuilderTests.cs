using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class ServerPackBuilderTests
{
    public static async Task RunAllAsync()
    {
        await BuildsRunnableZipFromLocalContentAsync();
        await ManifestBuildUsesSourceArtifactAsync();
        await MissingManifestDownloadBlocksOutputAsync();
        await DownloadFailureLeavesNoOutputAsync();
        await RejectsMismatchedBuildRequestsAsync();
    }

    private static async Task ManifestBuildUsesSourceArtifactAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            byte[] serverBytes = Encoding.UTF8.GetBytes("server");
            byte[] sourceBytes = Encoding.UTF8.GetBytes("source-mod");
            int targetRequests = 0;
            HttpResponseMessage CountTargetRequest()
            {
                targetRequests++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            using var http = new HttpClient(new StubHandler(request => request.RequestUri?.AbsoluteUri switch
            {
                "https://example.test/server.jar" => BytesResponse(serverBytes),
                "https://example.test/source.jar" => BytesResponse(sourceBytes),
                "https://example.test/target.jar" => CountTargetRequest(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var cores = new ServerCoreService(http);
            using var builder = new ServerPackBuilder(cores, http);
            var item = new ContentItem
            {
                Name = "Source Mod",
                FileName = "source.jar",
                FileSize = sourceBytes.Length,
                DownloadUrl = "https://example.test/source.jar",
                Hashes = new Dictionary<string, string>
                {
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(),
                },
                TargetFileName = "target.jar",
                TargetFileSize = 1,
                TargetDownloadUrl = "https://example.test/target.jar",
                TargetHashes = new Dictionary<string, string> { ["sha256"] = new string('0', 64) },
                Status = "found",
            };
            var source = new ServerPackSource
            {
                InputKind = ServerInputKinds.Modrinth,
                MinecraftVersion = "1.21.1",
                LoaderType = "fabric",
                LoaderVersion = "0.16.10",
                Mods =
                [
                    new ServerModEntry
                    {
                        Name = item.Name,
                        Origin = ServerModOrigins.Manifest,
                        Selected = true,
                        ContentItem = item,
                    },
                ],
            };
            ServerCoreOption core = DirectCore(serverBytes);
            string output = Path.Combine(root, "source-only.zip");
            var phases = new List<ServerBuildPhase>();

            ServerBuildResult result = await builder.BuildAsync(
                Request(source, output, core), core, progress: new InlineProgress<ServerBuildPhase>(phases.Add));

            True(result.Succeeded, "A source manifest artifact could not be packaged.");
            Equal(0, targetRequests, "Server packaging requested migration target metadata.");
            True(phases.SequenceEqual([
                    ServerBuildPhase.DownloadingCore,
                    ServerBuildPhase.DownloadingMods,
                    ServerBuildPhase.WritingLaunchFiles,
                    ServerBuildPhase.CompressingArchive,
                ]), $"Unexpected manifest build phases: {string.Join(", ", phases)}");
            using var archive = ZipFile.OpenRead(output);
            Contains(archive.Entries.Select(entry => entry.FullName), "mods/source.jar");
            True(archive.GetEntry("mods/target.jar") is null, "A migration target artifact reached the server ZIP.");
        });
    }

    private static async Task BuildsRunnableZipFromLocalContentAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            byte[] serverBytes = Encoding.UTF8.GetBytes("server-core");
            using var http = new HttpClient(new StubHandler(request =>
                request.RequestUri?.AbsoluteUri == "https://example.test/server.jar"
                    ? BytesResponse(serverBytes)
                    : new HttpResponseMessage(HttpStatusCode.NotFound)));
            using var cores = new ServerCoreService(http);
            using var builder = new ServerPackBuilder(cores, http);

            string modsRoot = Path.Combine(root, "source", "mods", "nested");
            string configRoot = Path.Combine(root, "source", "config");
            string worldRoot = Path.Combine(root, "source", "saves", "Demo");
            Directory.CreateDirectory(modsRoot);
            Directory.CreateDirectory(configRoot);
            Directory.CreateDirectory(worldRoot);
            string modPath = Path.Combine(modsRoot, "example.jar");
            await File.WriteAllTextAsync(modPath, "mod");
            await File.WriteAllTextAsync(Path.Combine(configRoot, "example.toml"), "config");
            await File.WriteAllTextAsync(Path.Combine(worldRoot, "level.dat"), "level");
            Directory.CreateDirectory(Path.Combine(worldRoot, "region"));
            await File.WriteAllTextAsync(Path.Combine(worldRoot, "region", "r.0.0.mca"), "region");

            var source = new ServerPackSource
            {
                InputKind = ServerInputKinds.Directory,
                SourcePath = Path.Combine(root, "source"),
                ContentRoot = Path.Combine(root, "source"),
                MinecraftVersion = "1.21.1",
                LoaderType = "fabric",
                LoaderVersion = "0.16.10",
                Mods =
                [
                    new ServerModEntry
                    {
                        Name = "Example",
                        RelativePath = "nested/example.jar",
                        SourcePath = modPath,
                        Origin = ServerModOrigins.Local,
                        Selected = true,
                    },
                ],
                Worlds = [new ServerWorldEntry { Name = "Demo", SourcePath = worldRoot }],
                OptionalDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["config"] = configRoot,
                },
            };
            var core = DirectCore(serverBytes);
            string output = Path.Combine(root, "output", "server.zip");
            var request = Request(source, output, core, includeConfig: true, world: source.Worlds[0]);
            string selectedJava = Path.Combine(root, "selected java's & 工具", "bin", "java.exe");
            var phases = new List<ServerBuildPhase>();

            ServerBuildResult result = await builder.BuildAsync(
                request, core, selectedJava, new InlineProgress<ServerBuildPhase>(phases.Add));

            True(result.Succeeded, "A complete local server build was reported as incomplete.");
            True(phases.SequenceEqual([
                    ServerBuildPhase.DownloadingCore,
                    ServerBuildPhase.CopyingMods,
                    ServerBuildPhase.CopyingConfiguration,
                    ServerBuildPhase.CopyingWorld,
                    ServerBuildPhase.WritingLaunchFiles,
                    ServerBuildPhase.CompressingArchive,
                ]), $"Unexpected local build phases: {string.Join(", ", phases)}");
            True(File.Exists(output), "The server ZIP was not created.");
            using var archive = ZipFile.OpenRead(output);
            string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();
            Contains(entries, "server.jar");
            Contains(entries, "mods/nested/example.jar");
            Contains(entries, "config/example.toml");
            Contains(entries, "world/level.dat");
            Contains(entries, "world/region/r.0.0.mca");
            Contains(entries, "eula.txt");
            Contains(entries, "start.bat");
            Contains(entries, "server-console.ps1");
            Equal("eula=true\r\n", await ReadEntryAsync(archive, "eula.txt"), "The EULA value is wrong.");
            string startScript = await ReadEntryAsync(archive, "start.bat");
            True(startScript.Contains("server-console.ps1", StringComparison.Ordinal)
                 && startScript.Contains("-ExecutionPolicy Bypass", StringComparison.Ordinal),
                "start.bat does not invoke the PowerShell server console reliably.");
            string consoleScript = await ReadEntryAsync(archive, "server-console.ps1");
            True(consoleScript.Contains(selectedJava.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal),
                "The selected Java path was not written to server-console.ps1.");
            True(consoleScript.Contains("java -jar server.jar nogui", StringComparison.Ordinal)
                 && consoleScript.Contains("JAVA_HOME", StringComparison.Ordinal),
                "server-console.ps1 does not preserve the launch command and Java fallback environment.");
            True(consoleScript.Contains("RedirectStandardInput = $false", StringComparison.Ordinal)
                 && consoleScript.Contains("ReadLineAsync()", StringComparison.Ordinal),
                "The colored console must preserve interactive server input while draining both output streams.");
            True(consoleScript.Contains("ForegroundColor Yellow", StringComparison.Ordinal)
                 && consoleScript.Contains("ForegroundColor Red", StringComparison.Ordinal)
                 && consoleScript.Contains("SERVER STARTED SUCCESSFULLY", StringComparison.Ordinal),
                "The colored console is missing warning, error, or successful-start highlighting.");
            True(!startScript.Contains("\r\r\n", StringComparison.Ordinal)
                 && !consoleScript.Contains("\r\r\n", StringComparison.Ordinal),
                "A generated launch script contains malformed doubled carriage returns.");
            ZipArchiveEntry consoleEntry = archive.GetEntry("server-console.ps1")!;
            await using Stream consoleStream = consoleEntry.Open();
            var bom = new byte[3];
            await consoleStream.ReadExactlyAsync(bom);
            True(bom.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }),
                "server-console.ps1 must have a UTF-8 BOM for Windows PowerShell 5.1.");
            if (OperatingSystem.IsWindows())
            {
                string scriptPath = Path.Combine(root, "server-console-syntax.ps1");
                await File.WriteAllTextAsync(scriptPath, consoleScript, new UTF8Encoding(true));
                var parser = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                parser.ArgumentList.Add("-NoLogo");
                parser.ArgumentList.Add("-NoProfile");
                parser.ArgumentList.Add("-NonInteractive");
                parser.ArgumentList.Add("-Command");
                parser.ArgumentList.Add(
                    "$tokens=$null;$errors=$null;[void][System.Management.Automation.Language.Parser]::ParseFile($env:MC_TOOL_SCRIPT_TO_PARSE,[ref]$tokens,[ref]$errors);if($errors.Count){$errors|ForEach-Object{[Console]::Error.WriteLine($_)};exit 1}");
                parser.Environment["MC_TOOL_SCRIPT_TO_PARSE"] = scriptPath;
                using Process process = Process.Start(parser)
                    ?? throw new InvalidOperationException("Windows PowerShell parser did not start.");
                string parserOutput = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                True(process.ExitCode == 0, $"server-console.ps1 has invalid Windows PowerShell 5.1 syntax: {parserOutput}");
            }
        });
    }

    private static async Task MissingManifestDownloadBlocksOutputAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            int requests = 0;
            using var http = new HttpClient(new StubHandler(_ =>
            {
                requests++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }));
            using var cores = new ServerCoreService(http);
            using var builder = new ServerPackBuilder(cores, http);
            var item = new ContentItem { Name = "Restricted", FileName = "restricted.jar" };
            var source = new ServerPackSource
            {
                InputKind = ServerInputKinds.CurseForge,
                MinecraftVersion = "1.21.1",
                LoaderType = "fabric",
                LoaderVersion = "0.16.10",
                Mods =
                [
                    new ServerModEntry
                    {
                        Name = item.Name,
                        Origin = ServerModOrigins.Manifest,
                        Selected = true,
                        ContentItem = item,
                    },
                ],
            };
            var core = DirectCore(Encoding.UTF8.GetBytes("server"));
            string output = Path.Combine(root, "missing.zip");

            ServerBuildResult result = await builder.BuildAsync(Request(source, output, core), core);

            True(!result.Succeeded, "A manifest item without a download URL did not block the build.");
            True(!File.Exists(output), "An incomplete server ZIP was left behind.");
            Equal(0, requests, "The core was downloaded before manifest preflight completed.");
        });
    }

    private static async Task DownloadFailureLeavesNoOutputAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            byte[] serverBytes = Encoding.UTF8.GetBytes("server");
            using var http = new HttpClient(new StubHandler(request =>
                request.RequestUri?.AbsoluteUri == "https://example.test/server.jar"
                    ? BytesResponse(serverBytes)
                    : new HttpResponseMessage(HttpStatusCode.NotFound)));
            using var cores = new ServerCoreService(http);
            using var builder = new ServerPackBuilder(cores, http);
            var item = new ContentItem
            {
                Name = "Unavailable",
                FileName = "unavailable.jar",
                DownloadUrl = "https://example.test/unavailable.jar",
                DownloadUrls = ["https://example.test/unavailable.jar"],
                Hashes = new Dictionary<string, string> { ["sha1"] = new string('0', 40) },
            };
            var source = new ServerPackSource
            {
                InputKind = ServerInputKinds.Modrinth,
                MinecraftVersion = "1.21.1",
                LoaderType = "fabric",
                LoaderVersion = "0.16.10",
                Mods =
                [
                    new ServerModEntry
                    {
                        Name = item.Name,
                        RelativePath = item.FileName,
                        Origin = ServerModOrigins.Manifest,
                        Selected = true,
                        ContentItem = item,
                    },
                ],
            };
            var core = DirectCore(serverBytes);
            string output = Path.Combine(root, "failed.zip");

            ServerBuildResult result = await builder.BuildAsync(Request(source, output, core), core);

            True(!result.Succeeded, "A failed mod download was reported as success.");
            True(!File.Exists(output), "A failed build left an output ZIP.");
        });
    }

    private static async Task RejectsMismatchedBuildRequestsAsync()
    {
        byte[] serverBytes = Encoding.UTF8.GetBytes("server");
        using var builder = new ServerPackBuilder();
        var source = new ServerPackSource
        {
            InputKind = ServerInputKinds.Modrinth,
            MinecraftVersion = "1.21.1",
            LoaderType = "fabric",
            LoaderVersion = "0.16.10",
        };
        ServerCoreOption core = DirectCore(serverBytes);
        string output = Path.Combine(Path.GetTempPath(), $"invalid-server-{Guid.NewGuid():N}.zip");

        await ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(new ServerBuildRequest
        {
            Source = source,
            CoreId = ServerCoreIds.Vanilla,
            OutputPath = output,
        }, core), "A mismatched core ID was accepted.");

        var wrongLoader = new ServerPackSource
        {
            InputKind = ServerInputKinds.Modrinth,
            MinecraftVersion = "1.21.1",
            LoaderType = "forge",
            LoaderVersion = "47.4.0",
        };
        await ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(new ServerBuildRequest
        {
            Source = wrongLoader,
            CoreId = core.Id,
            OutputPath = output,
        }, core), "A mod-loader change was accepted.");

        var wrongMinecraft = new ServerPackSource
        {
            InputKind = ServerInputKinds.Modrinth,
            MinecraftVersion = "1.21.2",
            LoaderType = "fabric",
            LoaderVersion = "0.16.10",
        };
        await ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(new ServerBuildRequest
        {
            Source = wrongMinecraft,
            CoreId = core.Id,
            OutputPath = output,
        }, core), "A server core for another Minecraft version was accepted.");

        var wrongLoaderVersion = new ServerPackSource
        {
            InputKind = ServerInputKinds.Modrinth,
            MinecraftVersion = "1.21.1",
            LoaderType = "fabric",
            LoaderVersion = "0.16.11",
        };
        await ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(new ServerBuildRequest
        {
            Source = wrongLoaderVersion,
            CoreId = core.Id,
            OutputPath = output,
        }, core), "A mismatched server-core loader version was accepted.");
    }

    private static ServerBuildRequest Request(
        ServerPackSource source,
        string output,
        ServerCoreOption core,
        bool includeConfig = false,
        ServerWorldEntry? world = null) => new()
    {
        Source = source,
        CoreId = core.Id,
        OutputPath = output,
        IncludeConfig = includeConfig,
        World = world,
        EulaAccepted = true,
    };

    private static ServerCoreOption DirectCore(byte[] bytes) => new()
    {
        Id = ServerCoreIds.Fabric,
        Name = "Fabric",
        CoreVersion = "test",
        MinecraftVersion = "1.21.1",
        LoaderType = "fabric",
        LoaderVersion = "0.16.10",
        InstallStrategy = ServerCoreInstallStrategy.DirectFiles,
        Artifacts =
        [
            new ServerCoreArtifact
            {
                Role = ServerCoreArtifactRole.ServerJar,
                DownloadUrl = "https://example.test/server.jar",
                RelativePath = "server.jar",
                Size = bytes.Length,
                Hashes = new Dictionary<string, string>
                {
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                },
            },
        ],
    };

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Missing ZIP entry: {path}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), $"mc-server-builder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static void Contains(IEnumerable<string> values, string expected)
    {
        if (!values.Contains(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Missing ZIP entry: {expected}");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
