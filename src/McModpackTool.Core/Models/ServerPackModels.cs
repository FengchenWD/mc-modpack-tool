namespace McModpackTool.Core.Models;

public static class ServerInputKinds
{
    public const string Directory = "directory";
    public const string CurseForge = "curseforge";
    public const string Modrinth = "modrinth";
}

public static class ServerModOrigins
{
    public const string Manifest = "manifest";
    public const string Local = "local";
}

public static class ServerSupportKinds
{
    public const string Recommended = "recommended";
    public const string Optional = "optional";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";
}

public enum ServerBuildPhase
{
    DownloadingCore,
    CopyingMods,
    DownloadingMods,
    CopyingConfiguration,
    CopyingWorld,
    WritingLaunchFiles,
    CompressingArchive,
}

public sealed class ServerModEntry
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Origin { get; set; } = ServerModOrigins.Local;
    public string ServerSupport { get; set; } = ServerSupportKinds.Unknown;
    public string SupportReason { get; set; } = string.Empty;
    public List<string> JavaVersionRequirements { get; } = [];
    public bool Selected { get; set; } = true;
    public bool Disabled { get; set; }
    public ContentItem? ContentItem { get; set; }
}

public sealed class ServerWorldEntry
{
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
}

public sealed class ServerVersionCandidate
{
    public string Id { get; set; } = string.Empty;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string LoaderType { get; set; } = string.Empty;
    public string LoaderVersion { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(LoaderType)
        ? $"{Id} - Minecraft {MinecraftVersion}"
        : $"{Id} - Minecraft {MinecraftVersion} / {LoaderType} {LoaderVersion}";
}

public sealed class ServerPackSource
{
    public string InputKind { get; set; } = ServerInputKinds.Directory;
    public string SourcePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string LoaderType { get; set; } = string.Empty;
    public string LoaderVersion { get; set; } = string.Empty;
    public string ContentRoot { get; set; } = string.Empty;
    public string TemporaryRoot { get; set; } = string.Empty;
    public ModpackInfo? ManifestPack { get; set; }
    public List<ServerModEntry> Mods { get; set; } = [];
    public List<ServerWorldEntry> Worlds { get; set; } = [];
    public Dictionary<string, string> OptionalDirectories { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GameDirectoryDiscovery
{
    public string ContentRoot { get; set; } = string.Empty;
    public bool RequiresInstanceDirectory { get; set; }
    public List<ServerVersionCandidate> VersionCandidates { get; set; } = [];
}

public sealed class ServerBuildRequest
{
    public required ServerPackSource Source { get; init; }
    public required string CoreId { get; init; }
    public required string OutputPath { get; init; }
    public bool IncludeConfig { get; init; }
    public IReadOnlySet<string> IncludedOptionalDirectories { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public ServerWorldEntry? World { get; init; }
    public bool EulaAccepted { get; init; }
    public bool Overwrite { get; init; }
}

public sealed class ServerBuildResult
{
    public List<string> Warnings { get; } = [];
    public List<string> MissingFiles { get; } = [];
    public bool Succeeded => MissingFiles.Count == 0;
}
