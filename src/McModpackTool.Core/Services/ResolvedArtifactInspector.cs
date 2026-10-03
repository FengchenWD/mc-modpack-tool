using System.Collections.Concurrent;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed record ResolvedArtifactInspection(
    int ItemIndex,
    ArtifactCompatibilityMetadata? Metadata,
    string ArtifactPath,
    string Warning = "");

/// <summary>
/// Downloads the exact resolved target JAR into a content-addressed cache and reads its
/// loader metadata. Platform metadata remains useful, but the selected artifact is the
/// authoritative source for mod ids, dependency ranges and side declarations.
/// </summary>
public sealed class ResolvedArtifactInspector : IDisposable
{
    private const int DefaultConcurrency = 4;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cacheDirectory;

    public ResolvedArtifactInspector(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MC整合包工具",
                "Cache",
                "Artifacts")
            : Path.GetFullPath(cacheDirectory);
    }

    public async Task<IReadOnlyList<ResolvedArtifactInspection>> InspectAsync(
        IReadOnlyList<ContentItem> items,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        Directory.CreateDirectory(_cacheDirectory);
        var results = new ConcurrentBag<ResolvedArtifactInspection>();
        using var gate = new SemaphoreSlim(DefaultConcurrency, DefaultConcurrency);
        int completed = 0;
        Task[] tasks = items.Select((item, index) => InspectOneGuardedAsync(item, index)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OrderBy(result => result.ItemIndex).ToArray();

        async Task InspectOneGuardedAsync(ContentItem item, int index)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results.Add(await InspectOneAsync(item, index, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref completed));
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<ResolvedArtifactInspection> InspectOneAsync(
        ContentItem item,
        int index,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.Excluded || item.Passthrough || item.Disabled ||
            !item.Category.Equals("mod", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedArtifactInspection(index, null, string.Empty);
        }

        string fileName = FirstNonEmpty(item.TargetFileName, item.FileName);
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedArtifactInspection(index, null, string.Empty);
        }

        string url = FirstNonEmpty(item.TargetDownloadUrl, item.DownloadUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ResolvedArtifactInspection(index, null, string.Empty,
                "The resolved mod does not expose a verified HTTPS download URL for deep inspection.");
        }

        IReadOnlyDictionary<string, string> hashes = item.TargetHashes.Count > 0
            ? item.TargetHashes
            : item.Hashes;
        string cacheKey = CacheKey(hashes);
        if (cacheKey.Length == 0)
        {
            return new ResolvedArtifactInspection(index, null, string.Empty,
                "The resolved mod has no strong hash and was not downloaded for deep inspection.");
        }

        string cachedPath = Path.Combine(_cacheDirectory, cacheKey + ".jar");
        if (!File.Exists(cachedPath))
        {
            string temporaryName = cacheKey + ".download.jar";
            string temporaryPath = Path.Combine(_cacheDirectory, temporaryName);
            TryDeleteFile(temporaryPath);
            bool downloaded = await ArchiveSafety.DownloadFileAsync(
                _httpClient,
                url,
                _cacheDirectory,
                temporaryName,
                expectedSize: item.TargetFileSize > 0 ? item.TargetFileSize : item.FileSize,
                expectedHashes: hashes,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!downloaded)
            {
                return new ResolvedArtifactInspection(index, null, string.Empty,
                    "The resolved mod could not be downloaded and verified for deep inspection.");
            }
            try
            {
                File.Move(temporaryPath, cachedPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(cachedPath))
            {
                TryDeleteFile(temporaryPath);
            }
        }

        try
        {
            ArtifactCompatibilityMetadata metadata = await Task.Run(
                () => ArtifactMetadataReader.Read(cachedPath, cancellationToken: cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new ResolvedArtifactInspection(index, metadata, cachedPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(cachedPath);
            return new ResolvedArtifactInspection(index, null, string.Empty,
                $"The resolved mod metadata could not be inspected: {exception.Message}");
        }
    }

    private static string CacheKey(IReadOnlyDictionary<string, string> hashes)
    {
        foreach (string algorithm in new[] { "sha512", "sha256", "sha1" })
        {
            if (!hashes.TryGetValue(algorithm, out string? value))
            {
                continue;
            }
            string normalized = new(value.Where(Uri.IsHexDigit).ToArray());
            if (normalized.Length > 0)
            {
                return algorithm + "-" + normalized.ToLowerInvariant();
            }
        }
        return string.Empty;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
