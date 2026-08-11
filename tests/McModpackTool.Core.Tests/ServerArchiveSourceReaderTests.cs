using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class ServerArchiveSourceReaderTests
{
    public static async Task RunAllAsync()
    {
        await ModrinthReaderKeepsOnlyServerContentAsync();
        await CurseForgeReaderUsesProjectClassesAsync();
        await QuiltPackIsRejectedAsync();
        await UnsafeArchivePathIsRejectedAsync();
    }

    private static async Task ModrinthReaderKeepsOnlyServerContentAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var index = new JsonObject
            {
                ["game"] = "minecraft",
                ["formatVersion"] = 1,
                ["name"] = "MR Server Source",
                ["versionId"] = "1.0.0",
                ["dependencies"] = new JsonObject
                {
                    ["minecraft"] = "1.21.1",
                    ["fabric-loader"] = "0.16.10",
                },
                ["files"] = new JsonArray
                {
                    ModrinthFile("mods/server.jar", "required", "server"),
                    ModrinthFile("mods/optional.jar", "optional", "optional"),
                    ModrinthFile("mods/client.jar", "unsupported", "client"),
                    ModrinthFile("resourcepacks/visual.zip", "required", "resource"),
                    ModrinthFile("shaderpacks/visual.zip", "required", "shader"),
                },
            };
            var archivePath = Path.Combine(root, "source.mrpack");
            await CreateArchiveAsync(archivePath, new Dictionary<string, byte[]>
            {
                ["modrinth.index.json"] = Encoding.UTF8.GetBytes(index.ToJsonString()),
                ["overrides/mods/nested/local.jar"] = Encoding.UTF8.GetBytes("local"),
                ["overrides/mods/client-only.jar"] = CreateFabricModJar("client"),
                ["overrides/mods/disabled.jar.disabled"] = Encoding.UTF8.GetBytes("disabled"),
                ["overrides/config/shared.toml"] = Encoding.UTF8.GetBytes("base"),
                ["overrides/scripts/setup.js"] = Encoding.UTF8.GetBytes("setup"),
                ["overrides/saves/World/level.dat"] = Encoding.UTF8.GetBytes("level"),
                ["overrides/saves/World/region/r.0.0.mca"] = Encoding.UTF8.GetBytes("region"),
                ["overrides/saves/NotAWorld/data.bin"] = Encoding.UTF8.GetBytes("data"),
                ["overrides/resourcepacks/local.zip"] = Encoding.UTF8.GetBytes("resource"),
                ["overrides/shaderpacks/local.zip"] = Encoding.UTF8.GetBytes("shader"),
                ["server-overrides/config/shared.toml"] = Encoding.UTF8.GetBytes("server"),
                ["server-overrides/defaultconfigs/default.toml"] = Encoding.UTF8.GetBytes("default"),
                ["server-overrides/kubejs/server_scripts/main.js"] = Encoding.UTF8.GetBytes("script"),
                ["client-overrides/mods/client-local.jar"] = Encoding.UTF8.GetBytes("client"),
                ["client-overrides/config/client.toml"] = Encoding.UTF8.GetBytes("client"),
            });

            await AssertThrowsAsync<InvalidDataException>(() => PackParser.ParseAsync(archivePath));

            using var http = new HttpClient(new RejectingHandler());
            using var curseForge = new CurseForgeClient("test-key", http);
            var reader = new ServerArchiveSourceReader(curseForge);
            var source = await reader.ReadAsync(archivePath, Path.Combine(root, "temporary"));

            Equal(ServerInputKinds.Modrinth, source.InputKind, "Modrinth input kind was not retained.");
            Equal("MR Server Source", source.DisplayName, "Pack name was not retained.");
            Equal("1.21.1", source.MinecraftVersion, "Minecraft version was not retained.");
            Equal("fabric", source.LoaderType, "Loader was not normalized.");
            Equal(3, source.ManifestPack!.Items.Count, "Non-mod downloads reached the server manifest.");

            var manifestMods = source.Mods.Where(mod => mod.Origin == ServerModOrigins.Manifest).ToList();
            Equal(3, manifestMods.Count, "Modrinth manifest mod count is wrong.");
            var clientOnly = manifestMods.Single(mod => mod.RelativePath == "client.jar");
            Equal(ServerSupportKinds.Unsupported, clientOnly.ServerSupport,
                "env.server=unsupported was not retained.");
            True(!clientOnly.Selected, "A client-only mod was selected by default.");
            var optional = manifestMods.Single(mod => mod.RelativePath == "optional.jar");
            Equal(ServerSupportKinds.Optional, optional.ServerSupport,
                "env.server=optional was promoted to recommended.");
            True(!optional.Selected, "An optional server mod was selected by default.");

            var localMods = source.Mods.Where(mod => mod.Origin == ServerModOrigins.Local).ToList();
            Equal(3, localMods.Count, "Recursive override mod discovery is wrong.");
            Equal("nested/local.jar", localMods.Single(mod => mod.RelativePath == "nested/local.jar").RelativePath,
                "Nested mod path was not relative to the mods root.");
            True(!localMods.Single(mod => mod.Disabled).Selected,
                "A disabled override mod was selected by default.");
            var clientLocal = localMods.Single(mod => mod.RelativePath == "client-only.jar");
            Equal(ServerSupportKinds.Unsupported, clientLocal.ServerSupport,
                "Client-only override metadata was not classified.");
            True(!clientLocal.Selected, "A client-only override mod was selected by default.");

            Equal(4, source.OptionalDirectories.Count, "Server optional directories were incomplete.");
            Equal("server", await File.ReadAllTextAsync(
                Path.Combine(source.ContentRoot, "config", "shared.toml")),
                "server-overrides did not take precedence over overrides.");
            True(!File.Exists(Path.Combine(source.ContentRoot, "mods", "client-local.jar")),
                "client-overrides content was extracted.");
            True(!Directory.Exists(Path.Combine(source.ContentRoot, "resourcepacks")),
                "Resource packs were extracted.");
            True(!Directory.Exists(Path.Combine(source.ContentRoot, "shaderpacks")),
                "Shader packs were extracted.");
            Equal(1, source.Worlds.Count, "Only direct saves children containing level.dat are worlds.");
            Equal("World", source.Worlds[0].Name, "World name is wrong.");
        });
    }

    private static async Task CurseForgeReaderUsesProjectClassesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var manifest = new JsonObject
            {
                ["name"] = "CF Server Source",
                ["overrides"] = "overrides",
                ["minecraft"] = new JsonObject
                {
                    ["version"] = "1.20.1",
                    ["modLoaders"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "forge-47.3.0", ["primary"] = true },
                    },
                },
                ["files"] = new JsonArray
                {
                    CurseForgeFile(10, 100),
                    CurseForgeFile(20, 200),
                    CurseForgeFile(30, 300),
                },
            };
            var archivePath = Path.Combine(root, "source.zip");
            await CreateArchiveAsync(archivePath, new Dictionary<string, byte[]>
            {
                ["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()),
                ["overrides/mods/local.jar"] = Encoding.UTF8.GetBytes("local"),
                ["overrides/config/common.toml"] = Encoding.UTF8.GetBytes("config"),
                ["overrides/resourcepacks/local.zip"] = Encoding.UTF8.GetBytes("resource"),
            });

            var projectRequests = 0;
            var fileRequests = 0;
            using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
            {
                var payload = JsonNode.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                if (request.RequestUri!.AbsolutePath == "/v1/mods")
                {
                    Equal(3, payload["modIds"]!.AsArray().Count, "Batch project IDs are incomplete.");
                    projectRequests++;
                    return JsonResponse(new
                    {
                        data = new[]
                        {
                            new { id = 10, name = "Server Mod", slug = "server-mod", classId = 6 },
                            new { id = 20, name = "Visual Pack", slug = "visual-pack", classId = 12 },
                            new { id = 30, name = "Shader", slug = "shader", classId = 6552 },
                        },
                    });
                }

                Equal("/v1/mods/files", request.RequestUri.AbsolutePath,
                    "CurseForge files were not queried through the batch endpoint.");
                Equal(3, payload["fileIds"]!.AsArray().Count, "Batch file IDs are incomplete.");
                fileRequests++;
                return JsonResponse(new
                {
                    data = new object[]
                    {
                        new
                        {
                            id = 100,
                            modId = 10,
                            fileName = "server-mod.jar",
                            fileLength = 123L,
                            downloadUrl = "",
                            hashes = new[] { new { algo = 1, value = "source-sha1" } },
                            dependencies = new[] { new { modId = 11, relationType = 3 } },
                        },
                        new { id = 200, modId = 20, fileName = "visual.zip", fileLength = 10L },
                        new { id = 300, modId = 30, fileName = "shader.zip", fileLength = 10L },
                    },
                });
            }));
            using var curseForge = new CurseForgeClient("test-key", http);
            var reader = new ServerArchiveSourceReader(curseForge);
            var source = await reader.ReadAsync(archivePath, Path.Combine(root, "temporary"));

            Equal(1, projectRequests, "CurseForge projects were not queried in one batch.");
            Equal(1, fileRequests, "CurseForge files were not queried in one batch.");
            Equal(1, source.ManifestPack!.Items.Count,
                "CurseForge resource or shader projects reached the server manifest.");
            var manifestMod = source.Mods.Single(mod => mod.Origin == ServerModOrigins.Manifest);
            Equal("Server Mod", manifestMod.Name, "CurseForge project name was not applied.");
            Equal("server-mod.jar", manifestMod.ContentItem!.FileName,
                "CurseForge source file name was not retained.");
            Equal(123L, manifestMod.ContentItem.FileSize,
                "CurseForge source file size was not retained.");
            Equal(string.Empty, manifestMod.ContentItem.DownloadUrl,
                "A forbidden CurseForge download acquired a fabricated URL.");
            Equal("source-sha1", manifestMod.ContentItem.Hashes["sha1"],
                "CurseForge source hash was not retained.");
            Equal(1, manifestMod.ContentItem.TargetDependencies.Count,
                "CurseForge source dependencies were not retained.");
            Equal(1, source.Mods.Count(mod => mod.Origin == ServerModOrigins.Local),
                "CurseForge override mod was not discovered.");
            True(!Directory.Exists(Path.Combine(source.ContentRoot, "resourcepacks")),
                "CurseForge resource packs were extracted.");
        });
    }

    private static async Task QuiltPackIsRejectedAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var index = new JsonObject
            {
                ["game"] = "minecraft",
                ["formatVersion"] = 1,
                ["dependencies"] = new JsonObject
                {
                    ["minecraft"] = "1.20.1",
                    ["quilt-loader"] = "0.26.3",
                },
                ["files"] = new JsonArray(),
            };
            var archivePath = Path.Combine(root, "quilt.mrpack");
            await CreateArchiveAsync(archivePath, new Dictionary<string, byte[]>
            {
                ["modrinth.index.json"] = Encoding.UTF8.GetBytes(index.ToJsonString()),
            });

            using var http = new HttpClient(new RejectingHandler());
            using var curseForge = new CurseForgeClient("test-key", http);
            var reader = new ServerArchiveSourceReader(curseForge);
            await AssertThrowsAsync<InvalidDataException>(() =>
                reader.ReadAsync(archivePath, Path.Combine(root, "temporary")));
        });
    }

    private static async Task UnsafeArchivePathIsRejectedAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            var index = new JsonObject
            {
                ["game"] = "minecraft",
                ["formatVersion"] = 1,
                ["dependencies"] = new JsonObject { ["minecraft"] = "1.21.1" },
                ["files"] = new JsonArray(),
            };
            var archivePath = Path.Combine(root, "unsafe.mrpack");
            await CreateArchiveAsync(archivePath, new Dictionary<string, byte[]>
            {
                ["modrinth.index.json"] = Encoding.UTF8.GetBytes(index.ToJsonString()),
                ["server-overrides/mods/../../escaped.jar"] = Encoding.UTF8.GetBytes("unsafe"),
            });

            using var http = new HttpClient(new RejectingHandler());
            using var curseForge = new CurseForgeClient("test-key", http);
            var reader = new ServerArchiveSourceReader(curseForge);
            await AssertThrowsAsync<InvalidDataException>(() =>
                reader.ReadAsync(archivePath, Path.Combine(root, "temporary")));
            True(!File.Exists(Path.Combine(root, "escaped.jar")), "Unsafe archive path escaped extraction.");
        });
    }

    private static JsonObject ModrinthFile(string path, string server, string id) => new()
    {
        ["path"] = path,
        ["downloads"] = new JsonArray(
            $"https://cdn.modrinth.com/data/{id}/versions/v1/{Path.GetFileName(path)}"),
        ["hashes"] = new JsonObject { ["sha1"] = "abc" },
        ["fileSize"] = 3,
        ["env"] = new JsonObject { ["client"] = "required", ["server"] = server },
    };

    private static JsonObject CurseForgeFile(long projectId, long fileId) => new()
    {
        ["projectID"] = projectId,
        ["fileID"] = fileId,
        ["required"] = true,
    };

    private static async Task CreateArchiveAsync(
        string path,
        IReadOnlyDictionary<string, byte[]> entries)
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

    private static byte[] CreateFabricModJar(string environment)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            ZipArchiveEntry entry = archive.CreateEntry("fabric.mod.json");
            using Stream target = entry.Open();
            byte[] metadata = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id = "client_only_test",
                version = "1.0.0",
                environment,
            }));
            target.Write(metadata);
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

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

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"server-archive-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}");
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
