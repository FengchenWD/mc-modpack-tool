using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.App.Views;

public partial class MigrationView
{
    private async void Analyze_Click(object sender, RoutedEventArgs e) => await RunAnalysisAsync(resolveTargets: true, promptOnErrors: true);

    private async Task RunAnalysisAsync(bool resolveTargets, bool promptOnErrors)
    {
        if (!EnsureIdle()) return;
        AnalysisInputs inputs = CaptureAnalysisInputs();
        if (_pack is null || !PathsEqual(inputs.InputPath, _parsedInputPath))
        {
            MessageBox.Show(App.Localization["dialog.read_first"], App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TargetIsComplete(inputs))
        {
            MessageBox.Show(App.Localization["dialog.target_required"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (promptOnErrors)
            _shownDependencyWarnings.Clear();

        InvalidateAnalysis();
        _operationCts = new CancellationTokenSource();
        var cancellationToken = _operationCts.Token;
        try
        {
            if (resolveTargets)
            {
                SetWorking(true, "status.searching", indeterminate: false);
                int total = _pack.Items.Count(item => !item.Excluded && !item.Passthrough);
                OperationProgress.Maximum = Math.Max(1, total);
                OperationProgress.Value = 0;
                var progress = new Progress<int>(value => OperationProgress.Value = value);
                Log("INFO", App.Localization.Translate("log.analysis_start", inputs.TargetMinecraft, inputs.TargetLoader));
                TargetResolutionResult result = await _targetResolver.ResolveAsync(
                    _pack,
                    inputs.TargetMinecraft,
                    inputs.TargetLoader,
                    pendingOnly: false,
                    progress,
                    cancellationToken);
                Log("INFO", App.Localization.Translate("log.analysis_complete", result.Found, result.Preserved, result.Missing));
                RefreshContentRows();
            }

            SetWorking(true, "status.analyzing", indeterminate: true);
            CompatibilityReport report = await AnalyzeStaticAsync(inputs, cancellationToken);
            if (!AnalysisInputsAreCurrent(inputs))
            {
                InvalidateAnalysis();
                return;
            }
            ApplyCompatibilityReport(report, inputs.Snapshot);
            ShowMissingDependencyNotice(report);
            if (promptOnErrors && report.HasErrors)
                await ResolveCompatibilityErrorsAsync(buildAfter: false);
        }
        catch (OperationCanceledException)
        {
            InvalidateAnalysis();
            RefreshContentRows();
            SetStatus("status.cancelled");
            Log("WARN", App.Localization["log.analysis_cancelled"]);
        }
        catch (Exception exception)
        {
            InvalidateAnalysis();
            RefreshContentRows();
            SetStatus("status.analysis_failed");
            Log("ERROR", exception.ToString());
            MessageBox.Show(App.Localization["dialog.analysis_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetWorking(false);
        }
    }

    private Task<CompatibilityReport> AnalyzeStaticAsync(
        AnalysisInputs inputs,
        CancellationToken cancellationToken)
    {
        if (_pack is null) throw new InvalidOperationException("Pack is not loaded.");
        var items = CompatibilityContentItemAdapter.FromContentItems(_pack.Items, cancellationToken);
        var request = new CompatibilityAnalysisRequest
        {
            Items = items,
            SourceMinecraftVersion = _pack.MinecraftVersion,
            TargetMinecraftVersion = inputs.TargetMinecraft,
            SourceLoader = _pack.LoaderType,
            TargetLoader = inputs.TargetLoader,
            SourceLoaderVersion = _pack.LoaderVersion,
            TargetLoaderVersion = inputs.TargetLoaderVersion,
            TargetFormat = _pack.FormatType,
            PassthroughPaths = _pack.OverridePaths
        };
        return Task.Run(() => _compatibilityAnalyzer.Analyze(request, cancellationToken), cancellationToken);
    }

    private Task<CompatibilityReport> AnalyzeStaticAsync(CancellationToken cancellationToken) =>
        AnalyzeStaticAsync(CaptureAnalysisInputs(), cancellationToken);

    private void ApplyCompatibilityReport(CompatibilityReport report, string? snapshot = null)
    {
        _report = report;
        _analysisSnapshot = snapshot ?? CurrentSnapshot();
        RenderCompatibilityReport(report);
        RefreshAnalysisButton.IsEnabled = !_working;
        BuildButton.IsEnabled = !_working;
        ResultTabs.SelectedIndex = 0;
        SetStatus("status.ready_build");
    }

    private void RenderCompatibilityReport(CompatibilityReport report)
    {
        _compatibilityRows.Clear();
        foreach (CompatibilityIssue issue in report.Issues)
        {
            _compatibilityRows.Add(new CompatibilityRow(
                issue,
                SeverityText(issue.Severity),
                ScopeText(issue.Scope),
                string.IsNullOrWhiteSpace(issue.Item) ? "-" : issue.Item!,
                LocalizeIssue(issue)));
        }
        foreach (string limitation in report.Limitations)
        {
            var issue = new CompatibilityIssue { Code = "limitation", Severity = "info", Scope = "general", Message = limitation };
            _compatibilityRows.Add(new CompatibilityRow(issue, SeverityText("info"), App.Localization["compat.boundary"], App.Localization["compat.static_check"], LocalizeLimitation(limitation)));
        }
        if (report.Issues.Count == 0)
        {
            var issue = new CompatibilityIssue { Code = "pass", Severity = "info", Scope = "general", Message = App.Localization["compat.no_issues"] };
            _compatibilityRows.Add(new CompatibilityRow(issue, App.Localization["compat.pass"], App.Localization["compat.whole"], App.Localization["compat.pack"], App.Localization["compat.no_issues"]));
        }
        var counts = report.Counts;
        int errors = counts.GetValueOrDefault(CompatibilitySeverity.Error);
        int warnings = counts.GetValueOrDefault(CompatibilitySeverity.Warning);
        int items = report.Stats.GetValueOrDefault("content_items_checked");
        CompatibilitySummary.Text = App.Localization.Translate("compat.summary", errors, warnings, items);
        CompatibilitySummary.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            errors > 0 ? "DangerBrush" : warnings > 0 ? "WarningBrush" : "SuccessBrush");
        CompatibilityDetail.Visibility = Visibility.Collapsed;
    }

    private async Task ResolveCompatibilityErrorsAsync(bool buildAfter)
    {
        if (_pack is null || _report is null) return;
        bool changed = false;
        var errors = _report.Issues.Where(issue => issue.Severity == CompatibilitySeverity.Error).ToList();

        foreach (CompatibilityIssue issue in errors.Where(issue => issue.Code == "item_not_found"))
        {
            ContentItem? item = ItemForIssue(issue);
            if (item is null || item.Excluded) continue;
            MessageBoxResult answer = MessageBox.Show(
                App.Localization.Translate("resolution.not_found", DisplayName(item)),
                App.Localization["common.warning"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                item.Excluded = true;
                item.PreserveOriginal = false;
                changed = true;
            }
        }

        string[] excludableCodes =
        [
            "required_embedded_download_unavailable", "required_embedded_scope_unsupported",
            "unsafe_output_path", "override_output_collision", "explicitly_incompatible_item",
            "explicit_incompatibility", "dependency_version_mismatch", "loader_version_mismatch",
            "loader_dependency_mismatch", "minecraft_version_mismatch"
        ];
        foreach (var group in errors.Where(issue => excludableCodes.Contains(issue.Code, StringComparer.Ordinal)).GroupBy(ItemForIssue))
        {
            ContentItem? item = group.Key;
            if (item is null || item.Excluded) continue;
            string details = string.Join(Environment.NewLine, group.Select(issue => "- " + LocalizeIssue(issue)));
            string prompt = App.Localization.Translate("resolution.incompatible", DisplayName(item), details);
            if (MessageBox.Show(prompt, App.Localization["common.warning"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                item.Excluded = true;
                item.PreserveOriginal = false;
                changed = true;
            }
        }

        foreach (CompatibilityIssue issue in errors.Where(issue => issue.Code == "duplicate_output_path"))
        {
            var indexes = EvidenceIndexes(issue, "item_indexes").Where(index => index >= 0 && index < _pack.Items.Count).ToArray();
            foreach (int index in indexes.Skip(1))
            {
                ContentItem item = _pack.Items[index];
                if (item.Excluded) continue;
                string prompt = App.Localization.Translate("resolution.duplicate", DisplayName(item), issue.Path ?? string.Empty);
                if (MessageBox.Show(prompt, App.Localization["common.warning"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    item.Excluded = true;
                    item.PreserveOriginal = false;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            RefreshContentRows();
            AnalysisInputs inputs = CaptureAnalysisInputs();
            bool ownsOperation = _operationCts is null;
            if (ownsOperation)
            {
                _operationCts = new CancellationTokenSource();
                SetWorking(true, "status.analyzing", indeterminate: true);
            }

            CompatibilityReport? refreshed = null;
            try
            {
                CancellationToken refreshToken = _operationCts?.Token ?? CancellationToken.None;
                refreshed = await AnalyzeStaticAsync(inputs, refreshToken);
                if (!AnalysisInputsAreCurrent(inputs))
                {
                    InvalidateAnalysis();
                    return;
                }
                ApplyCompatibilityReport(refreshed, inputs.Snapshot);
                ShowMissingDependencyNotice(refreshed);
            }
            catch (OperationCanceledException) when (ownsOperation)
            {
                InvalidateAnalysis();
                SetStatus("status.cancelled");
                return;
            }
            catch (Exception exception) when (ownsOperation)
            {
                InvalidateAnalysis();
                SetStatus("status.analysis_failed");
                Log("ERROR", exception.ToString());
                MessageBox.Show(App.Localization["dialog.analysis_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                if (ownsOperation)
                {
                    _operationCts?.Dispose();
                    _operationCts = null;
                    SetWorking(false);
                }
            }

            if (buildAfter && refreshed is not null)
            {
                if (refreshed.HasErrors)
                {
                    int count = refreshed.Counts.GetValueOrDefault(CompatibilitySeverity.Error);
                    MessageBox.Show(App.Localization.Translate("resolution.blocked", count), App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
                    ResultTabs.SelectedIndex = 0;
                }
                else
                {
                    await BuildPackCoreAsync();
                }
            }
            return;
        }

        if (buildAfter && _report.HasErrors)
        {
            int count = _report.Counts.GetValueOrDefault(CompatibilitySeverity.Error);
            MessageBox.Show(App.Localization.Translate("resolution.blocked", count), App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            ResultTabs.SelectedIndex = 0;
        }
    }

    private void ShowMissingDependencyNotice(CompatibilityReport report)
    {
        var groups = new Dictionary<string, (string Source, string Reference, List<string> Owners)>(StringComparer.Ordinal);
        foreach (CompatibilityIssue issue in report.Issues.Where(issue => issue.Code == "missing_required_dependency"))
        {
            string source = EvidenceString(issue, "source").ToLowerInvariant();
            string referenceType = EvidenceString(issue, "dependency_reference_type").ToLowerInvariant();
            if (referenceType.Length == 0) referenceType = "project_id";
            string normalizedReference = EvidenceString(issue, "dependency");
            if (normalizedReference.Length == 0) continue;
            string key = string.Join('\u001f', source, referenceType, normalizedReference.ToLowerInvariant());
            if (_shownDependencyWarnings.Contains(key)) continue;

            string displayReference = EvidenceString(issue, "dependency_exact");
            if (displayReference.Length == 0) displayReference = normalizedReference;
            if (!groups.TryGetValue(key, out var group))
            {
                group = (source, displayReference, []);
                groups.Add(key, group);
            }
            string owner = string.IsNullOrWhiteSpace(issue.Item) ? "-" : issue.Item!;
            if (!group.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase))
                group.Owners.Add(owner);
        }
        if (groups.Count == 0) return;
        _shownDependencyWarnings.UnionWith(groups.Keys);

        var lines = new List<string>();
        foreach (var group in groups.Values.Take(20))
        {
            string platform = group.Source switch
            {
                "modrinth" => "Modrinth",
                "curseforge" => "CurseForge",
                _ => group.Source.Length == 0 ? "-" : group.Source
            };
            string separator = App.Localization.Language == "en_US" ? ", " : "、";
            string owners = string.Join(separator, group.Owners.Take(3));
            if (group.Owners.Count > 3)
                owners += App.Localization.Translate("deps.more_owners", group.Owners.Count - 3);
            lines.Add("- " + App.Localization.Translate("deps.entry", group.Reference, platform, owners));
        }
        if (groups.Count > 20)
            lines.Add("- " + App.Localization.Translate("deps.more", groups.Count - 20));
        MessageBox.Show(App.Localization.Translate("deps.body", string.Join(Environment.NewLine, lines)), App.Localization["deps.title"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CompatibilityGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompatibilityGrid.SelectedItem is not CompatibilityRow row)
        {
            CompatibilityDetail.Visibility = Visibility.Collapsed;
            return;
        }
        CompatibilityIssue issue = row.Issue;
        var builder = new StringBuilder(row.Message)
            .AppendLine().Append(App.Localization["detail.code"]).Append(": ").Append(issue.Code)
            .AppendLine().Append(App.Localization["detail.confidence"]).Append(": ").Append(issue.Confidence);
        if (!string.IsNullOrWhiteSpace(issue.Path)) builder.AppendLine().Append(App.Localization["detail.path"]).Append(": ").Append(issue.Path);
        if (issue.Evidence.Count > 0) builder.AppendLine().Append(App.Localization["detail.evidence"]).Append(": ").Append(JsonSerializer.Serialize(issue.Evidence));
        CompatibilityDetail.Text = builder.ToString();
        CompatibilityDetail.Visibility = Visibility.Visible;
    }

    private async void ToggleExclude_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureIdle()) return;
        ContentRow? row = (sender as FrameworkElement)?.DataContext as ContentRow ?? FilesGrid.SelectedItem as ContentRow;
        if (row is null || row.Item.Passthrough) return;
        bool refreshReport = _report is not null;
        bool restoring = row.Item.Excluded;
        if (!restoring && MessageBox.Show(
                App.Localization.Translate("action.exclude_prompt", DisplayName(row.Item)),
                App.Localization["action.exclude_title"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        row.Item.Excluded = !restoring;
        if (restoring)
        {
            row.Item.ResetTarget();
            Log("INFO", App.Localization.Translate("log.restored", DisplayName(row.Item)));
        }
        else
        {
            row.Item.PreserveOriginal = false;
            Log("INFO", App.Localization.Translate("log.excluded", DisplayName(row.Item)));
        }
        InvalidateAnalysis();
        RefreshContentRows();
        if (!refreshReport) return;
        if (restoring)
        {
            await RunAnalysisAsync(resolveTargets: true, promptOnErrors: false);
            return;
        }

        AnalysisInputs inputs = CaptureAnalysisInputs();
        _operationCts = new CancellationTokenSource();
        SetWorking(true, "status.analyzing", indeterminate: true);
        try
        {
            CompatibilityReport refreshed = await AnalyzeStaticAsync(inputs, _operationCts.Token);
            if (!AnalysisInputsAreCurrent(inputs))
            {
                InvalidateAnalysis();
                return;
            }
            ApplyCompatibilityReport(refreshed, inputs.Snapshot);
            ShowMissingDependencyNotice(refreshed);
        }
        catch (OperationCanceledException)
        {
            SetStatus("status.cancelled");
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
            SetStatus("status.analysis_failed");
            MessageBox.Show(App.Localization["dialog.analysis_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetWorking(false);
        }
    }

    private void OpenCurseForge_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ContentRow row) return;
        string url = CurseForgeClient.MakeProjectUrl(row.Item.CurseForgeSlug, long.TryParse(row.Item.ProjectId, out long id) ? id : 0, row.Item.Category);
        OpenUrl(url);
    }

    private void OpenModrinth_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ContentRow row) return;
        OpenUrl(ModrinthClient.MakeProjectUrl(row.Item.ProjectId, row.Item.ModrinthSlug));
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureIdle()) return;
        if (_pack is null)
        {
            MessageBox.Show(App.Localization["dialog.read_first"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!PathsEqual(InputPathBox.Text, _parsedInputPath))
        {
            InvalidateAnalysis();
            MessageBox.Show(App.Localization["dialog.read_first"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_report is null || _analysisSnapshot.Length == 0)
        {
            MessageBox.Show(App.Localization["dialog.analysis_first"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_analysisSnapshot != CurrentSnapshot())
        {
            InvalidateAnalysis();
            MessageBox.Show(App.Localization["dialog.analysis_first"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_report.HasErrors)
        {
            await ResolveCompatibilityErrorsAsync(buildAfter: true);
            return;
        }
        await BuildPackCoreAsync();
    }

    private async Task BuildPackCoreAsync()
    {
        if (_pack is null || _working) return;
        AnalysisInputs inputs = CaptureAnalysisInputs();
        if (_report is null
            || _analysisSnapshot.Length == 0
            || !string.Equals(_analysisSnapshot, inputs.Snapshot, StringComparison.Ordinal)
            || !PathsEqual(inputs.InputPath, _parsedInputPath))
        {
            InvalidateAnalysis();
            MessageBox.Show(App.Localization["dialog.analysis_first"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        string? outputPath = GetOutputPath();
        if (outputPath is null) return;
        if (PathsEqual(outputPath, _parsedInputPath))
        {
            MessageBox.Show(App.Localization["dialog.output_is_input"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        bool overwrite = File.Exists(outputPath);
        if (overwrite && MessageBox.Show(App.Localization["dialog.overwrite"], App.Localization["common.warning"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        App.Settings.TargetMinecraft = inputs.TargetMinecraft;
        App.Settings.TargetLoaderType = inputs.TargetLoader;
        App.Settings.TargetLoaderVersion = inputs.TargetLoaderVersion;
        App.Settings.OutputDirectory = OutputDirectoryBox.Text.Trim();
        try { await App.SettingsStore.SaveAsync(App.Settings); } catch { }

        _operationCts = new CancellationTokenSource();
        SetWorking(true, "status.building", indeterminate: true);
        try
        {
            BuildResult result = _pack.FormatType.Equals("curseforge", StringComparison.OrdinalIgnoreCase)
                ? await PackBuilder.BuildCurseForgeAsync(
                    outputPath, _pack, inputs.TargetMinecraft, inputs.TargetLoader, inputs.TargetLoaderVersion,
                    _pack.OverridesDirectory, downloadFiles: false, packName: OutputNameBox.Text.Trim(),
                    overwrite: overwrite, cancellationToken: _operationCts.Token)
                : await PackBuilder.BuildModrinthAsync(
                    outputPath, _pack, inputs.TargetMinecraft, inputs.TargetLoader, inputs.TargetLoaderVersion,
                    _pack.OverridesDirectory, downloadFiles: false, packName: OutputNameBox.Text.Trim(),
                    overwrite: overwrite, cancellationToken: _operationCts.Token);

            if (!result.Succeeded || result.MissingFiles.Count > 0)
            {
                string missing = string.Join(
                    Environment.NewLine,
                    result.MissingFiles.Take(20).Select(item => $"- {LocalizeBuildMessage(item)}"));
                if (result.MissingFiles.Count > 20)
                    missing += $"{Environment.NewLine}- … {result.MissingFiles.Count - 20}";
                SetStatus("build.incomplete_status");
                string error = App.Localization.Translate(
                    "build.incomplete",
                    result.MissingFiles.Count,
                    missing);
                Log("ERROR", error);
                MessageBox.Show(error, App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var message = new StringBuilder(App.Localization["build.notice"])
                .AppendLine().AppendLine()
                .Append(App.Localization.Translate("build.location", outputPath));
            if (result.Warnings.Count > 0)
            {
                message.AppendLine().AppendLine().Append(App.Localization["build.notes"]).AppendLine();
                foreach (string warning in result.Warnings.Take(20)) message.Append("- ").AppendLine(LocalizeBuildMessage(warning));
            }
            SetStatus("build.complete");
            Log("INFO", App.Localization.Translate("log.build_complete", outputPath));
            new BuildSuccessWindow(message.ToString(), outputPath) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            SetStatus("status.cancelled");
        }
        catch (Exception exception)
        {
            SetStatus("build.incomplete_status");
            Log("ERROR", exception.ToString());
            MessageBox.Show(App.Localization["build.failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetWorking(false);
        }
    }

    private string LocalizeBuildMessage(string message)
    {
        (string Suffix, string Key)[] warningSuffixes =
        [
            ("：目标路径与 overrides 现有文件同名，已保留原文件并使用联网安装引用。", "build.warning.cf_override_collision"),
            ("：目标路径与 overrides 现有文件同名，已保留原文件和联网安装引用。", "build.warning.mr_override_collision"),
            ("：下载失败，已回退为 CurseForge 联网安装引用。", "build.warning.cf_download_fallback"),
            ("：平台未提供下载地址，已保留 CurseForge 联网安装引用。", "build.warning.cf_no_download"),
            ("：为保留 Modrinth env 作用域，已保留联网安装引用。", "build.warning.mr_env_reference"),
            ("：下载失败，已回退为 Modrinth 联网安装引用。", "build.warning.mr_download_fallback"),
            ("：目标下载失败，已保留旧禁用版本。", "build.warning.disabled_download_preserved"),
            ("：未找到目标版本，已保留旧禁用版本。", "build.warning.disabled_no_target_preserved")
        ];
        foreach ((string suffix, string key) in warningSuffixes)
        {
            if (!message.EndsWith(suffix, StringComparison.Ordinal)) continue;
            string name = message[..^suffix.Length];
            if (name.StartsWith("[禁用] ", StringComparison.Ordinal)) name = name[5..];
            return App.Localization.Translate(key, name);
        }
        if (message.StartsWith("[禁用] ", StringComparison.Ordinal))
            return App.Localization.Translate("build.disabled_item", message[5..]);

        var itemMatch = System.Text.RegularExpressions.Regex.Match(
            message,
            @"^(?<name>.+) \[(?<category>[^\]]+)\](?<reason>.*)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!itemMatch.Success) return message;
        string categoryCode = itemMatch.Groups["category"].Value;
        string categoryKey = $"category.{categoryCode}";
        string category = App.Localization[categoryKey];
        if (category == categoryKey) category = categoryCode;
        string item = App.Localization.Translate("build.item", itemMatch.Groups["name"].Value, category);
        return itemMatch.Groups["reason"].Value switch
        {
            "（与 overrides 现有文件同名，未覆盖原文件）" => App.Localization.Translate("build.reason.override_collision", item),
            "（无法保留 env 作用域）" => App.Localization.Translate("build.reason.env_scope", item),
            _ => item
        };
    }

    private string? GetOutputPath()
    {
        string directory = OutputDirectoryBox.Text.Trim();
        string name = OutputNameBox.Text.Trim();
        if (directory.Length == 0 || name.Length == 0 || !Directory.Exists(directory))
        {
            MessageBox.Show(App.Localization["dialog.output_invalid"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        string expectedExtension = _pack?.FormatType.Equals("modrinth", StringComparison.OrdinalIgnoreCase) == true ? ".mrpack" : ".zip";
        if (name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) name = name[..^7];
        else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(App.Localization["dialog.output_invalid_chars"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        return Path.Combine(Path.GetFullPath(directory), name + expectedExtension);
    }

    private AnalysisInputs CaptureAnalysisInputs()
    {
        string inputPath = InputPathBox.Text.Trim();
        string normalizedInput;
        try { normalizedInput = inputPath.Length == 0 ? string.Empty : Path.GetFullPath(inputPath); }
        catch { normalizedInput = inputPath; }
        string minecraft = MinecraftBox.Text.Trim();
        string loader = SelectedLoader;
        string loaderVersion = LoaderVersionBox.Text.Trim();
        return new AnalysisInputs(
            inputPath,
            minecraft,
            loader,
            loaderVersion,
            string.Join("|", normalizedInput, minecraft, loader, loaderVersion));
    }

    private static bool TargetIsComplete(AnalysisInputs inputs) =>
        System.Text.RegularExpressions.Regex.IsMatch(inputs.TargetMinecraft, @"^\d+\.\d+(?:\.\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
        && inputs.TargetLoader is "fabric" or "forge" or "neoforge" or "quilt"
        && inputs.TargetLoaderVersion.Length > 0;

    private string CurrentSnapshot() => CaptureAnalysisInputs().Snapshot;

    private bool AnalysisInputsAreCurrent(AnalysisInputs inputs) =>
        _pack is not null
        && PathsEqual(inputs.InputPath, _parsedInputPath)
        && string.Equals(inputs.Snapshot, CurrentSnapshot(), StringComparison.Ordinal);

    private ContentItem? ItemForIssue(CompatibilityIssue issue)
    {
        if (_pack is null || !issue.Evidence.TryGetValue("item_index", out object? raw)) return null;
        try
        {
            int index = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return index >= 0 && index < _pack.Items.Count ? _pack.Items[index] : null;
        }
        catch { return null; }
    }

    private static IEnumerable<int> EvidenceIndexes(CompatibilityIssue issue, string key)
    {
        if (!issue.Evidence.TryGetValue(key, out object? raw) || raw is null) yield break;
        if (raw is IEnumerable<int> integers)
        {
            foreach (int value in integers) yield return value;
            yield break;
        }
        if (raw is System.Collections.IEnumerable sequence && raw is not string)
        {
            foreach (object? value in sequence)
            {
                int parsed;
                try { parsed = Convert.ToInt32(value, CultureInfo.InvariantCulture); }
                catch { continue; }
                yield return parsed;
            }
        }
    }

    private static string EvidenceString(CompatibilityIssue issue, string key) =>
        issue.Evidence.TryGetValue(key, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;

    private static string DisplayName(ContentItem item) => string.IsNullOrWhiteSpace(item.Name) ? item.FileName : item.Name;

    private string SeverityText(string severity) => severity switch
    {
        CompatibilitySeverity.Error => App.Localization["compat.error"],
        CompatibilitySeverity.Warning => App.Localization["compat.warning"],
        _ => App.Localization["compat.info"]
    };

    private string ScopeText(string scope)
    {
        string key = scope switch
        {
            "mod" => "compat.scope.mod",
            "resourcepack" => "compat.scope.resourcepack",
            "shaderpack" => "compat.scope.shaderpack",
            "content" => "compat.scope.content",
            "dependency" => "compat.scope.dependency",
            "output" => "compat.scope.output",
            _ => "compat.scope.general"
        };
        return App.Localization[key];
    }

    private string LocalizeIssue(CompatibilityIssue issue)
    {
        string dependency = EvidenceString(issue, "dependency_exact");
        if (dependency.Length == 0) dependency = EvidenceString(issue, "dependency");
        string incompatible = EvidenceString(issue, "incompatible_with_exact");
        if (incompatible.Length == 0) incompatible = EvidenceString(issue, "incompatible_with");
        if (incompatible.Length == 0) incompatible = "-";
        return issue.Code switch
        {
            "item_not_found" => App.Localization["compat.issue.item_not_found"],
            "missing_required_dependency" => App.Localization.Translate("compat.issue.missing_required_dependency", dependency),
            "dependency_version_mismatch" => App.Localization["compat.issue.dependency_version_mismatch"],
            "loader_version_mismatch" => App.Localization["compat.issue.loader_version_mismatch"],
            "loader_dependency_mismatch" => App.Localization["compat.issue.loader_dependency_mismatch"],
            "minecraft_version_mismatch" => App.Localization["compat.issue.minecraft_version_mismatch"],
            "explicit_incompatibility" => App.Localization.Translate("compat.issue.explicit_incompatibility", incompatible),
            "explicitly_incompatible_item" => App.Localization["compat.issue.explicitly_incompatible_item"],
            "duplicate_project" => App.Localization["compat.issue.duplicate_project"],
            "duplicate_output_path" => App.Localization["compat.issue.duplicate_output_path"],
            "unsafe_output_path" => App.Localization["compat.issue.unsafe_output_path"],
            "unsafe_override_path" => App.Localization["compat.issue.unsafe_override_path"],
            "override_output_collision" => App.Localization["compat.issue.override_output_collision"],
            "required_embedded_download_unavailable" => App.Localization["compat.issue.required_embedded_download_unavailable"],
            "required_embedded_scope_unsupported" => App.Localization["compat.issue.required_embedded_scope_unsupported"],
            "dependency_version_unverified" => App.Localization["compat.issue.dependency_version_unverified"],
            "incompatibility_version_unverified" => App.Localization["compat.issue.incompatibility_version_unverified"],
            _ => App.Localization["compat.issue.unknown"]
        };
    }

    private string LocalizeLimitation(string text)
    {
        if (text.StartsWith("Static analysis cannot", StringComparison.Ordinal))
            return App.Localization["compat.limit.runtime"];
        if (text.StartsWith("Only recognized direct", StringComparison.Ordinal))
            return App.Localization["compat.limit.relations"];
        if (text.StartsWith("Java runtime requirements", StringComparison.Ordinal))
            return App.Localization["compat.limit.java"];

        var metadata = System.Text.RegularExpressions.Regex.Match(
            text,
            @"^Dependency/conflict metadata was absent for (?<missing>\d+) of (?<total>\d+) active resolved items;",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (metadata.Success)
            return App.Localization.Translate("compat.limit.metadata", metadata.Groups["missing"].Value, metadata.Groups["total"].Value);

        var artifact = System.Text.RegularExpressions.Regex.Match(
            text,
            @"^Artifact metadata for '(?<item>.*)' was incomplete: (?<warning>.*)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (artifact.Success)
            return App.Localization.Translate(
                "compat.limit.artifact",
                artifact.Groups["item"].Value,
                LocalizeArtifactWarning(artifact.Groups["warning"].Value));
        return text;
    }

    private string LocalizeArtifactWarning(string warning)
    {
        var multiple = System.Text.RegularExpressions.Regex.Match(
            warning,
            @"^Multiple '(?<file>[^']+)' entries were found; metadata is ambiguous\.$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (multiple.Success)
            return App.Localization.Translate("compat.limit.artifact_multiple", multiple.Groups["file"].Value);

        var unreadable = System.Text.RegularExpressions.Regex.Match(
            warning,
            @"^Could not read '(?<file>[^']+)':",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (unreadable.Success)
            return App.Localization.Translate("compat.limit.artifact_read", unreadable.Groups["file"].Value);

        var invalid = System.Text.RegularExpressions.Regex.Match(
            warning,
            @"^Could not parse '(?<file>[^']+)':",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (invalid.Success)
            return App.Localization.Translate("compat.limit.artifact_parse", invalid.Groups["file"].Value);

        return App.Localization["compat.limit.artifact_unknown"];
    }

    private sealed record AnalysisInputs(
        string InputPath,
        string TargetMinecraft,
        string TargetLoader,
        string TargetLoaderVersion,
        string Snapshot);
}
