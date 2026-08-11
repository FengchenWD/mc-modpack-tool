using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class JavaRuntimeTests
{
    public static async Task RunAllAsync()
    {
        RecommendedVersionsMatchMinecraftRequirements();
        ModRequirementsRaiseTheRecommendedVersion();
        SelectionPrefersExactThenNearestRuntime();
        await DiscoveryReturnsOnlyProbedExecutablesAsync();
    }

    private static void RecommendedVersionsMatchMinecraftRequirements()
    {
        Equal(8, JavaRuntimeService.RecommendedMajorVersion("1.12.2"), "Minecraft 1.12.2 should use Java 8.");
        Equal(8, JavaRuntimeService.RecommendedMajorVersion("1.16.5"), "Minecraft 1.16.5 should use Java 8.");
        Equal(16, JavaRuntimeService.RecommendedMajorVersion("1.17.1"), "Minecraft 1.17.1 should use Java 16.");
        Equal(17, JavaRuntimeService.RecommendedMajorVersion("1.20.4"), "Minecraft 1.20.4 should use Java 17.");
        Equal(21, JavaRuntimeService.RecommendedMajorVersion("1.20.5"), "Minecraft 1.20.5 should use Java 21.");
        Equal(21, JavaRuntimeService.RecommendedMajorVersion("1.21.1"), "Minecraft 1.21.1 should use Java 21.");
    }

    private static void SelectionPrefersExactThenNearestRuntime()
    {
        JavaRuntimeInfo java8 = Runtime("C:/java8", 8);
        JavaRuntimeInfo java13 = Runtime("C:/java13", 13);
        JavaRuntimeInfo java17 = Runtime("C:/java17", 17);
        JavaRuntimeInfo java21 = Runtime("C:/java21", 21);

        Equal(java17, JavaRuntimeService.SelectBest([java8, java17, java21], 17),
            "An exact Java major version should be preferred.");
        Equal(java13, JavaRuntimeService.SelectBest([java13, java21], 17),
            "An older runtime should win an equal-distance tie.");
    }

    private static void ModRequirementsRaiseTheRecommendedVersion()
    {
        Equal(21, JavaRuntimeService.RecommendedMajorVersion("1.20.4", [">=21"]),
            "A selected mod's Java requirement did not raise the Minecraft baseline.");
        Equal(21, JavaRuntimeService.RecommendedMajorVersion("1.20.4", [">=18", ">=21 <22"]),
            "Multiple Java requirements were not resolved to their lowest common major version.");
        Equal(17, JavaRuntimeService.RecommendedMajorVersion("1.20.4", ["*"]),
            "An unrestricted Java predicate changed the Minecraft baseline.");
    }

    private static async Task DiscoveryReturnsOnlyProbedExecutablesAsync()
    {
        var service = new JavaRuntimeService(TimeSpan.FromSeconds(2));
        JavaRuntimeDiscoveryResult result = await service.DiscoverAsync("1.12.2");

        Equal(8, result.RecommendedMajorVersion, "Discovery returned the wrong recommended Java version.");
        if (result.Runtimes.Any(runtime => runtime.MajorVersion <= 0 || !File.Exists(runtime.ExecutablePath)))
        {
            throw new InvalidOperationException("Discovery returned an invalid or unprobed Java executable.");
        }
    }

    private static JavaRuntimeInfo Runtime(string path, int major) => new()
    {
        ExecutablePath = path,
        Version = major == 8 ? "1.8.0_401" : $"{major}.0.1",
        MajorVersion = major,
    };

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }
}
