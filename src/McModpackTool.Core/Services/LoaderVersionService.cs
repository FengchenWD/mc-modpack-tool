using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace McModpackTool.Core.Services;

public sealed partial class LoaderVersionService : IDisposable
{
    private static readonly HashSet<string> SupportedLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt",
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly Action<string>? _logWarning;

    public LoaderVersionService(
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        Action<string>? logWarning = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _logWarning = logWarning;
    }

    /// <summary>
    /// Gets the newest stable numeric loader version compatible with an exact Minecraft version.
    /// Network and platform failures return an empty string; caller cancellation is propagated.
    /// </summary>
    public async Task<string> FetchLatestAsync(
        string loaderType,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var loader = (loaderType ?? string.Empty).Trim().ToLowerInvariant();
        var gameVersion = (minecraftVersion ?? string.Empty).Trim();
        if (!SupportedLoaders.Contains(loader))
        {
            _logWarning?.Invoke($"不支持的加载器: {loader}");
            return string.Empty;
        }

        if (!MinecraftVersionRegex().IsMatch(gameVersion))
        {
            _logWarning?.Invoke($"无效的 Minecraft 版本: {gameVersion}");
            return string.Empty;
        }

        try
        {
            return loader switch
            {
                "fabric" => await FetchFabricAsync(gameVersion, cancellationToken).ConfigureAwait(false),
                "forge" => await FetchForgeAsync(gameVersion, cancellationToken).ConfigureAwait(false),
                "neoforge" => await FetchNeoForgeAsync(gameVersion, cancellationToken).ConfigureAwait(false),
                "quilt" => await FetchQuiltAsync(gameVersion, cancellationToken).ConfigureAwait(false),
                _ => string.Empty,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logWarning?.Invoke($"获取加载器版本失败 ({loader}): {exception.Message}");
            return string.Empty;
        }
    }

    public static string LatestNumericVersion(IEnumerable<string?> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return versions
            .Where(version => !string.IsNullOrWhiteSpace(version) && NumericVersionRegex().IsMatch(version))
            .Select(version => version!)
            .OrderByDescending(version => version, NumericVersionComparer.Instance)
            .FirstOrDefault() ?? string.Empty;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<string> FetchFabricAsync(
        string gameVersion,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}",
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var versions = new List<string>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var loader = entry.TryGetProperty("loader", out var nested) ? nested : entry;
            if (loader.ValueKind == JsonValueKind.Object
                && loader.TryGetProperty("stable", out var stable)
                && stable.ValueKind == JsonValueKind.True
                && loader.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                versions.Add(version.GetString() ?? string.Empty);
            }
        }

        return LatestNumericVersion(versions);
    }

    private async Task<string> FetchForgeAsync(
        string gameVersion,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",
            cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("promos", out var promotions)
            || promotions.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var suffix in new[] { "latest", "recommended" })
        {
            if (promotions.TryGetProperty($"{gameVersion}-{suffix}", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                return version.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private async Task<string> FetchNeoForgeAsync(
        string gameVersion,
        CancellationToken cancellationToken)
    {
        bool legacy1201 = gameVersion == "1.20.1";
        string metadataUrl = legacy1201
            ? "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml"
            : "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
        var xml = await GetTextAsync(
            metadataUrl,
            cancellationToken).ConfigureAwait(false);
        var parts = gameVersion.Split('.').Select(part => int.Parse(
            part,
            NumberStyles.None,
            CultureInfo.InvariantCulture)).ToArray();
        if (parts.Length < 2 || parts[0] != 1)
        {
            return string.Empty;
        }

        var document = XDocument.Parse(xml, LoadOptions.None);
        IEnumerable<string> versions = document
            .Descendants()
            .Where(element => element.Name.LocalName == "version")
            .Select(element => element.Value.Trim());
        if (legacy1201)
        {
            const string legacyPrefix = "1.20.1-";
            versions = versions
                .Where(version => version.StartsWith(legacyPrefix + "47.1.", StringComparison.Ordinal))
                .Select(version => version[legacyPrefix.Length..]);
        }
        else
        {
            string prefix = $"{parts[1].ToString(CultureInfo.InvariantCulture)}."
                + $"{(parts.Length > 2 ? parts[2] : 0).ToString(CultureInfo.InvariantCulture)}.";
            versions = versions.Where(version => NeoForgeNumericVersionRegex(prefix).IsMatch(version));
        }
        return LatestNumericVersion(versions);
    }

    private async Task<string> FetchQuiltAsync(
        string gameVersion,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(gameVersion)}",
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var versions = new List<string>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var loader = entry.TryGetProperty("loader", out var nested) ? nested : entry;
            if (loader.ValueKind == JsonValueKind.Object
                && loader.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                versions.Add(version.GetString() ?? string.Empty);
            }
        }

        return LatestNumericVersion(versions);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "FengchenWD/MCPackMigrator/1.0.0-beta.1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
    }

    private static Regex NeoForgeNumericVersionRegex(string prefix) =>
        new($"^{Regex.Escape(prefix)}\\d+$", RegexOptions.CultureInvariant);

    private sealed class NumericVersionComparer : IComparer<string>
    {
        public static NumericVersionComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var leftParts = left.Split('.').Select(ParsePart).ToArray();
            var rightParts = right.Split('.').Select(ParsePart).ToArray();
            for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
            {
                var leftPart = index < leftParts.Length ? leftParts[index] : 0;
                var rightPart = index < rightParts.Length ? rightParts[index] : 0;
                var comparison = leftPart.CompareTo(rightPart);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return string.Compare(left, right, StringComparison.Ordinal);
        }

        private static int ParsePart(string value) => int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : 0;
    }

    [GeneratedRegex(@"^\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex MinecraftVersionRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericVersionRegex();
}
