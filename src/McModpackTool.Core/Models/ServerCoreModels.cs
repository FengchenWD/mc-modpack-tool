namespace McModpackTool.Core.Models;

public static class ServerCoreIds
{
    public const string Vanilla = "vanilla";
    public const string Fabric = "fabric";
    public const string Cardboard = "cardboard";
    public const string Forge = "forge";
    public const string NeoForge = "neoforge";
    public const string Mohist = "mohist";
    public const string CatServer = "catserver";
}

public enum ServerCoreInstallStrategy
{
    DirectFiles,
    JavaInstaller,
}

public enum ServerCoreArtifactRole
{
    ServerJar,
    Mod,
    Installer,
}

public sealed record ServerCoreQuery
{
    public required string MinecraftVersion { get; init; }

    /// <summary>The source loader type. The catalog never substitutes a different mod loader.</summary>
    public required string LoaderType { get; init; }

    /// <summary>The requested loader build, used when an official core exposes exact builds.</summary>
    public string LoaderVersion { get; init; } = string.Empty;
}

public sealed record ServerCoreArtifact
{
    public required ServerCoreArtifactRole Role { get; init; }
    public required string DownloadUrl { get; init; }
    public required string RelativePath { get; init; }
    public long Size { get; init; }
    public IReadOnlyDictionary<string, string> Hashes { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool DeleteAfterInstall { get; init; }
}

public sealed record ServerCoreJavaInstaller
{
    public required string ArtifactRelativePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = ["--installServer"];
}

public sealed record ServerCoreOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CoreVersion { get; init; }
    public required string MinecraftVersion { get; init; }

    /// <summary>The loader type retained from the source. Vanilla is the only loader-neutral option.</summary>
    public required string LoaderType { get; init; }

    /// <summary>The exact loader embedded or selected by this core when the provider publishes it.</summary>
    public string LoaderVersion { get; init; } = string.Empty;
    public required ServerCoreInstallStrategy InstallStrategy { get; init; }
    public required IReadOnlyList<ServerCoreArtifact> Artifacts { get; init; }
    public ServerCoreJavaInstaller? JavaInstaller { get; init; }
}

public sealed record ServerCoreUnavailable
{
    public required string CoreId { get; init; }
    public required string Reason { get; init; }
}

public sealed record ServerCoreCatalogResult
{
    public required IReadOnlyList<ServerCoreOption> Options { get; init; }
    public required IReadOnlyList<ServerCoreUnavailable> Unavailable { get; init; }
}

public sealed record ServerCoreInstallRequest
{
    public required ServerCoreOption Option { get; init; }
    public required string DestinationDirectory { get; init; }

    /// <summary>Required only for <see cref="ServerCoreInstallStrategy.JavaInstaller"/>.</summary>
    public string JavaExecutable { get; init; } = string.Empty;
}

public sealed record ServerCoreInstallResult
{
    public required IReadOnlyList<string> InstalledFiles { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>A Windows batch command that starts the installed core. Empty when installation failed.</summary>
    public string LaunchCommand { get; init; } = string.Empty;
    public bool Succeeded => Errors.Count == 0 && LaunchCommand.Length > 0;
}

public interface IServerCoreJavaRunner
{
    Task<int> RunAsync(
        string javaExecutable,
        string installerPath,
        IReadOnlyList<string> installerArguments,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
