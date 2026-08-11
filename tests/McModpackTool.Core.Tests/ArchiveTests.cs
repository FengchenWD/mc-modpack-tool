using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class ArchiveTests
{
    public static async Task RunAllAsync()
    {
        await ZipSlipIsRejectedAsync();
        await ArchiveMemberLimitIsEnforcedAsync();
        await ModrinthParsingSeparatesOverridesAsync();
        await CurseForgeParsingUsesPrimaryLoaderAsync();
        await CurseForgeBuildPreservesOverridesAsync();
        await ModrinthBuildPreservesOverridesAsync();
        await IncompleteModrinthBuildDoesNotPublishAsync();
        await DownloadValidationIsAtomicAsync();
    }

    private static async Task ZipSlipIsRejectedAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var archivePath = Path.Combine(root, "malicious.mrpack");
            await CreateArchiveAsync(archivePath, new Dictionary<string, byte[]>
            {
                ["overrides/../escaped.txt"] = Encoding.UTF8.GetBytes("must not escape"),
            });
            var extraction = Path.Combine(root, "nested", "overrides");
            Directory.CreateDirectory(extraction);

            await AssertThrowsAsync<InvalidDataException>(() =>
                PackParser.ExtractOverridesAsync(archivePath, extraction));
            Assert(!File.Exists(Path.Combine(root, "nested", "escaped.txt")),
                "Zip-slip entry escaped the extraction directory.");
        });
    }

    private static async Task ArchiveMemberLimitIsEnforcedAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var archivePath = Path.Combine(root, "oversized.zip");
            await CreateArchiveAsync(archivePath, new Dictionary<string, byte[]>
            {
                ["overrides/config/example.cfg"] = [1, 2, 3, 4],
            });

            await AssertThrowsAsync<InvalidDataException>(() => ArchiveSafety.ValidateArchiveAsync(
                archivePath,
                new ArchiveSafetyOptions { MaxMemberBytes = 3 }));
        });
    }

    private static async Task ModrinthParsingSeparatesOverridesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var index = new JsonObject
            {
                ["game"] = "minecraft",
                ["formatVersion"] = 1,
                ["name"] = "Example",
                ["versionId"] = "1.0.0",
                ["dependencies"] = new JsonObject
                {
                    ["minecraft"] = "1.21.1",
                    ["fabric-loader"] = "0.16.0",
                },
                ["files"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["path"] = "mods/ftb-ultimine.jar",
                        ["downloads"] = new JsonArray
                        {
                            "https://mediafilez.forgecdn.net/files/7570/483/ftb-ultimine.jar",
                        },
                        ["hashes"] = new JsonObject { ["sha1"] = "abc" },
                        ["fileSize"] = 176360,
                    },
                    new JsonObject
                    {
                        ["path"] = "config/generated.json",
                        ["downloads"] = new JsonArray { "https://example.invalid/generated.json" },
                        ["hashes"] = new JsonObject { ["sha1"] = "def" },
                        ["fileSize"] = 5,
                    },
                },
            };
            var archivePath = Path.Combine(root, "source.mrpack");
            await CreateArchiveAsync(archivePath, new Dictionary<string, byte[]>
            {
                ["modrinth.index.json"] = Encoding.UTF8.GetBytes(index.ToJsonString()),
                ["overrides/mods/local.jar"] = Encoding.UTF8.GetBytes("local"),
                ["overrides/config/example.toml"] = Encoding.UTF8.GetBytes("enabled=true"),
            });

            var parsed = await PackParser.ParseAsync(archivePath);
            AssertEqual("modrinth", parsed.FormatType, "Modrinth format was not detected.");
            AssertEqual("1.21.1", parsed.MinecraftVersion, "Minecraft version was not parsed.");
            AssertEqual("fabric", parsed.LoaderType, "Loader type was not normalized.");
            AssertEqual(1, parsed.Items.Count, "Overrides or passthrough entries became analysis inputs.");
            AssertEqual("7570483", parsed.Items[0].FileId, "ForgeCDN file identity was not retained.");
            Assert(parsed.Items[0].IdentityLocked, "Exact ForgeCDN identity was not locked.");
            AssertEqual(1, parsed.PassthroughFiles.Count, "Non-content index entry was not preserved.");
            Assert(parsed.OverridePaths.SetEquals(["mods/local.jar", "config/example.toml"]),
                "Override paths were not recorded independently.");
        });
    }

    private static async Task CurseForgeParsingUsesPrimaryLoaderAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var manifest = new JsonObject
            {
                ["minecraft"] = new JsonObject
                {
                    ["version"] = "1.21.1",
                    ["modLoaders"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "forge-52.0.0", ["primary"] = false },
                        new JsonObject { ["id"] = "fabric-0.16.0", ["primary"] = true },
                    },
                },
                ["files"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["projectID"] = 123,
                        ["fileID"] = 456,
                        ["required"] = false,
                    },
                },
            };
            var archivePath = Path.Combine(root, "source.zip");
            await CreateArchiveAsync(archivePath, new Dictionary<string, byte[]>
            {
                ["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            });

            var parsed = await PackParser.ParseAsync(archivePath);
            AssertEqual("curseforge", parsed.FormatType, "CurseForge format was not detected.");
            AssertEqual("fabric", parsed.LoaderType, "Primary loader was not selected.");
            AssertEqual("0.16.0", parsed.LoaderVersion, "Loader version was not split correctly.");
            Assert(!parsed.Items[0].Required, "CurseForge required=false was not preserved.");
        });
    }

    private static async Task CurseForgeBuildPreservesOverridesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var overrides = Path.Combine(root, "source-overrides");
            Directory.CreateDirectory(Path.Combine(overrides, "mods"));
            var payload = new byte[] { 0, 1, 2, 255 };
            await File.WriteAllBytesAsync(Path.Combine(overrides, "mods", "local.jar"), payload);
            var output = Path.Combine(root, "output.zip");
            var pack = new ModpackInfo
            {
                FormatType = "curseforge",
                OverridePaths = new HashSet<string>(["mods/local.jar"], StringComparer.OrdinalIgnoreCase),
                RawData = new JsonObject { ["name"] = "Source Pack", ["version"] = "1.0.0" },
                Items =
                [
                    new ContentItem
                    {
                        Name = "Example",
                        ProjectId = "123",
                        TargetFileId = "456",
                        Status = "found",
                    },
                ],
            };

            var result = await PackBuilder.BuildCurseForgeAsync(
                output, pack, "1.21.4", "fabric", "0.16.10", overrides);
            Assert(result.Succeeded, "Minimal CurseForge build reported missing files.");
            using var archive = ZipFile.OpenRead(output);
            var manifest = await ReadJsonAsync(archive.GetEntry("manifest.json")!);
            AssertEqual(1, manifest["files"]!.AsArray().Count, "CurseForge reference was not emitted.");
            var builtPayload = await ReadBytesAsync(archive.GetEntry("overrides/mods/local.jar")!);
            Assert(payload.SequenceEqual(builtPayload),
                "CurseForge build changed override bytes.");
        });
    }

    private static async Task ModrinthBuildPreservesOverridesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var overrides = Path.Combine(root, "source-overrides");
            Directory.CreateDirectory(Path.Combine(overrides, "saves", "World"));
            var payload = new byte[] { 9, 8, 7, 0, 255 };
            await File.WriteAllBytesAsync(Path.Combine(overrides, "saves", "World", "level.dat"), payload);
            var output = Path.Combine(root, "output.mrpack");
            var pack = new ModpackInfo
            {
                FormatType = "modrinth",
                OverridePaths = new HashSet<string>(["saves/World/level.dat"], StringComparer.OrdinalIgnoreCase),
                RawData = new JsonObject { ["name"] = "Source Pack", ["versionId"] = "1.0.0" },
                Items =
                [
                    new ContentItem
                    {
                        Name = "Example",
                        ProjectId = "project",
                        Source = "modrinth",
                        Status = "found",
                        TargetDownloadUrl = "https://cdn.modrinth.com/data/project/versions/version/example.jar",
                        TargetFileName = "example.jar",
                        TargetFileSize = 123,
                        TargetHashes = new Dictionary<string, string> { ["sha1"] = "abc" },
                        Environment = new Dictionary<string, string>
                        {
                            ["client"] = "required",
                            ["server"] = "unsupported",
                        },
                    },
                ],
            };

            var result = await PackBuilder.BuildModrinthAsync(
                output, pack, "1.21.4", "fabric", "0.16.10", overrides);
            Assert(result.Succeeded, "Minimal Modrinth build reported missing files.");
            using var archive = ZipFile.OpenRead(output);
            var index = await ReadJsonAsync(archive.GetEntry("modrinth.index.json")!);
            AssertEqual("unsupported", index["files"]![0]!["env"]!["server"]!.GetValue<string>(),
                "Modrinth environment rules were not preserved.");
            var builtPayload = await ReadBytesAsync(
                archive.GetEntry("overrides/saves/World/level.dat")!);
            Assert(payload.SequenceEqual(builtPayload),
                "Modrinth build changed override bytes.");
        });
    }

    private static async Task IncompleteModrinthBuildDoesNotPublishAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            byte[] originalOutput = Encoding.UTF8.GetBytes("existing output must survive");
            byte[] downloadedPayload = Encoding.UTF8.GetBytes("unexpected payload");
            string output = Path.Combine(root, "output.mrpack");
            await File.WriteAllBytesAsync(output, originalOutput);
            using var client = new HttpClient(new StaticResponseHandler(downloadedPayload));
            var pack = new ModpackInfo
            {
                FormatType = "modrinth",
                RawData = new JsonObject { ["name"] = "Source Pack", ["versionId"] = "1.0.0" },
                Items =
                [
                    new ContentItem
                    {
                        Name = "CurseForge-only Mod",
                        Source = "curseforge",
                        Category = "mod",
                        Status = "found",
                        ProjectId = "123",
                        TargetFileId = "456",
                        TargetDownloadUrl = "https://example.invalid/mod.jar",
                        TargetFileName = "mod.jar",
                        TargetFileSize = downloadedPayload.Length,
                        TargetHashes = new Dictionary<string, string>
                        {
                            ["sha1"] = new string('0', 40),
                        },
                    },
                ],
            };

            BuildResult result = await PackBuilder.BuildModrinthAsync(
                output,
                pack,
                "1.21.4",
                "fabric",
                "0.16.10",
                overridesDirectory: string.Empty,
                overwrite: true,
                httpClient: client);

            Assert(!result.Succeeded && result.MissingFiles.Count == 1,
                "A failed required embed was not reported as an incomplete build.");
            byte[] preservedOutput = await File.ReadAllBytesAsync(output);
            Assert(originalOutput.SequenceEqual(preservedOutput),
                "An incomplete build replaced the existing output archive.");
        });
    }

    private static async Task DownloadValidationIsAtomicAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var payload = Encoding.UTF8.GetBytes("verified download");
            var handler = new StaticResponseHandler(payload);
            using var client = new HttpClient(handler);
            var destination = Path.Combine(root, "mods");
            var expectedHash = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
            var transferUpdates = new List<DownloadTransferProgress>();
            var downloaded = await ArchiveSafety.DownloadFileAsync(
                client,
                "https://example.invalid/example.jar",
                destination,
                "example.jar",
                expectedSize: payload.Length,
                expectedHashes: new Dictionary<string, string> { ["sha1"] = expectedHash },
                transferProgress: new InlineProgress<DownloadTransferProgress>(transferUpdates.Add));
            Assert(downloaded, "A correctly sized and hashed download was rejected.");
            Assert(transferUpdates.Any(update =>
                    update.IsActive && update.BytesReceived == payload.Length && update.BytesPerSecond > 0),
                "The download did not report a usable transfer rate.");
            Assert(transferUpdates.Count > 0 && !transferUpdates[^1].IsActive,
                "The transfer progress remained active after the download completed.");
            var downloadedPayload = await File.ReadAllBytesAsync(Path.Combine(destination, "example.jar"));
            Assert(payload.SequenceEqual(downloadedPayload),
                "A verified download changed bytes.");

            var mismatch = await ArchiveSafety.DownloadFileAsync(
                client,
                "https://example.invalid/bad.jar",
                destination,
                "bad.jar",
                expectedSize: payload.Length,
                expectedHashes: new Dictionary<string, string> { ["sha1"] = new string('0', 40) });
            Assert(!mismatch, "A hash mismatch was accepted.");
            Assert(!File.Exists(Path.Combine(destination, "bad.jar")),
                "A failed download left a destination file.");
            Assert(!Directory.EnumerateFiles(destination, "*.part").Any(),
                "A failed download left a partial file.");

            var requestsBeforeCollision = handler.RequestCount;
            var collision = await ArchiveSafety.DownloadFileAsync(
                client,
                "https://example.invalid/replacement.jar",
                destination,
                "example.jar");
            Assert(!collision, "An existing override was overwritten by a download.");
            AssertEqual(requestsBeforeCollision, handler.RequestCount,
                "An avoidable network request was sent for an existing destination.");
        });
    }

    private static async Task CreateArchiveAsync(string path, IReadOnlyDictionary<string, byte[]> entries)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        foreach (var (name, payload) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            await using var target = entry.Open();
            await target.WriteAsync(payload);
        }
    }

    private static async Task<JsonObject> ReadJsonAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        return (await JsonNode.ParseAsync(stream))!.AsObject();
    }

    private static async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> operation)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcpack-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await operation(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup should not hide a failed assertion.
            }
        }
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            });
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
