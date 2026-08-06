using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using McModpackTool.Core.Models;

namespace McModpackTool.Core.Services;

public sealed record CandidateMatch<T>(
    T Candidate,
    double Score,
    bool Exact,
    int Hits,
    int ExtraCount)
    where T : notnull;

/// <summary>
/// Filename identity matching shared by Modrinth and CurseForge fallback searches.
/// Matching is intentionally conservative: a plausible but ambiguous result is no result.
/// </summary>
public static partial class SearchMatcher
{
    public const double SearchScoreMargin = 8.0;
    public const int MaximumVerifiedCandidates = 5;

    private static readonly string[] SearchExtensions =
    [
        ".jar.disabled", ".disabled", ".jar", ".zip", ".litematic", ".mrpack",
    ];

    private static readonly HashSet<string> IgnoredTokens = new(StringComparer.Ordinal)
    {
        "and", "the", "for", "with", "of", "in", "on", "to", "by",
        "mod", "mods", "pack", "resource", "texture", "shader", "edition", "version",
        "mc", "minecraft", "fabric", "forge", "neoforge", "quilt", "loader", "jar", "zip",
    };

    private static readonly HashSet<string> RoleTokens = new(StringComparer.Ordinal)
    {
        "addon", "addons", "bridge", "compat", "compatibility", "extension", "integration",
        "patch", "plugin", "port", "support", "unofficial", "plus", "fork", "forked",
        "redux", "reborn", "continued", "tweak", "tweaks",
    };

    private static readonly Dictionary<string, string> TokenAliases = new(StringComparer.Ordinal)
    {
        ["lib"] = "library",
        ["libs"] = "library",
    };

    private static readonly string[] CommonConcatenatedWords = new[]
    {
        "compatibility", "cobblemon", "resource", "cupboard", "ultimine", "library",
        "smooth", "chunk", "quest", "freeze", "roughly", "enough", "config", "fabric",
        "better", "tensura", "shader", "mining", "chain", "items", "cloth", "forge",
        "block", "shine", "compat", "save", "board", "pack", "core", "ores", "stay",
        "true", "mini", "just", "api", "mod", "map", "cup", "ftb", "jei", "fix", "in",
    }.OrderByDescending(word => word.Length).ToArray();

    public static IReadOnlyList<string> GenerateGameVersionCandidates(string targetMinecraft, bool strict = false)
    {
        var target = (targetMinecraft ?? string.Empty).Trim();
        if (strict || target.Length == 0)
        {
            return target.Length == 0 ? [] : [target];
        }

        var parts = target.Split('.');
        return parts.Length >= 3
            ? [target, string.Join('.', parts.Take(2))]
            : [target];
    }

    public static string NormalizeLoaderName(string? loader)
    {
        var normalized = NonAlphaNumericRegex().Replace(
            (loader ?? string.Empty).ToLowerInvariant(),
            string.Empty);
        return normalized switch
        {
            "fabricloader" => "fabric",
            "quiltloader" => "quilt",
            "neoforged" => "neoforge",
            "neo" => "neoforge",
            _ => normalized,
        };
    }

