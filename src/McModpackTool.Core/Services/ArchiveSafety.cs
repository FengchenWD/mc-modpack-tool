using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McModpackTool.Core.Services;

public sealed record ArchiveSafetyOptions
{
    public int MaxEntries { get; init; } = 100_000;
    public long MaxMemberBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxUncompressedBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public double MaxCompressionRatio { get; init; } = 1_000;
    public long CompressionRatioCheckThresholdBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxMetadataBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxDownloadBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int CopyBufferBytes { get; init; } = 1024 * 1024;
    public bool AllowServerOverrides { get; init; }
    public bool IgnoreClientOverrides { get; init; }

    public static ArchiveSafetyOptions Default { get; } = new();
}

public static class ArchiveSafety
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static async Task ValidateArchiveAsync(
        string archivePath,
        ArchiveSafetyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        options ??= ArchiveSafetyOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        await using var file = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            options.CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (await ContainsEncryptedEntryAsync(file, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("整合包包含加密条目，无法安全读取。");
        }

        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
        if (archive.Entries.Count > options.MaxEntries)
        {
            throw new InvalidDataException($"整合包条目过多（上限 {options.MaxEntries}）。");
        }

        long totalSize = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = ValidateEntryPath(entry.FullName);
            var scopedRoot = segments.FirstOrDefault(segment =>
                (segment.Equals("server-overrides", StringComparison.OrdinalIgnoreCase)
                    && !options.AllowServerOverrides) ||
                (segment.Equals("client-overrides", StringComparison.OrdinalIgnoreCase)
                    && !options.IgnoreClientOverrides));
            if (scopedRoot is not null)
            {
                throw new InvalidDataException(
                    $"当前版本无法在保持作用域的前提下迁移 {scopedRoot}，已停止以避免静默丢失内容。");
            }

            if (IsDirectory(entry))
            {
                continue;
            }

            if (IsUnixSymbolicLink(entry))
            {
                throw new InvalidDataException($"整合包包含不允许的符号链接条目: {entry.FullName}");
            }

            if (entry.Length > options.MaxMemberBytes)
            {
                throw new InvalidDataException($"整合包单个文件过大: {entry.FullName}");
            }

            checked
            {
                totalSize += entry.Length;
            }

            if (totalSize > options.MaxUncompressedBytes)
            {
                throw new InvalidDataException("整合包解压后总大小超过安全上限。");
            }

            if (entry.Length >= options.CompressionRatioCheckThresholdBytes)
            {
                var ratio = entry.Length / (double)Math.Max(entry.CompressedLength, 1);
                if (ratio > options.MaxCompressionRatio)
                {
                    throw new InvalidDataException($"整合包条目压缩比异常: {entry.FullName}");
                }
            }
        }
    }

    public static string[] ValidateEntryPath(string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException($"整合包包含不安全路径: {entryName}");
        }

        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.Contains(':'))
        {
            throw new InvalidDataException($"整合包包含不安全路径: {entryName}");
        }

        var rawSegments = normalized.Split('/');
        var segments = new List<string>(rawSegments.Length);
        for (var index = 0; index < rawSegments.Length; index++)
        {
            var segment = rawSegments[index];
            // A final empty segment is the normal directory marker in ZIP archives.
            if (segment.Length == 0 && index == rawSegments.Length - 1)
            {
                continue;
            }

            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new InvalidDataException($"整合包包含不安全路径: {entryName}");
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw new InvalidDataException($"整合包包含 Windows 不安全路径: {entryName}");
            }

            var stem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
            if (WindowsReservedNames.Contains(stem))
            {
                throw new InvalidDataException($"整合包包含 Windows 不安全路径: {entryName}");
            }

            segments.Add(segment);
        }

        return [.. segments];
    }

    public static async Task<JsonObject> ReadJsonObjectAsync(
        ZipArchiveEntry entry,
        int? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        var limit = maxBytes ?? ArchiveSafetyOptions.Default.MaxMetadataBytes;
        if (entry.Length > limit)
        {
            throw new InvalidDataException($"整合包元数据文件过大: {entry.FullName}");
        }

        var bytes = await ReadEntryBytesAsync(entry, limit, cancellationToken).ConfigureAwait(false);
        try
        {
            var json = bytes.AsSpan();
            if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
            {
                json = json[3..];
            }
            var node = JsonNode.Parse(
                json,
                nodeOptions: new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128,
                });
            return node as JsonObject
                ?? throw new InvalidDataException($"整合包元数据根节点必须是对象: {entry.FullName}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"整合包元数据无效: {entry.FullName}: {exception.Message}", exception);
        }
    }

    public static async Task<byte[]> ReadEntryBytesAsync(
        ZipArchiveEntry entry,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (entry.Length > maxBytes)
        {
            throw new InvalidDataException($"整合包条目超过读取上限: {entry.FullName}");
        }

        await using var source = entry.Open();
        using var destination = new MemoryStream((int)Math.Min(entry.Length, maxBytes));
        var buffer = new byte[Math.Min(128 * 1024, maxBytes + 1)];
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"整合包条目超过读取上限: {entry.FullName}");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    public static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var source = new DirectoryInfo(Path.GetFullPath(sourceDirectory));
        if (!source.Exists)
        {
            return;
        }

        if (source.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("overrides 根目录不能是符号链接或重解析点。");
        }

        var destination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destination);
        await CopyDirectoryCoreAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    public static async Task CreateZipAtomicAsync(
        string outputPath,
        string sourceDirectory,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var output = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(output)
            ?? throw new ArgumentException("输出路径必须包含父目录。", nameof(outputPath));
        Directory.CreateDirectory(parent);

        var files = EnumerateFilesWithoutReparsePoints(sourceRoot, cancellationToken)
            .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var temporary = Path.Combine(parent, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var outputStream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                ArchiveSafetyOptions.Default.CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
                foreach (var item in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(item.RelativePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = ClampZipTimestamp(item.File.LastWriteTimeUtc);
                    await using var source = new FileStream(
                        item.File.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        ArchiveSafetyOptions.Default.CopyBufferBytes,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var target = entry.Open();
                    await source.CopyToAsync(target, ArchiveSafetyOptions.Default.CopyBufferBytes, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite);
            temporary = string.Empty;
        }
        finally
        {
            if (temporary.Length > 0)
            {
                TryDeleteFile(temporary);
            }
        }
    }

    public static async Task<bool> DownloadFileAsync(
        HttpClient httpClient,
        string url,
        string destinationDirectory,
        string? fileName = null,
        string suffix = "",
        long expectedSize = 0,
        IReadOnlyDictionary<string, string>? expectedHashes = null,
        ArchiveSafetyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        options ??= ArchiveSafetyOptions.Default;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (expectedSize < 0 || expectedSize > options.MaxDownloadBytes)
        {
            return false;
        }

        string temporary = string.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                if (suffix.Length > 0 && !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    fileName += suffix;
                }
                ValidateLocalName(fileName);
                Directory.CreateDirectory(destinationDirectory);
                if (File.Exists(Path.Combine(Path.GetFullPath(destinationDirectory), fileName)))
                {
                    return false;
                }
            }

            using var response = await httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is < 0 || contentLength > options.MaxDownloadBytes ||
                (expectedSize > 0 && contentLength > expectedSize))
            {
                return false;
            }

            fileName = ResolveDownloadFileName(response, url, fileName, suffix);
            Directory.CreateDirectory(destinationDirectory);
            var finalPath = Path.Combine(Path.GetFullPath(destinationDirectory), fileName);
            if (File.Exists(finalPath))
            {
                return false;
            }

            temporary = Path.Combine(
                Path.GetDirectoryName(finalPath)!,
                $".download-{Guid.NewGuid():N}.part");

            var hashers = CreateHashers(expectedHashes);
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    options.CopyBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long size = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    size += read;
                    if (size > options.MaxDownloadBytes || (expectedSize > 0 && size > expectedSize))
                    {
                        return false;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    foreach (var hasher in hashers.Values)
                    {
                        hasher.AppendData(buffer, 0, read);
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (expectedSize > 0 && size != expectedSize)
                {
                    return false;
                }

                foreach (var (name, hasher) in hashers)
                {
                    var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    var expected = NormalizeHash(expectedHashes![name]);
                    if (expected.Length > 0 && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                foreach (var hasher in hashers.Values)
                {
                    hasher.Dispose();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, finalPath, overwrite: false);
            temporary = string.Empty;
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout is a recoverable download failure; user cancellation is not.
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (temporary.Length > 0)
            {
                TryDeleteFile(temporary);
            }
        }
    }

    private static async Task CopyDirectoryCoreAsync(
        DirectoryInfo source,
        string destination,
        CancellationToken cancellationToken)
    {
        foreach (var directory in source.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"overrides 包含不允许的符号链接目录: {directory.Name}");
            }

            ValidateLocalName(directory.Name);
            var childDestination = Path.Combine(destination, directory.Name);
            Directory.CreateDirectory(childDestination);
            await CopyDirectoryCoreAsync(directory, childDestination, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in source.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"overrides 包含不允许的符号链接文件: {file.Name}");
            }

            ValidateLocalName(file.Name);
            var destinationFile = Path.Combine(destination, file.Name);
            await using (var input = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ArchiveSafetyOptions.Default.CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                destinationFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                ArchiveSafetyOptions.Default.CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, ArchiveSafetyOptions.Default.CopyBufferBytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.SetLastWriteTimeUtc(destinationFile, file.LastWriteTimeUtc);
        }
    }

    private static IReadOnlyList<(FileInfo File, string RelativePath)> EnumerateFilesWithoutReparsePoints(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(sourceRoot);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException(sourceRoot);
        }

        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("打包源目录不能是符号链接或重解析点。");
        }

        var result = new List<(FileInfo File, string RelativePath)>();
        Walk(root, string.Empty);
        return result;

        void Walk(DirectoryInfo directory, string relativeDirectory)
        {
            foreach (var child in directory.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"打包源目录包含不允许的符号链接目录: {child.FullName}");
                }

                ValidateLocalName(child.Name);
                Walk(child, CombineArchivePath(relativeDirectory, child.Name));
            }

            foreach (var file in directory.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"打包源目录包含不允许的符号链接文件: {file.FullName}");
                }

                ValidateLocalName(file.Name);
                result.Add((file, CombineArchivePath(relativeDirectory, file.Name)));
            }
        }
    }

    private static string ResolveDownloadFileName(
        HttpResponseMessage response,
        string url,
        string? requestedName,
        string suffix)
    {
        var fileName = requestedName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName;
            fileName = fileName?.Trim().Trim('"');
        }

        if (string.IsNullOrWhiteSpace(fileName) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            fileName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidDataException("下载响应没有安全的文件名。");
        }

        if (suffix.Length > 0 && !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            fileName += suffix;
        }

        ValidateLocalName(fileName);
        return fileName;
    }

    private static Dictionary<string, IncrementalHash> CreateHashers(
        IReadOnlyDictionary<string, string>? expectedHashes)
    {
        var result = new Dictionary<string, IncrementalHash>(StringComparer.OrdinalIgnoreCase);
        if (expectedHashes is null)
        {
            return result;
        }

        foreach (var (name, value) in expectedHashes)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            var algorithm = normalized switch
            {
                "md5" => HashAlgorithmName.MD5,
                "sha1" => HashAlgorithmName.SHA1,
                "sha256" => HashAlgorithmName.SHA256,
                "sha384" => HashAlgorithmName.SHA384,
                "sha512" => HashAlgorithmName.SHA512,
                _ => default,
            };
            if (algorithm == default)
            {
                continue;
            }

            result[name] = IncrementalHash.CreateHash(algorithm);
        }

        return result;
    }

    private static string NormalizeHash(string value) =>
        value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    internal static void ValidateLocalName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || Path.GetFileName(name) != name ||
            name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
        {
            throw new InvalidDataException($"不安全的文件名: {name}");
        }

        var stem = name.Split('.', 2)[0].TrimEnd(' ', '.');
        if (WindowsReservedNames.Contains(stem))
        {
            throw new InvalidDataException($"Windows 不安全文件名: {name}");
        }
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0;

    private static bool IsUnixSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static string CombineArchivePath(string directory, string name) =>
        directory.Length == 0 ? name : $"{directory}/{name}";

    private static DateTimeOffset ClampZipTimestamp(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        if (utc.Year < 1980)
        {
            utc = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }
        else if (utc.Year > 2107)
        {
            utc = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        }

        return new DateTimeOffset(utc);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cleanup must never hide the operation's original failure.
        }
    }

    private static async Task<bool> ContainsEncryptedEntryAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Length < 22)
        {
            return false;
        }

        const uint endOfCentralDirectorySignature = 0x06054B50;
        const uint zip64EndSignature = 0x06064B50;
        const uint zip64LocatorSignature = 0x07064B50;
        const uint centralEntrySignature = 0x02014B50;
        var tailLength = (int)Math.Min(stream.Length, ushort.MaxValue + 22L);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);

        var eocdIndex = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == endOfCentralDirectorySignature)
            {
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2));
                if (index + 22 + commentLength == tail.Length)
                {
                    eocdIndex = index;
                    break;
                }
            }
        }

        if (eocdIndex < 0)
        {
            return false; // ZipArchive will provide the canonical malformed-archive exception.
        }

        long entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocdIndex + 10, 2));
        long centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocdIndex + 16, 4));
        if (entryCount == ushort.MaxValue || centralOffset == uint.MaxValue)
        {
            var locatorPosition = stream.Length - tailLength + eocdIndex - 20;
            if (locatorPosition < 0)
            {
                return false;
            }

            var locator = new byte[20];
            stream.Position = locatorPosition;
            await stream.ReadExactlyAsync(locator, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != zip64LocatorSignature)
            {
                return false;
            }

            var zip64Offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8, 8)));
            var zip64Header = new byte[56];
            stream.Position = zip64Offset;
            await stream.ReadExactlyAsync(zip64Header, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64Header) != zip64EndSignature)
            {
                return false;
            }

            entryCount = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64Header.AsSpan(32, 8)));
            centralOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64Header.AsSpan(48, 8)));
        }

        var header = new byte[46];
        stream.Position = centralOffset;
        for (long index = 0; index < entryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != centralEntrySignature)
            {
                return false;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
            if ((flags & 0x1) != 0)
            {
                return true;
            }

            var variableLength =
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2)) +
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2)) +
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
            stream.Position += variableLength;
        }

        return false;
    }
}
