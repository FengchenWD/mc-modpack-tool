using System.Windows;
using System.Windows.Controls;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;
using MessageBox = McModpackTool.App.MessageBox;

namespace McModpackTool.App.Views;

public partial class ClientPackView
{
    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_working || _inputPickerOpen) return;
        _inputPickerOpen = true;
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = App.Localization["client.dialog.choose_directory"],
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(InputPathBox.Text) ? InputPathBox.Text : string.Empty,
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                InputPathBox.Text = dialog.SelectedPath;
        }
        finally
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => _inputPickerOpen = false));
        }
    }

    private async void Read_Click(object sender, RoutedEventArgs e) => await ReadSourceAsync();

    private async Task ReadSourceAsync()
    {
        if (_working) return;
        string path = InputPathBox.Text.Trim().Trim('"');
        ClearSourceState();
        if (!Directory.Exists(path))
        {
            SetStatus("client.ready");
            MessageBox.Show(App.Localization["client.dialog.read_failed"], App.Localization["common.error"],
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        CancellationToken cancellationToken = BeginOperation();
        SetWorking(true, "client.reading", indeterminate: true);
        try
        {
            Log("INFO", $"Read client directory: {path}");
            GameDirectoryDiscovery discovery = await ClientDirectoryScanner.DiscoverAsync(path, cancellationToken);
            if (discovery.RequiresInstanceDirectory)
            {
                MessageBox.Show(App.Localization["client.dialog.reselect_instance"], App.Localization["common.warning"],
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("client.ready");
                return;
            }
            if (discovery.VersionCandidates.Count == 0)
            {
                MessageBox.Show(App.Localization["client.dialog.no_version"], App.Localization["common.error"],
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("client.ready");
                return;
            }

            ServerVersionCandidate? candidate = discovery.VersionCandidates.Count == 1
                ? discovery.VersionCandidates[0]
                : VersionSelectionWindow.Select(Window.GetWindow(this), discovery.VersionCandidates);
            if (candidate is null)
            {
                SetStatus("client.ready");
                return;
            }

            ClientPackSource source = await ClientDirectoryScanner.ReadAsync(path, candidate, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplySource(source, path);
            Log("INFO", $"Client source ready: Minecraft {source.MinecraftVersion}, {source.LoaderType} {source.LoaderVersion}, items={source.Items.Count}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("client.cancelled");
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
            SetStatus("client.ready");
            MessageBox.Show($"{App.Localization["client.dialog.read_failed"]}\n\n{exception.Message}",
                App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
            EndOperation();
        }
    }

    private void ApplySource(ClientPackSource source, string inputPath)
    {
        _source = source;
        _readPath = Path.GetFullPath(inputPath);
        _applyingSource = true;
        try
        {
            InputPathBox.Text = _readPath;
            MinecraftVersionBox.Text = source.MinecraftVersion;
            LoaderBox.Text = source.LoaderType;
            LoaderVersionBox.Text = source.LoaderVersion;
        }
        finally { _applyingSource = false; }

        _defaultSelections.Clear();
        foreach (ClientContentEntry item in source.Items) _defaultSelections[item] = item.Selected;
        RebuildGroups();
        RefreshOverview();
        UpdateAutomaticOutputName(force: true);
        SetStatus("client.read_ready");
        RefreshBuildAvailability();
    }

    private void RebuildGroups()
    {
        _groups.Clear();
        if (_source is null) return;
        foreach (IGrouping<string, ClientContentEntry> group in _source.Items.GroupBy(item =>
                     item.Kind == ClientContentKinds.Other ? ClientContentKinds.ModData : item.Kind))
            _groups.Add(new ClientContentGroup(group.Key, group, SelectionChanged));
    }

    private void InputPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingSource || _source is null) return;
        if (PathsEqual(InputPathBox.Text, _readPath)) return;
        ClearSourceState();
        SetStatus("client.path_changed");
    }

    private void ClearSourceState()
    {
        _source = null;
        _readPath = string.Empty;
        _defaultSelections.Clear();
        _groups.Clear();
        MinecraftVersionBox.Clear();
        LoaderBox.Clear();
        LoaderVersionBox.Clear();
        OverviewBox.Clear();
        RefreshBuildAvailability();
    }

    private void OutputNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressOutputNameChange && !OutputNameBox.Text.Equals(_lastAutomaticName, StringComparison.Ordinal))
            _outputNameEdited = true;
        RefreshBuildAvailability();
    }

    private void SelectDefaults_Click(object sender, RoutedEventArgs e)
    {
        _batchSelection = true;
        try
        {
            foreach (ClientContentGroup group in _groups)
                group.SetSelections(row => _defaultSelections.GetValueOrDefault(row.Entry));
        }
        finally { _batchSelection = false; }
        SelectionChanged();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _batchSelection = true;
        try
        {
            foreach (ClientContentGroup group in _groups)
                group.SetSelections(_ => false);
        }
        finally { _batchSelection = false; }
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        if (_batchSelection) return;
        RefreshOverview();
        RefreshBuildAvailability();
    }

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingFormats) return;
        RefreshBuildAvailability();
    }

    private void Format_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingFormats || ModrinthFormatCheckBox is null || CurseForgeFormatCheckBox is null) return;
        if (ModrinthFormatCheckBox.IsChecked == true || CurseForgeFormatCheckBox.IsChecked == true)
        {
            RefreshBuildAvailability();
            return;
        }

        _updatingFormats = true;
        try
        {
            if (sender is CheckBox checkBox) checkBox.IsChecked = true;
        }
        finally { _updatingFormats = false; }
        MessageBox.Show(App.Localization["client.dialog.format_required"], App.Localization["common.warning"],
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e) => TryChooseOutputDirectory();

    private bool TryChooseOutputDirectory()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = App.Localization["dialog.choose_output"],
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(OutputDirectoryBox.Text) ? OutputDirectoryBox.Text : string.Empty,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
        OutputDirectoryBox.Text = dialog.SelectedPath;
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        SetStatus("status.canceling");
    }

    private async void Build_Click(object sender, RoutedEventArgs e) => await BuildAsync();

    private async Task BuildAsync()
    {
        if (_source is null || _working || !PathsEqual(InputPathBox.Text, _readPath))
        {
            MessageBox.Show(App.Localization["client.dialog.read_first"], App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ClientContentEntry[] selectedItems = _source.Items.Where(item => item.Selected).ToArray();
        if (selectedItems.Length == 0)
        {
            MessageBox.Show(App.Localization["client.dialog.content_required"], App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text) && !TryChooseOutputDirectory()) return;

        IReadOnlyList<string> formats = SelectedFormats();
        IReadOnlyList<string>? outputPaths = ResolveOutputPaths(formats);
        if (outputPaths is null)
        {
            MessageBox.Show(App.Localization["client.dialog.output_invalid"], App.Localization["common.error"],
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        bool anyExisting = outputPaths.Any(File.Exists);
        if (anyExisting && MessageBox.Show(App.Localization["dialog.overwrite"], App.Localization["common.warning"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        CancellationToken cancellationToken = BeginOperation();
        SetWorking(true, "client.building", indeterminate: false);
        var successful = new List<string>();
        var failures = new List<string>();
        try
        {
            int totalPhases = formats.Count * 4;
            for (int formatIndex = 0; formatIndex < formats.Count; formatIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string format = formats[formatIndex];
                string outputPath = outputPaths[formatIndex];
                int basePhase = formatIndex * 4;
                var progress = new Progress<ClientBuildPhase>(phase =>
                {
                    int phaseIndex = phase switch
                    {
                        ClientBuildPhase.MatchingPlatformFiles => 1,
                        ClientBuildPhase.CopyingOverrides => 2,
                        ClientBuildPhase.WritingManifest => 3,
                        ClientBuildPhase.CompressingArchive => 4,
                        _ => 0,
                    };
                    OperationProgress.Value = (basePhase + phaseIndex) / (double)totalPhases;
                    string key = phase switch
                    {
                        ClientBuildPhase.MatchingPlatformFiles => "client.building_match",
                        ClientBuildPhase.CopyingOverrides => "client.building_copy",
                        ClientBuildPhase.WritingManifest => "client.building_manifest",
                        ClientBuildPhase.CompressingArchive => "client.building_archive",
                        _ => "client.building",
                    };
                    SetStatus(key);
                });
                var request = new ClientBuildRequest
                {
                    Source = _source,
                    Format = format,
                    OutputPath = outputPath,
                    IncludedItems = selectedItems,
                    Overwrite = File.Exists(outputPath),
                };
                Log("INFO", $"Build {format}: {outputPath}");
                ClientBuildResult result = await _builder.BuildAsync(request, progress, cancellationToken);
                foreach (string warning in result.Warnings) Log("WARN", warning);
                if (result.Succeeded)
                {
                    successful.Add(outputPath);
                    Log("INFO", $"Client pack complete: remote={result.RemoteItems}, embedded={result.EmbeddedItems}");
                }
                else
                {
                    failures.AddRange(result.MissingFiles.Select(item => $"{Path.GetFileName(outputPath)}: {item}"));
                }
            }

            string detail = string.Join(Environment.NewLine, failures.Take(20).Select(item => $"- {item}"));
            if (failures.Count > 0 && successful.Count == 0)
            {
                MessageBox.Show(App.Localization.Translate("client.dialog.build_failed_detail", detail),
                    App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("client.ready");
                return;
            }

            bool partial = failures.Count > 0;
            SetStatus(partial ? "client.build_partial" : "client.build_complete");
            string locations = string.Join(Environment.NewLine, successful.Select(path => App.Localization.Translate("build.location", path)));
            string message = $"{App.Localization[partial ? "client.build_partial" : "client.build_complete"]}\n\n{locations}";
            if (partial)
                message += $"\n\n{App.Localization.Translate("client.dialog.build_failed_detail", detail)}";
            new BuildSuccessWindow(message, successful[0]) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            SetStatus("client.cancelled");
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
            SetStatus("client.ready");
            MessageBox.Show($"{App.Localization["client.dialog.build_failed"]}\n\n{exception.Message}",
                App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
            EndOperation();
        }
    }

    private IReadOnlyList<string> SelectedFormats()
    {
        var formats = new List<string>(2);
        if (ModrinthFormatCheckBox.IsChecked == true) formats.Add(ClientPackFormats.Modrinth);
        if (CurseForgeFormatCheckBox.IsChecked == true) formats.Add(ClientPackFormats.CurseForge);
        return formats;
    }

    private IReadOnlyList<string>? ResolveOutputPaths(IReadOnlyList<string> formats)
    {
        string directory = OutputDirectoryBox.Text.Trim();
        string name = OutputNameBox.Text.Trim();
        if (directory.Length == 0 || name.Length == 0) return null;
        if (name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) name = name[..^7];
        else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        try
        {
            return formats.Select(format => Path.GetFullPath(Path.Combine(directory,
                name + (format == ClientPackFormats.Modrinth ? ".mrpack" : ".zip")))).ToArray();
        }
        catch { return null; }
    }

    private void SetWorking(bool working, string? statusKey = null, bool indeterminate = false)
    {
        _working = working;
        InputPathBox.IsEnabled = !working;
        BrowseInputButton.IsEnabled = !working;
        ReadButton.IsEnabled = !working;
        ContentGroupsControl.IsEnabled = !working;
        SelectDefaultsButton.IsEnabled = !working;
        ClearAllButton.IsEnabled = !working;
        ModrinthFormatCheckBox.IsEnabled = !working;
        CurseForgeFormatCheckBox.IsEnabled = !working;
        OutputDirectoryBox.IsEnabled = !working;
        BrowseOutputButton.IsEnabled = !working;
        OutputNameBox.IsEnabled = !working;
        CancelButton.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = working;
        OperationProgress.IsIndeterminate = working && indeterminate;
        OperationProgress.Value = working ? 0 : 1;
        if (statusKey is not null) SetStatus(statusKey);
        RefreshBuildAvailability();
    }

    private void RefreshBuildAvailability()
    {
        if (BuildButton is null) return;
        BuildButton.IsEnabled = !_working && _source is not null && PathsEqual(InputPathBox.Text, _readPath) &&
            _source.Items.Any(item => item.Selected) && SelectedFormats().Count > 0;
    }

    private void SetStatus(string key)
    {
        _statusKey = key;
        StatusText.Text = App.Localization[key];
    }

    private CancellationToken BeginOperation()
    {
        _operationCts = new CancellationTokenSource();
        _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _operationCts.Token;
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
        TaskCompletionSource? completion = _operationCompletion;
        _operationCompletion = null;
        completion?.TrySetResult();
    }

    private void UpdateAutomaticOutputName(bool force)
    {
        if (_source is null || !force && _outputNameEdited) return;
        string generated = MigrationView.GenerateOutputPackName(
            _source.DisplayName, _source.MinecraftVersion, _source.MinecraftVersion);
        _suppressOutputNameChange = true;
        OutputNameBox.Text = generated;
        _suppressOutputNameChange = false;
        _lastAutomaticName = generated;
        _outputNameEdited = false;
    }

    private void RefreshOverview()
    {
        if (_source is null)
        {
            OverviewBox?.Clear();
            return;
        }
        OverviewBox.Text = App.Localization.Translate(
            "client.overview", _source.DisplayName, _source.MinecraftVersion, _source.LoaderType,
            _source.LoaderVersion, _source.Items.Count, _source.Items.Count(item => item.Selected));
    }

    private void Log(string level, string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {level,-5} {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return Path.GetFullPath(first.Trim().Trim('"')).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