    public static bool IsSameContentEnvironment(
        ModpackInfo pack,
        string targetMinecraft,
        string targetLoader)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return string.Equals(
                pack.MinecraftVersion.Trim(),
                (targetMinecraft ?? string.Empty).Trim(),
                StringComparison.Ordinal)
            && string.Equals(
                NormalizeLoaderName(pack.LoaderType),
                NormalizeLoaderName(targetLoader),
                StringComparison.Ordinal);
    }

    public static bool TryPreserveOriginalReference(ContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.OriginalEntry is null)
        {
            return false;
        }

        item.PreserveOriginal = true;
        item.Status = "preserved";
        item.Note = "目标环境未变化，保留原文件";
        item.TargetFileName = item.FileName;
        item.TargetDownloadUrl = item.DownloadUrl;
        item.TargetFileSize = item.FileSize;
        item.TargetHashes = new Dictionary<string, string>(item.Hashes, StringComparer.OrdinalIgnoreCase);
        item.TargetVersionId = item.VersionId;
        item.TargetVersionNumber = string.Empty;
        return true;
    }

    public static string ParseCurseForgeFileId(IEnumerable<string?> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        foreach (var value in urls)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (host != "forgecdn.net" && !host.EndsWith(".forgecdn.net", StringComparison.Ordinal))
            {
                continue;
            }

            var match = ForgeCdnFileRegex().Match(uri.AbsolutePath);
            if (!match.Success
                || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
                || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix))
            {
                continue;
            }

            try
            {
                return checked((prefix * 1000L) + suffix).ToString(CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                // Ignore malformed, excessively large identifiers.
            }
        }

        return string.Empty;
    }

    public static bool EnvironmentRequiresScopedHandling(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
        {
            return false;
        }

        var client = environment.TryGetValue("client", out var clientValue)
            ? clientValue
            : "required";
        var server = environment.TryGetValue("server", out var serverValue)
            ? serverValue
            : "required";
        return !string.Equals(client, "required", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(server, "required", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ExtractSearchQueries(string? fileName)
    {
        var tokens = ExtractIdentityTokens(fileName);
        if (tokens.Count == 0)
        {
            return [];
        }

        var candidates = new List<string>
        {
            string.Join(' ', tokens),
            string.Join('-', tokens),
            string.Concat(tokens),
        };
        if (tokens.Count > 3)
        {
            candidates.Add(string.Join(' ', tokens.Take(3)));
        }

        if (tokens.Count > 2)
        {
            candidates.Add(string.Join(' ', tokens.Take(2)));
        }

        if (tokens.Count > 1 && tokens[0].Length >= 3)
        {
            candidates.Add(tokens[0]);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            var normalized = WhitespaceRegex().Replace(candidate, " ").Trim().ToLowerInvariant();
            if (normalized.Length >= 2 && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    public static IReadOnlyList<string> GenerateCurseForgeSearchQueries(string? fileName) =>
        ExtractSearchQueries(fileName);

    public static IReadOnlyList<string> ExtractIdentityTokens(string? value)
    {
        var cleaned = StripSearchNoise(value);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in AsciiTokenRegex().Matches(cleaned.ToLowerInvariant()))
        {
            var token = Alias(match.Value);
            if (token.Length < 2 || IgnoredTokens.Contains(token) || IsVersionToken(token))
            {
                continue;
            }

            var expanded = token.All(char.IsLetter) && token.Length >= 6
                ? SplitConcatenatedWords(token)
                : token;
            var pieces = expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var candidates = pieces.Length > 1 && pieces.All(piece => piece.Length >= 2)
                ? pieces
                : [token];

            foreach (var piece in candidates)
            {
                var candidate = Alias(piece);
                if (!IgnoredTokens.Contains(candidate) && seen.Add(candidate))
                {
                    result.Add(candidate);
                }
            }
        }

        return result;
    }

    public static CandidateMatch<T>? EvaluateCandidate<T>(
        T candidate,
        string originalFileName,
        string searchQuery,
        Func<T, string?> nameSelector,
        Func<T, string?> slugSelector)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(nameSelector);
        ArgumentNullException.ThrowIfNull(slugSelector);

        var originalTokens = ExtractIdentityTokens(originalFileName).ToHashSet(StringComparer.Ordinal);
        if (originalTokens.Count == 0)
        {
            return null;
        }

        var fields = new[] { nameSelector(candidate), slugSelector(candidate) }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new CandidateField(
                ExtractIdentityTokens(value).ToHashSet(StringComparer.Ordinal),
                IdentityCompact(value)))
            .Where(field => field.Tokens.Count > 0)
            .ToList();
        if (fields.Count == 0)
        {
            return null;
        }

        var candidateTokens = fields
            .SelectMany(field => field.Tokens)
            .ToHashSet(StringComparer.Ordinal);
        var addedRoles = candidateTokens.Intersect(RoleTokens).Except(originalTokens.Intersect(RoleTokens));
        if (addedRoles.Any())
        {
            return null;
        }

        var originalCompact = IdentityCompact(originalFileName);
        foreach (var field in fields)
        {
            var extras = field.Tokens.Except(originalTokens).ToArray();
            if (originalTokens.IsSubsetOf(field.Tokens)
                && extras.Length > 0
                && !string.Equals(field.Compact, originalCompact, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var queryTokens = ExtractIdentityTokens(searchQuery).ToHashSet(StringComparer.Ordinal);
        var queryCompact = IdentityCompact(searchQuery);
        var fieldMatches = new List<CandidateMatch<T>>();
        foreach (var field in fields)
        {
            var exact = originalCompact.Length > 0
                && string.Equals(field.Compact, originalCompact, StringComparison.Ordinal);
            var matched = originalTokens.Intersect(field.Tokens).Count();
            var extras = field.Tokens.Except(originalTokens).Count();
            if (exact)
            {
                var exactScore = 135.0 + (queryCompact == field.Compact ? 5.0 : 0.0);
                fieldMatches.Add(new CandidateMatch<T>(candidate, exactScore, true, originalTokens.Count, 0));
                continue;
            }

            if (matched < originalTokens.Count)
            {
                continue;
            }

            var precision = (double)matched / field.Tokens.Count;
            if (precision < 0.75)
            {
                continue;
            }

            var queryCoverage = (double)queryTokens.Intersect(field.Tokens).Count()
                / Math.Max(queryTokens.Count, 1);
            var weightedScore = 70.0 + (precision * 25.0) + (queryCoverage * 5.0) - (extras * 6.0);
            fieldMatches.Add(new CandidateMatch<T>(candidate, weightedScore, false, matched, extras));
        }

        return fieldMatches
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Exact)
            .ThenByDescending(match => match.Hits)
            .ThenBy(match => match.ExtraCount)
            .FirstOrDefault();
    }

    public static IReadOnlyList<CandidateMatch<T>> RankCandidates<T>(
        IEnumerable<T> candidates,
        string originalFileName,
        string searchQuery,
        Func<T, string?> identitySelector,
        Func<T, string?> nameSelector,
        Func<T, string?> slugSelector)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(identitySelector);

        var deduplicated = new Dictionary<string, CandidateMatch<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var match = EvaluateCandidate(candidate, originalFileName, searchQuery, nameSelector, slugSelector);
            if (match is null)
            {
                continue;
            }

            var identity = identitySelector(candidate);
            if (string.IsNullOrWhiteSpace(identity))
            {
                identity = $"{nameSelector(candidate)}|{slugSelector(candidate)}";
            }

            if (!deduplicated.TryGetValue(identity, out var previous) || match.Score > previous.Score)
            {
                deduplicated[identity] = match;
            }
        }

        return deduplicated.Values
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Exact)
            .ThenByDescending(match => match.Hits)
            .ThenBy(match => match.ExtraCount)
            .ToList();
    }

    public static CandidateMatch<T>? PickUnambiguous<T>(IReadOnlyList<CandidateMatch<T>> matches)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0)
        {
            return null;
        }

        var best = matches[0];
        if (matches.Count > 1)
        {
            var runnerUp = matches[1];
            var tooClose = best.Score - runnerUp.Score < SearchScoreMargin;
            if (tooClose && (!best.Exact || runnerUp.Exact))
            {
                return null;
            }
        }

        return best;
    }

    public static ModrinthProject? PickBestModrinthResult(
        IEnumerable<ModrinthProject> candidates,
        string originalFileName,
        string searchQuery)
    {
        var ranked = RankCandidates(
            candidates,
            originalFileName,
            searchQuery,
            candidate => candidate.EffectiveId,
            candidate => candidate.Title,
            candidate => candidate.Slug);
        return PickUnambiguous(ranked)?.Candidate;
    }

    public static CurseForgeProject? PickBestCurseForgeResult(
        IEnumerable<CurseForgeProject> candidates,
        string originalFileName,
        string searchQuery)
    {
        var ranked = RankCandidates(
            candidates,
            originalFileName,
            searchQuery,
            candidate => candidate.Id.ToString(CultureInfo.InvariantCulture),
            candidate => candidate.Name,
            candidate => candidate.Slug);
        return PickUnambiguous(ranked)?.Candidate;
    }

    public static ModrinthFile? SelectPrimaryFile(IEnumerable<ModrinthFile>? files)
    {
        if (files is null)
        {
            return null;
        }

        var materialized = files.ToList();
        return materialized.FirstOrDefault(file => file.Primary is true) ?? materialized.FirstOrDefault();
    }

    public static ModrinthFile? SelectUsablePrimaryFile(IEnumerable<ModrinthFile>? files)
    {
        var primary = SelectPrimaryFile(files);
        return primary is not null
            && !string.IsNullOrWhiteSpace(primary.FileName)
            && !string.IsNullOrWhiteSpace(primary.Url)
            && primary.Hashes.Count > 0
                ? primary
                : null;
    }

    public static Dictionary<string, string> ExtractCurseForgeHashes(CurseForgeFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in file.Hashes)
        {
            var algorithm = hash.Algorithm switch
            {
                1 => "sha1",
                2 => "md5",
                _ => string.Empty,
            };
            if (algorithm.Length > 0 && !string.IsNullOrWhiteSpace(hash.Value))
            {
                result[algorithm] = hash.Value.ToLowerInvariant();
            }
        }

        return result;
    }

    public static bool CurseForgeFileMatchesSource(ContentItem item, CurseForgeFile file)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(file);
        if (!long.TryParse(item.FileId, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId)
            || file.Id != fileId
            || file.ModId <= 0)
        {
            return false;
        }

        var downloads = new List<string>(item.DownloadUrls);
        if (item.OriginalEntry?["downloads"] is JsonArray originalDownloads)
        {
            foreach (var node in originalDownloads)
            {
                if (node is JsonValue value
                    && value.TryGetValue<string>(out var url)
                    && !string.IsNullOrWhiteSpace(url))
                {
                    downloads.Add(url);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(item.DownloadUrl))
        {
            downloads.Add(item.DownloadUrl);
        }

        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var download in downloads.Distinct(StringComparer.Ordinal))
        {
            if (!string.Equals(ParseCurseForgeFileId([download]), item.FileId, StringComparison.Ordinal)
                || !Uri.TryCreate(download, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var sourceName = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? string.Empty).Trim('/');
            if (sourceName.Length > 0)
            {
                sourceNames.Add(sourceName);
            }
        }

        if (sourceNames.Count > 0
            && file.FileName.Length > 0
            && !sourceNames.Contains(file.FileName))
        {
            return false;
        }

        if (item.FileSize > 0 && file.FileLength > 0 && item.FileSize != file.FileLength)
        {
            return false;
        }

        var returnedHashes = ExtractCurseForgeHashes(file);
        foreach (var algorithm in item.Hashes.Keys.Intersect(returnedHashes.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(
                item.Hashes[algorithm],
                returnedHashes[algorithm],
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string Alias(string token) =>
        TokenAliases.TryGetValue(token, out var replacement) ? replacement : token;

    private static string IdentityCompact(string? value) => string.Concat(ExtractIdentityTokens(value));

    private static string StripSearchNoise(string? value)
    {
        var text = (value ?? string.Empty).Replace('\\', '/');
        var separator = text.LastIndexOf('/');
        if (separator >= 0)
        {
            text = text[(separator + 1)..];
        }

        try
        {
            text = Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            // Keep the original filename if a malformed percent escape is present.
        }

        text = text.Trim();
        foreach (var extension in SearchExtensions)
        {
            if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^extension.Length];
                break;
            }
        }

        text = BracketRegex().Replace(text, " ");
        text = CamelBoundaryRegex().Replace(text, " ");
        return text.Trim();
    }

    private static bool IsVersionToken(string token) =>
        token.All(char.IsDigit)
        || PrefixedVersionRegex().IsMatch(token)
        || PrereleaseVersionRegex().IsMatch(token);

    private static string SplitConcatenatedWords(string text)
    {
        if (text.Length == 0 || text.Contains(' '))
        {
            return text;
        }

        var separated = LowerUpperBoundaryRegex().Replace(text, "$1 $2");
        separated = LetterDigitBoundaryRegex().Replace(separated, "$1 $2");
        separated = DigitLetterBoundaryRegex().Replace(separated, "$1 $2");
        if (!string.Equals(separated, text, StringComparison.Ordinal))
        {
            return separated.ToLowerInvariant();
        }

        var remaining = text.ToLowerInvariant();
        var found = new List<string>();
        while (remaining.Length > 0)
        {
            var word = CommonConcatenatedWords.FirstOrDefault(
                candidate => remaining.StartsWith(candidate, StringComparison.Ordinal));
            if (word is null)
            {
                found.Add(remaining[..1]);
                remaining = remaining[1..];
            }
            else
            {
                found.Add(word);
                remaining = remaining[word.Length..];
            }
        }

        var result = string.Join(' ', found);
        return string.Equals(result, text, StringComparison.OrdinalIgnoreCase) ? text : result;
    }

    private sealed record CandidateField(HashSet<string> Tokens, string Compact);

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"(?:^|/)files/(\d+)/(\d{1,3})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForgeCdnFileRegex();

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\[\]\(\)【】（）《》「」]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundaryRegex();

    [GeneratedRegex(@"^(?:mc|v|r)\d+(?:[a-z]*\d*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedVersionRegex();

    [GeneratedRegex(@"^\d+(?:alpha|beta|pre|preview|rc|snapshot|build)\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrereleaseVersionRegex();

    [GeneratedRegex(@"([a-z])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex LowerUpperBoundaryRegex();

    [GeneratedRegex(@"([a-zA-Z])(\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LetterDigitBoundaryRegex();

    [GeneratedRegex(@"(\d)([a-zA-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex DigitLetterBoundaryRegex();
}

public sealed record TargetResolutionResult(
    int Processed,
    int Found,
    int Preserved,
    int Passthrough,
    int Missing);

/// <summary>
/// Resolves target files while keeping trusted platform identity ahead of filename search.
/// </summary>
public sealed class ContentTargetResolver
{
    private readonly CurseForgeClient _curseForge;
    private readonly ModrinthClient _modrinth;

    public ContentTargetResolver(CurseForgeClient curseForge, ModrinthClient modrinth)
    {
        _curseForge = curseForge ?? throw new ArgumentNullException(nameof(curseForge));
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
    }

    public async Task<TargetResolutionResult> ResolveAsync(
        ModpackInfo pack,
        string targetMinecraft,
        string targetLoader,
        bool pendingOnly = false,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var activeItems = pack.Items
            .Where(item => !item.Excluded && !item.Passthrough)
            .Where(item => !pendingOnly || item.Status == "pending")
            .ToList();
        if (!pendingOnly)
        {
            foreach (var item in pack.Items)
            {
                PrepareForLookup(item);
            }
        }

        var sameEnvironment = SearchMatcher.IsSameContentEnvironment(
            pack,
            targetMinecraft,
            targetLoader);
        IReadOnlyDictionary<long, CurseForgeFile> sourceFiles = new Dictionary<long, CurseForgeFile>();
        if (pack.FormatType.Equals("modrinth", StringComparison.OrdinalIgnoreCase)
            && !sameEnvironment)
        {
            var sourceIds = activeItems
                .Select(item => long.TryParse(
                        item.FileId,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsed)
                    ? parsed
                    : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            if (sourceIds.Length > 0)
            {
                // A failure here must abort the batch: silently falling back to a filename
                // search would discard the strongest identity evidence and can select an addon.
                sourceFiles = await _curseForge
                    .GetFilesByIdsAsync(sourceIds, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        for (var index = 0; index < activeItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = activeItems[index];
            if (sameEnvironment && SearchMatcher.TryPreserveOriginalReference(item))
            {
                progress?.Report(index + 1);
                continue;
            }

            try
            {
                if (pack.FormatType.Equals("curseforge", StringComparison.OrdinalIgnoreCase))
                {
                    await ResolveCurseForgeManifestItemAsync(
                        pack,
                        item,
                        targetMinecraft,
                        targetLoader,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ResolveModrinthItemAsync(
                        item,
                        targetMinecraft,
                        targetLoader,
                        sourceFiles,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (PlatformNotFoundException)
            {
                MarkMissing(item, "平台项目或版本不存在");
            }

            progress?.Report(index + 1);
        }

        if (pack.FormatType.Equals("modrinth", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackItems = activeItems.Where(item =>
                item.Status == "not_found" && !item.IdentityLocked).ToList();
            if (_curseForge.ApiKey.Length == 0)
            {
                foreach (var item in fallbackItems)
                {
                    item.Note = "未配置 CurseForge API Key，已跳过 CurseForge 备用搜索";
                }
            }
            else
            {
                foreach (var item in fallbackItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ResolveCurseForgeFallbackAsync(
                        item,
                        targetMinecraft,
                        targetLoader,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new TargetResolutionResult(
            activeItems.Count,
            activeItems.Count(item => item.Status is "found" or "warning"),
            activeItems.Count(item => item.Status == "preserved"),
            activeItems.Count(item => item.Passthrough || item.Status == "passthrough"),
            activeItems.Count(item => item.Status == "not_found"));
    }

    private static void PrepareForLookup(ContentItem item)
    {
        if (item.Passthrough)
        {
            item.Status = "passthrough";
            return;
        }

        if (item.OriginalSource.Length == 0)
        {
            item.OriginalSource = item.Source;
            item.OriginalProjectId = item.ProjectId;
        }

        item.Source = item.OriginalSource;
        item.ProjectId = item.OriginalProjectId;
        item.ResetTarget();
        if (item.Excluded)
        {
            item.Status = "excluded";
        }
    }

    private async Task ResolveCurseForgeManifestItemAsync(
        ModpackInfo pack,
        ContentItem item,
        string targetMinecraft,
        string targetLoader,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(
            item.ProjectId,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var projectId))
        {
            MarkMissing(item, "CurseForge 项目 ID 无效");
            return;
        }

        var project = await _curseForge.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        item.Name = project.Name.Length > 0 ? project.Name : item.Name;
        item.CurseForgeSlug = project.Slug;
        var category = project.ClassId switch
        {
            6 => "mod",
            12 => "resourcepack",
            6552 => "shaderpack",
            _ => string.Empty,
        };
        if (category.Length == 0)
        {
            item.Category = "other";
            item.Status = "passthrough";
            item.Passthrough = true;
            if (item.OriginalEntry is not null && !ContainsPassthrough(pack, item))
            {
                pack.PassthroughFiles.Add(item.OriginalEntry.DeepClone().AsObject());
            }

            return;
        }

        item.Category = category;
        var strictMinecraft = item.Category == "mod";
        var expectedLoader = strictMinecraft ? targetLoader : string.Empty;
        var target = await _curseForge.FindTargetFileAsync(
            projectId,
            targetMinecraft,
            expectedLoader,
            strictMinecraft,
            cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            MarkMissing(item, "原 CurseForge 项目没有目标版本");
            return;
        }

        await ApplyCurseForgeTargetAsync(item, target, projectId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ResolveModrinthItemAsync(
        ContentItem item,
        string targetMinecraft,
        string targetLoader,
        IReadOnlyDictionary<long, CurseForgeFile> sourceFiles,
        CancellationToken cancellationToken)
    {
        var strictMinecraft = item.Category == "mod";
        var expectedLoader = strictMinecraft ? targetLoader : string.Empty;
        var identityFailures = new List<string>();
        var attemptedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (item.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(
                item.ProjectId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var curseForgeProjectId))
        {
            item.IdentityLocked = true;
            var target = await FindCurseForgeTargetOrNullAsync(
                curseForgeProjectId,
                targetMinecraft,
                expectedLoader,
                strictMinecraft,
                cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                MarkMissing(item, "原 CurseForge 项目没有目标版本");
                return false;
            }

            await ApplyCurseForgeTargetAsync(
                item,
                target,
                curseForgeProjectId,
                cancellationToken).ConfigureAwait(false);
            var project = await TryGetCurseForgeProjectAsync(
                curseForgeProjectId,
                cancellationToken).ConfigureAwait(false);
            if (project is not null)
            {
                item.Name = project.Name.Length > 0 ? project.Name : item.Name;
                item.CurseForgeSlug = project.Slug;
            }

            return true;
        }

        if (item.ProjectId.Length > 0)
        {
            item.IdentityLocked = true;
            attemptedProjects.Add(item.ProjectId);
            var target = await FindModrinthTargetOrNullAsync(
                item.ProjectId,
                targetMinecraft,
                expectedLoader,
                strictMinecraft,
                cancellationToken).ConfigureAwait(false);
            if (target is not null && ApplyModrinthTarget(item, target))
            {
                await EnrichModrinthProjectAsync(item, item.ProjectId, cancellationToken).ConfigureAwait(false);
                return true;
            }

            identityFailures.Add("原 Modrinth 项目没有可用的目标版本");
        }

        if (long.TryParse(
            item.FileId,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var sourceFileId))
        {
            item.IdentityLocked = true;
            if (sourceFiles.TryGetValue(sourceFileId, out var sourceFile)
                && SearchMatcher.CurseForgeFileMatchesSource(item, sourceFile))
            {
                var target = await FindCurseForgeTargetOrNullAsync(
                    sourceFile.ModId,
                    targetMinecraft,
                    expectedLoader,
                    strictMinecraft,
                    cancellationToken).ConfigureAwait(false);
                if (target is not null)
                {
                    await ApplyCurseForgeTargetAsync(
                        item,
                        target,
                        sourceFile.ModId,
                        cancellationToken).ConfigureAwait(false);
                    item.Name = sourceFile.DisplayName.Length > 0
                        ? sourceFile.DisplayName
                        : sourceFile.FileName.Length > 0
                            ? sourceFile.FileName
                            : item.Name;
                    var project = await TryGetCurseForgeProjectAsync(
                        sourceFile.ModId,
                        cancellationToken).ConfigureAwait(false);
                    if (project is not null)
                    {
                        item.CurseForgeSlug = project.Slug;
                    }

                    return true;
                }

                identityFailures.Add("原 CurseForge 项目没有目标版本");
            }
            else
            {
                identityFailures.Add("无法验证原 CurseForge 文件身份");
            }
        }

        foreach (var algorithm in new[] { "sha1", "sha512" })
        {
            if (!item.Hashes.TryGetValue(algorithm, out var hash) || hash.Length == 0)
            {
                continue;
            }

            var sourceVersion = await _modrinth
                .LookupByHashAsync(hash, algorithm, cancellationToken)
                .ConfigureAwait(false);
            var projectId = sourceVersion?.ProjectId ?? string.Empty;
            if (projectId.Length == 0 || !attemptedProjects.Add(projectId))
            {
                continue;
            }

            item.IdentityLocked = true;
            var target = await FindModrinthTargetOrNullAsync(
                projectId,
                targetMinecraft,
                expectedLoader,
                strictMinecraft,
                cancellationToken).ConfigureAwait(false);
            if (target is null || !ApplyModrinthTarget(item, target))
            {
                identityFailures.Add("哈希对应项目没有可用的目标版本");
                continue;
            }

            item.ProjectId = projectId;
            item.Source = "modrinth";
            if (item.OriginalProjectId.Length == 0)
            {
                item.OriginalProjectId = projectId;
                item.OriginalSource = "modrinth";
            }

            await EnrichModrinthProjectAsync(item, projectId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (identityFailures.Count > 0)
        {
            MarkMissing(item, string.Join('；', identityFailures.Distinct()));
            return false;
        }

        var queries = SearchMatcher.ExtractSearchQueries(item.FileName);
        var projectType = item.Category switch
        {
            "resourcepack" => "resourcepack",
            "shaderpack" => "shader",
            _ => "mod",
        };
        var searchResults = new List<ModrinthProject>();
        foreach (var query in queries.Where(query => query.Length >= 2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            searchResults.AddRange(await _modrinth.SearchProjectsAsync(
                query,
                expectedLoader.Length > 0 ? expectedLoader : null,
                projectType,
                cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        var ranked = SearchMatcher.RankCandidates(
            searchResults,
            item.FileName,
            queries.FirstOrDefault() ?? string.Empty,
            project => project.EffectiveId,
            project => project.Title,
            project => project.Slug);
        var compatible = new List<CandidateMatch<ModrinthProject>>();
        var targetVersions = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in ranked.Take(SearchMatcher.MaximumVerifiedCandidates))
        {
            var projectId = match.Candidate.EffectiveId;
            if (projectId.Length == 0)
            {
                continue;
            }

            var target = await FindModrinthTargetOrNullAsync(
                projectId,
                targetMinecraft,
                expectedLoader,
                strictMinecraft,
                cancellationToken).ConfigureAwait(false);
            if (target is null || SearchMatcher.SelectUsablePrimaryFile(target.Files) is null)
            {
                continue;
            }

            compatible.Add(match);
            targetVersions[projectId] = target;
        }

        var selected = SearchMatcher.PickUnambiguous(compatible);
        if (selected is null)
        {
            MarkMissing(item, "没有高置信度的平台身份匹配");
            return false;
        }

        var selectedProject = selected.Candidate;
        var selectedId = selectedProject.EffectiveId;
        if (!targetVersions.TryGetValue(selectedId, out var selectedVersion)
            || !ApplyModrinthTarget(item, selectedVersion))
        {
            MarkMissing(item, "候选项目没有目标版本");
            return false;
        }

        item.ProjectId = selectedId;
        item.Name = selectedProject.Title.Length > 0 ? selectedProject.Title : item.Name;
        item.ModrinthSlug = selectedProject.Slug;
        return true;
    }

    private async Task ResolveCurseForgeFallbackAsync(
        ContentItem item,
        string targetMinecraft,
        string targetLoader,
        CancellationToken cancellationToken)
    {
        var strictMinecraft = item.Category == "mod";
        var expectedLoader = strictMinecraft ? targetLoader : string.Empty;
        var queries = SearchMatcher.GenerateCurseForgeSearchQueries(item.FileName);
        var searchResults = new List<CurseForgeProject>();
        foreach (var query in queries.Where(query => query.Length >= 2))
        {
            searchResults.AddRange(await _curseForge.SearchProjectsAsync(
                query,
                category: item.Category,
                cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        var ranked = SearchMatcher.RankCandidates(
            searchResults,
            item.FileName,
            queries.FirstOrDefault() ?? string.Empty,
            project => project.Id.ToString(CultureInfo.InvariantCulture),
            project => project.Name,
            project => project.Slug);
        var compatible = new List<CandidateMatch<CurseForgeProject>>();
        var targetFiles = new Dictionary<long, CurseForgeFile>();
        foreach (var match in ranked.Take(SearchMatcher.MaximumVerifiedCandidates))
        {
            var target = await FindCurseForgeTargetOrNullAsync(
                match.Candidate.Id,
                targetMinecraft,
                expectedLoader,
                strictMinecraft,
                cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                continue;
            }

            compatible.Add(match);
            targetFiles[match.Candidate.Id] = target;
        }

        var selected = SearchMatcher.PickUnambiguous(compatible);
        if (selected is null
            || !targetFiles.TryGetValue(selected.Candidate.Id, out var targetFile))
        {
            MarkMissing(item, "没有高置信度的 CurseForge 匹配");
            return;
        }

        await ApplyCurseForgeTargetAsync(
            item,
            targetFile,
            selected.Candidate.Id,
            cancellationToken).ConfigureAwait(false);
        item.Name = selected.Candidate.Name.Length > 0 ? selected.Candidate.Name : item.Name;
        item.CurseForgeSlug = selected.Candidate.Slug;
    }

    private async Task ApplyCurseForgeTargetAsync(
        ContentItem item,
        CurseForgeFile target,
        long projectId,
        CancellationToken cancellationToken)
    {
        var downloadUrl = target.DownloadUrl;
        if (downloadUrl.Length == 0 && target.Id > 0)
        {
            // Resolve before mutating the item so a platform failure cannot leave partial state.
            downloadUrl = await _curseForge.GetDownloadUrlAsync(
                projectId,
                target.Id,
                cancellationToken).ConfigureAwait(false);
        }

        item.Status = "found";
        item.Source = "curseforge";
        item.ProjectId = projectId.ToString(CultureInfo.InvariantCulture);
        item.TargetVersionId = string.Empty;
        item.TargetVersionNumber = string.Empty;
        item.TargetFileId = target.Id.ToString(CultureInfo.InvariantCulture);
        item.TargetFileName = target.FileName;
        item.TargetFileSize = target.FileLength;
        item.TargetHashes = SearchMatcher.ExtractCurseForgeHashes(target);
        item.TargetDependencies = (target.Dependencies ?? [])
            .Where(dependency => dependency.ModId > 0)
            .Select(DependencyReference.FromCurseForge)
            .ToList();
        item.DependencyMetadataAvailable = target.Dependencies is not null;
        item.Name = target.DisplayName.Length > 0 ? target.DisplayName : item.Name;
        item.TargetDownloadUrl = downloadUrl;
        if (target.ReleaseType != 1)
        {
            item.Status = "warning";
            item.Note = "仅 Beta/Alpha 版";
        }
    }

    private static bool ApplyModrinthTarget(ContentItem item, ModrinthVersion target)
    {
        item.TargetFileId = string.Empty;
        item.TargetVersionId = target.Id;
        item.TargetVersionNumber = target.VersionNumber;
        item.TargetDependencies = (target.Dependencies ?? [])
            .Select(DependencyReference.FromModrinth)
            .ToList();
        item.DependencyMetadataAvailable = target.Dependencies is not null;
        var primary = SearchMatcher.SelectUsablePrimaryFile(target.Files);
        if (primary is null)
        {
            MarkMissing(item, "目标版本缺少可用主文件");
            return false;
        }

        item.Status = "found";
        item.Source = "modrinth";
        item.TargetFileName = primary.FileName;
        item.TargetDownloadUrl = primary.Url;
        item.TargetFileSize = primary.Size;
        item.TargetHashes = new Dictionary<string, string>(primary.Hashes, StringComparer.OrdinalIgnoreCase);
        if (!target.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "warning";
            item.Note = $"仅 {target.VersionType} 版";
        }

        return true;
    }

    private async Task EnrichModrinthProjectAsync(
        ContentItem item,
        string projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var project = await _modrinth.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            item.Name = project.Title.Length > 0 ? project.Title : item.Name;
            item.ModrinthSlug = project.Slug;
        }
        catch (PlatformApiException)
        {
            // Project metadata is supplementary; the already verified target stays valid.
        }
    }

    private async Task<CurseForgeProject?> TryGetCurseForgeProjectAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _curseForge.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformApiException)
        {
            return null;
        }
    }

    private async Task<ModrinthVersion?> FindModrinthTargetOrNullAsync(
        string projectId,
        string targetMinecraft,
        string expectedLoader,
        bool strictMinecraft,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _modrinth.FindTargetVersionAsync(
                projectId,
                targetMinecraft,
                expectedLoader,
                strictMinecraft,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformNotFoundException)
        {
            return null;
        }
    }

    private async Task<CurseForgeFile?> FindCurseForgeTargetOrNullAsync(
        long projectId,
        string targetMinecraft,
        string expectedLoader,
        bool strictMinecraft,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _curseForge.FindTargetFileAsync(
                projectId,
                targetMinecraft,
                expectedLoader,
                strictMinecraft,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformNotFoundException)
        {
            return null;
        }
    }

    private static void MarkMissing(ContentItem item, string note)
    {
        item.Status = "not_found";
        item.Note = note;
    }

    private static bool ContainsPassthrough(ModpackInfo pack, ContentItem item)
    {
        var projectId = item.ProjectId;
        var fileId = item.FileId;
        return pack.PassthroughFiles.Any(entry =>
            string.Equals(entry["projectID"]?.ToString(), projectId, StringComparison.Ordinal)
            && string.Equals(entry["fileID"]?.ToString(), fileId, StringComparison.Ordinal));
    }
}
