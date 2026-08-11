using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed class ModrinthClient : IDisposable
{
    public const string BaseAddress = "https://api.modrinth.com/v2";
    public const int DefaultSearchLimit = 30;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly string _userAgent;

    public ModrinthClient(
        HttpClient? httpClient = null,
        string userAgent = "FengchenWD/MCPackMigrator/1.0.0-beta.1",
        TimeSpan? requestTimeout = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _userAgent = userAgent;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    public Task<ModrinthProject> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        GetRequiredAsync<ModrinthProject>(
            $"/project/{Uri.EscapeDataString(projectId)}",
            null,
            "Modrinth 项目接口返回了意外的数据格式。",
            cancellationToken);

    /// <summary>
    /// Modrinth returns the complete project version list from this endpoint; it is not paginated.
    /// </summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetAllVersionsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetAsync<List<ModrinthVersion>>(
            $"/project/{Uri.EscapeDataString(projectId)}/version",
            null,
            cancellationToken).ConfigureAwait(false);
        return versions ?? [];
    }

    public async Task<ModrinthVersion?> FindTargetVersionAsync(
        string projectId,
        string targetMinecraft,
        string? targetLoader,
        bool strictMinecraft = true,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetAllVersionsAsync(projectId, cancellationToken).ConfigureAwait(false);
        foreach (var gameVersion in SearchMatcher.GenerateGameVersionCandidates(
            targetMinecraft,
            strictMinecraft))
        {
            var matching = versions
                .Where(version => version.GameVersions.Contains(gameVersion, StringComparer.Ordinal))
                .Where(version => string.IsNullOrWhiteSpace(targetLoader)
                    || version.Loaders.Contains(targetLoader, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count > 0)
            {
                return PickBestVersion(matching);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ModrinthProject>> SearchProjectsAsync(
        string query,
        string? loader = null,
        string projectType = "mod",
        int limit = DefaultSearchLimit,
        CancellationToken cancellationToken = default)
    {
        var facets = new List<List<string>>();
        if (!string.IsNullOrWhiteSpace(loader))
        {
            facets.Add([$"categories:{loader}"]);
        }

        facets.Add([$"project_type:{projectType}"]);
        var parameters = new Dictionary<string, string>
        {
            ["query"] = query ?? string.Empty,
            ["limit"] = Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture),
            ["facets"] = JsonSerializer.Serialize(facets, JsonOptions),
        };
        var response = await GetAsync<SearchResponse>(
            "/search",
            parameters,
            cancellationToken).ConfigureAwait(false);
        return response?.Hits ?? [];
    }

    public async Task<ModrinthVersion?> LookupByHashAsync(
        string hash,
        string algorithm = "sha1",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        try
        {
            var version = await GetAsync<ModrinthVersion>(
                $"/version_file/{Uri.EscapeDataString(hash)}",
                new Dictionary<string, string> { ["algorithm"] = algorithm },
                cancellationToken).ConfigureAwait(false);
            return version is { ProjectId.Length: > 0 } ? version : null;
        }
        catch (PlatformNotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> LookupByHashesAsync(
        IEnumerable<string> hashes,
        string algorithm = "sha1",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        var normalized = hashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in normalized.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versions = await PostAsync<Dictionary<string, ModrinthVersion>>(
                "/version_files",
                new VersionFilesRequest { Hashes = batch, Algorithm = algorithm },
                cancellationToken).ConfigureAwait(false) ?? [];
            foreach (var (hash, version) in versions)
            {
                result[hash] = version;
            }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, ModrinthProject>> GetProjectsByIdsAsync(
        IEnumerable<string> projectIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        var normalized = projectIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new Dictionary<string, ModrinthProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in normalized.Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projects = await GetAsync<List<ModrinthProject>>(
                "/projects",
                new Dictionary<string, string>
                {
                    ["ids"] = JsonSerializer.Serialize(batch, JsonOptions),
                },
                cancellationToken).ConfigureAwait(false) ?? [];
            foreach (var project in projects)
            {
                if (project.EffectiveId.Length > 0)
                {
                    result[project.EffectiveId] = project;
                }
            }
        }
        return result;
    }

    public static ModrinthVersion? PickBestVersion(IEnumerable<ModrinthVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return versions
            .OrderBy(version => version.VersionType.ToLowerInvariant() switch
            {
                "release" => 1,
                "beta" => 2,
                "alpha" => 3,
                _ => 4,
            })
            .ThenByDescending(version => ParseDate(version.DatePublished))
            .FirstOrDefault();
    }

    public static string MakeProjectUrl(string? projectId = null, string? slug = null)
    {
        var identity = !string.IsNullOrWhiteSpace(projectId) ? projectId : slug;
        return !string.IsNullOrWhiteSpace(identity)
            ? $"https://modrinth.com/mod/{Uri.EscapeDataString(identity)}"
            : "https://modrinth.com/search";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<T> GetRequiredAsync<T>(
        string endpoint,
        IReadOnlyDictionary<string, string>? parameters,
        string invalidDataMessage,
        CancellationToken cancellationToken)
        where T : class
    {
        return await GetAsync<T>(endpoint, parameters, cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformApiException("Modrinth", invalidDataMessage);
    }

    private async Task<T?> GetAsync<T>(
        string endpoint,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(endpoint, parameters));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlatformApiException("Modrinth", "Modrinth API 超时。", null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PlatformApiException("Modrinth", "无法连接 Modrinth API。", exception.StatusCode, exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new PlatformNotFoundException("Modrinth", "Modrinth 项目或版本不存在。");
            }

            if ((int)response.StatusCode == 429)
            {
                throw new PlatformApiException(
                    "Modrinth",
                    "Modrinth API 请求过于频繁（429），请稍后重试。",
                    response.StatusCode);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new PlatformApiException(
                    "Modrinth",
                    $"Modrinth API 返回 HTTP {(int)response.StatusCode}。",
                    response.StatusCode);
            }

            try
            {
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new PlatformApiException(
                    "Modrinth",
                    "Modrinth API 返回了无效 JSON。",
                    response.StatusCode,
                    exception);
            }
        }
    }

    private async Task<T?> PostAsync<T>(
        string endpoint,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(endpoint, null))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlatformApiException("Modrinth", "Modrinth API request timed out.", null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PlatformApiException(
                "Modrinth", "Unable to connect to the Modrinth API.", exception.StatusCode, exception);
        }

        using (response)
        {
            if ((int)response.StatusCode == 429)
            {
                throw new PlatformApiException(
                    "Modrinth", "Modrinth API rate limit exceeded (429).", response.StatusCode);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new PlatformApiException(
                    "Modrinth", $"Modrinth API returned HTTP {(int)response.StatusCode}.", response.StatusCode);
            }
            try
            {
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new PlatformApiException(
                    "Modrinth", "Modrinth API returned invalid JSON.", response.StatusCode, exception);
            }
        }
    }

    private static Uri BuildUri(string endpoint, IReadOnlyDictionary<string, string>? parameters)
    {
        var builder = new StringBuilder(BaseAddress).Append(endpoint);
        if (parameters is { Count: > 0 })
        {
            builder.Append('?');
            builder.AppendJoin('&', parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private sealed class SearchResponse
    {
        [JsonPropertyName("hits")]
        public List<ModrinthProject> Hits { get; set; } = [];
    }

    private sealed class VersionFilesRequest
    {
        [JsonPropertyName("hashes")]
        public required string[] Hashes { get; init; }

        [JsonPropertyName("algorithm")]
        public required string Algorithm { get; init; }
    }
}
