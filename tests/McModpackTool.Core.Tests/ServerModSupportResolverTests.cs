using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class ServerModSupportResolverTests
{
    public static async Task RunAllAsync()
    {
        await ReadsFabricEntrypointsAndNestedIdsAsync();
        await RetainsNestedJavaRequirementsAsync();
        await KeepsFabricApiAggregateSelectedAsync();
        await PromotesClientEntrypointOnlyRequiredLibraryAsync();
        await DetectsUnsafeFabricCommonEntrypointReferencesAsync();
        await ReadsLegacyForgeMetadataAsync();
        await ResolvesExactPlatformSidesAndLocalFallbacksAsync();
        await InspectsFabricCandidatesInsideForgePacksAsync();
        await PromotesCreativeCoreRequiredByPlayerReviveAsync();
        await ResolvesManifestIdentitiesAndDependenciesAsync();
        await PlatformFailureKeepsUnknownLocalModSelectedAsync();
        await ExcludesKnownClientModsWithoutPlatformMetadataAsync();
        await LocalIndustrialSampleExcludesClientModsAsync();
        await LocalForgeConnectorSampleExcludesClientModsAsync();
    }

    private static async Task ReadsLegacyForgeMetadataAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string path = Path.Combine(root, "legacy-client.jar");
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                WriteEntry(archive, "mcmod.info", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        modid = "legacy_map",
                        name = "Legacy Minimap",
                        description = "Client-side minimap",
                        version = "1.0.0",
                        clientSideOnly = true,
                        requiredMods = new[] { "forge@[14.23.5.2859,)" },
                    },
                })));
            }

            ArtifactCompatibilityMetadata metadata = ArtifactMetadataReader.Read(path);

            Equal("legacy_map", metadata.Id, "Legacy Forge mod ID was not read.");
            Equal("Legacy Minimap", metadata.Name, "Legacy Forge display name was not read.");
            Equal("client", metadata.ServerEnvironment, "Legacy clientSideOnly was not retained.");
            True(metadata.Relations.Any(relation => relation.ExactReference == "forge"),
                "Legacy requiredMods dependency was not retained.");
        });
    }

    private static async Task ReadsFabricEntrypointsAndNestedIdsAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string path = Path.Combine(root, "outer.jar");
            byte[] nested = CreateFabricJarBytes("fabric-resource-loader-v0", null, ["main"]);
            await CreateFabricJarAsync(
                path,
                "fabric-api",
                "*",
                ["client", "main"],
                nestedArtifacts: new Dictionary<string, byte[]>
                {
                    ["META-INF/jars/resource-loader.jar"] = nested,
                });

            ArtifactCompatibilityMetadata metadata = ArtifactMetadataReader.Read(path);

            True(metadata.HasClientEntrypoint, "Fabric client entrypoint was not retained.");
            True(metadata.HasCommonEntrypoint, "Fabric common entrypoint was not retained.");
            True(!metadata.HasServerEntrypoint, "A Fabric server entrypoint was fabricated.");
            True(metadata.ModIds.Contains("fabric-resource-loader-v0", StringComparer.OrdinalIgnoreCase),
                "A one-level nested Fabric mod ID was not retained.");
        });
    }

    private static async Task KeepsFabricApiAggregateSelectedAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string path = Path.Combine(root, "fabric-api-0.115.0+1.21.4.jar");
            byte[] nested = CreateFabricJarBytes("fabric-key-binding-api-v1", "client", ["client"]);
            await CreateFabricJarAsync(
                path,
                "fabric-api",
                "*",
                [],
                nestedArtifacts: new Dictionary<string, byte[]>
                {
                    ["META-INF/jars/fabric-key-binding-api-v1.jar"] = nested,
                });

            using var http = CreateEmptyPlatformClient();
            using var modrinth = new ModrinthClient(http);
            var source = new ServerPackSource
            {
                LoaderType = "fabric",
                Mods = [LocalEntry(path)],
            };

            await new ServerModSupportResolver(modrinth).ResolveAsync(source);

            AssertSupport(source, path, ServerSupportKinds.Recommended, selected: true);
        });
    }

    private static async Task RetainsNestedJavaRequirementsAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string path = Path.Combine(root, "framework-bundle.jar");
            byte[] nested = CreateFabricJarBytes(
                "framework",
                "*",
                ["main"],
                requiredDependencyRequirements: new Dictionary<string, string>
                {
                    ["java"] = ">=21",
                });
            await CreateFabricJarAsync(
                path,
                "framework-bundle",
                "*",
                ["main"],
                nestedArtifacts: new Dictionary<string, byte[]>
                {
                    ["META-INF/jars/framework.jar"] = nested,
                });

            using var http = CreateEmptyPlatformClient();
            using var modrinth = new ModrinthClient(http);
            var source = new ServerPackSource
            {
                LoaderType = "fabric",
                Mods = [LocalEntry(path)],
            };

            await new ServerModSupportResolver(modrinth).ResolveAsync(source);

            Equal(">=21", source.Mods.Single().JavaVersionRequirements.Single(),
                "The selected mod lost its nested Java runtime requirement.");
        });
    }

    private static async Task PromotesClientEntrypointOnlyRequiredLibraryAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string clothConfig = Path.Combine(root, "cloth-config.jar");
            string owner = Path.Combine(root, "server-mod.jar");
            await CreateFabricJarAsync(
                clothConfig,
                "cloth-config",
                null,
                ["client"],
                providedIds: ["cloth-config2"]);
            await CreateFabricJarAsync(
                owner,
                "server-mod",
                "*",
                ["main"],
                requiredDependencies: ["cloth-config2"]);

            using var http = CreateEmptyPlatformClient();
            using var modrinth = new ModrinthClient(http);
            var source = new ServerPackSource
            {
                LoaderType = "fabric",
                Mods = [LocalEntry(clothConfig), LocalEntry(owner)],
            };

            await new ServerModSupportResolver(modrinth).ResolveAsync(source);

            AssertSupport(source, clothConfig, ServerSupportKinds.Recommended, selected: true);
            AssertSupport(source, owner, ServerSupportKinds.Unknown, selected: true);
        });
    }

    private static async Task DetectsUnsafeFabricCommonEntrypointReferencesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string safePath = Path.Combine(root, "safe-main.jar");
            await CreateFabricEntrypointJarAsync(
                safePath,
                "safe_main",
                JsonValue.Create("example.safe.SafeMain")!,
                new Dictionary<string, byte[]>
                {
                    ["example/safe/SafeMain.class"] = CreateSyntheticClass("example/safe/SafeMain", "java/lang/Object"),
                    ["example/client/Unused.class"] = CreateSyntheticClass("net/minecraft/client/Minecraft"),
                });

            string fabricClientPath = Path.Combine(root, "fabric-client-main.jar");
            await CreateFabricEntrypointJarAsync(
                fabricClientPath,
                "fabric_client_main",
                new JsonArray("example.fabric.UnsafeMain"),
                new Dictionary<string, byte[]>
                {
                    ["example/fabric/UnsafeMain.class"] = CreateSyntheticClass(
                        "net/fabricmc/fabric/api/client/event/lifecycle/v1/ClientTickEvents"),
                });

            string minecraftClientPath = Path.Combine(root, "minecraft-client-main.jar");
            await CreateFabricEntrypointJarAsync(
                minecraftClientPath,
                "minecraft_client_main",
                new JsonObject
                {
                    ["adapter"] = "default",
                    ["value"] = "example.minecraft.UnsafeMain::initialize",
                },
                new Dictionary<string, byte[]>
                {
                    ["example/minecraft/UnsafeMain.class"] = CreateSyntheticClass(
                        "net/minecraft/client/MinecraftClient"),
                });

            ArtifactCompatibilityMetadata safe = ArtifactMetadataReader.Read(safePath);
            ArtifactCompatibilityMetadata fabricClient = ArtifactMetadataReader.Read(fabricClientPath);
            ArtifactCompatibilityMetadata minecraftClient = ArtifactMetadataReader.Read(minecraftClientPath);

            True(!safe.HasUnsafeClientReferencesInCommonEntrypoint,
                "A client reference outside the declared common entrypoint caused a false positive.");
            True(fabricClient.HasUnsafeClientReferencesInCommonEntrypoint,
                "A Fabric client API reference in an array main entrypoint was not detected.");
            True(minecraftClient.HasUnsafeClientReferencesInCommonEntrypoint,
                "A Minecraft client reference in an object/method main entrypoint was not detected.");
        });
    }

    private static async Task ResolvesExactPlatformSidesAndLocalFallbacksAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string explicitClient = Path.Combine(root, "explicit-client.jar");
            string clientEntrypoint = Path.Combine(root, "client-entrypoint.jar");
            string optional = Path.Combine(root, "optional.jar");
            string knownServerFeature = Path.Combine(root, "known-server-feature.jar");
            string required = Path.Combine(root, "required.jar");
            string dependency = Path.Combine(root, "dependency.jar");
            string owner = Path.Combine(root, "owner.jar");
            string privateMod = Path.Combine(root, "private.jar");
            string disabled = Path.Combine(root, "disabled.jar.disabled");
            await CreateFabricJarAsync(explicitClient, "explicit_client", "client", ["client"]);
            await CreateFabricJarAsync(clientEntrypoint, "client_entrypoint", null, ["client"]);
            await CreateFabricJarAsync(optional, "optional_mod", "*", ["client", "main"]);
            await CreateFabricJarAsync(knownServerFeature, "voicechat", "*", ["client", "main"]);
            await CreateFabricJarAsync(required, "required_mod", "*", ["client", "main"]);
            await CreateFabricJarAsync(dependency, "dependency_mod", "*", ["main"]);
            await CreateFabricJarAsync(owner, "owner_mod", "*", ["main"],
                requiredDependencies: ["dependency_mod"]);
            await CreateFabricJarAsync(privateMod, "private_mod", "*", ["main"]);
            await CreateFabricJarAsync(disabled, "disabled_mod", "*", ["main"]);

            string optionalHash = Sha1(optional);
            string knownServerFeatureHash = Sha1(knownServerFeature);
            string requiredHash = Sha1(required);
            string dependencyHash = Sha1(dependency);
            int hashRequests = 0;
            int projectRequests = 0;
            using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files")
                {
                    hashRequests++;
                    JsonObject body = JsonNode.Parse(
                        await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                    True(body["hashes"]!.AsArray().Count == 7, "Local SHA-1 lookup was not batched.");
                    return JsonResponse(new Dictionary<string, object>
                    {
                        [optionalHash] = new { id = "optional-version", project_id = "optional-project" },
                        [knownServerFeatureHash] = new { id = "known-server-feature-version", project_id = "known-server-feature-project" },
                        [requiredHash] = new { id = "required-version", project_id = "required-project" },
                        [dependencyHash] = new { id = "dependency-version", project_id = "dependency-project" },
                    });
                }
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects")
                {
                    projectRequests++;
                    return JsonResponse(new object[]
                    {
                        new { id = "optional-project", project_type = "mod", server_side = "optional" },
                        new { id = "known-server-feature-project", project_type = "mod", server_side = "optional" },
                        new { id = "required-project", project_type = "mod", server_side = "required" },
                        new { id = "dependency-project", project_type = "mod", server_side = "optional" },
                    });
                }
                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            }))
            {
                BaseAddress = new Uri(ModrinthClient.BaseAddress),
            };
            using var modrinth = new ModrinthClient(http);
            var resolver = new ServerModSupportResolver(modrinth);
            var source = new ServerPackSource
            {
                Mods =
                [
                    LocalEntry(explicitClient),
                    LocalEntry(clientEntrypoint),
                    LocalEntry(optional),
                    LocalEntry(knownServerFeature),
                    LocalEntry(required),
                    LocalEntry(dependency),
                    LocalEntry(owner),
                    LocalEntry(privateMod),
                    LocalEntry(disabled, disabled: true),
                ],
            };

            await resolver.ResolveAsync(source);

            AssertSupport(source, explicitClient, ServerSupportKinds.Unsupported, selected: false);
            AssertSupport(source, clientEntrypoint, ServerSupportKinds.Unsupported, selected: false);
            AssertSupport(source, optional, ServerSupportKinds.Optional, selected: false);
            AssertSupport(source, knownServerFeature, ServerSupportKinds.Recommended, selected: true);
            AssertSupport(source, required, ServerSupportKinds.Recommended, selected: true);
            AssertSupport(source, dependency, ServerSupportKinds.Recommended, selected: true);
            AssertSupport(source, owner, ServerSupportKinds.Unknown, selected: true);
            AssertSupport(source, privateMod, ServerSupportKinds.Unknown, selected: true);
            AssertSupport(source, disabled, ServerSupportKinds.Unknown, selected: false);
            Equal(1, hashRequests, "Hash metadata required more than one batch request.");
            Equal(1, projectRequests, "Project metadata required more than one batch request.");
        });
    }

    private static async Task PlatformFailureKeepsUnknownLocalModSelectedAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string path = Path.Combine(root, "private.jar");
            await CreateFabricJarAsync(path, "private_mod", "*", ["main"]);
            using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
            using var modrinth = new ModrinthClient(http);
            var resolver = new ServerModSupportResolver(modrinth);
            var source = new ServerPackSource { Mods = [LocalEntry(path)] };

            await resolver.ResolveAsync(source);

            AssertSupport(source, path, ServerSupportKinds.Unknown, selected: true);
        });
    }

    private static async Task InspectsFabricCandidatesInsideForgePacksAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string jarPath = Path.Combine(root, "connector-risk.jar");
            await CreateFabricEntrypointJarAsync(
                jarPath,
                "connector_risk",
                JsonValue.Create("example.connector.UnsafeMain")!,
                new Dictionary<string, byte[]>
                {
                    ["example/connector/UnsafeMain.class"] = CreateSyntheticClass(
                        "net/fabricmc/fabric/api/client/screen/v1/ScreenEvents"),
                });
            byte[] jarBytes = await File.ReadAllBytesAsync(jarPath);
            string hash = Convert.ToHexStringLower(SHA1.HashData(jarBytes));
            const string downloadUrl = "https://cdn.example.invalid/connector-risk.jar";

            using var platformHttp = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files")
                {
                    _ = await request.Content!.ReadAsStringAsync(cancellationToken);
                    return JsonResponse(new Dictionary<string, object>
                    {
                        [hash] = new
                        {
                            id = "connector-risk-version",
                            project_id = "connector-risk-project",
                            loaders = new[] { "fabric" },
                            files = new[]
                            {
                                new
                                {
                                    hashes = new Dictionary<string, string> { ["sha1"] = hash },
                                    url = downloadUrl,
                                    filename = "connector-risk.jar",
                                    primary = true,
                                    size = jarBytes.LongLength,
                                },
                            },
                        },
                    });
                }
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects")
                {
                    return JsonResponse(new[]
                    {
                        new
                        {
                            id = "connector-risk-project",
                            slug = "connector-risk-project",
                            project_type = "mod",
                            client_side = "optional",
                            server_side = "optional",
                        },
                    });
                }
                throw new InvalidOperationException($"Unexpected platform request: {request.Method} {request.RequestUri}");
            }))
            {
                BaseAddress = new Uri(ModrinthClient.BaseAddress),
            };
            int artifactRequests = 0;
            using var artifactHttp = new HttpClient(new DelegateHandler((request, _) =>
            {
                Equal(downloadUrl, request.RequestUri!.AbsoluteUri, "The wrong Connector candidate was downloaded.");
                artifactRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(jarBytes),
                });
            }));
            using var modrinth = new ModrinthClient(platformHttp);
            var resolver = new ServerModSupportResolver(modrinth, artifactHttpClient: artifactHttp);
            ServerModEntry entry = ManifestEntry("connector-risk", "connector-risk-project", hash);
            entry.ContentItem!.DownloadUrl = downloadUrl;
            entry.ContentItem.FileName = "connector-risk.jar";
            entry.ContentItem.FileSize = jarBytes.LongLength;
            var source = new ServerPackSource
            {
                LoaderType = "forge",
                Mods = [entry],
            };

            await resolver.ResolveAsync(source);

            AssertSupport(source, "connector-risk", ServerSupportKinds.Unsupported, selected: false);
            Equal(1, artifactRequests, "The Fabric-in-Forge candidate was not inspected exactly once.");
            True(entry.SupportReason.Contains("client-only APIs", StringComparison.Ordinal),
                "The bytecode evidence did not drive the server classification.");
        });
    }

    private static async Task PromotesCreativeCoreRequiredByPlayerReviveAsync()
    {
        const string playerReviveHash = "player-revive-hash";
        const string creativeCoreHash = "creative-core-hash";
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files")
            {
                _ = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse(new Dictionary<string, object>
                {
                    [playerReviveHash] = new
                    {
                        id = "fUdn8MeF",
                        project_id = "ABIMzABM",
                        loaders = new[] { "forge" },
                        dependencies = new[]
                        {
                            new { project_id = "OsZiaDHq", dependency_type = "required" },
                        },
                    },
                    [creativeCoreHash] = new
                    {
                        id = "IbFWHI5h",
                        project_id = "OsZiaDHq",
                        loaders = new[] { "forge" },
                        dependencies = Array.Empty<object>(),
                    },
                });
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects")
            {
                return JsonResponse(new object[]
                {
                    new
                    {
                        id = "ABIMzABM",
                        slug = "playerrevive",
                        project_type = "mod",
                        client_side = "required",
                        server_side = "required",
                    },
                    new
                    {
                        id = "OsZiaDHq",
                        slug = "creativecore",
                        project_type = "mod",
                        client_side = "required",
                        server_side = "optional",
                    },
                });
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }))
        {
            BaseAddress = new Uri(ModrinthClient.BaseAddress),
        };
        using var modrinth = new ModrinthClient(http);
        var resolver = new ServerModSupportResolver(modrinth);
        var source = new ServerPackSource
        {
            LoaderType = "forge",
            Mods =
            [
                ManifestEntry("PlayerRevive", "ABIMzABM", playerReviveHash),
                ManifestEntry("CreativeCore", "OsZiaDHq", creativeCoreHash),
            ],
        };

        await resolver.ResolveAsync(source);

        AssertSupport(source, "PlayerRevive", ServerSupportKinds.Recommended, selected: true);
        AssertSupport(source, "CreativeCore", ServerSupportKinds.Recommended, selected: true);
        ServerModEntry creativeCore = source.Mods.Single(entry => entry.SourcePath == "CreativeCore");
        True(creativeCore.SupportReason.Contains("PlayerRevive", StringComparison.Ordinal),
            "CreativeCore was selected without recording the required dependency owner.");
        True(creativeCore.ContentItem?.Excluded == false,
            "CreativeCore remained excluded from the exported manifest after dependency promotion.");
    }

    private static async Task ExcludesKnownClientModsWithoutPlatformMetadataAsync()
    {
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var modrinth = new ModrinthClient(http);
        var resolver = new ServerModSupportResolver(modrinth);
        var source = new ServerPackSource
        {
            Mods =
            [
                ManifestEntry("[体素地图] forgemod_VoxelMap-1.9.28_for_1.12.2.jar", string.Empty, "voxel-hash"),
                ManifestEntry("preview_OptiFine_1.12.2_HD_U_G6_pre1.jar", string.Empty, "optifine-hash"),
                ManifestEntry("[JEI物品管理器] jei_1.12.2-4.16.1.302.jar", string.Empty, "jei-hash"),
                ManifestEntry("private-server-map-helper.jar", string.Empty, "private-hash"),
            ],
        };

        await resolver.ResolveAsync(source);

        AssertSupport(source, "[体素地图] forgemod_VoxelMap-1.9.28_for_1.12.2.jar",
            ServerSupportKinds.Unsupported, selected: false);
        AssertSupport(source, "preview_OptiFine_1.12.2_HD_U_G6_pre1.jar",
            ServerSupportKinds.Unsupported, selected: false);
        AssertSupport(source, "[JEI物品管理器] jei_1.12.2-4.16.1.302.jar",
            ServerSupportKinds.Unsupported, selected: false);
        AssertSupport(source, "private-server-map-helper.jar",
            ServerSupportKinds.Unknown, selected: true);
    }

    private static async Task LocalIndustrialSampleExcludesClientModsAsync()
    {
        string? samplePath = FindLocalIndustrialSample();
        if (samplePath is null)
        {
            return;
        }

        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"industrial-server-source-{Guid.NewGuid():N}");
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var curseForge = new CurseForgeClient("test-key", http);
        using var modrinth = new ModrinthClient(http);
        try
        {
            var reader = new ServerArchiveSourceReader(curseForge);
            ServerPackSource source = await reader.ReadAsync(samplePath, temporaryRoot);
            await new ServerModSupportResolver(modrinth).ResolveAsync(source);

            string[] excludedNames =
            [
                "VoxelMap", "OptiFine", "xaerolib", "JEI", "CustomSkinLoader", "AppleSkin",
                "InventoryTweaks", "MouseTweaks", "Hwyla", "WailaHarvestability",
                "血条显示", "lanserverproperties",
            ];
            foreach (string expected in excludedNames)
            {
                ServerModEntry entry = source.Mods.Single(mod =>
                    mod.Name.Contains(expected, StringComparison.OrdinalIgnoreCase));
                Equal(ServerSupportKinds.Unsupported, entry.ServerSupport,
                    $"The local sample client mod '{entry.Name}' was not excluded.");
                True(!entry.Selected, $"The local sample client mod '{entry.Name}' was selected.");
            }

            ServerModEntry waystones = source.Mods.Single(mod =>
                mod.Name.Contains("Waystones", StringComparison.OrdinalIgnoreCase));
            True(waystones.Selected, "A common gameplay mod was removed by the client-only fallback.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static string? FindLocalIndustrialSample()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "C#", "测试样例", "1.12.2 工业-2.zip");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static async Task LocalForgeConnectorSampleExcludesClientModsAsync()
    {
        string? samplePath = FindLocalForgeConnectorSample();
        if (samplePath is null)
        {
            return;
        }

        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"connector-server-source-{Guid.NewGuid():N}");
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var curseForge = new CurseForgeClient("test-key", http);
        using var modrinth = new ModrinthClient(http);
        try
        {
            var reader = new ServerArchiveSourceReader(curseForge);
            ServerPackSource source = await reader.ReadAsync(samplePath, temporaryRoot);
            await new ServerModSupportResolver(modrinth, artifactHttpClient: http).ResolveAsync(source);

            string[] excludedNames =
            [
                "fancymenu", "konkrete", "melody", "mcwifipnp", "sound-physics-remastered",
                "PresenceFootsteps", "CTM-",
            ];
            foreach (string expected in excludedNames)
            {
                ServerModEntry entry = source.Mods.Single(mod =>
                    mod.Name.Contains(expected, StringComparison.OrdinalIgnoreCase));
                Equal(ServerSupportKinds.Unsupported, entry.ServerSupport,
                    $"The Connector sample client mod '{entry.Name}' was not excluded.");
                True(!entry.Selected, $"The Connector sample client mod '{entry.Name}' was selected.");
            }

            foreach (string expected in new[] { "new_soviet", "lazydfu", "fabric-api" })
            {
                ServerModEntry entry = source.Mods.Single(mod =>
                    mod.Name.Contains(expected, StringComparison.OrdinalIgnoreCase));
                True(entry.Selected,
                    $"The usable cross-loader sample mod '{entry.Name}' was excluded by a loader-only rule.");
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static string? FindLocalForgeConnectorSample()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "C#",
                "\u6d4b\u8bd5\u6837\u4f8b",
                "\u9ad8\u67b6\u60ca\u53d8\u6574\u5408\u5305-Forge.mrpack");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static async Task ResolvesManifestIdentitiesAndDependenciesAsync()
    {
        const string appHash = "app-hash";
        const string apiHash = "api-hash";
        const string voiceHash = "voice-hash";
        const string dependencyHash = "dependency-hash";
        const string optionalHash = "optional-hash";

        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files")
            {
                JsonObject body = JsonNode.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Equal(5, body["hashes"]!.AsArray().Count,
                    "Manifest hashes were not sent as one exact batch.");
                return JsonResponse(new Dictionary<string, object>
                {
                    [appHash] = new
                    {
                        id = "app-version",
                        project_id = "server-app",
                        dependencies = new[]
                        {
                            new { project_id = "P7dR8mSH", dependency_type = "required" },
                            new { project_id = "required-library", dependency_type = "required" },
                        },
                    },
                    [apiHash] = new { id = "api-version", project_id = "P7dR8mSH", dependencies = Array.Empty<object>() },
                    [voiceHash] = new { id = "voice-version", project_id = "9eGKb6K1", dependencies = Array.Empty<object>() },
                    [dependencyHash] = new { id = "dependency-version", project_id = "required-library", dependencies = Array.Empty<object>() },
                    [optionalHash] = new { id = "optional-version", project_id = "optional-project", dependencies = Array.Empty<object>() },
                });
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v2/projects")
            {
                return JsonResponse(new object[]
                {
                    new { id = "server-app", slug = "server-app", project_type = "mod", client_side = "optional", server_side = "required" },
                    new { id = "P7dR8mSH", slug = "fabric-api", project_type = "mod", client_side = "optional", server_side = "optional" },
                    new { id = "9eGKb6K1", slug = "simple-voice-chat", project_type = "mod", client_side = "optional", server_side = "optional" },
                    new { id = "required-library", slug = "required-library", project_type = "mod", client_side = "optional", server_side = "optional" },
                    new { id = "optional-project", slug = "optional-project", project_type = "mod", client_side = "required", server_side = "optional" },
                });
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }))
        {
            BaseAddress = new Uri(ModrinthClient.BaseAddress),
        };
        using var modrinth = new ModrinthClient(http);
        var resolver = new ServerModSupportResolver(modrinth);
        var source = new ServerPackSource
        {
            Mods =
            [
                ManifestEntry("server-app", "server-app", appHash),
                ManifestEntry("fabric-api", "P7dR8mSH", apiHash, "optional"),
                ManifestEntry("voicechat", "9eGKb6K1", voiceHash, "optional"),
                ManifestEntry("required-library", "required-library", dependencyHash, "optional"),
                ManifestEntry("optional", "optional-project", optionalHash, "optional"),
            ],
        };

        await resolver.ResolveAsync(source);

        AssertSupport(source, "server-app", ServerSupportKinds.Recommended, selected: true);
        AssertSupport(source, "fabric-api", ServerSupportKinds.Recommended, selected: true);
        AssertSupport(source, "voicechat", ServerSupportKinds.Recommended, selected: true);
        AssertSupport(source, "required-library", ServerSupportKinds.Recommended, selected: true);
        AssertSupport(source, "optional", ServerSupportKinds.Optional, selected: false);
    }

    private static ServerModEntry LocalEntry(string path, bool disabled = false) => new()
    {
        Name = Path.GetFileName(path),
        RelativePath = Path.GetFileName(path),
        SourcePath = path,
        Origin = ServerModOrigins.Local,
        Disabled = disabled,
    };

    private static ServerModEntry ManifestEntry(
        string name,
        string projectId,
        string hash,
        string? serverEnvironment = null) => new()
    {
        Name = name,
        RelativePath = $"mods/{name}.jar",
        // The manifest fixture represents the archive entry by its relative path.
        // Keeping SourcePath aligned lets the assertions identify the same entry
        // without requiring a physical downloaded JAR on disk.
        SourcePath = name,
        Origin = ServerModOrigins.Manifest,
        ContentItem = new ContentItem
        {
            Name = name,
            ProjectId = projectId,
            Source = "modrinth",
            OriginalSource = "modrinth",
            Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sha1"] = hash },
            Environment = serverEnvironment is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["server"] = serverEnvironment },
        },
    };

    private static void AssertSupport(
        ServerPackSource source,
        string path,
        string support,
        bool selected)
    {
        ServerModEntry entry = source.Mods.Single(item => item.SourcePath == path);
        Equal(support, entry.ServerSupport, $"Wrong support classification for {Path.GetFileName(path)}.");
        Equal(selected, entry.Selected, $"Wrong default selection for {Path.GetFileName(path)}.");
    }

    private static async Task CreateFabricJarAsync(
        string path,
        string id,
        string? environment,
        IReadOnlyList<string> entrypointKinds,
        IReadOnlyDictionary<string, byte[]>? nestedArtifacts = null,
        IReadOnlyList<string>? requiredDependencies = null,
        IReadOnlyList<string>? providedIds = null,
        IReadOnlyDictionary<string, string>? requiredDependencyRequirements = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, CreateFabricJarBytes(
            id,
            environment,
            entrypointKinds,
            nestedArtifacts,
            requiredDependencies,
            providedIds,
            requiredDependencyRequirements));
    }

    private static byte[] CreateFabricJarBytes(
        string id,
        string? environment,
        IReadOnlyList<string> entrypointKinds,
        IReadOnlyDictionary<string, byte[]>? nestedArtifacts = null,
        IReadOnlyList<string>? requiredDependencies = null,
        IReadOnlyList<string>? providedIds = null,
        IReadOnlyDictionary<string, string>? requiredDependencyRequirements = null)
    {
        var entrypoints = new JsonObject();
        foreach (string kind in entrypointKinds)
        {
            entrypoints[kind] = new JsonArray($"example.{kind}.Entrypoint");
        }
        var metadata = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["version"] = "1.0.0",
            ["entrypoints"] = entrypoints,
        };
        if (environment is not null)
        {
            metadata["environment"] = environment;
        }
        var dependencies = new JsonObject();
        foreach (string dependency in requiredDependencies ?? [])
        {
            dependencies[dependency] = "*";
        }
        foreach ((string dependency, string requirement) in
                 requiredDependencyRequirements ?? new Dictionary<string, string>())
        {
            dependencies[dependency] = requirement;
        }
        if (dependencies.Count > 0)
        {
            metadata["depends"] = dependencies;
        }
        if (providedIds is { Count: > 0 })
        {
            var provides = new JsonArray();
            foreach (string providedId in providedIds)
            {
                provides.Add(providedId);
            }
            metadata["provides"] = provides;
        }
        if (nestedArtifacts is { Count: > 0 })
        {
            var jars = new JsonArray();
            foreach (string nestedPath in nestedArtifacts.Keys)
            {
                jars.Add(new JsonObject { ["file"] = nestedPath });
            }
            metadata["jars"] = jars;
        }

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            WriteEntry(archive, "fabric.mod.json", Encoding.UTF8.GetBytes(metadata.ToJsonString()));
            foreach (var (nestedPath, payload) in nestedArtifacts ?? new Dictionary<string, byte[]>())
            {
                WriteEntry(archive, nestedPath, payload);
            }
        }
        return memory.ToArray();
    }

    private static HttpClient CreateEmptyPlatformClient() => new(new DelegateHandler((request, _) =>
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/version_files")
        {
            return Task.FromResult(JsonResponse(new Dictionary<string, object>()));
        }
        throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
    }))
    {
        BaseAddress = new Uri(ModrinthClient.BaseAddress),
    };

    private static async Task CreateFabricEntrypointJarAsync(
        string path,
        string id,
        JsonNode mainEntrypoint,
        IReadOnlyDictionary<string, byte[]> classes)
    {
        var metadata = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["version"] = "1.0.0",
            ["entrypoints"] = new JsonObject { ["main"] = mainEntrypoint },
        };
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        WriteEntry(archive, "fabric.mod.json", Encoding.UTF8.GetBytes(metadata.ToJsonString()));
        foreach ((string classPath, byte[] payload) in classes)
        {
            WriteEntry(archive, classPath, payload);
        }
    }

    private static byte[] CreateSyntheticClass(params string[] utf8Constants)
    {
        using var stream = new MemoryStream();
        stream.Write([0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x34]);
        WriteUInt16BigEndian(stream, checked((ushort)(utf8Constants.Length + 1)));
        foreach (string constant in utf8Constants)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(constant);
            stream.WriteByte(1);
            WriteUInt16BigEndian(stream, checked((ushort)bytes.Length));
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] payload)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(payload);
    }

    private static string Sha1(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> operation)
    {
        string root = Path.Combine(Path.GetTempPath(), $"server-support-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await operation(root);
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

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
