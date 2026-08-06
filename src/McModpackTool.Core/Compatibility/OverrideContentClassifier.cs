using System.Collections.ObjectModel;

namespace McModpackTool.Core.Compatibility;

public enum OverrideContentKind
{
    Mod,
    ResourcePack,
    ShaderPack,
    Other,
}

public sealed record OverrideContentEntry(
    string OriginalPath,
    string NormalizedPath,
    OverrideContentKind Kind,
    bool IsSafe,
    string? UnsafeReason = null);

public sealed record OverrideInventory
{
    public IReadOnlyList<OverrideContentEntry> Entries { get; init; }
        = Array.Empty<OverrideContentEntry>();
    public int SkippedReparsePoints { get; init; }
    public IReadOnlyList<string> ReadErrors { get; init; } = Array.Empty<string>();

    public IReadOnlySet<string> SafeNormalizedPaths => new HashSet<string>(
        Entries.Where(entry => entry.IsSafe).Select(entry => entry.NormalizedPath),
        StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Classifies override paths without opening their content. Configuration files, saves, and all
/// other non-content entries stay <see cref="OverrideContentKind.Other"/> and are never analyzed.
/// </summary>
public static class OverrideContentClassifier
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static OverrideContentKind Classify(string? relativePath)
    {
        var path = (relativePath ?? string.Empty).Replace('\\', '/');
        var separator = path.IndexOf('/');
        var first = (separator >= 0 ? path[..separator] : path).Trim().ToLowerInvariant();
        return first switch
        {
            "mods" => OverrideContentKind.Mod,
            "resourcepack" or "resourcepacks" => OverrideContentKind.ResourcePack,
            "shaderpack" or "shaderpacks" => OverrideContentKind.ShaderPack,
            _ => OverrideContentKind.Other,
        };
    }

    public static OverrideInventory FromArchivePaths(
        IEnumerable<string>? paths,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<OverrideContentEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isSafe = TryNormalizeRelativeArchivePath(path, out var normalized, out var reason);
            if (isSafe && !seen.Add(normalized))
            {
                entries.Add(new OverrideContentEntry(
                    path,
                    normalized,
                    Classify(normalized),
                    false,
                    "The path duplicates another overrides entry when compared case-insensitively."));
                continue;
            }
            entries.Add(new OverrideContentEntry(
                path,
                normalized,
                Classify(normalized.Length > 0 ? normalized : path),
                isSafe,
                reason));
        }
        return new OverrideInventory { Entries = entries };
    }

    public static OverrideInventory ScanDirectory(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Overrides directory does not exist: {root}");
        }

        var paths = new List<string>();
        var errors = new List<string>();
        var pending = new Stack<string>();
        var skippedReparsePoints = 0;
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetRelativePath(root, directory)}: {exception.Message}");
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{Path.GetRelativePath(root, child)}: {exception.Message}");
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedReparsePoints++;
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                    continue;
                }
                paths.Add(Path.GetRelativePath(root, child).Replace('\\', '/'));
            }
        }

        var inventory = FromArchivePaths(paths, cancellationToken);
        return inventory with
        {
            SkippedReparsePoints = skippedReparsePoints,
            ReadErrors = new ReadOnlyCollection<string>(errors),
        };
    }

    public static bool TryNormalizeRelativeArchivePath(
        string? path,
        out string normalized,
        out string? unsafeReason)
    {
        normalized = string.Empty;
        unsafeReason = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            unsafeReason = "The path is empty.";
            return false;
        }
        if (path.Length > 4096)
        {
            unsafeReason = "The path exceeds the safety length limit.";
            return false;
        }
        if (path.Any(character => character == '\0' || char.IsControl(character)))
        {
            unsafeReason = "The path contains a NUL or control character.";
            return false;
        }

        var forward = path.Replace('\\', '/');
        if (forward.StartsWith('/') || (forward.Length >= 2 && char.IsAsciiLetter(forward[0]) && forward[1] == ':'))
        {
            unsafeReason = "Absolute archive paths are not allowed.";
            return false;
        }

        var segments = forward.Split('/');
        if (segments.Length == 0 || segments.Any(segment => segment.Length == 0))
        {
            unsafeReason = "The path contains an empty component.";
            return false;
        }
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                unsafeReason = "Dot components and parent traversal are not allowed.";
                return false;
            }
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                unsafeReason = "Windows strips trailing spaces or periods from path components.";
                return false;
            }
            if (segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
            {
                unsafeReason = "The path contains a Windows-reserved character.";
                return false;
            }
            var deviceStem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
            if (WindowsReservedNames.Contains(deviceStem))
            {
                unsafeReason = "The path contains a Windows-reserved device name.";
                return false;
            }
        }

        normalized = string.Join('/', segments);
        return true;
    }
}
