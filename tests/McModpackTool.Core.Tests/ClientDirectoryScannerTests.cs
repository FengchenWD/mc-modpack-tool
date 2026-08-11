using System.Text.Json.Nodes;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class ClientDirectoryScannerTests
{
    public static async Task RunAllAsync()
    {
        await ClientOnlyContentIsAUsableInstanceAsync();
        await ScansContentGroupsAndDefaultsAsync();
        await ClassifiesCommonMapDataDirectoriesAsync();
        await ReadsVanillaInstanceAsync();
        await RejectsReparsePointsAsync();
    }

    private static async Task ClassifiesCommonMapDataDirectoriesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            await WriteVersionAsync(Path.Combine(root, "1.21.1.json"), "1.21.1");
            string[] mapDataDirectories =
            [
                "xaero",
                "XaeroWaypoints_Multiplayer_Server",
                "XaeroWorldMap_Singleplayer_World",
                "map exports",
                "map_exports",
                "journeymap",
                "voxelmap_cache",
                "antiqueatlas",
                "mapfrontiers",
                "waypoints",
            ];
            foreach (string directory in mapDataDirectories)
            {
                await WriteFileAsync(Path.Combine(root, directory, "map-data.bin"), 1);
            }
            await WriteFileAsync(Path.Combine(root, "XaeroWaypointsBackup", "notes.txt"), 1);

            GameDirectoryDiscovery discovery = await ClientDirectoryScanner.DiscoverAsync(root);
            Assert(!discovery.RequiresInstanceDirectory,
                "A client instance containing only recognized map data was rejected as empty.");
            ClientPackSource source = await ClientDirectoryScanner.ReadAsync(
                root,
                discovery.VersionCandidates.Single());

            foreach (string directory in mapDataDirectories)
            {
                AssertSelected(source, directory, ClientContentKinds.ModData, false);
            }
            AssertSelected(source, "XaeroWaypointsBackup", ClientContentKinds.Other, false);
        });
    }

    private static async Task ClientOnlyContentIsAUsableInstanceAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "resourcepacks"));
            await WriteVersionAsync(Path.Combine(root, "1.21.1.json"), "1.21.1");

            GameDirectoryDiscovery discovery = await ClientDirectoryScanner.DiscoverAsync(root);

            Assert(!discovery.RequiresInstanceDirectory,
                "A client instance containing only resource packs was rejected as an empty .minecraft root.");
            AssertEqual(1, discovery.VersionCandidates.Count,
                "Direct client version metadata was not discovered.");
        });
    }

    private static async Task ScansContentGroupsAndDefaultsAsync()
    {
        await WithTemporaryDirectoryAsync(async temporaryRoot =>
        {
            var versionsRoot = Path.Combine(temporaryRoot, ".minecraft", "versions");
            var root = Path.Combine(versionsRoot, "Quilt Profile");
            await WriteVersionAsync(
                Path.Combine(versionsRoot, "1.20.1", "1.20.1.json"),
                "1.20.1");
            await WriteVersionAsync(
                Path.Combine(root, "Quilt Profile.json"),
                "Quilt Profile",
                inheritsFrom: "1.20.1",
                libraries: ["org.quiltmc:quilt-loader:0.28.1"]);
            await WriteFileAsync(Path.Combine(root, "Quilt Profile.jar"), 13);

            await WriteFileAsync(Path.Combine(root, "mods", "enabled.jar"), 11);
            await WriteFileAsync(Path.Combine(root, "mods", "nested", "disabled.jar.disabled"), 7);
            await File.WriteAllTextAsync(Path.Combine(root, "mods", "README.txt"), "ignored");

            await WriteFileAsync(Path.Combine(root, "config", "main.toml"), 3);
            await WriteFileAsync(Path.Combine(root, "defaultconfigs", "defaults.toml"), 5);
            await WriteFileAsync(Path.Combine(root, "kubejs", "server_scripts", "main.js"), 9);
            await WriteFileAsync(Path.Combine(root, "scripts", "recipe.zs"), 4);

            await WriteFileAsync(Path.Combine(root, "resourcepacks", "Pack.zip"), 17);
            await WriteFileAsync(Path.Combine(root, "resourcepacks", "Folder Pack", "pack.mcmeta"), 2);
            await WriteFileAsync(Path.Combine(root, "resourcepacks", "Folder Pack", "assets", "example.txt"), 6);
            await WriteFileAsync(Path.Combine(root, "shaderpacks", "Shader.zip"), 19);
            await WriteFileAsync(Path.Combine(root, "saves", "My World", "level.dat"), 23);
            await WriteFileAsync(Path.Combine(root, "saves", "loose-file.txt"), 1);

            await WriteFileAsync(Path.Combine(root, "XaeroWaypoints", "waypoints.txt"), 29);
            await WriteFileAsync(Path.Combine(root, "screenshots", "shot.png"), 31);
            await WriteFileAsync(Path.Combine(root, "schematics", "house.litematic"), 37);
            await WriteFileAsync(Path.Combine(root, "replay_recordings", "recording.mcpr"), 41);
            await WriteFileAsync(Path.Combine(root, "UnknownModData", "state.bin"), 43);
            await WriteFileAsync(Path.Combine(root, "custom-state.bin"), 47);
            await WriteFileAsync(Path.Combine(root, "options.txt"), 53);
            await WriteFileAsync(Path.Combine(root, "optionsof.txt"), 59);
            await WriteFileAsync(Path.Combine(root, "servers.dat"), 61);
            await WriteFileAsync(Path.Combine(root, "servers.dat_old"), 67);
            await WriteFileAsync(Path.Combine(root, "command_history.txt"), 71);
            await WriteFileAsync(Path.Combine(root, "hotbar.nbt"), 73);

            await WriteFileAsync(Path.Combine(root, ".fabric", "cache", "remapped.jar"), 79);
            await WriteFileAsync(Path.Combine(root, "downloads", "download.tmp"), 83);
            await WriteFileAsync(Path.Combine(root, "logs", "latest.log"), 89);
            await WriteFileAsync(Path.Combine(root, "PCL", "Setup.ini"), 97);
            await WriteFileAsync(Path.Combine(root, "PCL2", "Setup.ini"), 98);
            await WriteFileAsync(Path.Combine(root, "webcache2", "cache.bin"), 99);
            await WriteFileAsync(Path.Combine(root, "Quilt Profile-natives", "native.dll"), 101);
            await WriteFileAsync(Path.Combine(root, "launcher_profiles.json"), 103);
            await WriteFileAsync(Path.Combine(root, "launcher_accounts_microsoft_store.json"), 104);
            await WriteFileAsync(Path.Combine(root, "launcher_ui_state.json"), 105);
            await WriteFileAsync(Path.Combine(root, "launcher_log.txt"), 106);
            await WriteFileAsync(Path.Combine(root, "launcher_cef_log.txt"), 107);
            await WriteFileAsync(Path.Combine(root, "usercache.json"), 107);

            GameDirectoryDiscovery discovery = await ClientDirectoryScanner.DiscoverAsync(root);
            ServerVersionCandidate candidate = discovery.VersionCandidates.Single();
            ClientPackSource source = await ClientDirectoryScanner.ReadAsync(root, candidate);

            AssertEqual("1.20.1", source.MinecraftVersion,
                "Minecraft version was not resolved through the inherited version JSON.");
            AssertEqual("quilt", source.LoaderType, "Quilt was not accepted by the client scanner.");
            AssertEqual("0.28.1", source.LoaderVersion, "Quilt loader version was not preserved.");

            ClientContentEntry enabled = Find(source, "mods/enabled.jar");
            AssertEqual(ClientContentKinds.Mod, enabled.Kind, "Enabled JAR was not classified as a mod.");
            Assert(enabled.Selected && !enabled.Disabled, "Enabled mod should be selected by default.");
            AssertEqual(11L, enabled.TotalBytes, "Mod file size is wrong.");

            ClientContentEntry disabled = Find(source, "mods/nested/disabled.jar.disabled");
            Assert(disabled.Disabled && !disabled.Selected,
                "Disabled mod should remain visible but unselected.");
            Assert(!source.Items.Any(item => item.RelativePath.Equals("mods/README.txt", StringComparison.OrdinalIgnoreCase)),
                "A non-JAR file inside mods was listed as a mod.");

            AssertSelected(source, "config", ClientContentKinds.Configuration, true);
            AssertSelected(source, "defaultconfigs", ClientContentKinds.Configuration, true);
            AssertSelected(source, "kubejs", ClientContentKinds.Configuration, true);
            AssertSelected(source, "scripts", ClientContentKinds.Configuration, true);
            AssertSelected(source, "resourcepacks/Pack.zip", ClientContentKinds.ResourcePack, true);
            AssertSelected(source, "shaderpacks/Shader.zip", ClientContentKinds.ShaderPack, true);
            AssertSelected(source, "saves/My World", ClientContentKinds.World, false);
            AssertSelected(source, "XaeroWaypoints", ClientContentKinds.ModData, false);
            AssertSelected(source, "options.txt", ClientContentKinds.Options, true);
            AssertSelected(source, "optionsof.txt", ClientContentKinds.Configuration, true);
            AssertSelected(source, "servers.dat", ClientContentKinds.ServerList, false);
            AssertSelected(source, "servers.dat_old", ClientContentKinds.ServerList, false);
            AssertSelected(source, "screenshots", ClientContentKinds.Screenshot, false);
            AssertSelected(source, "schematics", ClientContentKinds.Structure, false);
            AssertSelected(source, "replay_recordings", ClientContentKinds.Replay, false);
            AssertSelected(source, "command_history.txt", ClientContentKinds.CommandHistory, false);
            AssertSelected(source, "hotbar.nbt", ClientContentKinds.Hotbar, false);
            AssertSelected(source, "UnknownModData", ClientContentKinds.Other, false);
            AssertSelected(source, "custom-state.bin", ClientContentKinds.Other, false);

            ClientContentEntry folderPack = Find(source, "resourcepacks/Folder Pack");
            Assert(folderPack.IsDirectory, "A folder resource pack was flattened into files.");
            AssertEqual(2, folderPack.FileCount, "Folder resource pack file count is wrong.");
            AssertEqual(8L, folderPack.TotalBytes, "Folder resource pack recursive size is wrong.");

            string[] excludedPrefixes =
            [
                ".fabric",
                "downloads",
                "logs",
                "PCL",
                "PCL2",
                "webcache2",
                "Quilt Profile-natives",
                "launcher_accounts_microsoft_store.json",
                "launcher_ui_state.json",
                "launcher_log.txt",
                "launcher_cef_log.txt",
                "launcher_profiles.json",
                "usercache.json",
                "Quilt Profile.jar",
                "Quilt Profile.json",
            ];
            foreach (var prefix in excludedPrefixes)
            {
                Assert(!source.Items.Any(item =>
                        item.RelativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        item.RelativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)),
                    $"Runtime, launcher, account, or game metadata path '{prefix}' was exposed for packaging.");
            }
        });
    }

    private static async Task ReadsVanillaInstanceAsync()
    {
        await WithTemporaryDirectoryAsync(async temporaryRoot =>
        {
            string root = Path.Combine(temporaryRoot, "My Vanilla Instance", ".minecraft");
            await WriteVersionAsync(Path.Combine(root, "1.21.1.json"), "1.21.1");
            await WriteFileAsync(Path.Combine(root, "options.txt"), 1);

            GameDirectoryDiscovery discovery = await ClientDirectoryScanner.DiscoverAsync(root);
            ClientPackSource source = await ClientDirectoryScanner.ReadAsync(
                root,
                discovery.VersionCandidates.Single());

            AssertEqual("vanilla", source.LoaderType,
                "A version without a mod loader was not normalized as Vanilla.");
            AssertEqual(string.Empty, source.LoaderVersion,
                "Vanilla should not invent a loader version.");
            AssertEqual("My Vanilla Instance", source.DisplayName,
                "A .minecraft root should use its instance folder as the generated pack name.");
        });
    }

    private static async Task RejectsReparsePointsAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            await WriteVersionAsync(Path.Combine(root, "1.21.1.json"), "1.21.1");
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var external = Path.Combine(Path.GetTempPath(), $"client-directory-link-target-{Guid.NewGuid():N}");
            Directory.CreateDirectory(external);
            try
            {
                try
                {
                    Directory.CreateSymbolicLink(Path.Combine(root, "resourcepacks"), external);
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    return;
                }

                GameDirectoryDiscovery discovery = await ClientDirectoryScanner.DiscoverAsync(root);
                await AssertThrowsAsync<InvalidDataException>(() =>
                    ClientDirectoryScanner.ReadAsync(root, discovery.VersionCandidates.Single()));
            }
            finally
            {
                TryDelete(external);
            }
        });
    }

    private static ClientContentEntry Find(ClientPackSource source, string relativePath) =>
        source.Items.Single(item => item.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    private static void AssertSelected(
        ClientPackSource source,
        string relativePath,
        string kind,
        bool selected)
    {
        ClientContentEntry item = Find(source, relativePath);
        AssertEqual(kind, item.Kind, $"'{relativePath}' has the wrong content kind.");
        AssertEqual(selected, item.Selected, $"'{relativePath}' has the wrong default selection.");
    }

    private static async Task WriteVersionAsync(
        string path,
        string id,
        string inheritsFrom = "",
        IReadOnlyList<string>? libraries = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var libraryNodes = new JsonArray();
        foreach (var library in libraries ?? Array.Empty<string>())
        {
            libraryNodes.Add(new JsonObject { ["name"] = library });
        }
        var metadata = new JsonObject
        {
            ["id"] = id,
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["libraries"] = libraryNodes,
        };
        if (inheritsFrom.Length > 0)
        {
            metadata["inheritsFrom"] = inheritsFrom;
        }
        await File.WriteAllTextAsync(path, metadata.ToJsonString());
    }

    private static async Task WriteFileAsync(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[length]);
    }

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> operation)
    {
        var root = Path.Combine(Path.GetTempPath(), $"client-directory-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await operation(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test cleanup should not hide the original failure.
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }
}
