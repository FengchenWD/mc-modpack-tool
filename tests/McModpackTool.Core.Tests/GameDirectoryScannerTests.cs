using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class GameDirectoryScannerTests
{
    public static async Task RunAllAsync()
    {
        await DoesNotTreatVersionsAsInstanceContentAsync();
        await ReadsIsolatedFabricInstanceAsync();
        await ReadsOtherLoaderCoordinatesAsync();
        await ReturnsMultipleRootVersionCandidatesAsync();
    }

    private static async Task DoesNotTreatVersionsAsInstanceContentAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            await WriteVersionAsync(
                Path.Combine(root, "versions", "1.21.1", "1.21.1.json"),
                "1.21.1");

            GameDirectoryDiscovery discovery = await GameDirectoryScanner.DiscoverAsync(root);

            Assert(discovery.RequiresInstanceDirectory,
                "A versions child must not make the selected .minecraft root an instance content root.");
            AssertEqual(1, discovery.VersionCandidates.Count,
                "Version metadata should remain available for diagnosis without scanning its content.");
        });
    }

    private static async Task ReadsIsolatedFabricInstanceAsync()
    {
        await WithTemporaryDirectoryAsync(async temporaryRoot =>
        {
            var versions = Path.Combine(temporaryRoot, ".minecraft", "versions");
            var instance = Path.Combine(versions, "Fabric Profile");
            await WriteVersionAsync(
                Path.Combine(versions, "1.20.1", "1.20.1.json"),
                "1.20.1");
            await WriteVersionAsync(
                Path.Combine(instance, "Fabric Profile.json"),
                "Fabric Profile",
                inheritsFrom: "1.20.1",
                libraries: ["net.fabricmc:fabric-loader:0.15.11"]);

            var mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(Path.Combine(mods, "nested"));
            await CreateFabricModAsync(
                Path.Combine(mods, "nested", "universal.jar"),
                "universal",
                "*");
            await CreateFabricModAsync(
                Path.Combine(mods, "client-only.jar"),
                "client_only",
                "client");
            await CreateFabricModAsync(
                Path.Combine(mods, "off.jar.disabled"),
                "disabled",
                "*");
            await File.WriteAllTextAsync(Path.Combine(mods, "README.txt"), "ignored");

            foreach (var name in new[] { "config", "defaultconfigs", "kubejs", "scripts" })
            {
                var directory = Path.Combine(instance, name);
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "example.txt"), name);
            }

            var world = Path.Combine(instance, "saves", "Example World");
            Directory.CreateDirectory(world);
            await File.WriteAllBytesAsync(Path.Combine(world, "level.dat"), [1, 2, 3]);
            var incompleteWorld = Path.Combine(instance, "saves", "Not A World");
            Directory.CreateDirectory(incompleteWorld);
            await File.WriteAllTextAsync(Path.Combine(incompleteWorld, "notes.txt"), "ignored");

            GameDirectoryDiscovery discovery = await GameDirectoryScanner.DiscoverAsync(instance);

            Assert(!discovery.RequiresInstanceDirectory, "An isolated instance with mods was rejected.");
            AssertEqual(1, discovery.VersionCandidates.Count,
                "Selecting versions/<id> must not expose sibling versions as selectable instances.");
            ServerVersionCandidate candidate = discovery.VersionCandidates.Single();
            AssertEqual("1.20.1", candidate.MinecraftVersion,
                "Minecraft version was not resolved through inheritsFrom.");
            AssertEqual("fabric", candidate.LoaderType, "Fabric was not read from the Maven coordinate.");
            AssertEqual("0.15.11", candidate.LoaderVersion,
                "Fabric loader version was not read from the Maven coordinate.");

            ServerPackSource source = await GameDirectoryScanner.ReadAsync(instance, candidate);

            AssertEqual(3, source.Mods.Count, "Recursive JAR discovery returned the wrong count.");
            ServerModEntry universal = source.Mods.Single(mod => mod.Name == "universal.jar");
            AssertEqual("nested/universal.jar", universal.RelativePath,
                "Nested mod paths were not preserved relative to mods.");
            Assert(Path.IsPathFullyQualified(universal.SourcePath), "Mod source path is not absolute.");
            AssertEqual(ServerSupportKinds.Recommended, universal.ServerSupport,
                "Universal Fabric mod was not recommended for the server.");
            Assert(universal.Selected, "Universal Fabric mod should be selected by default.");

            ServerModEntry clientOnly = source.Mods.Single(mod => mod.Name == "client-only.jar");
            AssertEqual(ServerSupportKinds.Unsupported, clientOnly.ServerSupport,
                "Client-only Fabric metadata was ignored.");
            Assert(!clientOnly.Selected, "Client-only mod should not be selected by default.");

            ServerModEntry disabled = source.Mods.Single(mod => mod.Name == "off.jar.disabled");
            Assert(disabled.Disabled && !disabled.Selected,
                "A .jar.disabled file must be shown as disabled and unselected.");
            Assert(!source.Mods.Any(mod => mod.Name == "README.txt"), "Non-JAR files became mods.");
            AssertEqual(4, source.OptionalDirectories.Count,
                "Server-relevant optional directories were not recorded.");
            AssertEqual(1, source.Worlds.Count, "A directory without level.dat became a world.");
            AssertEqual("Example World", source.Worlds[0].Name, "World name was not preserved.");
        });
    }

    private static async Task ReturnsMultipleRootVersionCandidatesAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            await WriteVersionAsync(Path.Combine(root, "versions", "1.20.1", "1.20.1.json"), "1.20.1");
            await WriteVersionAsync(Path.Combine(root, "versions", "1.21.1", "1.21.1.json"), "1.21.1");

            GameDirectoryDiscovery discovery = await GameDirectoryScanner.DiscoverAsync(root);

            Assert(!discovery.RequiresInstanceDirectory, "A root-level config directory was not recognized.");
            AssertEqual(2, discovery.VersionCandidates.Count,
                "Multiple usable version candidates must be returned for UI selection.");
        });
    }

    private static async Task ReadsOtherLoaderCoordinatesAsync()
    {
        var loaders = new[]
        {
            (Name: "Forge", Coordinate: "net.minecraftforge:forge:1.20.1-47.3.22", Type: "forge", Version: "47.3.22"),
            (Name: "NeoForge", Coordinate: "net.neoforged:neoforge:21.1.172", Type: "neoforge", Version: "21.1.172"),
            (Name: "Quilt", Coordinate: "org.quiltmc:quilt-loader:0.28.1", Type: "quilt", Version: "0.28.1"),
        };
        foreach (var expected in loaders)
        {
            await WithTemporaryDirectoryAsync(async root =>
            {
                var versions = Path.Combine(root, "versions");
                var instance = Path.Combine(versions, expected.Name);
                Directory.CreateDirectory(Path.Combine(instance, "mods"));
                await WriteVersionAsync(
                    Path.Combine(versions, "1.20.1", "1.20.1.json"),
                    "1.20.1");
                await WriteVersionAsync(
                    Path.Combine(instance, expected.Name + ".json"),
                    expected.Name,
                    inheritsFrom: "1.20.1",
                    libraries: [expected.Coordinate]);

                ServerVersionCandidate candidate = (await GameDirectoryScanner.DiscoverAsync(instance))
                    .VersionCandidates.Single();

                AssertEqual(expected.Type, candidate.LoaderType,
                    $"{expected.Name} loader type was not normalized.");
                AssertEqual(expected.Version, candidate.LoaderVersion,
                    $"{expected.Name} loader version was not parsed.");
                if (expected.Type == "quilt")
                {
                    await AssertThrowsAsync<InvalidDataException>(() =>
                        GameDirectoryScanner.ReadAsync(instance, candidate));
                }
            });
        }
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

    private static async Task CreateFabricModAsync(
        string path,
        string id,
        string environment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        var entry = archive.CreateEntry("fabric.mod.json");
        await using var target = entry.Open();
        var metadata = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["version"] = "1.0.0",
            ["environment"] = environment,
        };
        var payload = Encoding.UTF8.GetBytes(metadata.ToJsonString());
        await target.WriteAsync(payload);
    }

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> operation)
    {
        var root = Path.Combine(Path.GetTempPath(), $"game-directory-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await operation(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup should not hide the original failure.
            }
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
