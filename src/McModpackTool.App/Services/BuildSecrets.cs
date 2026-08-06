namespace McModpackTool.App.Services;

/// <summary>
/// Release builds may add a gitignored BuildSecrets.Local.cs that implements ResolveEmbedded.
/// CURSEFORGE_API_KEY always has priority inside CurseForgeClient.
/// </summary>
internal static partial class BuildSecrets
{
    public static string CurseForgeApiKey
    {
        get
        {
            string value = string.Empty;
            ResolveEmbedded(ref value);
            return value;
        }
    }

    static partial void ResolveEmbedded(ref string value);
}
