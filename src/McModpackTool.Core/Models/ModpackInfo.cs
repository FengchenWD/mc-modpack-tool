using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace McModpackTool.Core.Models;

public sealed class ModpackInfo
{
    public string FormatType { get; set; } = string.Empty;

    public string MinecraftVersion { get; set; } = string.Empty;

    public string LoaderType { get; set; } = string.Empty;

    public string LoaderVersion { get; set; } = string.Empty;

    public List<ContentItem> Items { get; set; } = [];

    /// <summary>
    /// Compatibility alias for the Python model and archive code that calls all content "mods".
    /// </summary>
    [JsonIgnore]
    public List<ContentItem> Mods
    {
        get => Items;
        set => Items = value ?? [];
    }

    public string OverridesDirectory { get; set; } = string.Empty;

    public HashSet<string> OverridePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<JsonObject> PassthroughFiles { get; set; } = [];

    public JsonObject RawData { get; set; } = [];
}

public sealed class BuildResult
{
    public List<string> MissingFiles { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public bool Succeeded => MissingFiles.Count == 0;
}
