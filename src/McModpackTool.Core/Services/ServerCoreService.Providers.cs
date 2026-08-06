using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed partial class ServerCoreService
{
    private const string MojangManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string FabricMetaBase = "https://meta.fabricmc.net/v2/versions";
    private const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private const string LegacyNeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/forge";
    private const string MohistApiBase = "https://mohistmc.com/api/v2/projects/mohist";
    private const string CatServerReleasesUrl = "https://api.github.com/repos/Luohuayu/CatServer/releases?per_page=100";
    private const string ModrinthApiBase = "https://api.modrinth.com/v2";

    // CatServer's GitHub releases do not expose the embedded Forge build as structured metadata.
    // Keep this deliberately small: an option is safe only when the published pairing is known exactly.
    private static readonly IReadOnlyDictionary<string, string> CatServerForgeVersions
        = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.16.5"] = "36.2.39",
            ["1.18.2"] = "40.2.4",
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<ServerCoreOption?> QueryVanillaAsync(
        ServerCoreQuery query,
        CancellationToken cancellationToken)
    {
        using JsonDocument manifest = await GetJsonAsync(MojangManifestUrl, cancellationToken).ConfigureAwait(false);
        if (!manifest.RootElement.TryGetProperty("versions", out JsonElement versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement selected = default;
        foreach (JsonElement version in versions.EnumerateArray())
        {
            if (StringProperty(version, "id") == query.MinecraftVersion)
            {
                selected = version;
                break;
            }
        }
        string metadataUrl = StringProperty(selected, "url");
        if (!IsTrustedHttps(metadataUrl, "piston-meta.mojang.com", "launchermeta.mojang.com"))
        {
            return null;
        }

        using JsonDocument metadata = await GetJsonAsync(metadataUrl, cancellationToken).ConfigureAwait(false);
        if (!metadata.RootElement.TryGetProperty("downloads", out JsonElement downloads)
            || !downloads.TryGetProperty("server", out JsonElement server))
        {
            return null;
        }
        string url = StringProperty(server, "url");
        string sha1 = StringProperty(server, "sha1");
        long size = Int64Property(server, "size");
        if (!IsTrustedHttps(url, "piston-data.mojang.com", "launcher.mojang.com")
            || !IsHexHash(sha1, 40)
            || size <= 0)
        {
            return null;
        }

        return DirectOption(
            ServerCoreIds.Vanilla,
            "Vanilla Server",
            query.MinecraftVersion,
            query.MinecraftVersion,
            query.LoaderType,
            string.Empty,
            [ServerJar(url, size, new Dictionary<string, string> { ["sha1"] = sha1 })]);
    }

    private async Task<ServerCoreOption?> QueryFabricAsync(
        ServerCoreQuery query,
        CancellationToken cancellationToken)
    {
        Task<JsonDocument> loadersTask = GetJsonAsync(
            $"{FabricMetaBase}/loader/{Uri.EscapeDataString(query.MinecraftVersion)}",
            cancellationToken);
        Task<JsonDocument> installersTask = GetJsonAsync($"{FabricMetaBase}/installer", cancellationToken);
        using JsonDocument loaders = await loadersTask.ConfigureAwait(false);
        using JsonDocument installers = await installersTask.ConfigureAwait(false);
        if (loaders.RootElement.ValueKind != JsonValueKind.Array
            || installers.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var loaderVersions = new List<(string Version, bool Stable)>();
        foreach (JsonElement entry in loaders.RootElement.EnumerateArray())
        {
            JsonElement loader = entry.TryGetProperty("loader", out JsonElement nested) ? nested : entry;
            string version = StringProperty(loader, "version");
            if (version.Length > 0)
            {
                loaderVersions.Add((version, BooleanProperty(loader, "stable")));
            }
        }
        string loaderVersion = query.LoaderVersion;
        if (loaderVersion.Length > 0)
        {
            if (!loaderVersions.Any(item => item.Version.Equals(loaderVersion, StringComparison.Ordinal)))
            {
                return null;
            }
        }
        else
        {
            loaderVersion = LoaderVersionService.LatestNumericVersion(
                loaderVersions.Where(item => item.Stable).Select(item => item.Version));
        }

        string installerVersion = LoaderVersionService.LatestNumericVersion(
            installers.RootElement.EnumerateArray()
                .Where(entry => BooleanProperty(entry, "stable"))
                .Select(entry => StringProperty(entry, "version")));
        if (loaderVersion.Length == 0 || installerVersion.Length == 0)
        {
            return null;
        }

        string url = $"https://meta.fabricmc.net/v2/versions/loader/"
            + $"{Uri.EscapeDataString(query.MinecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/"
            + $"{Uri.EscapeDataString(installerVersion)}/server/jar";
        return DirectOption(
            ServerCoreIds.Fabric,
            "Fabric Server",
            loaderVersion,
            query.MinecraftVersion,
            "fabric",
            loaderVersion,
            [ServerJar(url)]);
    }

    private async Task<ServerCoreOption?> QueryCardboardAsync(
        ServerCoreQuery query,
        ServerCoreOption? fabric,
        CancellationToken cancellationToken)
    {
        if (fabric is null)
        {
            return null;
        }
        string gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { query.MinecraftVersion }));
        string loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { "fabric" }));
        using JsonDocument document = await GetJsonAsync(
            $"{ModrinthApiBase}/project/{CardboardProjectId}/version?game_versions={gameVersions}&loaders={loaders}",
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var versions = JsonSerializer.Deserialize<List<ModrinthVersion>>(
            document.RootElement.GetRawText(),
            JsonOptions) ?? [];
        ModrinthVersion? selected = ModrinthClient.PickBestVersion(versions.Where(version =>
            version.ProjectId.Equals(CardboardProjectId, StringComparison.Ordinal)
            && version.GameVersions.Contains(query.MinecraftVersion, StringComparer.Ordinal)
            && version.Loaders.Contains("fabric", StringComparer.OrdinalIgnoreCase)));
        ServerCoreArtifact? cardboard = CreateModrinthModArtifact(selected);
        if (selected?.Dependencies is null || cardboard is null)
        {
            return null;
        }

        var artifacts = fabric.Artifacts.ToList();
        var artifactUrlsByPath = artifacts.ToDictionary(
            artifact => artifact.RelativePath,
            artifact => artifact.DownloadUrl,
            StringComparer.OrdinalIgnoreCase);
        artifactUrlsByPath[cardboard.RelativePath] = cardboard.DownloadUrl;
        artifacts.Add(cardboard);

        foreach (ModrinthDependency dependency in selected.Dependencies)
        {
            if (!string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ModrinthVersion? dependencyVersion = await ResolveRequiredModrinthDependencyAsync(
                    dependency,
                    query.MinecraftVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            ServerCoreArtifact? dependencyArtifact = CreateModrinthModArtifact(dependencyVersion);
            if (dependencyArtifact is null)
            {
                return null;
            }

            if (artifactUrlsByPath.TryGetValue(dependencyArtifact.RelativePath, out string? existingUrl))
            {
                if (!existingUrl.Equals(dependencyArtifact.DownloadUrl, StringComparison.Ordinal))
                {
                    return null;
                }
                continue;
            }
            artifactUrlsByPath.Add(dependencyArtifact.RelativePath, dependencyArtifact.DownloadUrl);
            artifacts.Add(dependencyArtifact);
        }

        return DirectOption(
            ServerCoreIds.Cardboard,
            "Cardboard (Fabric + Bukkit plugins)",
            selected.VersionNumber.Length > 0 ? selected.VersionNumber : selected.Id,
            query.MinecraftVersion,
            "fabric",
            fabric.LoaderVersion,
            artifacts);
    }

    private async Task<ModrinthVersion?> ResolveRequiredModrinthDependencyAsync(
        ModrinthDependency dependency,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        string versionId = dependency.VersionId?.Trim() ?? string.Empty;
        string projectId = dependency.ProjectId?.Trim() ?? string.Empty;
        ModrinthVersion? selected;
        if (versionId.Length > 0)
        {
            using JsonDocument document = await GetJsonAsync(
                    $"{ModrinthApiBase}/version/{Uri.EscapeDataString(versionId)}",
                    cancellationToken)
                .ConfigureAwait(false);
            selected = JsonSerializer.Deserialize<ModrinthVersion>(document.RootElement.GetRawText(), JsonOptions);
            if (selected is null
                || !selected.Id.Equals(versionId, StringComparison.Ordinal)
                || (projectId.Length > 0 && !selected.ProjectId.Equals(projectId, StringComparison.Ordinal)))
            {
                return null;
            }
        }
        else if (projectId.Length > 0)
        {
            string gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion }));
            string loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { "fabric" }));
            using JsonDocument document = await GetJsonAsync(
                    $"{ModrinthApiBase}/project/{Uri.EscapeDataString(projectId)}/version"
                    + $"?game_versions={gameVersions}&loaders={loaders}",
                    cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var versions = JsonSerializer.Deserialize<List<ModrinthVersion>>(
                document.RootElement.GetRawText(),
                JsonOptions) ?? [];
            selected = ModrinthClient.PickBestVersion(versions.Where(version =>
                version.ProjectId.Equals(projectId, StringComparison.Ordinal)
                && ModrinthVersionMatchesFabric(version, minecraftVersion)));
        }
        else
        {
            return null;
        }

        return selected is not null && ModrinthVersionMatchesFabric(selected, minecraftVersion)
            ? selected
            : null;
    }

    private static bool ModrinthVersionMatchesFabric(ModrinthVersion version, string minecraftVersion) =>
        version.GameVersions.Contains(minecraftVersion, StringComparer.Ordinal)
        && version.Loaders.Contains("fabric", StringComparer.OrdinalIgnoreCase);

    private static ServerCoreArtifact? CreateModrinthModArtifact(ModrinthVersion? version)
    {
        if (version is null)
        {
            return null;
        }
        List<ModrinthFile> files = version.Files ?? [];
        ModrinthFile? file = files.Count(file => file.Primary is true) switch
        {
            1 => files.Single(file => file.Primary is true),
            0 when files.Count == 1 => files[0],
            _ => null,
        };
        if (file is null
            || file.Size <= 0
            || !file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || !IsTrustedHttps(file.Url, "cdn.modrinth.com")
            || !IsSafeLocalName(file.FileName))
        {
            return null;
        }

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in file.Hashes ?? [])
        {
            string normalizedName = name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            int expectedLength = normalizedName switch
            {
                "sha1" => 40,
                "sha256" => 64,
                "sha512" => 128,
                _ => 0,
            };
            if (expectedLength == 0)
            {
                continue;
            }
            string normalizedValue = value.Trim().ToLowerInvariant();
            if (!IsHexHash(normalizedValue, expectedLength))
            {
                return null;
            }
            hashes[normalizedName] = normalizedValue;
        }
        if (hashes.Count == 0)
        {
            return null;
        }

        return new ServerCoreArtifact
        {
            Role = ServerCoreArtifactRole.Mod,
            DownloadUrl = file.Url,
            RelativePath = $"mods/{file.FileName}",
            Size = file.Size,
            Hashes = hashes,
        };
    }

    private async Task<ServerCoreOption?> QueryForgeAsync(
        ServerCoreQuery query,
        CancellationToken cancellationToken)
    {
        if (query.LoaderVersion.Length == 0)
        {
            return null;
        }
        string fullVersion = query.LoaderVersion.StartsWith(query.MinecraftVersion + "-", StringComparison.Ordinal)
            ? query.LoaderVersion
            : $"{query.MinecraftVersion}-{query.LoaderVersion}";
        string metadata = await GetTextAsync($"{ForgeMavenBase}/maven-metadata.xml", cancellationToken)
            .ConfigureAwait(false);
        if (!ReadMavenVersions(metadata).Contains(fullVersion, StringComparer.Ordinal))
        {
            return null;
        }
        string url = $"{ForgeMavenBase}/{fullVersion}/forge-{fullVersion}-installer.jar";
        string sha1 = await ReadRequiredSha1Async(url, cancellationToken).ConfigureAwait(false);
        return JavaInstallerOption(
            ServerCoreIds.Forge,
            "Forge Server",
            query.LoaderVersion,
            query.MinecraftVersion,
            "forge",
            query.LoaderVersion,
            url,
            $".installers/forge-{fullVersion}-installer.jar",
            sha1);
    }

    private async Task<ServerCoreOption?> QueryNeoForgeAsync(
        ServerCoreQuery query,
        CancellationToken cancellationToken)
    {
        if (query.LoaderVersion.Length == 0
            || !NeoForgeVersionMatchesMinecraft(query.MinecraftVersion, query.LoaderVersion))
        {
            return null;
        }
        bool legacy1201 = query.MinecraftVersion == "1.20.1";
        string mavenBase = legacy1201 ? LegacyNeoForgeMavenBase : NeoForgeMavenBase;
        string publishedVersion = legacy1201
            ? query.LoaderVersion.StartsWith("1.20.1-", StringComparison.Ordinal)
                ? query.LoaderVersion
                : $"1.20.1-{query.LoaderVersion}"
            : query.LoaderVersion;
        string artifactName = legacy1201 ? "forge" : "neoforge";
        string metadata = await GetTextAsync($"{mavenBase}/maven-metadata.xml", cancellationToken)
            .ConfigureAwait(false);
        if (!ReadMavenVersions(metadata).Contains(publishedVersion, StringComparer.Ordinal))
        {
            return null;
        }
        string url = $"{mavenBase}/{publishedVersion}/{artifactName}-{publishedVersion}-installer.jar";
        string sha1 = await ReadRequiredSha1Async(url, cancellationToken).ConfigureAwait(false);
        return JavaInstallerOption(
            ServerCoreIds.NeoForge,
            "NeoForge Server",
            query.LoaderVersion,
            query.MinecraftVersion,
            "neoforge",
            query.LoaderVersion,
            url,
            $".installers/neoforge-{query.LoaderVersion}-installer.jar",
            sha1);
    }

    private async Task<ServerCoreOption?> QueryMohistAsync(
        ServerCoreQuery query,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetJsonAsync(
            $"{MohistApiBase}/{Uri.EscapeDataString(query.MinecraftVersion)}/builds",
            cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (!StringProperty(root, "projectVersion").Equals(query.MinecraftVersion, StringComparison.Ordinal)
            || !root.TryGetProperty("builds", out JsonElement builds)
            || builds.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = builds.EnumerateArray()
            .Where(build => build.ValueKind == JsonValueKind.Object)
            .Select(build => new MohistBuild(
                FirstNonEmpty(
                    StringProperty(build, "id"),
                    NumberTextProperty(build, "number"),
                    StringProperty(build, "gitSha")),
                StringProperty(build, "forgeVersion"),
                StringProperty(build, "url"),
                StringProperty(build, "fileMd5"),
                StringProperty(build, "fileSha256"),
                Int64Property(build, "createdAt")))
            .Where(build => build.Id.Length > 0
                && build.ForgeVersion.Length > 0
                && IsTrustedHttps(build.Url, "mohistmc.com"))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        if (query.LoaderVersion.Length > 0)
        {
            candidates = candidates
                .Where(build => build.ForgeVersion.Equals(query.LoaderVersion, StringComparison.Ordinal))
                .ToList();
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        MohistBuild selected = candidates
            .OrderByDescending(build => build.CreatedAt)
            .ThenByDescending(build => build.Id, StringComparer.Ordinal)
            .First();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (IsHexHash(selected.Sha256, 64)) hashes["sha256"] = selected.Sha256;
        if (IsHexHash(selected.Md5, 32)) hashes["md5"] = selected.Md5;
        if (hashes.Count == 0)
        {
            return null;
        }
        return DirectOption(
            ServerCoreIds.Mohist,
            "Mohist",
            selected.Id,
            query.MinecraftVersion,
            "forge",
            selected.ForgeVersion,
            [ServerJar(selected.Url, hashes: hashes)]);
    }

    private async Task<ServerCoreOption?> QueryCatServerAsync(
        ServerCoreQuery query,
        CancellationToken cancellationToken)
    {
        if (!CatServerForgeVersions.TryGetValue(query.MinecraftVersion, out string? embeddedForgeVersion)
            || !query.LoaderVersion.Equals(embeddedForgeVersion, StringComparison.Ordinal))
        {
            return null;
        }

        using JsonDocument document = await GetJsonAsync(CatServerReleasesUrl, cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = new List<CatServerRelease>();
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            if (BooleanProperty(release, "draft")
                || !release.TryGetProperty("assets", out JsonElement assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            string releaseText = StringProperty(release, "tag_name") + " " + StringProperty(release, "name");
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string fileName = StringProperty(asset, "name");
                string combined = releaseText + " " + fileName;
                if (!ContainsExactVersion(combined, query.MinecraftVersion)
                    || !IsUsableCatServerJar(fileName))
                {
                    continue;
                }
                string url = StringProperty(asset, "browser_download_url");
                if (!IsTrustedHttps(url, "github.com"))
                {
                    continue;
                }
                string digest = StringProperty(asset, "digest");
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    && IsHexHash(digest[7..], 64))
                {
                    hashes["sha256"] = digest[7..];
                }
                candidates.Add(new CatServerRelease(
                    StringProperty(release, "tag_name"),
                    url,
                    Int64Property(asset, "size"),
                    hashes,
                    BooleanProperty(release, "prerelease"),
                    ParseDate(StringProperty(release, "published_at"))));
            }
        }
        CatServerRelease? selected = candidates
            .OrderBy(candidate => candidate.Prerelease)
            .ThenByDescending(candidate => candidate.PublishedAt)
            .FirstOrDefault();
        if (selected is null || selected.Size <= 0)
        {
            return null;
        }
        return DirectOption(
            ServerCoreIds.CatServer,
            "CatServer",
            selected.Version,
            query.MinecraftVersion,
            "forge",
            embeddedForgeVersion,
            [ServerJar(selected.Url, selected.Size, selected.Hashes)]);
    }

    private static ServerCoreOption DirectOption(
        string id,
        string name,
        string coreVersion,
        string minecraftVersion,
        string loaderType,
        string loaderVersion,
        IReadOnlyList<ServerCoreArtifact> artifacts) => new()
    {
        Id = id,
        Name = name,
        CoreVersion = coreVersion,
        MinecraftVersion = minecraftVersion,
        LoaderType = loaderType,
        LoaderVersion = loaderVersion,
        InstallStrategy = ServerCoreInstallStrategy.DirectFiles,
        Artifacts = artifacts,
    };

    private static ServerCoreOption JavaInstallerOption(
        string id,
        string name,
        string coreVersion,
        string minecraftVersion,
        string loaderType,
        string loaderVersion,
        string url,
        string relativePath,
        string sha1) => new()
    {
        Id = id,
        Name = name,
        CoreVersion = coreVersion,
        MinecraftVersion = minecraftVersion,
        LoaderType = loaderType,
        LoaderVersion = loaderVersion,
        InstallStrategy = ServerCoreInstallStrategy.JavaInstaller,
        Artifacts =
        [
            new ServerCoreArtifact
            {
                Role = ServerCoreArtifactRole.Installer,
                DownloadUrl = url,
                RelativePath = relativePath,
                Hashes = new Dictionary<string, string> { ["sha1"] = sha1 },
                DeleteAfterInstall = true,
            },
        ],
        JavaInstaller = new ServerCoreJavaInstaller { ArtifactRelativePath = relativePath },
    };

    private static ServerCoreArtifact ServerJar(
        string url,
        long size = 0,
        IReadOnlyDictionary<string, string>? hashes = null) => new()
    {
        Role = ServerCoreArtifactRole.ServerJar,
        DownloadUrl = url,
        RelativePath = "server.jar",
        Size = size,
        Hashes = hashes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    private async Task<string> ReadRequiredSha1Async(string artifactUrl, CancellationToken cancellationToken)
    {
        string text = await GetTextAsync(artifactUrl + ".sha1", cancellationToken).ConfigureAwait(false);
        string hash = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? string.Empty;
        if (!IsHexHash(hash, 40))
        {
            throw new InvalidDataException("The official Maven repository did not publish a valid SHA-1 checksum.");
        }
        return hash.ToLowerInvariant();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        byte[] bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        byte[] bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "FengchenWD/MCPackMigrator/1.0.0-beta.1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long limit = ArchiveSafetyOptions.Default.MaxMetadataBytes;
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength.Value > limit)
        {
            throw new InvalidDataException("Server core metadata exceeds the safety limit.");
        }
        await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > limit)
                throw new InvalidDataException("Server core metadata exceeds the safety limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static IReadOnlyList<string> ReadMavenVersions(string xml)
    {
        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "version")
            .Select(element => element.Value.Trim())
            .Where(version => version.Length > 0)
            .ToArray();
    }

    private static bool NeoForgeVersionMatchesMinecraft(string minecraft, string loaderVersion)
    {
        if (minecraft == "1.20.1")
        {
            return loaderVersion.StartsWith("47.1.", StringComparison.Ordinal)
                || loaderVersion.StartsWith("1.20.1-47.1.", StringComparison.Ordinal);
        }
        string[] parts = minecraft.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out int major)
            || !int.TryParse(parts[1], out int minor))
        {
            return false;
        }
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int parsedPatch) ? parsedPatch : 0;
        string prefix = major == 1 ? $"{minor}.{patch}." : $"{major}.{minor}.";
        return loaderVersion.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool BooleanProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    private static long Int64Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.TryGetInt64(out long result)
            ? result
            : 0;

    private static string NumberTextProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool IsHexHash(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);

    private static bool IsTrustedHttps(string value, params string[] hosts) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && hosts.Contains(uri.IdnHost.TrimEnd('.'), StringComparer.OrdinalIgnoreCase);

    private static bool IsSafeLocalName(string value)
    {
        try
        {
            ArchiveSafety.ValidateLocalName(value);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool ContainsExactVersion(string value, string version)
    {
        int index = value.IndexOf(version, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            bool left = index == 0 || (value[index - 1] != '.' && !char.IsDigit(value[index - 1]));
            int end = index + version.Length;
            bool right = end == value.Length || (value[end] != '.' && !char.IsDigit(value[end]));
            if (left && right)
            {
                return true;
            }
            index = value.IndexOf(version, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool IsUsableCatServerJar(string fileName) =>
        IsSafeLocalName(fileName)
        && fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        && !fileName.Contains("source", StringComparison.OrdinalIgnoreCase)
        && !fileName.Contains("javadoc", StringComparison.OrdinalIgnoreCase)
        && !fileName.Contains("installer", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private sealed record MohistBuild(
        string Id,
        string ForgeVersion,
        string Url,
        string Md5,
        string Sha256,
        long CreatedAt);

    private sealed record CatServerRelease(
        string Version,
        string Url,
        long Size,
        IReadOnlyDictionary<string, string> Hashes,
        bool Prerelease,
        DateTimeOffset PublishedAt);
}
