namespace McModpackTool.Core.Models;

/// <summary>One Java runtime discovered on the local machine.</summary>
public sealed record JavaRuntimeInfo
{
    /// <summary>Absolute path to the Java launcher.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The version text reported by <c>java -version</c>.</summary>
    public required string Version { get; init; }

    /// <summary>The Java feature version (8, 16, 17, 21, ...).</summary>
    public required int MajorVersion { get; init; }

    /// <summary>A short vendor/runtime name when it can be inferred.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>The Java home directory containing the <c>bin</c> folder.</summary>
    public string HomePath
    {
        get
        {
            try
            {
                DirectoryInfo? bin = Directory.GetParent(ExecutablePath);
                return bin is not null && bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    ? bin.Parent?.FullName ?? string.Empty
                    : bin?.FullName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Text suitable for a Java selection control.</summary>
    public string DisplayName
    {
        get
        {
            string vendor = string.IsNullOrWhiteSpace(Vendor) ? "Java" : Vendor.Trim();
            return $"Java {MajorVersion} - {vendor} ({Version}) - {ExecutablePath}";
        }
    }
}

/// <summary>Result of a bounded local Java runtime scan.</summary>
public sealed record JavaRuntimeDiscoveryResult
{
    public required IReadOnlyList<JavaRuntimeInfo> Runtimes { get; init; }
    public required int RecommendedMajorVersion { get; init; }
    public JavaRuntimeInfo? Recommended { get; init; }

    /// <summary>Non-empty when no exact recommended runtime was found.</summary>
    public string Warning { get; init; } = string.Empty;
}
