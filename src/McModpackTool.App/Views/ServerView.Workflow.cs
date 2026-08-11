using System.Windows;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.App.Views;

public partial class ServerView
{
    private async void RefreshCores_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null || _working)
        {
            return;
        }
        CancellationToken cancellationToken = BeginOperation();
        SetWorking(true, "server.preparing", indeterminate: true);
        try
        {
            await RefreshCoreOptionsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus("server.ready");
        }
        catch (OperationCanceledException)
        {
            SetStatus("server.cancelled");
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
            SetStatus("server.ready");
            MessageBox.Show(exception.Message, App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
            EndOperation();
        }
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e) => await PrepareAsync();

    private async Task PrepareAsync()
    {
        RefreshJavaRecommendation();
        if (_source is null || _working || !PathsEqual(InputPathBox.Text, _readPath))
        {
            MessageBox.Show(App.Localization["server.dialog.prepare_first"], App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string sourceMinecraft = _source.MinecraftVersion;
        if (sourceMinecraft.Length == 0)
        {
            MessageBox.Show(App.Localization["server.dialog.source_version_missing"], App.Localization["common.error"],
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        string sourceLoaderVersion = _source.LoaderVersion;
        MinecraftVersionBox.Text = sourceMinecraft;
        LoaderVersionBox.Text = sourceLoaderVersion;

        InvalidatePreparation();
        CancellationToken cancellationToken = BeginOperation();
        SetWorking(true, "server.preparing", indeterminate: true);
        try
        {
            if (_source.ManifestPack is not null)
            {
                foreach (ServerModEntry entry in _source.Mods.Where(entry => entry.ContentItem is not null))
                {
                    entry.ContentItem!.Excluded = !entry.Selected;
                }
            }

            CompatibilitySnapshot snapshot = await CreateCompatibilitySnapshotAsync(cancellationToken);
            CompatibilityReport report = await Task.Run(
                () => _compatibilityAnalyzer.Analyze(snapshot.Request, cancellationToken),
                cancellationToken);
            ShowMissingDependencyNotice(report);
            if (!await ResolveBlockingIssuesAsync(report, snapshot, cancellationToken))
            {
                SetStatus("server.ready");
                return;
            }

            await RefreshCoreOptionsAsync(cancellationToken);
            if (_coreRows.Count == 0)
            {
                MessageBox.Show(App.Localization["server.core_none"], App.Localization["common.warning"],
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("server.ready");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _preparedSnapshot = CurrentSnapshot();
            BuildButton.IsEnabled = true;
            SetStatus("server.prepared");
            Log("INFO", $"Preparation complete: cores={_coreRows.Count}, selected mods={_source.Mods.Count(mod => mod.Selected)}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("server.cancelled");
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
            SetStatus("server.ready");
            MessageBox.Show($"{App.Localization["dialog.analysis_failed"]}\n\n{exception.Message}",
                App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
            EndOperation();
        }
    }

    private async Task<CompatibilitySnapshot> CreateCompatibilitySnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            throw new InvalidOperationException("The source is not loaded.");
        }
        return await Task.Run(() =>
        {
            var items = new List<CompatibilityContentItem>();
            var entries = new Dictionary<int, ServerModEntry>();
            if (_source.ManifestPack is not null)
            {
                for (int index = 0; index < _source.ManifestPack.Items.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ContentItem item = _source.ManifestPack.Items[index];
                    items.Add(CompatibilityContentItemAdapter.FromContentItem(item, index));
                    ServerModEntry? entry = _source.Mods.FirstOrDefault(mod => ReferenceEquals(mod.ContentItem, item));
                    if (entry is not null)
                    {
                        entries[index] = entry;
                    }
                }
            }

            foreach (ServerModEntry entry in _source.Mods.Where(mod => mod.Origin == ServerModOrigins.Local))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = items.Count;
                var item = new CompatibilityContentItem
                {
                    OriginalIndex = index,
                    Name = entry.Name,
                    Source = "local",
                    Category = "mod",
                    Status = entry.Selected ? "found" : "excluded",
                    FileName = Path.GetFileName(entry.SourcePath),
                    TargetFileName = Path.GetFileName(entry.SourcePath),
                    TargetPath = $"mods/{entry.RelativePath}",
                    Disabled = entry.Disabled,
                    Excluded = !entry.Selected,
                };
                if (entry.Selected && File.Exists(entry.SourcePath))
                {
                    try
                    {
                        item = ArtifactMetadataReader.Enrich(item, ArtifactMetadataReader.Read(entry.SourcePath, cancellationToken: cancellationToken));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or IOException)
                    {
                        item = item with { MetadataWarnings = [$"Could not inspect the selected JAR: {exception.Message}"] };
                    }
                }
                items.Add(item);
                entries[index] = entry;
            }

            var request = new CompatibilityAnalysisRequest
            {
                Items = items,
                SourceMinecraftVersion = _source.MinecraftVersion,
                TargetMinecraftVersion = _source.MinecraftVersion,
                SourceLoader = _source.LoaderType,
                TargetLoader = _source.LoaderType,
                SourceLoaderVersion = _source.LoaderVersion,
                TargetLoaderVersion = _source.LoaderVersion,
                TargetFormat = "server",
            };
            return new CompatibilitySnapshot(request, entries);
        }, cancellationToken);
    }

    private async Task<bool> ResolveBlockingIssuesAsync(
        CompatibilityReport report,
        CompatibilitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var duplicatePrompted = new HashSet<ServerModEntry>();
        var compatibilityPrompted = new HashSet<ServerModEntry>();
        var accepted = new HashSet<ServerModEntry>();
        var unmappedIssues = new List<string>();
        bool unresolved = false;
        foreach (CompatibilityIssue issue in report.Issues.Where(issue => issue.Severity == CompatibilitySeverity.Error))
        {
            List<ServerModEntry> issueEntries = FindIssueEntries(issue, snapshot.Entries);
            if (issueEntries.Count == 0)
            {
                unresolved = true;
                string item = string.IsNullOrWhiteSpace(issue.Item) ? issue.Code : issue.Item;
                string message = string.IsNullOrWhiteSpace(issue.Message) ? issue.Code : issue.Message;
                unmappedIssues.Add($"- {item}: {message}");
                continue;
            }
            List<ServerModEntry> candidates = issueEntries.Where(entry => entry.Selected).ToList();
            if (candidates.Count == 0 || issue.Code == "duplicate_output_path" && candidates.Count == 1)
            {
                continue;
            }

            string details = string.IsNullOrWhiteSpace(issue.Message) ? issue.Code : issue.Message;
            if (issue.Code == "duplicate_output_path")
            {
                int remaining = candidates.Count;
                foreach (ServerModEntry entry in candidates)
                {
                    if (remaining <= 1 || !duplicatePrompted.Add(entry))
                    {
                        continue;
                    }

                    MessageBoxResult answer = MessageBox.Show(
                        App.Localization.Translate("resolution.incompatible", entry.Name, $"- {details}"),
                        App.Localization["common.warning"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Yes)
                    {
                        ExcludeServerMod(entry);
                        remaining--;
                    }
                }
                if (remaining > 1)
                {
                    unresolved = true;
                }
                continue;
            }

            ServerModEntry entryToPrompt = candidates[0];
            if (!compatibilityPrompted.Add(entryToPrompt))
            {
                continue;
            }
            MessageBoxResult issueAnswer = MessageBox.Show(
                App.Localization.Translate("server.dialog.incompatible", entryToPrompt.Name, $"- {details}"),
                App.Localization["common.warning"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (issueAnswer == MessageBoxResult.Yes)
            {
                ExcludeServerMod(entryToPrompt);
            }
            else
            {
                accepted.Add(entryToPrompt);
            }
        }
        if (unresolved)
        {
            if (unmappedIssues.Count > 0)
            {
                MessageBox.Show(
                    App.Localization.Translate("resolution.unmapped", string.Join(Environment.NewLine, unmappedIssues.Distinct().Take(20))),
                    App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            MessageBox.Show(App.Localization.Translate("resolution.blocked", report.Counts.GetValueOrDefault(CompatibilitySeverity.Error)),
                App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        CompatibilitySnapshot refreshed = await CreateCompatibilitySnapshotAsync(cancellationToken);
        CompatibilityReport refreshedReport = await Task.Run(
            () => _compatibilityAnalyzer.Analyze(refreshed.Request, cancellationToken), cancellationToken);
        ShowMissingDependencyNotice(refreshedReport);
        int remainingErrors = refreshedReport.Issues.Count(issue =>
            issue.Severity == CompatibilitySeverity.Error &&
            IsUnresolvedAfterDecision(issue, refreshed.Entries, accepted));
        if (remainingErrors > 0)
        {
            MessageBox.Show(App.Localization.Translate("resolution.blocked", remainingErrors),
                App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private static bool IsUnresolvedAfterDecision(
        CompatibilityIssue issue,
        IReadOnlyDictionary<int, ServerModEntry> entries,
        IReadOnlySet<ServerModEntry> accepted)
    {
        List<ServerModEntry> issueEntries = FindIssueEntries(issue, entries);
        List<ServerModEntry> selected = issueEntries.Where(entry => entry.Selected).ToList();
        if (selected.Count == 0)
        {
            return issueEntries.Count == 0;
        }
        if (issue.Code == "duplicate_output_path")
        {
            return selected.Count > 1;
        }
        return !selected.Any(accepted.Contains);
    }

    private void ExcludeServerMod(ServerModEntry entry)
    {
        ServerModRow? row = _modRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Entry, entry));
        if (row is not null)
        {
            row.Selected = false;
            return;
        }

        entry.Selected = false;
        if (entry.ContentItem is not null)
        {
            entry.ContentItem.Excluded = true;
        }
    }

    private static List<ServerModEntry> FindIssueEntries(
        CompatibilityIssue issue,
        IReadOnlyDictionary<int, ServerModEntry> entries)
    {
        var result = new List<ServerModEntry>();
        if (issue.Evidence.TryGetValue("item_index", out object? value))
        {
            AddIndexes(value, entries, result);
        }
        if (issue.Evidence.TryGetValue("item_indexes", out value))
        {
            AddIndexes(value, entries, result);
        }
        if (result.Count == 0)
        {
            ServerModEntry? named = entries.Values.FirstOrDefault(entry =>
                entry.Name.Equals(issue.Item, StringComparison.OrdinalIgnoreCase));
            if (named is not null)
            {
                result.Add(named);
            }
        }
        return result;
    }

    private static void AddIndexes(
        object? value,
        IReadOnlyDictionary<int, ServerModEntry> entries,
        ICollection<ServerModEntry> destination)
    {
        if (value is System.Collections.IEnumerable values and not string)
        {
            foreach (object? item in values)
            {
                AddIndex(item, entries, destination);
            }
            return;
        }
        AddIndex(value, entries, destination);
    }

    private static void AddIndex(
        object? value,
        IReadOnlyDictionary<int, ServerModEntry> entries,
        ICollection<ServerModEntry> destination)
    {
        if (TryConvertIndex(value, out int index) && entries.TryGetValue(index, out ServerModEntry? entry) &&
            !destination.Contains(entry))
        {
            destination.Add(entry);
        }
    }

    private static bool TryConvertIndex(object? value, out int index)
    {
        try
        {
            index = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            index = -1;
            return false;
        }
    }

    private void ShowMissingDependencyNotice(CompatibilityReport report)
    {
        string[] entries = report.Issues
            .Where(issue => issue.Code == "missing_required_dependency")
            .Select(issue => $"- {issue.Item}: {issue.Message}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (entries.Length == 0)
        {
            return;
        }
        MessageBox.Show(
            App.Localization.Translate("deps.body", string.Join(Environment.NewLine, entries)),
            App.Localization["deps.title"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task RefreshCoreOptionsAsync(CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            return;
        }
        ServerCoreCatalogResult catalog = await _coreService.GetAvailableAsync(
            new ServerCoreQuery
            {
                MinecraftVersion = _source.MinecraftVersion,
                LoaderType = _source.LoaderType,
                LoaderVersion = _source.LoaderVersion,
            },
            cancellationToken);
        string? previousId = (CoreCombo.SelectedItem as CoreRow)?.Option.Id;
        _coreRows.Clear();
        foreach (ServerCoreOption option in catalog.Options)
        {
            _coreRows.Add(new CoreRow(option));
        }
        CoreCombo.SelectedItem = _coreRows.FirstOrDefault(row => row.Option.Id == previousId) ?? _coreRows.FirstOrDefault();
        foreach (ServerCoreUnavailable unavailable in catalog.Unavailable)
        {
            Log("INFO", $"Core unavailable: {unavailable.CoreId} ({unavailable.Reason})");
        }
        Log("INFO", $"Available server cores: {_coreRows.Count}");
    }

    private async void Build_Click(object sender, RoutedEventArgs e) => await BuildAsync();

    private async Task BuildAsync()
    {
        RefreshJavaRecommendation();
        if (_source is null || _working || !PreparationIsCurrent())
        {
            MessageBox.Show(App.Localization["server.dialog.prepare_first"], App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (CoreCombo.SelectedItem is not CoreRow coreRow)
        {
            MessageBox.Show(App.Localization["server.dialog.core_required"], App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (coreRow.Option.Id == ServerCoreIds.Vanilla &&
            MessageBox.Show(App.Localization["server.dialog.vanilla"], App.Localization["common.warning"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text) && !TryChooseOutputDirectory())
        {
            return;
        }
        string? outputPath = ResolveOutputPath();
        if (outputPath is null)
        {
            MessageBox.Show(App.Localization["server.dialog.output_invalid"], App.Localization["common.error"],
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        bool overwrite = File.Exists(outputPath);
        if (overwrite && MessageBox.Show(App.Localization["dialog.overwrite"], App.Localization["common.warning"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        bool eulaAccepted = MessageBox.Show(
            App.Localization["server.dialog.eula"], App.Localization["common.warning"],
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        string? javaExecutable = await ResolveJavaExecutableAsync();
        if (javaExecutable is null)
        {
            return;
        }

        var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (DefaultConfigsCheckBox.IsChecked == true) optional.Add("defaultconfigs");
        if (KubeJsCheckBox.IsChecked == true) optional.Add("kubejs");
        if (ScriptsCheckBox.IsChecked == true) optional.Add("scripts");
        var request = new ServerBuildRequest
        {
            Source = _source,
            CoreId = coreRow.Option.Id,
            OutputPath = outputPath,
            IncludeConfig = ConfigCheckBox.IsChecked == true,
            IncludedOptionalDirectories = optional,
            World = (WorldCombo.SelectedItem as WorldRow)?.World,
            EulaAccepted = eulaAccepted,
            Overwrite = overwrite,
        };

        CancellationToken cancellationToken = BeginOperation();
        SetWorking(true, "server.building", indeterminate: true);
        try
        {
            var progress = new Progress<ServerBuildPhase>(phase =>
            {
                if (phase is not (ServerBuildPhase.DownloadingCore or ServerBuildPhase.DownloadingMods))
                {
                    HideDownloadSpeed();
                }
                string statusKey = phase switch
                {
                    ServerBuildPhase.DownloadingCore => "server.building_core",
                    ServerBuildPhase.CopyingMods => "server.building_mods_copy",
                    ServerBuildPhase.DownloadingMods => "server.building_mods_download",
                    ServerBuildPhase.CopyingConfiguration => "server.building_config",
                    ServerBuildPhase.CopyingWorld => "server.building_world",
                    ServerBuildPhase.WritingLaunchFiles => "server.building_files",
                    ServerBuildPhase.CompressingArchive => "server.building_archive",
                    _ => "server.building",
                };
                SetStatus(statusKey);
                Log("INFO", App.Localization[statusKey]);
            });
            var transferProgress = new Progress<DownloadTransferProgress>(UpdateDownloadSpeed);
            ServerBuildResult result = await _builder.BuildAsync(
                request,
                coreRow.Option,
                javaExecutable,
                progress: progress,
                cancellationToken: cancellationToken,
                transferProgress: transferProgress);
            if (!result.Succeeded)
            {
                string missing = string.Join(Environment.NewLine, result.MissingFiles.Take(20).Select(item => $"- {item}"));
                SetStatus("server.ready");
                MessageBox.Show(App.Localization.Translate("server.dialog.blocked", missing),
                    App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus("server.build_complete");
            string message = $"{App.Localization["server.build_complete"]}\n\n{App.Localization["server.build_launch_hint"]}\n\n{App.Localization.Translate("build.location", outputPath)}";
            new BuildSuccessWindow(message, outputPath) { Owner = Window.GetWindow(this) }.ShowDialog();
            Log("INFO", $"Server ZIP complete: {outputPath}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("server.cancelled");
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
            SetStatus("server.ready");
            MessageBox.Show($"{App.Localization["server.dialog.build_failed"]}\n\n{exception.Message}",
                App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
            EndOperation();
        }
    }

    private async Task<string?> ResolveJavaExecutableAsync()
    {
        string selectedPath = (JavaCombo.SelectedItem as JavaRuntimeInfo)?.ExecutablePath
            ?? _selectedJavaPath;
        if (!string.IsNullOrWhiteSpace(selectedPath) && File.Exists(selectedPath))
        {
            // Re-probe at export time. A runtime can be upgraded or replaced after
            // the initial read, so cached metadata must not bypass compatibility checks.
            JavaRuntimeInfo? selectedRuntime = await _javaRuntimeService.ProbeExecutableAsync(selectedPath);
            if (selectedRuntime is null)
            {
                MessageBox.Show(App.Localization["server.dialog.java_required"], App.Localization["common.warning"],
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            JavaRuntimeInfo? cachedRuntime = _javaRuntimes.FirstOrDefault(runtime =>
                runtime.ExecutablePath.Equals(selectedRuntime.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            _suppressJavaSelection = true;
            try
            {
                if (cachedRuntime is null)
                {
                    _javaRuntimes.Add(selectedRuntime);
                }
                else
                {
                    int index = _javaRuntimes.IndexOf(cachedRuntime);
                    if (index >= 0)
                    {
                        _javaRuntimes[index] = selectedRuntime;
                    }
                }
                JavaCombo.SelectedItem = selectedRuntime;
            }
            finally
            {
                _suppressJavaSelection = false;
            }
            _selectedJavaPath = selectedRuntime.ExecutablePath;
            if (selectedRuntime.MajorVersion > 0 && selectedRuntime.MajorVersion != _recommendedJavaMajor)
            {
                MessageBox.Show(
                    JavaCompatibilityMessage(_recommendedJavaMajor, selectedRuntime.MajorVersion),
                    App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return selectedRuntime.ExecutablePath;
        }
        MessageBox.Show(JavaMissingMessage(), App.Localization["common.warning"],
            MessageBoxButton.OK, MessageBoxImage.Warning);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = App.Localization["server.dialog.choose_java"],
            Filter = "Java executable (java.exe)|java.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return null;
        }
        JavaRuntimeInfo? runtime = await _javaRuntimeService.ProbeExecutableAsync(dialog.FileName);
        if (runtime is null)
        {
            MessageBox.Show(App.Localization["server.dialog.java_required"], App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (runtime.MajorVersion > 0 && runtime.MajorVersion != _recommendedJavaMajor)
        {
            MessageBox.Show(
                App.Localization.Translate("server.dialog.java_incompatible",
                    MinecraftVersionBox.Text.Trim(), _recommendedJavaMajor, runtime.MajorVersion),
                App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        JavaRuntimeInfo? existing = _javaRuntimes.FirstOrDefault(candidate =>
            candidate.ExecutablePath.Equals(runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _javaRuntimes.Add(runtime);
            existing = runtime;
        }
        _selectedJavaPath = existing.ExecutablePath;
        _suppressJavaSelection = true;
        try { JavaCombo.SelectedItem = existing; }
        finally { _suppressJavaSelection = false; }
        RefreshJavaHint();
        return existing.ExecutablePath;
    }

    private string JavaMissingMessage()
    {
        string language = App.Localization.Language;
        return language.Equals("en_US", StringComparison.OrdinalIgnoreCase)
            ? "No Java runtime is selected. Choose the Java executable that matches the imported Minecraft version."
            : language.Equals("zh_HK", StringComparison.OrdinalIgnoreCase)
                ? "尚未選擇 Java 執行環境。請選擇與匯入的 Minecraft 版本相容的 Java 可執行檔。"
                : "尚未选择 Java 运行环境。请选择与导入的 Minecraft 版本兼容的 Java 可执行文件。";
    }

    private string JavaCompatibilityMessage(int required, int selected)
        => App.Localization.Translate(
            "server.dialog.java_incompatible",
            MinecraftVersionBox.Text.Trim(),
            required,
            selected);

    private string? ResolveOutputPath()
    {
        string directory = OutputDirectoryBox.Text.Trim();
        string name = OutputNameBox.Text.Trim();
        if (directory.Length == 0 || name.Length == 0 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            name += ".zip";
        }
        try
        {
            return Path.GetFullPath(Path.Combine(directory, name));
        }
        catch
        {
            return null;
        }
    }

    private sealed record CompatibilitySnapshot(
        CompatibilityAnalysisRequest Request,
        IReadOnlyDictionary<int, ServerModEntry> Entries);
}
