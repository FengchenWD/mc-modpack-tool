using McModpackTool.Core.Compatibility;

namespace McModpackTool.Core.Tests;

internal static class CompatibilityTests
{
    public static Task RunAllAsync()
    {
        DetectsIrisSodiumVersionConflict();
        ReportsMissingDependencyWithoutMutatingItems();
        ReportsUnavailableDisabledAndOptionalItemsAsErrors();
        LoaderDependencyMismatchIsBlockingUntilOwnerExcluded();
        IgnoresConfigurationAndWorldPaths();
        DetectsDuplicateOutputs();
        return Task.CompletedTask;
    }

    private static void DetectsIrisSodiumVersionConflict()
    {
        var iris = new CompatibilityContentItem
        {
            OriginalIndex = 0,
            Name = "Iris",
            Source = "modrinth",
            ProjectId = "iris",
            Category = "mod",
            Status = "found",
            Version = "1.8.8+mc1.21.1",
            TargetFileName = "iris-fabric-1.8.8+mc1.21.1.jar",
            ModIds = ["iris"],
            DependencyMetadataAvailable = true,
            Relations =
            [
                new CompatibilityRelation
                {
                    Kind = CompatibilityRelationKinds.Required,
                    Reference = "sodium",
                    ExactReference = "sodium",
                    ReferenceType = CompatibilityReferenceTypes.ModId,
                    VersionRequirement = ">=0.6.0 <0.7.0"
                }
            ]
        };
        var sodium = new CompatibilityContentItem
        {
            OriginalIndex = 1,
            Name = "Sodium",
            Source = "modrinth",
            ProjectId = "sodium",
            Category = "mod",
            Status = "found",
            Version = "0.8.12+mc1.21.1",
            TargetFileName = "sodium-fabric-0.8.12+mc1.21.1.jar",
            ModIds = ["sodium"],
            DependencyMetadataAvailable = true,
            Relations =
            [
                new CompatibilityRelation
                {
                    Kind = CompatibilityRelationKinds.Incompatible,
                    Reference = "iris",
                    ExactReference = "iris",
                    ReferenceType = CompatibilityReferenceTypes.ModId,
                    VersionRequirement = "<1.8.13"
                }
            ]
        };
        var report = Analyze([iris, sodium]);
        Assert(report.Issues.Any(issue => issue.Code == "dependency_version_mismatch" && issue.Item == "Iris"), "Iris must reject Sodium outside 0.6.x.");
        Assert(report.Issues.Any(issue => issue.Code == "explicit_incompatibility" && issue.Item == "Sodium"), "Sodium must reject Iris below 1.8.13.");
    }

    private static void ReportsMissingDependencyWithoutMutatingItems()
    {
        var item = new CompatibilityContentItem
        {
            Name = "Example",
            Category = "mod",
            Status = "found",
            TargetFileName = "example.jar",
            Relations =
            [
                new CompatibilityRelation
                {
                    Kind = CompatibilityRelationKinds.Required,
                    Reference = "missing-library",
                    ReferenceType = CompatibilityReferenceTypes.ModId
                }
            ]
        };
        var report = Analyze([item]);
        CompatibilityIssue issue = report.Issues.Single(value => value.Code == "missing_required_dependency");
        Assert(issue.Severity == CompatibilitySeverity.Warning, "Missing dependency is a notice, not an automatic list edit.");
    }

    private static void ReportsUnavailableDisabledAndOptionalItemsAsErrors()
    {
        var disabled = new CompatibilityContentItem
        {
            Name = "Disabled Mod",
            Category = "mod",
            Status = "not_found",
            Disabled = true,
        };
        var optional = new CompatibilityContentItem
        {
            Name = "Optional Mod",
            Category = "mod",
            Status = "not_found",
            Required = false,
        };

        CompatibilityReport report = Analyze([disabled, optional]);
        CompatibilityIssue[] issues = report.Issues
            .Where(issue => issue.Code == "item_not_found")
            .ToArray();

        Assert(issues.Length == 2, "Disabled and optional unresolved items must both be reported.");
        Assert(issues.All(issue => issue.Severity == CompatibilitySeverity.Error),
            "Every unresolved item must block export regardless of disabled/required metadata.");
        Assert(report.HasErrors, "Unresolved disabled or optional items must keep the report blocking.");
    }

    private static void LoaderDependencyMismatchIsBlockingUntilOwnerExcluded()
    {
        var forgeOnly = new CompatibilityContentItem
        {
            Name = "Forge-only Mod",
            Category = "mod",
            Status = "found",
            TargetFileName = "forge-only.jar",
            Relations =
            [
                new CompatibilityRelation
                {
                    Kind = CompatibilityRelationKinds.Required,
                    Reference = "forge",
                    ExactReference = "forge",
                    ReferenceType = CompatibilityReferenceTypes.ModId,
                },
            ],
        };

        CompatibilityReport blockingReport = Analyze([forgeOnly]);
        CompatibilityIssue issue = blockingReport.Issues.Single(value =>
            value.Code == "loader_dependency_mismatch");
        Assert(issue.Severity == CompatibilitySeverity.Error,
            "A missing required loader component must be a blocking error.");
        Assert(blockingReport.HasErrors, "A loader dependency mismatch must block export.");

        CompatibilityReport excludedReport = Analyze([forgeOnly with { Excluded = true }]);
        Assert(!excludedReport.Issues.Any(value => value.Code == "loader_dependency_mismatch"),
            "Excluding the owning mod must remove its loader dependency mismatch.");
        Assert(!excludedReport.HasErrors, "An excluded mismatching mod must no longer block export.");
        Assert(excludedReport.Stats.GetValueOrDefault("items_excluded") == 1,
            "The analyzer must record the excluded owner.");
    }

    private static void IgnoresConfigurationAndWorldPaths()
    {
        var report = new CompatibilityAnalyzer().Analyze(new CompatibilityAnalysisRequest
        {
            Items = Array.Empty<CompatibilityContentItem>(),
            SourceMinecraftVersion = "1.21.1",
            TargetMinecraftVersion = "1.21.2",
            SourceLoader = "fabric",
            TargetLoader = "fabric",
            PassthroughPaths = ["config/example.toml", "saves/Test/level.dat", "options.txt"]
        });
        Assert(report.Issues.Count == 0, "Configuration and world files must pass through without compatibility reports.");
    }

    private static void DetectsDuplicateOutputs()
    {
        var one = new CompatibilityContentItem { Name = "One", Category = "mod", Status = "found", TargetFileName = "same.jar" };
        var two = new CompatibilityContentItem { Name = "Two", Category = "mod", Status = "found", TargetFileName = "SAME.jar" };
        var report = Analyze([one, two]);
        Assert(report.Issues.Any(issue => issue.Code == "duplicate_output_path"), "Output collisions must be blocking.");
    }

    private static CompatibilityReport Analyze(IEnumerable<CompatibilityContentItem> items) =>
        new CompatibilityAnalyzer().Analyze(new CompatibilityAnalysisRequest
        {
            Items = items,
            SourceMinecraftVersion = "1.21.1",
            TargetMinecraftVersion = "1.21.1",
            SourceLoader = "fabric",
            TargetLoader = "fabric",
            TargetLoaderVersion = "0.16.14"
        });

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
