using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

internal static class SampleRegressionTests
{
    public static async Task RunAllAsync()
    {
        string? sampleDirectory = FindSampleDirectory();
        if (sampleDirectory is null)
        {
            Console.WriteLine("SKIP  Sample packs are not present.");
            return;
        }

        string[] names =
        [
            "1.20.1 录制&小游戏 new.zip",
            "1.21.1 录制.mrpack",
            "1.21.1 汀五生存服 纯原版new.zip",
            "1.21.10 录制.zip"
        ];
        int executed = 0;
        foreach (string name in names)
        {
            string source = Path.Combine(sampleDirectory, name);
            Assert(File.Exists(source), $"Expected local regression sample is missing: {source}");
            await ParseAndRoundTripAsync(source);
            executed++;
        }
        Assert(executed == names.Length, $"Expected {names.Length} local samples but executed {executed}.");
        Console.WriteLine($"SAMPLE  Executed {executed} local pack round trips.");
    }

    private static async Task ParseAndRoundTripAsync(string source)
    {
        ModpackInfo pack = await PackParser.ParseAsync(source);
        Assert(pack.FormatType is "curseforge" or "modrinth", $"Unknown format for {source}");
        Assert(pack.MinecraftVersion.Length > 0, $"Missing Minecraft version for {source}");
        Assert(pack.LoaderType.Length > 0, $"Missing loader type for {source}");

        string temporaryRoot = Path.Combine(Path.GetTempPath(), "McModpackToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            string overrides = Path.Combine(temporaryRoot, "source-overrides");
            await PackParser.ExtractOverridesAsync(source, overrides);
            pack.OverridesDirectory = overrides;

            using var curseForge = new CurseForgeClient(string.Empty);
            using var modrinth = new ModrinthClient();
            var resolver = new ContentTargetResolver(curseForge, modrinth);
            TargetResolutionResult resolution = await resolver.ResolveAsync(pack, pack.MinecraftVersion, pack.LoaderType);
            Assert(resolution.Missing == 0, $"Same-environment round trip unexpectedly lost items in {source}");

            string extension = pack.FormatType == "modrinth" ? ".mrpack" : ".zip";
            string output = Path.Combine(temporaryRoot, "roundtrip" + extension);
            string loaderVersion = pack.LoaderVersion.Length > 0 ? pack.LoaderVersion : "0.0.0";
            BuildResult result = pack.FormatType == "modrinth"
                ? await PackBuilder.BuildModrinthAsync(output, pack, pack.MinecraftVersion, pack.LoaderType, loaderVersion, overrides, packName: "Round Trip", overwrite: false)
                : await PackBuilder.BuildCurseForgeAsync(output, pack, pack.MinecraftVersion, pack.LoaderType, loaderVersion, overrides, packName: "Round Trip", overwrite: false);
            Assert(result.MissingFiles.Count == 0, $"Round trip has missing files in {source}");

            ModpackInfo rebuilt = await PackParser.ParseAsync(output);
            Assert(rebuilt.FormatType == pack.FormatType, $"Round trip changed format in {source}");
            Assert(rebuilt.OverridePaths.SetEquals(pack.OverridePaths), $"Round trip changed overrides paths in {source}");
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? FindSampleDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "Python", "样例", "整合包");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
