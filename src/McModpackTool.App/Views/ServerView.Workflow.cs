using System.Text;
using System.Net.Http;
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
        if (_source is null || _working || !PathsEqual(InputPathBox.Text, _readPath))
        {
            MessageBox.Show(App.Localization["server.dialog.prepare_first"], App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string targetMinecraft = TargetVersionBox.Text.Trim();
        if (targetMinecraft.Length == 0)
        {
            MessageBox.Show(App.Localization["server.dialog.target_required"], App.Localization["common.error"],
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_source.InputKind == ServerInputKinds.Directory &&
            !targetMinecraft.Equals(_source.MinecraftVersion, StringComparison.Ordinal))
        {
            TargetVersionBox.Text = _source.MinecraftVersion;
            return;
        }

        InvalidatePreparation();
        CancellationToken cancellationToken = BeginOperation();
        SetWorking(true, "server.preparing", indeterminate: true);
        try
        {
            string targetLoaderVersion = await ResolveTargetLoaderVersionAsync(targetMinecraft, cancellationToken);
            if (_source.InputKind != ServerInputKinds.Directory &&
                !string.IsNullOrWhiteSpace(_source.LoaderType) &&
                string.IsNullOrWhiteSpace(targetLoaderVersion))
            {
                MessageBox.Show(App.Localization["server.dialog.loader_version_unavailable"],
                    App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LoaderVersionBox.Text = targetLoaderVersion;

            if (_source.ManifestPack is not null)
            {
                foreach (ServerModEntry entry in _source.Mods.Where(entry => entry.ContentItem is not null))
                {
                    entry.ContentItem!.Excluded = !entry.Selected;
                }
                Log("INFO", $"Resolve target mods: Minecraft {targetMinecraft}, {_source.LoaderType}");
                await _targetResolver.ResolveAsync(
                    _source.ManifestPack,
                    targetMinecraft,
                    _source.LoaderType,
                    pendingOnly: false,
                    cancellationToken: cancellationToken);
                ModsGrid.Items.Refresh();
                if (!PromptForMissingTargets())
                {
                    return;
                }
            }

            CompatibilitySnapshot snapshot = await CreateCompatibilitySnapshotAsync(
                targetMinecraft,
                targetLoaderVersion,
                cancellationToken);
            CompatibilityReport report = await Task.Run(
                () => _compatibilityAnalyzer.Analyze(snapshot.Request, cancellationToken),
                cancellationToken);
            ShowMissingDependencyNotice(report);
            if (!await ResolveBlockingIssuesAsync(report, snapshot, targetMinecraft, targetLoaderVersion, cancellationToken))
            {
                return;
            }

            await RefreshCoreOptionsAsync(cancellationToken);
            if (_coreRows.Count == 0)
            {
                MessageBox.Show(App.Localization["server.core_none"], App.Localization["common.warning"],
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show($"{App.Localization["dialog.analysis_failed"]}\n\n{exception.Message}",
                App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
            EndOperation();
        }
    }

    private async Task<string> ResolveTargetLoaderVersionAsync(
        string targetMinecraft,
        CancellationToken cancellationToken)
    {
        if (_source is null || string.IsNullOrWhiteSpace(_source.LoaderType) ||
            targetMinecraft.Equals(_source.MinecraftVersion, StringComparison.Ordinal))
        {
            return _source?.LoaderVersion ?? string.Empty;
        }
        try
        {
            string version = await _loaderVersions.FetchLatestAsync(
                _source.LoaderType,
                targetMinecraft,
                cancellationToken);
            Log("INFO", $"Target loader: {_source.LoaderType} {version}");
            return version;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException)
        {
            Log("WARN", $"Could not query the target loader version: {exception.Message}");
            return string.Empty;
        }
    }

    private bool PromptForMissingTargets()
    {
        if (_source is null)
        {
            return false;
        }
        bool unresolved = false;
        foreach (ServerModEntry entry in _source.Mods.Where(entry =>
                     entry.Selected && entry.ContentItem?.Status == "not_found"))
        {
            MessageBoxResult answer = MessageBox.Show(
                App.Localization.Translate("resolution.not_found", entry.Name),
                App.Localization["common.warning"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                entry.Selected = false;
                entry.ContentItem!.Excluded = true;
                ServerModRow? row = _modRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Entry, entry));
                if (row is not null)
                {
                    row.Selected = false;
                }
            }
            else
            {
                unresolved = true;
            }
        }
        if (unresolved)
        {
            MessageBox.Show(App.Localization.Translate("resolution.blocked", 1), App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return !unresolved;
    }

    private async Task<CompatibilitySnapshot> CreateCompatibilitySnapshotAsync(
        string targetMinecraft,
        string targetLoaderVersion,
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
                TargetMinecraftVersion = targetMinecraft,
                SourceLoader = _source.LoaderType,
                TargetLoader = _source.LoaderType,
                SourceLoaderVersion = _source.LoaderVersion,
                TargetLoaderVersion = targetLoaderVersion,
                TargetFormat = "server",
            };
            return new CompatibilitySnapshot(request, entries);
        }, cancellationToken);
    }

    private async Task<bool> ResolveBlockingIssuesAsync(
        CompatibilityReport report,
        CompatibilitySnapshot snapshot,
        string targetMinecraft,
        string targetLoaderVersion,
        CancellationToken cancellationToken)
    {
        var prompted = new HashSet<ServerModEntry>();
        bool unresolved = false;
        foreach (CompatibilityIssue issue in report.Issues.Where(issue => issue.Severity == CompatibilitySeverity.Error))
        {
            ServerModEntry? entry = FindIssueEntry(issue, snapshot.Entries);
            if (entry is null || !entry.Selected || !prompted.Add(entry))
            {
                unresolved |= entry is null;
                continue;
            }
            string details = string.IsNullOrWhiteSpace(issue.Message) ? issue.Code : issue.Message;
            MessageBoxResult answer = MessageBox.Show(
                App.Localization.Translate("resolution.incompatible", entry.Name, $"- {details}"),
                App.Localization["common.warning"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                ServerModRow? row = _modRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Entry, entry));
                if (row is not null)
                {
                    row.Selected = false;
                }
                else
                {
                    entry.Selected = false;
                    if (entry.ContentItem is not null) entry.ContentItem.Excluded = true;
                }
            }
            else
            {
                unresolved = true;
            }
        }
        if (unresolved)
        {
            MessageBox.Show(App.Localization.Translate("resolution.blocked", report.Counts.GetValueOrDefault(CompatibilitySeverity.Error)),
                App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (prompted.Count == 0)
        {
            return !report.HasErrors;
        }

        CompatibilitySnapshot refreshed = await CreateCompatibilitySnapshotAsync(
            targetMinecraft, targetLoaderVersion, cancellationToken);
        CompatibilityReport refreshedReport = await Task.Run(
            () => _compatibilityAnalyzer.Analyze(refreshed.Request, cancellationToken), cancellationToken);
        ShowMissingDependencyNotice(refreshedReport);
        if (refreshedReport.HasErrors)
        {
            MessageBox.Show(App.Localization.Translate("resolution.blocked", refreshedReport.Counts.GetValueOrDefault(CompatibilitySeverity.Error)),
                App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private static ServerModEntry? FindIssueEntry(
        CompatibilityIssue issue,
        IReadOnlyDictionary<int, ServerModEntry> entries)
    {
        if (issue.Evidence.TryGetValue("item_index", out object? value) && TryConvertIndex(value, out int index) &&
            entries.TryGetValue(index, out ServerModEntry? entry))
        {
            return entry;
        }
        return entries.Values.FirstOrDefault(entry =>
            entry.Name.Equals(issue.Item, StringComparison.OrdinalIgnoreCase));
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
        string targetMinecraft = TargetVersionBox.Text.Trim();
        ServerCoreCatalogResult catalog = await _coreService.GetAvailableAsync(
            new ServerCoreQuery
            {
                MinecraftVersion = targetMinecraft,
                LoaderType = _source.LoaderType,
                LoaderVersion = LoaderVersionBox.Text.Trim(),
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
        string? javaExecutable = ResolveJavaExecutable(coreRow.Option);
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
            TargetMinecraftVersion = TargetVersionBox.Text.Trim(),
            TargetLoaderType = _source.LoaderType,
            TargetLoaderVersion = LoaderVersionBox.Text.Trim(),
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
            var progress = new Progress<string>(message => Log("INFO", message));
            ServerBuildResult result = await _builder.BuildAsync(
                request,
                coreRow.Option,
                javaExecutable,
                progress,
                cancellationToken);
            if (!result.Succeeded)
            {
                string missing = string.Join(Environment.NewLine, result.MissingFiles.Take(20).Select(item => $"- {item}"));
                MessageBox.Show(App.Localization.Translate("server.dialog.blocked", missing),
                    App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            string message = $"{App.Localization["server.build_complete"]}\n\n{App.Localization.Translate("build.location", outputPath)}";
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
            MessageBox.Show($"{App.Localization["server.dialog.build_failed"]}\n\n{exception.Message}",
                App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
            EndOperation();
        }
    }

    private string? ResolveJavaExecutable(ServerCoreOption option)
    {
        if (option.InstallStrategy != ServerCoreInstallStrategy.JavaInstaller)
        {
            return string.Empty;
        }

        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            string candidate = Path.Combine(javaHome.Trim().Trim('"'), "bin", "java.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim('"'), "java.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries and let the user choose Java below.
                }
            }
        }

        MessageBox.Show(App.Localization["server.dialog.java_required"], App.Localization["common.warning"],
            MessageBoxButton.OK, MessageBoxImage.Warning);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = App.Localization["server.dialog.choose_java"],
            Filter = "Java executable (java.exe)|java.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

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
