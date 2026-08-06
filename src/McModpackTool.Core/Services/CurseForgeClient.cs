using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed class CurseForgeClient : IDisposable
{
    public const string BaseAddress = "https://api.curseforge.com/v1";
    public const int MinecraftGameId = 432;
    public const int DefaultSearchLimit = 30;

    private static readonly IReadOnlyDictionary<string, int> ClassIds =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["mod"] = 6,
            ["resourcepack"] = 12,
            ["shaderpack"] = 6552,
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly string _userAgent;
    private string _apiKey;

    public CurseForgeClient(
        string? apiKey = null,
        HttpClient? httpClient = null,
        string userAgent = "MCPackMigrator/1.0.0-beta.1",
        TimeSpan? requestTimeout = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _userAgent = userAgent;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _apiKey = ResolveApiKey(apiKey);
    }

    public string ApiKey => _apiKey;

    /// <summary>
    /// Resolves the runtime override before an optional build-generated embedded key.
    /// The embedded value is supplied by the release build and is never stored here.
    /// </summary>
    public static string ResolveApiKey(string? embeddedKey = null) =>
        (Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY") ?? string.Empty).Trim()
        is { Length: > 0 } environmentKey
            ? environmentKey
            : (embeddedKey ?? string.Empty).Trim();

    public void SetApiKey(string? apiKey) => _apiKey = (apiKey ?? string.Empty).Trim();

    public async Task<IReadOnlyList<CurseForgeProject>> SearchProjectsAsync(
        string query,
        int limit = DefaultSearchLimit,
        string category = "mod",
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["gameId"] = MinecraftGameId.ToString(CultureInfo.InvariantCulture),
            ["searchFilter"] = query ?? string.Empty,
            ["pageSize"] = Math.Clamp(limit, 1, 50).ToString(CultureInfo.InvariantCulture),
            ["index"] = "0",
        };
        if (ClassIds.TryGetValue(category ?? string.Empty, out var classId))
        {
            parameters["classId"] = classId.ToString(CultureInfo.InvariantCulture);
        }

        var data = await GetAsync<List<CurseForgeProject>>(
            "/mods/search",
            parameters,
            cancellationToken).ConfigureAwait(false);
        return data ?? [];
    }

    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var allFiles = new List<CurseForgeFile>();
        for (var index = 0; ; index += 50)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await GetAsync<List<CurseForgeFile>>(
                $"/mods/{projectId.ToString(CultureInfo.InvariantCulture)}/files",
                new Dictionary<string, string>
                {
                    ["index"] = index.ToString(CultureInfo.InvariantCulture),
                    ["pageSize"] = "50",
                },
                cancellationToken).ConfigureAwait(false)
                ?? throw UnexpectedData("CurseForge 文件接口返回了意外的数据格式。");
            allFiles.AddRange(page);
            if (page.Count < 50)
            {
                return allFiles;
            }
        }
    }

    public async Task<CurseForgeProject> GetProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        return await GetAsync<CurseForgeProject>(
            $"/mods/{projectId.ToString(CultureInfo.InvariantCulture)}",
            null,
            cancellationToken).ConfigureAwait(false)
            ?? throw UnexpectedData("CurseForge 项目接口返回了意外的数据格式。");
    }

    public async Task<IReadOnlyDictionary<long, CurseForgeFile>> GetFilesByIdsAsync(
        IEnumerable<long> fileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var normalized = fileIds.Where(id => id > 0).Distinct().Order().ToArray();
        var result = new Dictionary<long, CurseForgeFile>();
        foreach (var batch in normalized.Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = await PostAsync<List<CurseForgeFile>>(
                "/mods/files",
                new BulkFilesRequest { FileIds = batch },
                cancellationToken).ConfigureAwait(false)
                ?? throw UnexpectedData("CurseForge 批量文件接口返回了意外的数据格式。");
            foreach (var file in files.Where(file => file.Id > 0))
            {
                result[file.Id] = file;
            }
        }

        return result;
    }

    public Task<IReadOnlyDictionary<long, CurseForgeFile>> GetFilesByIdsAsync(
        IEnumerable<string> fileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        return GetFilesByIdsAsync(
            fileIds.Select(value => long.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                ? parsed
                : 0),
            cancellationToken);
    }

    public async Task<CurseForgeFile?> FindTargetFileAsync(
        long projectId,
        string targetMinecraft,
        string? targetLoader,
        bool strictMinecraft = true,
        CancellationToken cancellationToken = default)
    {
        var files = await GetFilesAsync(projectId, cancellationToken).ConfigureAwait(false);
        foreach (var gameVersion in SearchMatcher.GenerateGameVersionCandidates(
            targetMinecraft,
            strictMinecraft))
        {
            var candidates = files
                .Where(file => file.GameVersions.Contains(gameVersion, StringComparer.Ordinal))
                .Where(file => string.IsNullOrWhiteSpace(targetLoader)
                    || file.GameVersions.Contains(targetLoader, StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => file.ReleaseType)
                .ThenByDescending(file => ParseDate(file.FileDate))
                .ToList();
            if (candidates.Count > 0)
            {
                return candidates[0];
            }
        }

        return null;
    }

    public async Task<string> GetDownloadUrlAsync(
        long projectId,
        long fileId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync<string>(
                $"/mods/{projectId.ToString(CultureInfo.InvariantCulture)}/files/"
                    + $"{fileId.ToString(CultureInfo.InvariantCulture)}/download-url",
                null,
                cancellationToken).ConfigureAwait(false)
                ?? string.Empty;
        }
        catch (PlatformNotFoundException)
        {
            return string.Empty;
        }
        catch (PlatformApiException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            return string.Empty;
        }
    }

    public static string MakeProjectUrl(
        string? slug = null,
        long projectId = 0,
        string category = "mod")
    {
        var categoryPath = category.ToLowerInvariant() switch
        {
            "resourcepack" => "texture-packs",
            "shaderpack" => "shaders",
            _ => "mc-mods",
        };
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return $"https://www.curseforge.com/minecraft/{categoryPath}/{Uri.EscapeDataString(slug)}";
        }

        return projectId > 0
            ? $"https://www.curseforge.com/minecraft/{categoryPath}/{projectId.ToString(CultureInfo.InvariantCulture)}"
            : "https://www.curseforge.com/minecraft/search";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private Task<T?> GetAsync<T>(
        string endpoint,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(endpoint, parameters));
        return SendAsync<T>(request, cancellationToken);
    }

    private Task<T?> PostAsync<T>(
        string endpoint,
        object payload,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(endpoint, null))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        return SendAsync<T>(request, cancellationToken);
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            if (_apiKey.Length == 0)
            {
                throw new InvalidOperationException("未配置 CurseForge API Key。");
            }

            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
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
                throw new PlatformApiException("CurseForge", "CurseForge API 超时。", null, exception);
            }
            catch (HttpRequestException exception)
            {
                throw new PlatformApiException("CurseForge", "无法连接 CurseForge API。", exception.StatusCode, exception);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new PlatformNotFoundException("CurseForge", "CurseForge 项目或文件不存在。");
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new PlatformApiException(
                        "CurseForge",
                        $"CurseForge API Key 无效或无权访问（{(int)response.StatusCode}）。",
                        response.StatusCode);
                }

                if ((int)response.StatusCode == 429)
                {
                    throw new PlatformApiException(
                        "CurseForge",
                        "CurseForge API 请求过于频繁（429），请稍后重试。",
                        response.StatusCode);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new PlatformApiException(
                        "CurseForge",
                        $"CurseForge API 返回 HTTP {(int)response.StatusCode}。",
                        response.StatusCode);
                }

                try
                {
                    await using var stream = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var envelope = await JsonSerializer.DeserializeAsync<ApiEnvelope<T>>(
                        stream,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                    return envelope is null
                        ? throw UnexpectedData("CurseForge API 返回了意外的数据格式。")
                        : envelope.Data;
                }
                catch (JsonException exception)
                {
                    throw new PlatformApiException(
                        "CurseForge",
                        "CurseForge API 返回了无效 JSON。",
                        response.StatusCode,
                        exception);
                }
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

    private static PlatformApiException UnexpectedData(string message) =>
        new("CurseForge", message);

    private sealed class ApiEnvelope<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class BulkFilesRequest
    {
        [JsonPropertyName("fileIds")]
        public long[] FileIds { get; set; } = [];
    }
}
