namespace McModpackTool.Core.Models;

public static class ClientPackFormats
{
    public const string CurseForge = "curseforge";
    public const string Modrinth = "modrinth";
}

public static class ClientContentKinds
{
    public const string Mod = "mod";
    public const string ResourcePack = "resource_pack";
    public const string ShaderPack = "shader_pack";
    public const string World = "world";
    public const string Configuration = "configuration";
    public const string ModData = "mod_data";
    public const string Options = "options";
    public const string ServerList = "server_list";
    public const string Screenshot = "screenshot";
    public const string Structure = "structure";
    public const string Replay = "replay";
    public const string CommandHistory = "command_history";
    public const string Hotbar = "hotbar";
    public const string Other = "other";
}

public enum ClientBuildPhase
{
    MatchingPlatformFiles,
    CopyingOverrides,
    WritingManifest,
    CompressingArchive,
}

public sealed class ClientContentEntry
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Kind { get; set; } = ClientContentKinds.Other;
    public bool IsDirectory { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public bool Selected { get; set; }
    public bool Disabled { get; set; }
}

public sealed class ClientPackSource
{
    public string SourcePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string LoaderType { get; set; } = string.Empty;
    public string LoaderVersion { get; set; } = string.Empty;
    public string ContentRoot { get; set; } = string.Empty;
    public List<ClientContentEntry> Items { get; set; } = [];
}

public sealed class ClientBuildRequest
{
    public required ClientPackSource Source { get; init; }
    public required string Format { get; init; }
    public required string OutputPath { get; init; }
    public IReadOnlyList<ClientContentEntry>? IncludedItems { get; init; }
    public bool Overwrite { get; init; }
}

public sealed class ClientBuildResult
{
    public string OutputPath { get; set; } = string.Empty;
    public int EmbeddedItems { get; set; }
    public int RemoteItems { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> MissingFiles { get; } = [];
    public bool Succeeded => MissingFiles.Count == 0;
}
