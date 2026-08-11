using System.Net;
using System.Text.Json.Serialization;

namespace McModpackTool.Core.Models;

public class PlatformApiException : HttpRequestException
{
    public PlatformApiException(
        string service,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException, statusCode)
    {
        Service = service;
    }

    public string Service { get; }
}

public sealed class PlatformNotFoundException : PlatformApiException
{
    public PlatformNotFoundException(string service, string message)
        : base(service, message, HttpStatusCode.NotFound)
    {
    }
}

public sealed class CurseForgeProject
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("classId")]
    public int ClassId { get; set; }
}

public sealed class CurseForgeFile
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("modId")]
    public long ModId { get; set; }

    [JsonPropertyName("fileFingerprint")]
    public uint FileFingerprint { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("fileLength")]
    public long FileLength { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("releaseType")]
    public int ReleaseType { get; set; } = 1;

    [JsonPropertyName("fileDate")]
    public string FileDate { get; set; } = string.Empty;

    [JsonPropertyName("gameVersions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("hashes")]
    public List<CurseForgeHash> Hashes { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<CurseForgeDependency>? Dependencies { get; set; }
}

public sealed class CurseForgeHash
{
    [JsonPropertyName("algo")]
    public int Algorithm { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public sealed class CurseForgeDependency
{
    [JsonPropertyName("modId")]
    public long ModId { get; set; }

    [JsonPropertyName("relationType")]
    public int RelationType { get; set; }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = string.Empty;

    [JsonPropertyName("client_side")]
    public string ClientSide { get; set; } = string.Empty;

    [JsonPropertyName("server_side")]
    public string ServerSide { get; set; } = string.Empty;

    [JsonIgnore]
    public string EffectiveId => string.IsNullOrWhiteSpace(ProjectId) ? Id : ProjectId;
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = string.Empty;

    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = "release";

    [JsonPropertyName("date_published")]
    public string DatePublished { get; set; } = string.Empty;

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<ModrinthDependency>? Dependencies { get; set; }
}

public sealed class ModrinthFile
{
    [JsonPropertyName("hashes")]
    public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool? Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("dependency_type")]
    public string? DependencyType { get; set; }

    [JsonPropertyName("version_requirement")]
    public string? VersionRequirement { get; set; }
}
