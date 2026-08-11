using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class ServerCoreServiceTests
{
    public static async Task RunAllAsync()
    {
        await FabricAndCardboardUseExactMetadataAsync();
        await CardboardRejectsUnresolvedRequiredDependencyAsync();
        await ForgeProvidersUseVerifiedBuildsAsync();
        await CatServerRequiresExactEmbeddedForgeAsync();
        await LegacyNeoForgeUsesOfficialForgeArtifactAsync();
        await NeoForgeInstallerRunsJavaAsync();
        await DirectCoreUsesPublishedJarPathAsync();
        await LegacyForgeJarIsRecognizedAsync();
        await InvalidInstallerRoleIsRejectedAsync();
        await ReparsePointArtifactPathIsRejectedAsync();
        await FailedHashDoesNotPublishArtifactAsync();
    }

    private static async Task FabricAndCardboardUseExactMetadataAsync()
    {
        byte[] vanilla = Bytes("vanilla-server");
        byte[] fabric = Bytes("fabric-server");
        byte[] cardboard = Bytes("cardboard-mod");
        byte[] fabricApi = Bytes("fabric-api-mod");
        byte[] iCommon = Bytes("icommon-mod");
        string vanillaSha1 = Sha1(vanilla);
        string cardboardSha1 = Sha1(cardboard);
        string fabricApiSha1 = Sha1(fabricApi);
        string iCommonSha1 = Sha1(iCommon);
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            if (host == "piston-meta.mojang.com" && path.EndsWith("version_manifest_v2.json", StringComparison.Ordinal))
                return Json(new { versions = new[] { new { id = "1.21.1", url = "https://piston-meta.mojang.com/meta/1.21.1.json" } } });
            if (host == "piston-meta.mojang.com" && path == "/meta/1.21.1.json")
                return Json(new { downloads = new { server = new { url = "https://piston-data.mojang.com/server.jar", sha1 = vanillaSha1, size = vanilla.Length } } });
            if (host == "meta.fabricmc.net" && path == "/v2/versions/loader/1.21.1")
                return Json(new object[] { new { loader = new { version = "0.16.9", stable = false } } });
            if (host == "meta.fabricmc.net" && path == "/v2/versions/installer")
                return Json(new object[] { new { version = "1.0.1", stable = true }, new { version = "1.0.3", stable = true } });
            if (host == "api.modrinth.com" && path == $"/v2/project/{ServerCoreService.CardboardProjectId}/version")
                return Json(new object[]
                {
                    new
                    {
                        id = "cardboard-version", project_id = ServerCoreService.CardboardProjectId,
                        version_number = "1.21.1-9", version_type = "release",
                        date_published = "2025-09-27T04:23:51Z", game_versions = new[] { "1.21.1" },
                        loaders = new[] { "fabric" }, files = new object[]
                        {
                            new { filename = "Cardboard-1.21.jar", url = "https://cdn.modrinth.com/data/cardboard.jar", primary = true, size = cardboard.Length, hashes = new { sha1 = cardboardSha1 } },
                        },
                        dependencies = new object[]
                        {
                            new { project_id = "fabric-api", version_id = (string?)null, dependency_type = "required" },
                            new { project_id = "icommon", version_id = "icommon-version", dependency_type = "required" },
                            new { project_id = "optional-project", version_id = "optional-version", dependency_type = "optional" },
                        },
                    },
                    new { id = "wrong-game", project_id = ServerCoreService.CardboardProjectId, version_number = "1.21.2-1", version_type = "release", date_published = "2026-01-01T00:00:00Z", game_versions = new[] { "1.21.2" }, loaders = new[] { "fabric" }, files = Array.Empty<object>() },
                });
            if (host == "api.modrinth.com" && path == "/v2/project/fabric-api/version")
                return Json(new object[]
                {
                    new
                    {
                        id = "fabric-api-version", project_id = "fabric-api", version_number = "1.0.0",
                        version_type = "release", date_published = "2025-01-01T00:00:00Z",
                        game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" },
                        files = new[]
                        {
                            new { filename = "fabric-api.jar", url = "https://cdn.modrinth.com/data/fabric-api.jar", primary = true, size = fabricApi.Length, hashes = new { sha1 = fabricApiSha1 } },
                        },
                    },
                });
            if (host == "api.modrinth.com" && path == "/v2/version/icommon-version")
                return Json(new
                {
                    id = "icommon-version", project_id = "icommon", version_number = "1.0.0",
                    version_type = "release", date_published = "2025-01-01T00:00:00Z",
                    game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" },
                    files = new[]
                    {
                        new { filename = "iCommon.jar", url = "https://cdn.modrinth.com/data/icommon.jar", primary = true, size = iCommon.Length, hashes = new { sha1 = iCommonSha1 } },
                    },
                });
            if (host == "meta.fabricmc.net" && path.EndsWith("/server/jar", StringComparison.Ordinal)) return Binary(fabric);
            if (host == "cdn.modrinth.com" && path == "/data/cardboard.jar") return Binary(cardboard);
            if (host == "cdn.modrinth.com" && path == "/data/fabric-api.jar") return Binary(fabricApi);
            if (host == "cdn.modrinth.com" && path == "/data/icommon.jar") return Binary(iCommon);
            if (host == "piston-data.mojang.com") return Binary(vanilla);
            return Missing();
        }));
        using var service = new ServerCoreService(http);
        ServerCoreCatalogResult catalog = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.21.1", LoaderType = "fabric", LoaderVersion = "0.16.9",
        });
        SequenceEqual([ServerCoreIds.Fabric, ServerCoreIds.Cardboard, ServerCoreIds.Vanilla],
            catalog.Options.Select(option => option.Id), "Fabric catalog is wrong.");
        ServerCoreOption option = catalog.Options.Single(item => item.Id == ServerCoreIds.Cardboard);
        Equal(ServerCoreInstallStrategy.DirectFiles, option.InstallStrategy, "Cardboard strategy is wrong.");
        SequenceEqual(
            ["mods/Cardboard-1.21.jar", "mods/fabric-api.jar", "mods/iCommon.jar"],
            option.Artifacts.Where(artifact => artifact.Role == ServerCoreArtifactRole.Mod)
                .Select(artifact => artifact.RelativePath),
            "Cardboard required dependencies are incomplete.");

        string destination = TempDirectory();
        try
        {
            ServerCoreInstallResult result = await service.InstallAsync(new ServerCoreInstallRequest
            {
                Option = option, DestinationDirectory = destination,
            });
            True(result.Succeeded, "Cardboard installation failed.");
            Equal("java -jar server.jar nogui", result.LaunchCommand, "Direct launch command is wrong.");
            BytesEqual(fabric, await File.ReadAllBytesAsync(Path.Combine(destination, "server.jar")), "Fabric launcher is wrong.");
            BytesEqual(cardboard, await File.ReadAllBytesAsync(Path.Combine(destination, "mods", "Cardboard-1.21.jar")), "Cardboard file is wrong.");
            BytesEqual(fabricApi, await File.ReadAllBytesAsync(Path.Combine(destination, "mods", "fabric-api.jar")), "Fabric API file is wrong.");
            BytesEqual(iCommon, await File.ReadAllBytesAsync(Path.Combine(destination, "mods", "iCommon.jar")), "iCommon file is wrong.");
        }
        finally { Delete(destination); }
    }

    private static async Task CardboardRejectsUnresolvedRequiredDependencyAsync()
    {
        byte[] cardboard = Bytes("cardboard-mod");
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            if (host == "piston-meta.mojang.com") return Json(new { versions = Array.Empty<object>() });
            if (host == "meta.fabricmc.net" && path == "/v2/versions/loader/1.21.1")
                return Json(new object[] { new { loader = new { version = "0.16.9", stable = true } } });
            if (host == "meta.fabricmc.net" && path == "/v2/versions/installer")
                return Json(new object[] { new { version = "1.0.3", stable = true } });
            if (host == "api.modrinth.com" && path == $"/v2/project/{ServerCoreService.CardboardProjectId}/version")
                return Json(new object[]
                {
                    new
                    {
                        id = "cardboard-version", project_id = ServerCoreService.CardboardProjectId,
                        version_number = "1.21.1-9", version_type = "release",
                        date_published = "2025-09-27T04:23:51Z", game_versions = new[] { "1.21.1" },
                        loaders = new[] { "fabric" },
                        files = new[]
                        {
                            new { filename = "Cardboard.jar", url = "https://cdn.modrinth.com/data/cardboard.jar", primary = true, size = cardboard.Length, hashes = new { sha1 = Sha1(cardboard) } },
                        },
                        dependencies = new[]
                        {
                            new { project_id = "required-project", version_id = "unverifiable-version", dependency_type = "required" },
                        },
                    },
                });
            if (host == "api.modrinth.com" && path == "/v2/version/unverifiable-version")
                return Json(new
                {
                    id = "unverifiable-version", project_id = "required-project", version_number = "1.0.0",
                    version_type = "release", date_published = "2025-01-01T00:00:00Z",
                    game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" },
                    files = new[]
                    {
                        new { filename = "dependency.jar", url = "https://cdn.modrinth.com/data/dependency.jar", primary = true, size = 10, hashes = new { sha1 = "invalid" } },
                    },
                });
            return Missing();
        }));
        using var service = new ServerCoreService(http);
        ServerCoreCatalogResult catalog = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.21.1", LoaderType = "fabric", LoaderVersion = "0.16.9",
        });
        True(catalog.Options.All(option => option.Id != ServerCoreIds.Cardboard),
            "Cardboard was offered with an unresolved required dependency.");
    }

    private static async Task ForgeProvidersUseVerifiedBuildsAsync()
    {
        byte[] vanilla = Bytes("vanilla");
        byte[] installer = Bytes("forge-installer");
        byte[] mohist = Bytes("mohist");
        string vanillaSha1 = Sha1(vanilla);
        string installerSha1 = Sha1(installer);
        string mohistSha256 = Hex(SHA256.HashData(mohist));
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            if (host == "piston-meta.mojang.com" && path.EndsWith("version_manifest_v2.json", StringComparison.Ordinal))
                return Json(new { versions = new[] { new { id = "1.20.1", url = "https://piston-meta.mojang.com/meta/1.20.1.json" } } });
            if (host == "piston-meta.mojang.com" && path == "/meta/1.20.1.json")
                return Json(new { downloads = new { server = new { url = "https://piston-data.mojang.com/server.jar", sha1 = vanillaSha1, size = vanilla.Length } } });
            if (host == "maven.minecraftforge.net" && path.EndsWith("maven-metadata.xml", StringComparison.Ordinal))
                return Text("<metadata><versioning><versions><version>1.20.1-47.4.0</version></versions></versioning></metadata>", "application/xml");
            if (host == "maven.minecraftforge.net" && path.EndsWith("installer.jar.sha1", StringComparison.Ordinal)) return Text(installerSha1, "text/plain");
            if (host == "mohistmc.com")
                return Json(new { projectVersion = "1.20.1", builds = new object[]
                {
                    new { number = 900, forgeVersion = "47.3.0", fileSha256 = mohistSha256, url = "https://mohistmc.com/build/900", createdAt = 200L },
                    new { id = "exact", forgeVersion = "47.4.0", fileSha256 = mohistSha256, url = "https://mohistmc.com/build/exact", createdAt = 100L },
                } });
            return Missing();
        }));
        using var service = new ServerCoreService(http);
        ServerCoreCatalogResult catalog = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.20.1", LoaderType = "forge", LoaderVersion = "47.4.0",
        });
        SequenceEqual([ServerCoreIds.Forge, ServerCoreIds.Mohist, ServerCoreIds.Vanilla],
            catalog.Options.Select(option => option.Id), "Forge catalog is wrong.");
        ServerCoreOption forge = catalog.Options.Single(option => option.Id == ServerCoreIds.Forge);
        Equal(ServerCoreInstallStrategy.JavaInstaller, forge.InstallStrategy, "Forge must use Java installer.");
        Equal("--installServer", forge.JavaInstaller!.Arguments.Single(), "Forge argument is wrong.");
        Equal("exact", catalog.Options.Single(option => option.Id == ServerCoreIds.Mohist).CoreVersion,
            "Mohist should prefer the requested Forge loader.");
        True(catalog.Options.All(option => option.Id != ServerCoreIds.CatServer),
            "CatServer was offered without a proven embedded Forge version.");

        ServerCoreCatalogResult mismatch = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.20.1", LoaderType = "forge", LoaderVersion = "47.4.1",
        });
        True(mismatch.Options.All(option => option.Id != ServerCoreIds.Mohist),
            "Mohist fell back to a different Forge loader version.");
    }

    private static async Task CatServerRequiresExactEmbeddedForgeAsync()
    {
        int releaseQueries = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            if (host == "piston-meta.mojang.com") return Json(new { versions = Array.Empty<object>() });
            if (host == "maven.minecraftforge.net")
                return Text("<metadata><versioning><versions /></versioning></metadata>", "application/xml");
            if (host == "api.github.com")
            {
                releaseQueries++;
                return Json(new object[]
                {
                    new
                    {
                        tag_name = "catserver-1.16.5", name = "CatServer 1.16.5", draft = false,
                        prerelease = false, published_at = "2025-01-01T00:00:00Z",
                        assets = new[]
                        {
                            new { name = "CatServer-1.16.5-server.jar", browser_download_url = "https://github.com/Luohuayu/CatServer/releases/catserver-1.16.5.jar", size = 10L, digest = (string?)null },
                        },
                    },
                });
            }
            return Missing();
        }));
        using var service = new ServerCoreService(http);
        ServerCoreCatalogResult exact = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.16.5", LoaderType = "forge", LoaderVersion = "36.2.39",
        });
        ServerCoreOption option = exact.Options.Single(item => item.Id == ServerCoreIds.CatServer);
        Equal("36.2.39", option.LoaderVersion, "CatServer did not publish its exact embedded Forge version.");
        Equal(1, releaseQueries, "CatServer release metadata was not queried for a supported pairing.");

        ServerCoreCatalogResult mismatch = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.16.5", LoaderType = "forge", LoaderVersion = "36.2.40",
        });
        True(mismatch.Options.All(item => item.Id != ServerCoreIds.CatServer),
            "CatServer was offered for a mismatched Forge version.");
        Equal(1, releaseQueries, "CatServer queried releases even though the Forge pairing was unsupported.");
    }

    private static async Task LegacyNeoForgeUsesOfficialForgeArtifactAsync()
    {
        byte[] installer = Bytes("legacy-neoforge-installer");
        string sha1 = Sha1(installer);
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            if (host == "piston-meta.mojang.com") return Json(new { versions = Array.Empty<object>() });
            if (host == "maven.neoforged.net" && path == "/releases/net/neoforged/forge/maven-metadata.xml")
                return Text("<metadata><versioning><versions><version>1.20.1-47.1.106</version></versions></versioning></metadata>", "application/xml");
            if (host == "maven.neoforged.net" && path.EndsWith("installer.jar.sha1", StringComparison.Ordinal)) return Text(sha1, "text/plain");
            return Missing();
        }));
        using var service = new ServerCoreService(http);
        ServerCoreCatalogResult catalog = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.20.1", LoaderType = "neoforge", LoaderVersion = "47.1.106",
        });
        ServerCoreOption option = catalog.Options.Single(item => item.Id == ServerCoreIds.NeoForge);
        True(option.Artifacts.Single().DownloadUrl.Contains(
                "/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar",
                StringComparison.Ordinal),
            "NeoForge 1.20.1 used the wrong Maven artifact family.");

        ServerCoreCatalogResult fullyQualified = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.20.1", LoaderType = "neoforge", LoaderVersion = "1.20.1-47.1.106",
        });
        True(fullyQualified.Options.Any(item => item.Id == ServerCoreIds.NeoForge),
            "A fully-qualified NeoForge 1.20.1 loader version was rejected.");
    }

    private static async Task NeoForgeInstallerRunsJavaAsync()
    {
        byte[] installer = Bytes("neoforge-installer");
        string sha1 = Sha1(installer);
        var runner = new FakeJavaRunner((_, _, _, directory, _) =>
        {
            File.WriteAllText(Path.Combine(directory, "run.bat"), "@echo off");
            string argsDirectory = Path.Combine(directory, "libraries", "net", "neoforged", "test");
            Directory.CreateDirectory(argsDirectory);
            File.WriteAllText(Path.Combine(argsDirectory, "win_args.txt"), "-p libraries");
            File.WriteAllText(Path.Combine(directory, "user_jvm_args.txt"), "-Xmx4G");
            return Task.FromResult(0);
        });
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            if (host == "piston-meta.mojang.com") return Json(new { versions = Array.Empty<object>() });
            if (host == "maven.neoforged.net" && path.EndsWith("maven-metadata.xml", StringComparison.Ordinal))
                return Text("<metadata><versioning><versions><version>21.1.77</version></versions></versioning></metadata>", "application/xml");
            if (host == "maven.neoforged.net" && path.EndsWith("installer.jar.sha1", StringComparison.Ordinal)) return Text(sha1, "text/plain");
            if (host == "maven.neoforged.net" && path.EndsWith("installer.jar", StringComparison.Ordinal)) return Binary(installer);
            return Missing();
        }));
        using var service = new ServerCoreService(http, javaRunner: runner);
        ServerCoreCatalogResult catalog = await service.GetAvailableAsync(new ServerCoreQuery
        {
            MinecraftVersion = "1.21.1", LoaderType = "neoforge", LoaderVersion = "21.1.77",
        });
        ServerCoreOption option = catalog.Options.Single(item => item.Id == ServerCoreIds.NeoForge);
        string destination = TempDirectory();
        try
        {
            ServerCoreInstallResult result = await service.InstallAsync(new ServerCoreInstallRequest
            {
                Option = option, DestinationDirectory = destination, JavaExecutable = "java-test",
            });
            True(result.Succeeded, "NeoForge installation failed.");
            Equal("java @user_jvm_args.txt @libraries/net/neoforged/test/win_args.txt nogui",
                result.LaunchCommand,
                "Installer launch command should prefer the direct argument file over a pause-enabled run.bat.");
            Equal("java-test", runner.JavaExecutable, "Java executable was not forwarded.");
            Equal("--installServer", runner.Arguments!.Single(), "Installer arguments were not forwarded.");
            True(!Directory.EnumerateFiles(destination, "*installer.jar", SearchOption.AllDirectories).Any(),
                "Successful installer should be removed.");
        }
        finally { Delete(destination); }
    }

    private static async Task FailedHashDoesNotPublishArtifactAsync()
    {
        byte[] payload = Bytes("wrong-payload");
        using var http = new HttpClient(new StubHandler((_, _) => Binary(payload)));
        using var service = new ServerCoreService(http);
        var option = new ServerCoreOption
        {
            Id = ServerCoreIds.Vanilla, Name = "Vanilla", CoreVersion = "1.21.1",
            MinecraftVersion = "1.21.1", LoaderType = "fabric",
            InstallStrategy = ServerCoreInstallStrategy.DirectFiles,
            Artifacts = [new ServerCoreArtifact
            {
                Role = ServerCoreArtifactRole.ServerJar, DownloadUrl = "https://piston-data.mojang.com/server.jar",
                RelativePath = "server.jar", Size = payload.Length,
                Hashes = new Dictionary<string, string> { ["sha256"] = new string('0', 64) },
            }],
        };
        string destination = TempDirectory();
        try
        {
            ServerCoreInstallResult result = await service.InstallAsync(new ServerCoreInstallRequest
            {
                Option = option, DestinationDirectory = destination,
            });
            True(!result.Succeeded, "Hash mismatch was accepted.");
            True(!File.Exists(Path.Combine(destination, "server.jar")), "Unverified artifact was published.");
        }
        finally { Delete(destination); }
    }

    private static async Task DirectCoreUsesPublishedJarPathAsync()
    {
        byte[] payload = Bytes("nested-server");
        using var http = new HttpClient(new StubHandler((_, _) => Binary(payload)));
        using var service = new ServerCoreService(http);
        var option = new ServerCoreOption
        {
            Id = ServerCoreIds.Vanilla, Name = "Nested", CoreVersion = "test",
            MinecraftVersion = "1.21.1", LoaderType = "fabric",
            InstallStrategy = ServerCoreInstallStrategy.DirectFiles,
            Artifacts = [new ServerCoreArtifact
            {
                Role = ServerCoreArtifactRole.ServerJar,
                DownloadUrl = "https://example.test/nested.jar",
                RelativePath = "core/nested.jar",
                Size = payload.Length,
                Hashes = new Dictionary<string, string> { ["sha1"] = Sha1(payload) },
            }],
        };
        string destination = TempDirectory();
        try
        {
            ServerCoreInstallResult result = await service.InstallAsync(new ServerCoreInstallRequest
            {
                Option = option, DestinationDirectory = destination,
            });
            True(result.Succeeded, "A valid nested direct core did not install.");
            Equal("java -jar \"core/nested.jar\" nogui", result.LaunchCommand,
                "The direct launch command ignored the published JAR path.");
        }
        finally { Delete(destination); }
    }

    private static async Task LegacyForgeJarIsRecognizedAsync()
    {
        byte[] installer = Bytes("legacy-installer");
        var runner = new FakeJavaRunner((_, _, _, directory, _) =>
        {
            File.WriteAllText(Path.Combine(directory, "forge-1.16.5-36.2.42.jar"), "legacy");
            return Task.FromResult(0);
        });
        using var http = new HttpClient(new StubHandler((_, _) => Binary(installer)));
        using var service = new ServerCoreService(http, javaRunner: runner);
        var option = new ServerCoreOption
        {
            Id = ServerCoreIds.Forge, Name = "Forge", CoreVersion = "36.2.42",
            MinecraftVersion = "1.16.5", LoaderType = "forge", LoaderVersion = "36.2.42",
            InstallStrategy = ServerCoreInstallStrategy.JavaInstaller,
            Artifacts = [new ServerCoreArtifact
            {
                Role = ServerCoreArtifactRole.Installer,
                DownloadUrl = "https://example.test/forge-installer.jar",
                RelativePath = ".installers/forge-installer.jar",
                Size = installer.Length,
                Hashes = new Dictionary<string, string> { ["sha1"] = Sha1(installer) },
                DeleteAfterInstall = true,
            }],
            JavaInstaller = new ServerCoreJavaInstaller { ArtifactRelativePath = ".installers/forge-installer.jar" },
        };
        string destination = TempDirectory();
        try
        {
            ServerCoreInstallResult result = await service.InstallAsync(new ServerCoreInstallRequest
            {
                Option = option, DestinationDirectory = destination, JavaExecutable = "java-test",
            });
            True(result.Succeeded, "A legacy Forge output JAR was not recognized.");
            Equal("java -jar \"forge-1.16.5-36.2.42.jar\" nogui", result.LaunchCommand,
                "The legacy Forge launch command is wrong.");
        }
        finally { Delete(destination); }
    }

    private static async Task InvalidInstallerRoleIsRejectedAsync()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Missing()));
        using var service = new ServerCoreService(http);
        var option = new ServerCoreOption
        {
            Id = ServerCoreIds.Forge, Name = "Invalid", CoreVersion = "test",
            MinecraftVersion = "1.20.1", LoaderType = "forge",
            InstallStrategy = ServerCoreInstallStrategy.JavaInstaller,
            Artifacts =
            [
                new ServerCoreArtifact { Role = ServerCoreArtifactRole.Installer, DownloadUrl = "https://example.test/real.jar", RelativePath = "real.jar" },
                new ServerCoreArtifact { Role = ServerCoreArtifactRole.ServerJar, DownloadUrl = "https://example.test/wrong.jar", RelativePath = "wrong.jar" },
            ],
            JavaInstaller = new ServerCoreJavaInstaller { ArtifactRelativePath = "wrong.jar" },
        };
        string destination = TempDirectory();
        try
        {
            await AssertThrowsAsync<InvalidDataException>(() => service.InstallAsync(new ServerCoreInstallRequest
            {
                Option = option, DestinationDirectory = destination, JavaExecutable = "java",
            }));
        }
        finally { Delete(destination); }
    }

    private static async Task ReparsePointArtifactPathIsRejectedAsync()
    {
        string destination = TempDirectory();
        string external = TempDirectory();
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(destination, "mods"), external);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }
            using var http = new HttpClient(new StubHandler((_, _) => Binary(Bytes("payload"))));
            using var service = new ServerCoreService(http);
            var option = new ServerCoreOption
            {
                Id = ServerCoreIds.Vanilla, Name = "Unsafe", CoreVersion = "test",
                MinecraftVersion = "1.21.1", LoaderType = "fabric",
                InstallStrategy = ServerCoreInstallStrategy.DirectFiles,
                Artifacts = [new ServerCoreArtifact
                {
                    Role = ServerCoreArtifactRole.ServerJar,
                    DownloadUrl = "https://example.test/server.jar",
                    RelativePath = "mods/server.jar",
                }],
            };
            await AssertThrowsAsync<InvalidDataException>(() => service.InstallAsync(new ServerCoreInstallRequest
            {
                Option = option, DestinationDirectory = destination,
            }));
            True(!File.Exists(Path.Combine(external, "server.jar")), "A core artifact escaped through a reparse point.");
        }
        finally { Delete(destination); Delete(external); }
    }

    private static HttpResponseMessage Json(object value) => Text(JsonSerializer.Serialize(value), "application/json");
    private static HttpResponseMessage Text(string value, string type) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, type) };
    private static HttpResponseMessage Binary(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static HttpResponseMessage Missing() => new(HttpStatusCode.NotFound);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Sha1(byte[] value) => Hex(SHA1.HashData(value));
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    private static string TempDirectory() { string path = Path.Combine(Path.GetTempPath(), "McModpackToolServerCoreTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Delete(string path) { try { Directory.Delete(path, true); } catch { } }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}."); }
    private static void BytesEqual(byte[] expected, byte[] actual, string message) { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException(message); }
    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message) { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"{message} Expected: {string.Join(", ", expected)}; actual: {string.Join(", ", actual)}."); }
    private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : this((request, token) => Task.FromResult(handler(request, token))) { }
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }

    private sealed class FakeJavaRunner : IServerCoreJavaRunner
    {
        private readonly Func<string, string, IReadOnlyList<string>, string, CancellationToken, Task<int>> _run;
        public FakeJavaRunner(Func<string, string, IReadOnlyList<string>, string, CancellationToken, Task<int>> run) => _run = run;
        public string JavaExecutable { get; private set; } = string.Empty;
        public IReadOnlyList<string>? Arguments { get; private set; }
        public Task<int> RunAsync(string javaExecutable, string installerPath, IReadOnlyList<string> installerArguments, string workingDirectory, CancellationToken cancellationToken = default)
        {
            JavaExecutable = javaExecutable;
            Arguments = installerArguments;
            return _run(javaExecutable, installerPath, installerArguments, workingDirectory, cancellationToken);
        }
    }
}
