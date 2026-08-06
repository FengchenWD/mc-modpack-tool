namespace McModpackTool.Core.Compatibility;

public sealed class CompatibilityAnalysisCancelledException : OperationCanceledException
{
    public CompatibilityAnalysisCancelledException(CancellationToken cancellationToken)
        : base("Compatibility analysis was cancelled.", cancellationToken)
    {
    }
}

/// <summary>
/// Performs deterministic offline checks over resolved mods, resource packs, and shader packs.
/// It intentionally does not inspect or report settings files or world saves.
/// </summary>
public sealed class CompatibilityAnalyzer
{
    private static readonly HashSet<string> UnavailableStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "not_found", "not-found", "missing", "failed", "unresolved",
    };

    private static readonly HashSet<string> RecognizedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "mod", "mods", "resourcepack", "resourcepacks", "shader", "shaderpack", "shaderpacks",
    };

    public CompatibilityReport Analyze(
        CompatibilityAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckCancelled(cancellationToken);

        var allItems = request.Items?.ToList()
            ?? throw new ArgumentException("Items cannot be null.", nameof(request));
        var contexts = allItems
            .Select((item, index) => new ItemContext(item.OriginalIndex >= 0 ? item.OriginalIndex : index, item))
            .Where(context => !context.Item.Excluded && !IsPassthrough(context.Item))
            .ToList();
        var report = new CompatibilityReport(
            request.SourceMinecraftVersion,
            request.TargetMinecraftVersion,
            request.SourceLoader,
            request.TargetLoader);
        report.SetStat("content_items_checked", contexts.Count);
        report.SetStat("mods_checked", contexts.Count);
        report.SetStat("items_excluded", allItems.Count - contexts.Count);
        report.SetStat("dependency_relations_checked", 0);

        var inventory = OverrideContentClassifier.FromArchivePaths(request.PassthroughPaths, cancellationToken);
        AddOverrideSafetyResults(inventory, report, cancellationToken);
        CheckItems(contexts, inventory.SafeNormalizedPaths, request, report, cancellationToken);
        CheckCancelled(cancellationToken);

        report.AddLimitation(
            "Static analysis cannot inspect mod bytecode, mixins, registries, datapacks, or runtime-only conflicts.");
        report.AddLimitation(
            "Only recognized direct required/incompatible relations and supported version predicates are checked; recursive dependencies and cross-platform identity mapping are not verified.");
        return report;
    }

    public CompatibilityReport Analyze(
        IEnumerable<CompatibilityContentItem> items,
        string sourceMinecraftVersion,
        string targetMinecraftVersion,
        string sourceLoader = "",
        string targetLoader = "",
        string targetFormat = "",
        IEnumerable<string>? passthroughPaths = null,
        string sourceLoaderVersion = "",
        string targetLoaderVersion = "",
        CancellationToken cancellationToken = default) => Analyze(new CompatibilityAnalysisRequest
        {
            Items = items,
            SourceMinecraftVersion = sourceMinecraftVersion,
            TargetMinecraftVersion = targetMinecraftVersion,
            SourceLoader = sourceLoader,
            TargetLoader = targetLoader,
            SourceLoaderVersion = sourceLoaderVersion,
            TargetLoaderVersion = targetLoaderVersion,
            TargetFormat = targetFormat,
            PassthroughPaths = passthroughPaths,
        }, cancellationToken);

    private static void CheckItems(
        IReadOnlyList<ItemContext> contexts,
        IReadOnlySet<string> protectedOverridePaths,
        CompatibilityAnalysisRequest request,
        CompatibilityReport report,
        CancellationToken cancellationToken)
    {
        var activeContexts = contexts
            .Where(context => !context.Item.Disabled && !IsUnavailable(context.Item))
            .ToArray();
        var identities = BuildIdentityIndex(activeContexts, cancellationToken);
        var projectGroups = new Dictionary<string, List<ItemContext>>(StringComparer.OrdinalIgnoreCase);
        var outputGroups = new Dictionary<string, List<ItemContext>>(StringComparer.OrdinalIgnoreCase);
        var metadataItems = 0;
        var metadataCheckableItems = 0;
        var javaMetadataSeen = false;

        foreach (var context in contexts)
        {
            CheckCancelled(cancellationToken);
            var item = context.Item;
            var label = ItemLabel(context);
            var scope = ItemScope(item);
            if (IsUnavailable(item))
            {
                report.AddIssue(
                    "item_not_found",
                    CompatibilitySeverity.Error,
                    "No target artifact was found for this item.",
                    scope: scope,
                    item: label,
                    evidence: Evidence(
                        ("status", item.Status),
                        ("item_index", context.Index)));
            }

            if (request.TargetFormat.Equals("modrinth", StringComparison.OrdinalIgnoreCase) &&
                item.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase) &&
                !item.Disabled && !IsUnavailable(item) && string.IsNullOrWhiteSpace(item.TargetDownloadUrl))
            {
                report.AddIssue(
                    "required_embedded_download_unavailable",
                    CompatibilitySeverity.Error,
                    "A CurseForge fallback item must be embedded in a Modrinth pack, but no download URL is available.",
                    scope: CompatibilityScopes.Output,
                    item: label,
                    evidence: Evidence(("item_index", context.Index)));
            }
            if (request.TargetFormat.Equals("modrinth", StringComparison.OrdinalIgnoreCase) &&
                item.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase) &&
                !item.Disabled && !IsUnavailable(item) && HasScopedEnvironment(item))
            {
                report.AddIssue(
                    "required_embedded_scope_unsupported",
                    CompatibilitySeverity.Error,
                    "A CurseForge fallback item must be embedded, but its Modrinth environment scope cannot be preserved safely.",
                    scope: CompatibilityScopes.Output,
                    item: label,
                    evidence: Evidence(("item_index", context.Index)));
            }

            var projectKey = ProjectKey(item);
            if (projectKey.Length > 0)
            {
                AddGroup(projectGroups, projectKey, context);
            }

            var outputPath = OutputPath(item);
            if (outputPath.Length > 0)
            {
                if (!OverrideContentClassifier.TryNormalizeRelativeArchivePath(
                        outputPath, out var normalizedOutputPath, out var unsafeReason))
                {
                    report.AddIssue(
                        "unsafe_output_path",
                        CompatibilitySeverity.Error,
                        "The target archive path is absolute, invalid on Windows, or escapes its package directory.",
                        scope: CompatibilityScopes.Output,
                        item: label,
                        path: outputPath,
                        evidence: Evidence(
                            ("item_index", context.Index),
                            ("reason", unsafeReason)));
                }
                else
                {
                    if (protectedOverridePaths.Contains(normalizedOutputPath))
                    {
                        report.AddIssue(
                            "override_output_collision",
                            CompatibilitySeverity.Error,
                            "A migrated content item would overwrite an existing passthrough overrides file.",
                            scope: CompatibilityScopes.Output,
                            item: label,
                            path: outputPath,
                            evidence: Evidence(("item_index", context.Index)));
                    }
                    AddGroup(outputGroups, normalizedOutputPath, context);
                }
            }

            foreach (var warning in item.MetadataWarnings)
            {
                report.AddLimitation($"Artifact metadata for '{label}' was incomplete: {warning}");
            }

            var shouldCheckRelations = scope == CompatibilityScopes.Mod && !item.Disabled && !IsUnavailable(item);
            if (!shouldCheckRelations)
            {
                continue;
            }
            metadataCheckableItems++;
            if (item.DependencyMetadataAvailable || item.Relations.Count > 0 || item.ExplicitlyIncompatible)
            {
                metadataItems++;
            }

            if (item.ExplicitlyIncompatible)
            {
                report.AddIssue(
                    "explicitly_incompatible_item",
                    CompatibilitySeverity.Error,
                    "The supplied metadata explicitly marks this item as incompatible.",
                    scope: scope,
                    item: label,
                    evidence: Evidence(("item_index", context.Index)));
            }

            foreach (var relation in item.Relations.Distinct())
            {
                CheckCancelled(cancellationToken);
                if (relation.Kind == CompatibilityRelationKinds.IncompatibleSelf)
                {
                    report.AddIssue(
                        "explicitly_incompatible_item",
                        CompatibilitySeverity.Error,
                        "The supplied metadata explicitly marks this item as incompatible.",
                        scope: scope,
                        item: label,
                        evidence: Evidence(("item_index", context.Index)));
                    continue;
                }
                if (relation.Kind is not CompatibilityRelationKinds.Required and
                    not CompatibilityRelationKinds.Incompatible)
                {
                    continue;
                }
                report.IncrementStat("dependency_relations_checked");
                if (CompatibilityText.NormalizeReference(relation.Reference) == "java")
                {
                    javaMetadataSeen = true;
                    continue;
                }
                EvaluateRelation(context, relation, identities, request, report);
            }
        }

        AddDuplicateIssues(projectGroups, outputGroups, report, cancellationToken);
        if (metadataItems < metadataCheckableItems)
        {
            report.AddLimitation(
                $"Dependency/conflict metadata was absent for {metadataCheckableItems - metadataItems} of {metadataCheckableItems} active resolved items; " +
                "their required dependencies and explicit conflicts cannot be confirmed statically.");
        }
        if (javaMetadataSeen)
        {
            report.AddLimitation("Java runtime requirements declared by mod metadata were not evaluated because the selected runtime version is not part of the migration target.");
        }
    }

    private static void EvaluateRelation(
        ItemContext owner,
        CompatibilityRelation relation,
        IReadOnlyDictionary<string, IReadOnlyList<ItemContext>> identities,
        CompatibilityAnalysisRequest request,
        CompatibilityReport report)
    {
        var reference = CompatibilityText.NormalizeReference(relation.Reference);
        if (reference.Length == 0)
        {
            return;
        }
        var ownerLabel = ItemLabel(owner);
        var exactReference = string.IsNullOrWhiteSpace(relation.ExactReference)
            ? relation.Reference
            : relation.ExactReference;

        if (TryResolveEnvironmentReference(reference, request, out var environment))
        {
            EvaluateEnvironmentRelation(owner, ownerLabel, relation, exactReference, environment, report);
            return;
        }

        var matches = FindMatches(identities, relation);
        if (relation.Kind == CompatibilityRelationKinds.Required && matches.Count == 0)
        {
            report.AddIssue(
                "missing_required_dependency",
                CompatibilitySeverity.Warning,
                $"Required dependency '{reference}' is not present as an active resolved item.",
                scope: CompatibilityScopes.Dependency,
                item: ownerLabel,
                evidence: RelationEvidence(owner, relation, "dependency", reference));
            return;
        }
        if (relation.Kind == CompatibilityRelationKinds.Incompatible && matches.Count == 0)
        {
            return;
        }

        var requirement = relation.VersionRequirement.Trim();
        if (requirement.Length == 0)
        {
            if (relation.Kind == CompatibilityRelationKinds.Incompatible)
            {
                AddExplicitIncompatibility(owner, ownerLabel, relation, reference, matches, report);
            }
            return;
        }

        var evaluations = matches.Select(match => new MatchEvaluation(
            match,
            match.Item.Version,
            VersionRequirement.Evaluate(requirement, match.Item.Version))).ToArray();
        if (relation.Kind == CompatibilityRelationKinds.Required)
        {
            if (evaluations.Any(evaluation => evaluation.Result == VersionRequirementResult.Satisfied))
            {
                return;
            }
            if (evaluations.Any(evaluation => evaluation.Result == VersionRequirementResult.Unknown))
            {
                AddVersionUnverified(owner, ownerLabel, relation, reference, evaluations, report, incompatible: false);
                return;
            }
            report.AddIssue(
                "dependency_version_mismatch",
                CompatibilitySeverity.Error,
                $"Dependency '{reference}' does not satisfy required version '{requirement}'.",
                scope: CompatibilityScopes.Dependency,
                item: ownerLabel,
                evidence: VersionEvidence(owner, relation, reference, evaluations));
            return;
        }

        if (evaluations.Any(evaluation => evaluation.Result == VersionRequirementResult.Satisfied))
        {
            AddExplicitIncompatibility(owner, ownerLabel, relation, reference, matches, report, evaluations);
        }
        else if (evaluations.Any(evaluation => evaluation.Result == VersionRequirementResult.Unknown))
        {
            AddVersionUnverified(owner, ownerLabel, relation, reference, evaluations, report, incompatible: true);
        }
    }

    private static void EvaluateEnvironmentRelation(
        ItemContext owner,
        string ownerLabel,
        CompatibilityRelation relation,
        string exactReference,
        EnvironmentReference environment,
        CompatibilityReport report)
    {
        var reference = CompatibilityText.NormalizeReference(relation.Reference);
        if (!environment.IsPresent)
        {
            if (relation.Kind == CompatibilityRelationKinds.Required)
            {
                report.AddIssue(
                    "loader_dependency_mismatch",
                    CompatibilitySeverity.Error,
                    $"The selected target does not provide required loader component '{reference}'.",
                    scope: CompatibilityScopes.Dependency,
                    item: ownerLabel,
                    evidence: RelationEvidence(owner, relation, "dependency", reference));
            }
            return;
        }

        var requirement = relation.VersionRequirement.Trim();
        if (requirement.Length == 0)
        {
            if (relation.Kind == CompatibilityRelationKinds.Incompatible)
            {
                report.AddIssue(
                    "explicit_incompatibility",
                    CompatibilitySeverity.Error,
                    $"Explicitly incompatible environment component '{reference}' is present.",
                    scope: CompatibilityScopes.Dependency,
                    item: ownerLabel,
                    evidence: RelationEvidence(owner, relation, "incompatible_with", reference));
            }
            return;
        }

        var result = VersionRequirement.Evaluate(requirement, environment.Version);
        if (relation.Kind == CompatibilityRelationKinds.Required && result == VersionRequirementResult.NotSatisfied)
        {
            report.AddIssue(
                environment.Kind == "minecraft" ? "minecraft_version_mismatch" : "loader_version_mismatch",
                CompatibilitySeverity.Error,
                $"Target {environment.Kind} version '{environment.Version}' does not satisfy '{requirement}'.",
                scope: CompatibilityScopes.Dependency,
                item: ownerLabel,
                evidence: EnvironmentVersionEvidence(owner, relation, exactReference, environment));
        }
        else if (relation.Kind == CompatibilityRelationKinds.Incompatible &&
                 result == VersionRequirementResult.Satisfied)
        {
            report.AddIssue(
                "explicit_incompatibility",
                CompatibilitySeverity.Error,
                $"Target {environment.Kind} version '{environment.Version}' matches an incompatible range '{requirement}'.",
                scope: CompatibilityScopes.Dependency,
                item: ownerLabel,
                evidence: EnvironmentVersionEvidence(owner, relation, exactReference, environment));
        }
        else if (result == VersionRequirementResult.Unknown)
        {
            report.AddIssue(
                "dependency_version_unverified",
                CompatibilitySeverity.Warning,
                $"The version rule '{requirement}' for '{reference}' could not be verified.",
                confidence: CompatibilityConfidence.Incomplete,
                scope: CompatibilityScopes.Dependency,
                item: ownerLabel,
                evidence: EnvironmentVersionEvidence(owner, relation, exactReference, environment));
        }
    }

    private static void AddExplicitIncompatibility(
        ItemContext owner,
        string ownerLabel,
        CompatibilityRelation relation,
        string reference,
        IReadOnlyList<ItemContext> matches,
        CompatibilityReport report,
        IReadOnlyList<MatchEvaluation>? evaluations = null)
    {
        var evidence = RelationEvidence(owner, relation, "incompatible_with", reference);
        evidence["incompatible_item_indexes"] = matches.Select(match => match.Index).ToArray();
        if (evaluations is not null)
        {
            evidence["actual_versions"] = evaluations.Select(evaluation => evaluation.Version).ToArray();
            evidence["version_requirement"] = relation.VersionRequirement;
        }
        report.AddIssue(
            "explicit_incompatibility",
            CompatibilitySeverity.Error,
            $"Explicitly incompatible item '{reference}' is present.",
            scope: CompatibilityScopes.Dependency,
            item: ownerLabel,
            evidence: evidence);
    }

    private static void AddVersionUnverified(
        ItemContext owner,
        string ownerLabel,
        CompatibilityRelation relation,
        string reference,
        IReadOnlyList<MatchEvaluation> evaluations,
        CompatibilityReport report,
        bool incompatible)
    {
        report.AddIssue(
            incompatible ? "incompatibility_version_unverified" : "dependency_version_unverified",
            CompatibilitySeverity.Warning,
            incompatible
                ? $"The installed version of '{reference}' could not be checked against incompatible range '{relation.VersionRequirement}'."
                : $"The installed version of '{reference}' could not be checked against required range '{relation.VersionRequirement}'.",
            confidence: CompatibilityConfidence.Incomplete,
            scope: CompatibilityScopes.Dependency,
            item: ownerLabel,
            evidence: VersionEvidence(owner, relation, reference, evaluations));
    }

    private static Dictionary<string, object?> VersionEvidence(
        ItemContext owner,
        CompatibilityRelation relation,
        string reference,
        IReadOnlyList<MatchEvaluation> evaluations)
    {
        var evidence = RelationEvidence(owner, relation, "dependency", reference);
        evidence["version_requirement"] = relation.VersionRequirement;
        evidence["actual_versions"] = evaluations.Select(evaluation => evaluation.Version).ToArray();
        evidence["dependency_item_indexes"] = evaluations.Select(evaluation => evaluation.Context.Index).ToArray();
        return evidence;
    }

    private static Dictionary<string, object?> EnvironmentVersionEvidence(
        ItemContext owner,
        CompatibilityRelation relation,
        string exactReference,
        EnvironmentReference environment)
    {
        var evidence = RelationEvidence(
            owner,
            relation,
            relation.Kind == CompatibilityRelationKinds.Incompatible ? "incompatible_with" : "dependency",
            exactReference);
        evidence["version_requirement"] = relation.VersionRequirement;
        evidence["actual_version"] = environment.Version;
        evidence["environment_kind"] = environment.Kind;
        return evidence;
    }

    private static Dictionary<string, object?> RelationEvidence(
        ItemContext owner,
        CompatibilityRelation relation,
        string relationKey,
        string normalizedReference)
    {
        var exactKey = relationKey == "incompatible_with"
            ? "incompatible_with_exact"
            : "dependency_exact";
        var typeKey = relationKey == "incompatible_with"
            ? "incompatible_reference_type"
            : "dependency_reference_type";
        return Evidence(
            (relationKey, normalizedReference),
            (exactKey, string.IsNullOrWhiteSpace(relation.ExactReference)
                ? relation.Reference
                : relation.ExactReference),
            (typeKey, relation.ReferenceType),
            ("source", relation.Source),
            ("item_index", owner.Index));
    }

    private static IReadOnlyList<ItemContext> FindMatches(
        IReadOnlyDictionary<string, IReadOnlyList<ItemContext>> identities,
        CompatibilityRelation relation)
    {
        var reference = CompatibilityText.NormalizeReference(relation.Reference);
        var source = CompatibilityText.NormalizeReference(relation.Source);
        var type = NormalizeReferenceType(relation.ReferenceType);
        var key = IdentityKey(source, type, reference);
        return identities.TryGetValue(key, out var matches) ? matches : Array.Empty<ItemContext>();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ItemContext>> BuildIdentityIndex(
        IEnumerable<ItemContext> contexts,
        CancellationToken cancellationToken)
    {
        var mutable = new Dictionary<string, List<ItemContext>>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in contexts)
        {
            CheckCancelled(cancellationToken);
            var item = context.Item;
            var source = CompatibilityText.NormalizeReference(item.Source);
            AddIdentity(mutable, context, source, CompatibilityReferenceTypes.ProjectId, item.ProjectId);
            AddIdentity(mutable, context, source, CompatibilityReferenceTypes.VersionId, item.TargetVersionId);
            AddIdentity(mutable, context, source, CompatibilityReferenceTypes.Slug, item.Slug);
            AddIdentity(mutable, context, source, CompatibilityReferenceTypes.Slug, item.CurseForgeSlug);
            AddIdentity(mutable, context, source, CompatibilityReferenceTypes.Slug, item.ModrinthSlug);
            AddIdentity(mutable, context, source, CompatibilityReferenceTypes.Name, item.Name);
            AddFileIdentities(mutable, context, source, item.FileName);
            AddFileIdentities(mutable, context, source, item.TargetFileName);
            foreach (var id in item.ModIds.Concat(item.Aliases))
            {
                AddIdentity(mutable, context, string.Empty, CompatibilityReferenceTypes.ModId, id);
            }
        }
        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ItemContext>)pair.Value.Distinct().ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddIdentity(
        IDictionary<string, List<ItemContext>> identities,
        ItemContext context,
        string source,
        string type,
        string? value)
    {
        var normalized = CompatibilityText.NormalizeReference(value);
        if (normalized.Length == 0)
        {
            return;
        }
        AddGroup(identities, IdentityKey(source, type, normalized), context);
        if (source.Length > 0)
        {
            AddGroup(identities, IdentityKey(string.Empty, type, normalized), context);
        }
    }

    private static void AddFileIdentities(
        IDictionary<string, List<ItemContext>> identities,
        ItemContext context,
        string source,
        string? fileName)
    {
        var normalized = (fileName ?? string.Empty).Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var basename = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        AddIdentity(identities, context, source, CompatibilityReferenceTypes.FileName, basename);
        AddIdentity(identities, context, source, CompatibilityReferenceTypes.FileName, StripKnownSuffixes(basename));
    }

    private static string IdentityKey(string source, string type, string reference) =>
        $"{source}|{NormalizeReferenceType(type)}|{reference}";

    private static string NormalizeReferenceType(string? value)
    {
        var normalized = CompatibilityText.NormalizeReference(value).Replace('-', '_');
        return normalized switch
        {
            "versionid" => CompatibilityReferenceTypes.VersionId,
            "filename" => CompatibilityReferenceTypes.FileName,
            "projectid" => CompatibilityReferenceTypes.ProjectId,
            "modid" => CompatibilityReferenceTypes.ModId,
            "slug" => CompatibilityReferenceTypes.Slug,
            "name" => CompatibilityReferenceTypes.Name,
            _ => normalized.Length == 0 ? CompatibilityReferenceTypes.ProjectId : normalized,
        };
    }

    private static bool TryResolveEnvironmentReference(
        string reference,
        CompatibilityAnalysisRequest request,
        out EnvironmentReference environment)
    {
        var compact = new string(reference.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (compact == "minecraft")
        {
            environment = new EnvironmentReference("minecraft", true, request.TargetMinecraftVersion);
            return true;
        }

        var loader = CompatibilityText.NormalizeLoader(request.TargetLoader);
        var expectedLoader = compact switch
        {
            "fabric" or "fabricloader" => "fabric",
            "quilt" or "quiltloader" => "quilt",
            "forge" => "forge",
            "neoforge" or "neoforged" => "neoforge",
            _ => string.Empty,
        };
        if (expectedLoader.Length > 0)
        {
            environment = new EnvironmentReference(
                "loader",
                string.Equals(loader, expectedLoader, StringComparison.OrdinalIgnoreCase),
                request.TargetLoaderVersion);
            return true;
        }
        environment = default;
        return false;
    }

    private static void AddOverrideSafetyResults(
        OverrideInventory inventory,
        CompatibilityReport report,
        CancellationToken cancellationToken)
    {
        var contentEntries = 0;
        foreach (var entry in inventory.Entries)
        {
            CheckCancelled(cancellationToken);
            if (entry.Kind != OverrideContentKind.Other)
            {
                contentEntries++;
            }
            if (!entry.IsSafe)
            {
                report.AddIssue(
                    "unsafe_override_path",
                    CompatibilitySeverity.Error,
                    "An overrides entry has an unsafe or case-colliding archive path.",
                    scope: CompatibilityScopes.Output,
                    path: entry.OriginalPath,
                    evidence: Evidence(("reason", entry.UnsafeReason)));
            }
        }
        report.SetStat("override_content_items_classified", contentEntries);
    }

    private static void AddDuplicateIssues(
        IReadOnlyDictionary<string, List<ItemContext>> projectGroups,
        IReadOnlyDictionary<string, List<ItemContext>> outputGroups,
        CompatibilityReport report,
        CancellationToken cancellationToken)
    {
        foreach (var (project, group) in projectGroups)
        {
            CheckCancelled(cancellationToken);
            if (group.Count <= 1)
            {
                continue;
            }
            report.AddIssue(
                "duplicate_project",
                CompatibilitySeverity.Warning,
                "The same platform project appears more than once.",
                scope: CompatibilityScopes.Content,
                evidence: Evidence(
                    ("project", project),
                    ("items", group.Select(ItemLabel).ToArray()),
                    ("item_indexes", group.Select(context => context.Index).ToArray())));
        }
        foreach (var (path, group) in outputGroups)
        {
            CheckCancelled(cancellationToken);
            if (group.Count <= 1)
            {
                continue;
            }
            report.AddIssue(
                "duplicate_output_path",
                CompatibilitySeverity.Error,
                "Multiple items resolve to the same case-insensitive archive path.",
                scope: CompatibilityScopes.Output,
                path: path,
                evidence: Evidence(
                    ("items", group.Select(ItemLabel).ToArray()),
                    ("item_indexes", group.Select(context => context.Index).ToArray())));
        }
    }

    private static bool IsPassthrough(CompatibilityContentItem item) => item.Passthrough ||
        (!string.IsNullOrWhiteSpace(item.Category) && !RecognizedCategories.Contains(item.Category));

    private static bool IsUnavailable(CompatibilityContentItem item) => UnavailableStatuses.Contains(item.Status);

    private static bool HasScopedEnvironment(CompatibilityContentItem item)
    {
        if (item.Environment.Count == 0)
        {
            return false;
        }
        var client = item.Environment.GetValueOrDefault("client", "required");
        var server = item.Environment.GetValueOrDefault("server", "required");
        return !client.Equals("required", StringComparison.OrdinalIgnoreCase) ||
               !server.Equals("required", StringComparison.OrdinalIgnoreCase);
    }

    private static string ItemScope(CompatibilityContentItem item) =>
        CompatibilityText.NormalizeReference(item.Category) switch
        {
            "resourcepack" or "resourcepacks" => CompatibilityScopes.ResourcePack,
            "shader" or "shaderpack" or "shaderpacks" => CompatibilityScopes.ShaderPack,
            _ => CompatibilityScopes.Mod,
        };

    private static string ItemLabel(ItemContext context)
    {
        var item = context.Item;
        return FirstNonEmpty(item.Name, item.TargetFileName, item.FileName, item.ProjectId, $"item #{context.Index + 1}");
    }

    private static string ProjectKey(CompatibilityContentItem item)
    {
        var projectId = CompatibilityText.NormalizeReference(item.ProjectId);
        if (projectId.Length == 0)
        {
            return string.Empty;
        }
        var source = CompatibilityText.NormalizeReference(item.Source);
        return source.Length > 0 ? $"{source}:{projectId}" : projectId;
    }

    private static string OutputPath(CompatibilityContentItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.TargetPath))
        {
            return item.TargetPath.Replace('\\', '/');
        }
        var fileName = FirstNonEmpty(item.TargetFileName, item.FileName);
        if (fileName.Length == 0)
        {
            return string.Empty;
        }
        fileName = fileName.Replace('\\', '/');
        if (item.Disabled && !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".disabled";
        }
        var directory = ItemScope(item) switch
        {
            CompatibilityScopes.ResourcePack => "resourcepacks",
            CompatibilityScopes.ShaderPack => "shaderpacks",
            _ => "mods",
        };
        return $"{directory}/{fileName}";
    }

    private static string StripKnownSuffixes(string value)
    {
        foreach (var suffix in new[] { ".jar.disabled", ".disabled", ".jar", ".zip" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return value[..^suffix.Length];
            }
        }
        return value;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static Dictionary<string, object?> Evidence(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static void AddGroup<TKey>(IDictionary<TKey, List<ItemContext>> groups, TKey key, ItemContext context)
        where TKey : notnull
    {
        if (!groups.TryGetValue(key, out var group))
        {
            group = [];
            groups[key] = group;
        }
        group.Add(context);
    }

    private static void CheckCancelled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new CompatibilityAnalysisCancelledException(cancellationToken);
        }
    }

    private sealed record ItemContext(int Index, CompatibilityContentItem Item);
    private sealed record MatchEvaluation(
        ItemContext Context,
        string Version,
        VersionRequirementResult Result);
    private readonly record struct EnvironmentReference(string Kind, bool IsPresent, string Version);
}
