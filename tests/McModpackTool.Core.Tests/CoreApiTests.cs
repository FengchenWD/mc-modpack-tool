using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class CoreApiTests
{
    public static async Task RunAllAsync()
    {
        SearchQueriesRemoveLoaderVersionsAndAnnotations();
        ForgeCdnIdentityUsesOnlyTrustedHosts();
        FtbCompanionProjectsCannotReplaceBaseProjects();
        ExactSlugsAndConcatenatedNamesRemainSupported();
        ResourcePackVariantsRemainDistinct();
        AmbiguousIdentityIsRejected();
        SameEnvironmentPreservesOriginalReference();
        CurseForgeSourceIdentityChecksNameSizeAndHash();
        PrimaryModrinthFileIsSelected();
        await ModrinthUsesExactMinecraftAndLoaderAsync();
        await CurseForgeBulkIdentityLookupBatchesAsync();
        await ResolverPreservesSameEnvironmentDependenciesAsync();
        await LoaderVersionsUseLatestStableNumericBuildAsync();
        await ResolverVerifiesTargetAvailabilityBeforeSelectingIdentityAsync();
        await MissingCurseForgeKeySkipsOptionalFallbackAsync();
    }

    private static void SearchQueriesRemoveLoaderVersionsAndAnnotations()
    {
        var queries = SearchMatcher.ExtractSearchQueries(
            "[连锁破坏] ftb-ultimine-fabric-2101.1.13.jar");
        Equal("ftb ultimine", queries[0], "The strongest query should retain only identity tokens.");
        True(queries.All(query => !query.Contains("jar", StringComparison.Ordinal)), "Extension leaked into search query.");
        True(queries.All(query => !query.Contains("fabric", StringComparison.Ordinal)), "Loader leaked into search query.");
        True(queries.All(query => !query.Contains("2101", StringComparison.Ordinal)), "Version leaked into search query.");
    }

    private static void ForgeCdnIdentityUsesOnlyTrustedHosts()
    {
        Equal(
            "7420411",
            SearchMatcher.ParseCurseForgeFileId(
                ["https://mediafilez.forgecdn.net/files/7420/411/ftb-library.jar"]),
            "ForgeCDN file ID reconstruction failed.");
        Equal(
            "6230064",
            SearchMatcher.ParseCurseForgeFileId(
                ["https://edge.forgecdn.net/files/6230/64/Stay%20True.zip"]),
            "Short ForgeCDN suffix should be zero-positioned within the numeric ID.");
        Equal(
            string.Empty,
            SearchMatcher.ParseCurseForgeFileId(
                ["https://example.invalid/files/7420/411/not-curseforge.jar"]),
            "An untrusted host must not provide a CurseForge identity.");
    }

    private static void FtbCompanionProjectsCannotReplaceBaseProjects()
    {
        const string ultimineFile = "[连锁破坏] ftb-ultimine-fabric-2101.1.13.jar";
        const string libraryFile = "ftb-library-fabric-2101.1.30.jar";
        var ultimineAddon = new ModrinthProject
        {
            ProjectId = "PbTAQA4c",
            Title = "FTB Ultimine Cobblemon Compat",
            Slug = "ftb-ultimine-cobblemon-compat",
        };
        var ultimineBase = new ModrinthProject
        {
            ProjectId = "ftb-ultimine",
            Title = "FTB Ultimine",
            Slug = "ftb-ultimine-fabric",
        };
        Null(
            SearchMatcher.PickBestModrinthResult([ultimineAddon], ultimineFile, "ftb ultimine"),
            "FTB Ultimine addon was accepted as the base project.");
        Same(
            ultimineBase,
            SearchMatcher.PickBestModrinthResult(
                [ultimineAddon, ultimineBase],
                ultimineFile,
                "ftb ultimine"),
            "FTB Ultimine base project did not beat its addon.");

        var libraryAddon = new ModrinthProject
        {
            ProjectId = "UNCI38gZ",
            Title = "Tensura Compat - FTB",
            Slug = "tensura-compat-ftb",
        };
        var libraryBase = new ModrinthProject
        {
            ProjectId = "ftb-library",
            Title = "FTB Library",
            Slug = "ftb-library-fabric",
        };
        Null(
            SearchMatcher.PickBestModrinthResult([libraryAddon], libraryFile, "ftb"),
            "A short FTB query accepted an unrelated compatibility project.");
        Same(
            libraryBase,
            SearchMatcher.PickBestModrinthResult(
                [libraryBase, libraryAddon],
                libraryFile,
                "ftb library"),
            "FTB Library base project was not retained.");

        foreach (var suffix in new[] { "Plus", "Fork" })
        {
            var derived = new CurseForgeProject
            {
                Id = 99,
                Name = $"FTB Ultimine {suffix}",
                Slug = $"ftb-ultimine-{suffix.ToLowerInvariant()}",
            };
            Null(
                SearchMatcher.PickBestCurseForgeResult([derived], ultimineFile, "ftb ultimine"),
                $"Derived project '{suffix}' was accepted as the base project.");
        }
    }

    private static void ExactSlugsAndConcatenatedNamesRemainSupported()
    {
        var jei = new ModrinthProject
        {
            ProjectId = "jei",
            Title = "Just Enough Items",
            Slug = "jei",
        };
        Same(
            jei,
            SearchMatcher.PickBestModrinthResult(
                [jei],
                "jei-fabric-1.21.1-19.21.0.247.jar",
                "jei"),
            "An exact slug should permit an expanded platform title.");

        var extras = new CurseForgeProject
        {
            Id = 1,
            Name = "Sodium Extras",
            Slug = "sodium-extras",
        };
        Same(
            extras,
            SearchMatcher.PickBestCurseForgeResult(
                [extras],
                "sodiumextras-fabric-1.0.7.jar",
                "sodiumextras"),
            "Concatenated and separated project names should be equivalent.");
    }

    private static void ResourcePackVariantsRemainDistinct()
    {
        var wrong = new ModrinthProject
        {
            ProjectId = "wrong",
            Title = "Faithful 64x",
            Slug = "faithful-64x",
        };
        var expected = new ModrinthProject
        {
            ProjectId = "expected",
            Title = "Faithful 32x",
            Slug = "faithful-32x",
        };
        Null(
            SearchMatcher.PickBestModrinthResult(
                [wrong],
                "Faithful 32x 1.21.zip",
                "faithful 32x"),
            "A different resource-pack resolution was accepted.");
        Same(
            expected,
            SearchMatcher.PickBestModrinthResult(
                [wrong, expected],
                "Faithful 32x 1.21.zip",
                "faithful 32x"),
            "The exact resource-pack variant was not selected.");
    }

    private static void AmbiguousIdentityIsRejected()
    {
        var first = new ModrinthProject
        {
            ProjectId = "one",
            Title = "Example Library",
            Slug = "example-library",
        };
        var second = new ModrinthProject
        {
            ProjectId = "two",
            Title = "Example Library",
            Slug = "example-library",
        };
        Null(
            SearchMatcher.PickBestModrinthResult(
                [first, second],
                "example-library-fabric-1.0.0.jar",
                "example library"),
            "Equally plausible project identities must be rejected as ambiguous.");
    }

    private static void SameEnvironmentPreservesOriginalReference()
    {
        var original = new JsonObject
        {
            ["path"] = "mods/ftb-library.jar",
            ["downloads"] = new JsonArray("https://example.invalid/ftb-library.jar"),
        };
        var item = new ContentItem
        {
            FileName = "ftb-library.jar",
            FileSize = 10,
            DownloadUrl = "https://example.invalid/ftb-library.jar",
            VersionId = "source-version",
            Hashes = new Dictionary<string, string> { ["sha1"] = "abc" },
            OriginalEntry = original,
        };
        var pack = new ModpackInfo { MinecraftVersion = "1.21.1", LoaderType = "fabric" };
        True(SearchMatcher.IsSameContentEnvironment(pack, "1.21.1", "Fabric Loader"), "Loader aliases did not normalize.");
        True(SearchMatcher.TryPreserveOriginalReference(item), "Original entry was not preserved.");
        Equal("preserved", item.Status, "Preserved item has the wrong state.");
        Equal("source-version", item.TargetVersionId, "Original version ID was not retained.");
        Equal("abc", item.TargetHashes["sha1"], "Original hash was not retained.");
    }

    private static void CurseForgeSourceIdentityChecksNameSizeAndHash()
    {
        const string sourceUrl = "https://mediafilez.forgecdn.net/files/7570/483/ftb-ultimine-fabric-2101.1.13.jar";
        var item = new ContentItem
        {
            FileId = "7570483",
            DownloadUrl = sourceUrl,
            FileName = "ftb-ultimine-fabric-2101.1.13.jar",
            FileSize = 176360,
            Hashes = new Dictionary<string, string> { ["sha1"] = "source" },
            OriginalEntry = new JsonObject
            {
                ["downloads"] = new JsonArray(sourceUrl),
            },
        };
        var file = new CurseForgeFile
        {
            Id = 7570483,
            ModId = 448231,
            FileName = "ftb-ultimine-fabric-2101.1.13.jar",
            FileLength = 176360,
            Hashes = [new CurseForgeHash { Algorithm = 1, Value = "source" }],
        };
        True(SearchMatcher.CurseForgeFileMatchesSource(item, file), "Valid strong identity was rejected.");
        file.Hashes[0].Value = "different";
        True(!SearchMatcher.CurseForgeFileMatchesSource(item, file), "Hash mismatch did not reject strong identity.");
    }

    private static async Task ResolverPreservesSameEnvironmentDependenciesAsync()
    {
        var dependency = new DependencyReference
        {
            ProjectId = "required-library",
            Source = "modrinth",
            DependencyType = "required",
        };
        var item = new ContentItem
        {
            Name = "Example",
            FileName = "example.jar",
            DownloadUrl = "https://cdn.modrinth.com/data/example.jar",
            TargetDependencies = [dependency],
            DependencyMetadataAvailable = true,
            OriginalEntry = new JsonObject { ["path"] = "mods/example.jar" },
        };
        var pack = new ModpackInfo
        {
            FormatType = "modrinth",
            MinecraftVersion = "1.21.1",
            LoaderType = "fabric",
            Items = [item],
        };
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> rejectNetwork = (_, _) =>
            throw new InvalidOperationException("Same-environment resolution must not use the network.");
        using var http = new HttpClient(new StubHandler(rejectNetwork));
        using var curseForge = new CurseForgeClient("test-key", http);
        using var modrinth = new ModrinthClient(http);
        var resolver = new ContentTargetResolver(curseForge, modrinth);

        TargetResolutionResult result = await resolver.ResolveAsync(pack, "1.21.1", "fabric");

        Equal(1, result.Preserved, "The original same-environment file was not preserved.");
        Equal(1, item.TargetDependencies.Count, "Same-environment dependency metadata was cleared.");
        Equal("required-library", item.TargetDependencies[0].ProjectId,
            "The preserved dependency identity changed.");
        True(item.DependencyMetadataAvailable, "Dependency metadata availability was cleared.");
    }

    private static void PrimaryModrinthFileIsSelected()
    {
        var secondary = new ModrinthFile
        {
            FileName = "secondary.jar",
            Url = "https://example.invalid/secondary.jar",
            Hashes = new Dictionary<string, string> { ["sha1"] = "secondary" },
            Primary = false,
        };
        var primary = new ModrinthFile
        {
            FileName = "primary.jar",
            Url = "https://example.invalid/primary.jar",
            Hashes = new Dictionary<string, string> { ["sha1"] = "primary" },
            Primary = true,
        };
        Same(primary, SearchMatcher.SelectUsablePrimaryFile([secondary, primary]), "Primary file was not selected.");
    }

    private static async Task ModrinthUsesExactMinecraftAndLoaderAsync()
    {
        var requests = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            requests++;
            return JsonResponse(new[]
            {
                new
                {
                    id = "future",
                    game_versions = new[] { "1.21.11" },
                    loaders = new[] { "fabric" },
                    version_type = "release",
                    date_published = "2026-01-01T00:00:00Z",
                },
                new
                {
                    id = "fabric",
                    game_versions = new[] { "1.21.1" },
                    loaders = new[] { "fabric" },
                    version_type = "release",
                    date_published = "2025-01-01T00:00:00Z",
                },
            });
        }));
        using var api = new ModrinthClient(http);
        Null(
            await api.FindTargetVersionAsync("project", "1.21.1", "forge", true),
            "Loader mismatch returned a version.");
        var exact = await api.FindTargetVersionAsync("project", "1.21.1", "fabric", true);
        Equal("fabric", exact?.Id, "A similar Minecraft patch version replaced the exact version.");
        Equal(2, requests, "Each lookup should request the non-paginated versions endpoint exactly once.");
    }

    private static async Task CurseForgeBulkIdentityLookupBatchesAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            calls++;
            Equal("test-key", request.Headers.GetValues("x-api-key").Single(), "CurseForge key header missing.");
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var ids = payload["fileIds"]!.AsArray().Select(node => node!.GetValue<long>()).ToArray();
            return JsonResponse(new
            {
                data = ids.Select(id => new { id, modId = id + 1000 }).ToArray(),
            });
        }));
        using var api = new CurseForgeClient("test-key", http);
        var result = await api.GetFilesByIdsAsync(Enumerable.Range(1, 51).Select(id => (long)id));
        Equal(2, calls, "CurseForge identity requests were not split into batches of 50.");
        Equal(51, result.Count, "CurseForge bulk identity result was incomplete.");
        Equal(1051L, result[51].ModId, "CurseForge bulk identity result was not indexed by file ID.");
    }

    private static async Task LoaderVersionsUseLatestStableNumericBuildAsync()
    {
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "meta.fabricmc.net")
            {
                return JsonResponse(new[]
                {
                    new { loader = new { version = "0.17.0", stable = false } },
                    new { loader = new { version = "0.16.9", stable = true } },
                    new { loader = new { version = "0.16.10", stable = true } },
                });
            }

            if (uri.Host == "files.minecraftforge.net")
            {
                return JsonResponse(new { promos = new Dictionary<string, string>
                {
                    ["1.21.1-latest"] = "52.1.3",
                    ["1.21.1-recommended"] = "52.1.0",
                } });
            }

            if (uri.Host == "maven.neoforged.net")
            {
                if (uri.AbsolutePath.Contains("/net/neoforged/forge/", StringComparison.Ordinal))
                {
                    return TextResponse("<metadata><versioning><versions>"
                        + "<version>1.20.1-47.1.105</version><version>1.20.1-47.1.106</version>"
                        + "<version>1.20.1-47.2.0</version>"
                        + "</versions></versioning></metadata>", "application/xml");
                }
                return TextResponse("<metadata><versioning><versions>"
                    + "<version>21.1.42</version><version>21.1.100-beta</version>"
                    + "<version>21.1.99</version><version>21.2.1</version>"
                    + "</versions></versioning></metadata>", "application/xml");
            }

            if (uri.Host == "meta.quiltmc.org")
            {
                return JsonResponse(new[]
                {
                    new { loader = new { version = "0.27.0-beta.1" } },
                    new { loader = new { version = "0.26.3" } },
                    new { loader = new { version = "0.25.0" } },
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var service = new LoaderVersionService(http);
        Equal("0.16.10", await service.FetchLatestAsync("fabric", "1.21.1"), "Fabric stable ordering failed.");
        Equal("52.1.3", await service.FetchLatestAsync("forge", "1.21.1"), "Forge latest promotion failed.");
        Equal("21.1.99", await service.FetchLatestAsync("neoforge", "1.21.1"), "NeoForge prefix mapping failed.");
        Equal("47.1.106", await service.FetchLatestAsync("neoforge", "1.20.1"), "Legacy NeoForge metadata mapping failed.");
        Equal("0.26.3", await service.FetchLatestAsync("quilt", "1.21.1"), "Quilt prerelease filtering failed.");
    }

    private static async Task ResolverVerifiesTargetAvailabilityBeforeSelectingIdentityAsync()
    {
        var versionRequests = new List<string>();
        using var modrinthHttp = new HttpClient(new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/search")
            {
                return JsonResponse(new
                {
                    hits = new[]
                    {
                        new { project_id = "unavailable", title = "Example Library", slug = "example-library" },
                        new { project_id = "compatible", title = "Example Library", slug = "example-library" },
                    },
                });
            }

            if (path.EndsWith("/version", StringComparison.Ordinal))
            {
                var projectId = path.Split('/')[3];
                versionRequests.Add(projectId);
                return projectId == "compatible"
                    ? JsonResponse(new[]
                    {
                        new
                        {
                            id = "compatible-version",
                            project_id = "compatible",
                            game_versions = new[] { "1.21.2" },
                            loaders = new[] { "fabric" },
                            version_type = "release",
                            date_published = "2026-01-01T00:00:00Z",
                            files = new[]
                            {
                                new
                                {
                                    filename = "example-library-2.jar",
                                    url = "https://example.invalid/example-library.jar",
                                    size = 42,
                                    primary = true,
                                    hashes = new Dictionary<string, string> { ["sha1"] = "target" },
                                },
                            },
                            dependencies = Array.Empty<object>(),
                        },
                    })
                    : JsonResponse(Array.Empty<object>());
            }

            if (path == "/v2/project/compatible")
            {
                return JsonResponse(new { id = "compatible", title = "Example Library", slug = "example-library" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var curseForgeHttp = new HttpClient(new StubHandler(
            (Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) =>
                throw new InvalidOperationException("CurseForge fallback should not run for a resolved item."))));
        using var modrinth = new ModrinthClient(modrinthHttp);
        using var curseForge = new CurseForgeClient("test-key", curseForgeHttp);
        var resolver = new ContentTargetResolver(curseForge, modrinth);
        var item = new ContentItem
        {
            Name = "Example Library",
            FileName = "example-library-fabric-1.0.0.jar",
            Source = "unknown",
            Category = "mod",
        };
        var pack = new ModpackInfo
        {
            FormatType = "modrinth",
            MinecraftVersion = "1.21.1",
            LoaderType = "fabric",
            Items = [item],
        };

        var result = await resolver.ResolveAsync(pack, "1.21.2", "fabric");
        Equal("compatible", item.ProjectId, "Resolver selected an identity without a target version.");
        Equal("compatible-version", item.TargetVersionId, "Resolver did not apply the compatible target.");
        Equal(1, result.Found, "Resolved item count is wrong.");
        True(versionRequests.Contains("unavailable") && versionRequests.Contains("compatible"),
            "Resolver did not verify both ranked candidates against the target environment.");
    }

    private static async Task MissingCurseForgeKeySkipsOptionalFallbackAsync()
    {
        var curseForgeRequests = 0;
        using var modrinthHttp = new HttpClient(new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v2/search")
                return JsonResponse(new { hits = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var curseForgeHttp = new HttpClient(new StubHandler((_, _) =>
        {
            curseForgeRequests++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));
        using var modrinth = new ModrinthClient(modrinthHttp);
        using var curseForge = new CurseForgeClient(string.Empty, curseForgeHttp);
        curseForge.SetApiKey(string.Empty);
        var resolver = new ContentTargetResolver(curseForge, modrinth);
        var item = new ContentItem
        {
            Name = "Unknown Mod",
            FileName = "unknown-mod-fabric-1.0.0.jar",
            Source = "unknown",
            Category = "mod",
        };
        var pack = new ModpackInfo
        {
            FormatType = "modrinth",
            MinecraftVersion = "1.21.1",
            LoaderType = "fabric",
            Items = [item],
        };

        TargetResolutionResult result = await resolver.ResolveAsync(pack, "1.21.2", "fabric");

        Equal(1, result.Missing, "An unresolved item was not retained as missing.");
        Equal("not_found", item.Status, "The missing item received an unexpected status.");
        True(item.Note.Contains("CurseForge API Key", StringComparison.Ordinal),
            "The skipped fallback reason was not recorded.");
        Equal(0, curseForgeRequests, "Optional CurseForge fallback made a request without an API key.");
    }

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        TextResponse(JsonSerializer.Serialize(value), "application/json", status);

    private static HttpResponseMessage TextResponse(
        string text,
        string contentType,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(text, Encoding.UTF8, contentType),
        };

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Null(object? value, string message)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Same(object expected, object? actual, string message)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }
}
