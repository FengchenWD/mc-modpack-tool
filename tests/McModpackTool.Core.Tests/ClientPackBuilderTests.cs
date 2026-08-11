using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

internal static class ClientPackBuilderTests
{
    public static async Task RunAllAsync()
    {
        await ModrinthUsesOnlyExactSha1MatchesAsync();
        await CurseForgeUsesOnlyExactFingerprintsAsync();
        await PlatformFailureFallsBackToOverridesAsync();
        await WritesLoaderMetadataForEverySupportedLoaderAsync();
        await RejectsConflictsAndHonorsOverwriteAndCancellationAsync();
    }

    private static async Task ModrinthUsesOnlyExactSha1MatchesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string contentRoot = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(contentRoot, "mods"));
            byte[] knownBytes = Encoding.UTF8.GetBytes("known mod content");
            byte[] unknownBytes = Encoding.UTF8.GetBytes("private local mod");
            string knownPath = Path.Combine(contentRoot, "mods", "known.jar");
            string unknownPath = Path.Combine(contentRoot, "mods", "unknown.jar");
            string excludedPath = Path.Combine(contentRoot, "options.txt");
            await File.WriteAllBytesAsync(knownPath, knownBytes);
            await File.WriteAllBytesAsync(unknownPath, unknownBytes);
            await File.WriteAllTextAsync(excludedPath, "not selected");
            string knownSha1 = Convert.ToHexString(SHA1.HashData(knownBytes)).ToLowerInvariant();
            string knownSha512 = Convert.ToHexString(SHA512.HashData(knownBytes)).ToLowerInvariant();
            bool includeSha512 = true;
            bool returnNullFiles = false;
            var requests = new List<string>();

            using var modrinthHttp = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
            {
                requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
                if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/version_files"))
                {
                    string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using JsonDocument document = JsonDocument.Parse(body);
                    True(document.RootElement.GetProperty("hashes").EnumerateArray()
                        .Any(value => value.GetString() == knownSha1), "The SHA1 batch omitted the known mod.");
                    True(document.RootElement.GetProperty("algorithm").GetString() == "sha1",
                        "The Modrinth lookup did not use SHA1.");
                    var hashes = new JsonObject { ["sha1"] = knownSha1 };
                    if (includeSha512) hashes["sha512"] = knownSha512;
                    return JsonResponse(new JsonObject
                    {
                        [knownSha1] = new JsonObject
                        {
                            ["id"] = "version-id",
                            ["project_id"] = "project-id",
                            ["files"] = returnNullFiles ? null : new JsonArray
                            {
                                new JsonObject
                                {
                                    ["hashes"] = hashes,
                                    ["url"] = "https://cdn.modrinth.test/known.jar",
                                    ["filename"] = "known.jar",
                                    ["size"] = knownBytes.Length,
                                },
                            },
                        },
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using var modrinth = new ModrinthClient(modrinthHttp);
            using var curseForge = new CurseForgeClient("test-key", new HttpClient(new NotUsedHandler()));
            using var builder = new ClientPackBuilder(modrinth, curseForge);
            ClientPackSource source = Source(contentRoot, "fabric", "0.16.10",
            [
                Item(knownPath, "mods/known.jar", ClientContentKinds.Mod, selected: true),
                Item(unknownPath, "mods/unknown.jar", ClientContentKinds.Mod, selected: true),
                Item(excludedPath, "options.txt", ClientContentKinds.Options, selected: false),
            ]);
            string output = Path.Combine(root, "client.mrpack");
            var phases = new List<ClientBuildPhase>();

            ClientBuildResult result = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = source,
                Format = ClientPackFormats.Modrinth,
                OutputPath = output,
            }, new InlineProgress<ClientBuildPhase>(phases.Add));

            True(result.Succeeded, string.Join(" | ", result.MissingFiles));
            Equal(1, result.RemoteItems, "The exact Modrinth match was not referenced remotely.");
            Equal(1, result.EmbeddedItems, "The unmatched Modrinth file was not embedded.");
            True(requests.All(value => !value.Contains("/search", StringComparison.OrdinalIgnoreCase)),
                "The builder used a name-search endpoint.");
            True(phases.SequenceEqual([
                ClientBuildPhase.MatchingPlatformFiles,
                ClientBuildPhase.CopyingOverrides,
                ClientBuildPhase.WritingManifest,
                ClientBuildPhase.CompressingArchive,
            ]), "Unexpected client build phases.");
            using ZipArchive archive = ZipFile.OpenRead(output);
            Contains(archive, "modrinth.index.json");
            Contains(archive, "overrides/mods/unknown.jar");
            Missing(archive, "overrides/mods/known.jar");
            Missing(archive, "overrides/options.txt");
            JsonObject index = await ReadJsonAsync(archive, "modrinth.index.json");
            JsonObject file = index["files"]!.AsArray()[0]!.AsObject();
            Equal("mods/known.jar", file["path"]!.GetValue<string>(), "The Modrinth path changed.");
            Equal("fabric-loader", index["dependencies"]!.AsObject().First(pair =>
                pair.Key != "minecraft").Key, "Fabric metadata is incorrect.");

            includeSha512 = false;
            string incompleteOutput = Path.Combine(root, "missing-sha512.mrpack");
            ClientBuildResult incomplete = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = source,
                Format = ClientPackFormats.Modrinth,
                OutputPath = incompleteOutput,
            });
            True(incomplete.Succeeded && incomplete.Warnings.Count > 0,
                "Incomplete Modrinth hash metadata did not fall back to overrides.");
            using (ZipArchive incompleteArchive = ZipFile.OpenRead(incompleteOutput))
            {
                Contains(incompleteArchive, "overrides/mods/known.jar");
                Equal(0, (await ReadJsonAsync(incompleteArchive, "modrinth.index.json"))["files"]!.AsArray().Count,
                    "A Modrinth entry without SHA-512 was written to the index.");
            }

            includeSha512 = true;
            returnNullFiles = true;
            string malformedOutput = Path.Combine(root, "null-files.mrpack");
            ClientBuildResult malformed = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = source,
                Format = ClientPackFormats.Modrinth,
                OutputPath = malformedOutput,
            });
            True(malformed.Succeeded && malformed.Warnings.Count > 0,
                "Malformed Modrinth file metadata blocked the local fallback.");
            using ZipArchive malformedArchive = ZipFile.OpenRead(malformedOutput);
            Contains(malformedArchive, "overrides/mods/known.jar");
        });
    }

    private static async Task CurseForgeUsesOnlyExactFingerprintsAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string contentRoot = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(contentRoot, "mods"));
            byte[] knownBytes = Encoding.UTF8.GetBytes("known\r\n curse forge\tmod");
            byte[] unknownBytes = Encoding.UTF8.GetBytes("unknown mod");
            string knownPath = Path.Combine(contentRoot, "mods", "known.jar");
            string unknownPath = Path.Combine(contentRoot, "mods", "unknown.jar");
            await File.WriteAllBytesAsync(knownPath, knownBytes);
            await File.WriteAllBytesAsync(unknownPath, unknownBytes);
            uint knownFingerprint = ReferenceCurseForgeFingerprint(knownBytes);
            string knownSha1 = Convert.ToHexString(SHA1.HashData(knownBytes)).ToLowerInvariant();
            int projectClassId = 6;
            var requests = new List<string>();

            using var curseForgeHttp = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
            {
                requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
                if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/fingerprints/432"))
                {
                    string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using JsonDocument document = JsonDocument.Parse(body);
                    True(document.RootElement.GetProperty("fingerprints").EnumerateArray()
                        .Any(value => value.GetUInt32() == knownFingerprint),
                        "The fingerprint batch omitted the known mod.");
                    return JsonResponse(new JsonObject
                    {
                        ["data"] = new JsonObject
                        {
                            ["exactFingerprints"] = new JsonArray(knownFingerprint),
                            ["exactMatches"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["file"] = new JsonObject
                                    {
                                        ["id"] = 202,
                                        ["modId"] = 101,
                                        ["fileFingerprint"] = knownFingerprint,
                                        ["fileLength"] = knownBytes.Length,
                                        ["fileName"] = "known.jar",
                                        ["hashes"] = new JsonArray
                                        {
                                            new JsonObject { ["algo"] = 1, ["value"] = knownSha1 },
                                        },
                                    },
                                },
                            },
                        },
                    });
                }
                if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/mods"))
                {
                    return JsonResponse(new JsonObject
                    {
                        ["data"] = new JsonArray
                        {
                            new JsonObject { ["id"] = 101, ["name"] = "Known", ["classId"] = projectClassId },
                        },
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using var curseForge = new CurseForgeClient("test-key", curseForgeHttp);
            using var modrinth = new ModrinthClient(new HttpClient(new NotUsedHandler()));
            using var builder = new ClientPackBuilder(modrinth, curseForge);
            ClientPackSource source = Source(contentRoot, "forge", "47.2.0",
            [
                Item(knownPath, "mods/known.jar", ClientContentKinds.Mod, selected: true),
                Item(unknownPath, "mods/unknown.jar", ClientContentKinds.Mod, selected: true),
            ]);
            string output = Path.Combine(root, "client.zip");

            ClientBuildResult result = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = source,
                Format = ClientPackFormats.CurseForge,
                OutputPath = output,
            });

            True(result.Succeeded, string.Join(" | ", result.MissingFiles));
            True(requests.All(value => !value.Contains("/search", StringComparison.OrdinalIgnoreCase)),
                "The CurseForge builder used a name-search endpoint.");
            using ZipArchive archive = ZipFile.OpenRead(output);
            Contains(archive, "manifest.json");
            Contains(archive, "overrides/mods/unknown.jar");
            Missing(archive, "overrides/mods/known.jar");
            JsonObject manifest = await ReadJsonAsync(archive, "manifest.json");
            JsonObject remote = manifest["files"]!.AsArray()[0]!.AsObject();
            Equal(101, remote["projectID"]!.GetValue<int>(), "The CurseForge project ID is wrong.");
            Equal(202, remote["fileID"]!.GetValue<int>(), "The CurseForge file ID is wrong.");
            Equal("forge-47.2.0", manifest["minecraft"]!["modLoaders"]![0]!["id"]!.GetValue<string>(),
                "Forge metadata is incorrect.");

            projectClassId = 12;
            string wrongClassOutput = Path.Combine(root, "wrong-class.zip");
            ClientBuildResult wrongClass = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = source,
                Format = ClientPackFormats.CurseForge,
                OutputPath = wrongClassOutput,
            });
            True(wrongClass.Succeeded && wrongClass.Warnings.Count > 0,
                "A CurseForge project type mismatch did not fall back to overrides.");
            using ZipArchive wrongClassArchive = ZipFile.OpenRead(wrongClassOutput);
            Contains(wrongClassArchive, "overrides/mods/known.jar");
            Equal(0, (await ReadJsonAsync(wrongClassArchive, "manifest.json"))["files"]!.AsArray().Count,
                "A CurseForge project with the wrong class was written to the manifest.");

            projectClassId = 6;
            string renamedPath = Path.Combine(contentRoot, "mods", "renamed.jar");
            string nestedPath = Path.Combine(contentRoot, "mods", "nested", "known.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(nestedPath)!);
            await File.WriteAllBytesAsync(renamedPath, knownBytes);
            await File.WriteAllBytesAsync(nestedPath, knownBytes);
            ClientPackSource pathSource = Source(contentRoot, "forge", "47.2.0",
            [
                Item(renamedPath, "mods/renamed.jar", ClientContentKinds.Mod, selected: true),
                Item(nestedPath, "mods/nested/known.jar", ClientContentKinds.Mod, selected: true),
            ]);
            string pathOutput = Path.Combine(root, "preserve-paths.zip");
            ClientBuildResult pathResult = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = pathSource,
                Format = ClientPackFormats.CurseForge,
                OutputPath = pathOutput,
            });
            True(pathResult.Succeeded && pathResult.RemoteItems == 0 && pathResult.EmbeddedItems == 2,
                "CurseForge remote entries changed a renamed or nested local file path.");
            using ZipArchive pathArchive = ZipFile.OpenRead(pathOutput);
            Contains(pathArchive, "overrides/mods/renamed.jar");
            Contains(pathArchive, "overrides/mods/nested/known.jar");
            Equal(0, (await ReadJsonAsync(pathArchive, "manifest.json"))["files"]!.AsArray().Count,
                "A renamed or nested CurseForge file was written to the manifest.");
        });
    }

    private static async Task PlatformFailureFallsBackToOverridesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string contentRoot = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(contentRoot, "mods"));
            string modPath = Path.Combine(contentRoot, "mods", "offline.jar");
            await File.WriteAllTextAsync(modPath, "offline");
            using var http = new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
            using var modrinth = new ModrinthClient(http);
            using var curseForge = new CurseForgeClient("test-key", new HttpClient(new NotUsedHandler()));
            using var builder = new ClientPackBuilder(modrinth, curseForge);
            ClientPackSource source = Source(contentRoot, "vanilla", string.Empty,
                [Item(modPath, "mods/offline.jar", ClientContentKinds.Mod, selected: true)]);
            string output = Path.Combine(root, "offline.mrpack");

            ClientBuildResult result = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = source,
                Format = ClientPackFormats.Modrinth,
                OutputPath = output,
            });

            True(result.Succeeded, "A platform outage blocked a local fallback build.");
            True(result.Warnings.Count > 0, "The platform fallback did not emit a warning.");
            using ZipArchive archive = ZipFile.OpenRead(output);
            Contains(archive, "overrides/mods/offline.jar");
            JsonObject index = await ReadJsonAsync(archive, "modrinth.index.json");
            Equal(0, index["files"]!.AsArray().Count, "An unverified remote file was written.");
        });
    }

    private static async Task WritesLoaderMetadataForEverySupportedLoaderAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            using var builder = new ClientPackBuilder(
                new ModrinthClient(new HttpClient(new NotUsedHandler())),
                new CurseForgeClient("test-key", new HttpClient(new NotUsedHandler())));
            var loaders = new (string Loader, string Version, string? MrKey, string? CfId)[]
            {
                ("vanilla", string.Empty, null, null),
                ("fabric", "0.16.10", "fabric-loader", "fabric-0.16.10"),
                ("forge", "47.2.0", "forge", "forge-47.2.0"),
                ("neoforge", "21.1.1", "neoforge", "neoforge-21.1.1"),
                ("quilt", "0.27.1", "quilt-loader", "quilt-0.27.1"),
            };
            foreach ((string loader, string version, string? mrKey, string? cfId) in loaders)
            {
                string contentRoot = Path.Combine(root, loader);
                Directory.CreateDirectory(contentRoot);
                ClientPackSource source = Source(contentRoot, loader, version, []);
                string mrOutput = Path.Combine(root, $"{loader}.mrpack");
                string cfOutput = Path.Combine(root, $"{loader}.zip");
                True((await builder.BuildAsync(new ClientBuildRequest
                {
                    Source = source,
                    Format = ClientPackFormats.Modrinth,
                    OutputPath = mrOutput,
                })).Succeeded, $"{loader} Modrinth metadata build failed.");
                True((await builder.BuildAsync(new ClientBuildRequest
                {
                    Source = source,
                    Format = ClientPackFormats.CurseForge,
                    OutputPath = cfOutput,
                })).Succeeded, $"{loader} CurseForge metadata build failed.");

                using (ZipArchive archive = ZipFile.OpenRead(mrOutput))
                {
                    JsonObject dependencies = (await ReadJsonAsync(archive, "modrinth.index.json"))["dependencies"]!.AsObject();
                    Equal(mrKey is null ? 1 : 2, dependencies.Count, $"{loader} Modrinth dependencies are wrong.");
                    if (mrKey is not null) Equal(version, dependencies[mrKey]!.GetValue<string>(), $"{loader} version is wrong.");
                }
                using (ZipArchive archive = ZipFile.OpenRead(cfOutput))
                {
                    JsonArray modLoaders = (await ReadJsonAsync(archive, "manifest.json"))["minecraft"]!["modLoaders"]!.AsArray();
                    Equal(cfId is null ? 0 : 1, modLoaders.Count, $"{loader} CurseForge loaders are wrong.");
                    if (cfId is not null) Equal(cfId, modLoaders[0]!["id"]!.GetValue<string>(), $"{loader} CF ID is wrong.");
                }
            }
        });
    }

    private static async Task RejectsConflictsAndHonorsOverwriteAndCancellationAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string contentRoot = Path.Combine(root, "game");
            string configRoot = Path.Combine(contentRoot, "config");
            Directory.CreateDirectory(configRoot);
            string configPath = Path.Combine(configRoot, "demo.toml");
            await File.WriteAllTextAsync(configPath, "config");
            ClientContentEntry directory = Item(configRoot, "config", ClientContentKinds.Configuration, selected: true, directory: true);
            ClientContentEntry child = Item(configPath, "config/demo.toml", ClientContentKinds.Configuration, selected: true);
            ClientPackSource source = Source(contentRoot, "vanilla", string.Empty, [directory, child]);
            using var builder = new ClientPackBuilder(
                new ModrinthClient(new HttpClient(new NotUsedHandler())),
                new CurseForgeClient("test-key", new HttpClient(new NotUsedHandler())));
            string conflictOutput = Path.Combine(root, "conflict.mrpack");

            ClientBuildResult conflict = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = source,
                Format = ClientPackFormats.Modrinth,
                OutputPath = conflictOutput,
            });
            True(!conflict.Succeeded && !File.Exists(conflictOutput),
                "A conflicting relative path produced an archive.");

            string existing = Path.Combine(root, "existing.mrpack");
            await File.WriteAllTextAsync(existing, "keep");
            ClientPackSource emptySource = Source(contentRoot, "vanilla", string.Empty, []);
            ClientBuildResult noOverwrite = await builder.BuildAsync(new ClientBuildRequest
            {
                Source = emptySource,
                Format = ClientPackFormats.Modrinth,
                OutputPath = existing,
                Overwrite = false,
            });
            True(!noOverwrite.Succeeded, "An existing archive was overwritten without permission.");
            Equal("keep", await File.ReadAllTextAsync(existing), "The existing output changed.");

            string cancelledOutput = Path.Combine(root, "cancelled.mrpack");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => builder.BuildAsync(new ClientBuildRequest
            {
                Source = emptySource,
                Format = ClientPackFormats.Modrinth,
                OutputPath = cancelledOutput,
            }, cancellationToken: cancellation.Token), "Cancellation was ignored.");
            True(!File.Exists(cancelledOutput), "A cancelled build left an output archive.");
        });
    }

    private static ClientPackSource Source(
        string contentRoot,
        string loader,
        string loaderVersion,
        List<ClientContentEntry> items) => new()
    {
        SourcePath = contentRoot,
        ContentRoot = contentRoot,
        DisplayName = "Client Test Pack",
        MinecraftVersion = "1.20.1",
        LoaderType = loader,
        LoaderVersion = loaderVersion,
        Items = items,
    };

    private static ClientContentEntry Item(
        string sourcePath,
        string relativePath,
        string kind,
        bool selected,
        bool directory = false) => new()
    {
        Name = Path.GetFileName(sourcePath),
        SourcePath = sourcePath,
        RelativePath = relativePath,
        Kind = kind,
        Selected = selected,
        IsDirectory = directory,
    };

    private static uint ReferenceCurseForgeFingerprint(byte[] input)
    {
        byte[] normalized = input.Where(value => value is not (9 or 10 or 13 or 32)).ToArray();
        const uint multiplier = 0x5bd1e995;
        uint hash = 1u ^ (uint)normalized.Length;
        int index = 0;
        while (normalized.Length - index >= 4)
        {
            uint part = BitConverter.ToUInt32(normalized, index);
            part *= multiplier;
            part ^= part >> 24;
            part *= multiplier;
            hash *= multiplier;
            hash ^= part;
            index += 4;
        }
        switch (normalized.Length - index)
        {
            case 3:
                hash ^= (uint)normalized[index + 2] << 16;
                goto case 2;
            case 2:
                hash ^= (uint)normalized[index + 1] << 8;
                goto case 1;
            case 1:
                hash ^= normalized[index];
                hash *= multiplier;
                break;
        }
        hash ^= hash >> 13;
        hash *= multiplier;
        hash ^= hash >> 15;
        return hash;
    }

    private static HttpResponseMessage JsonResponse(JsonNode value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static async Task<JsonObject> ReadJsonAsync(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Missing ZIP entry: {path}");
        await using Stream stream = entry.Open();
        return (await JsonNode.ParseAsync(stream))!.AsObject();
    }

    private static void Contains(ZipArchive archive, string path) =>
        True(archive.GetEntry(path) is not null, $"Missing ZIP entry: {path}");

    private static void Missing(ZipArchive archive, string path) =>
        True(archive.GetEntry(path) is null, $"Unexpected ZIP entry: {path}");

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), $"client-pack-builder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class NotUsedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                $"Unexpected HTTP request: {request.Method} {request.RequestUri}");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
