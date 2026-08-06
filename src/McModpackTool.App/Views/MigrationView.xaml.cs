using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using McModpackTool.App.Services;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;
using Microsoft.Win32;

namespace McModpackTool.App.Views;

public partial class MigrationView : UserControl
{
    private readonly CurseForgeClient _curseForge;
    private readonly ModrinthClient _modrinth;
    private readonly LoaderVersionService _loaderVersions;
    private readonly ContentTargetResolver _targetResolver;
    private readonly CompatibilityAnalyzer _compatibilityAnalyzer = new();
    private readonly ObservableCollection<ContentRow> _contentRows = [];
    private readonly ObservableCollection<CompatibilityRow> _compatibilityRows = [];
    private CancellationTokenSource? _operationCts;
    private TaskCompletionSource? _operationCompletion;
    private CancellationTokenSource? _loaderFetchCts;
    private readonly object _loaderFetchGate = new();
    private readonly HashSet<Task> _loaderFetchTasks = [];
    private long _loaderFetchVersion;
    private ModpackInfo? _pack;
    private CompatibilityReport? _report;
    private string _parsedInputPath = string.Empty;
    private string _temporaryRoot = string.Empty;
    private string _lastAutomaticName = string.Empty;
    private string _analysisSnapshot = string.Empty;
    private bool _working;
    private bool _suppressTargetEvents;
    private bool _suppressOutputNameEvents;
    private bool _outputNameEdited;
    private bool _servicesDisposed;
    private bool _shutdownStarted;
    private string _lastTargetMinecraft = string.Empty;
    private string _lastTargetLoader = string.Empty;
    private string _statusKey = "migration.ready";
    private object[] _statusArguments = [];
    private readonly HashSet<string> _shownDependencyWarnings = new(StringComparer.OrdinalIgnoreCase);

    public MigrationView()
    {
        InitializeComponent();
        DataContext = App.Localization;
        FilesGrid.ItemsSource = _contentRows;
        CompatibilityGrid.ItemsSource = _compatibilityRows;

        _curseForge = new CurseForgeClient(BuildSecrets.CurseForgeApiKey);
        _modrinth = new ModrinthClient();
        _loaderVersions = new LoaderVersionService(logWarning: message => Log("WARN", message));
        _targetResolver = new ContentTargetResolver(_curseForge, _modrinth);

        _suppressTargetEvents = true;
        MinecraftBox.Text = App.Settings.TargetMinecraft;
        SelectLoader(App.Settings.TargetLoaderType);
        LoaderVersionBox.Text = App.Settings.TargetLoaderVersion;
        OutputDirectoryBox.Text = App.Settings.OutputDirectory;
        _suppressTargetEvents = false;
        _lastTargetMinecraft = MinecraftBox.Text.Trim();
        _lastTargetLoader = SelectedLoader;

        App.Localization.LanguageChanged += (_, _) => Dispatcher.Invoke(RefreshLocalizedContent);
    }

    public async Task ShutdownAsync()
    {
        _shutdownStarted = true;
        _operationCts?.Cancel();
        Interlocked.Increment(ref _loaderFetchVersion);
        CancellationTokenSource? activeLoaderFetch = _loaderFetchCts;
        _loaderFetchCts = null;
        activeLoaderFetch?.Cancel();
        Task? operation = _operationCompletion?.Task;
        if (operation is not null)
        {
            try { await operation; }
            catch (OperationCanceledException) { }
        }
        while (true)
        {
            Task[] loaderFetches;
            lock (_loaderFetchGate)
                loaderFetches = [.. _loaderFetchTasks];
            if (loaderFetches.Length == 0) break;
            try { await Task.WhenAll(loaderFetches); }
            catch (OperationCanceledException) { }
        }
        if (_servicesDisposed) return;
        _servicesDisposed = true;
        CleanupTemporaryDirectory();
        _curseForge.Dispose();
        _modrinth.Dispose();
        _loaderVersions.Dispose();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_working || _operationCts is null) return;
        CancelButton.IsEnabled = false;
        SetStatus("status.canceling");
        _operationCts.Cancel();
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureIdle()) return;
        var dialog = new OpenFileDialog
        {
            Title = App.Localization["dialog.choose_pack"],
            Filter = $"{App.Localization["dialog.filter_modpacks"]} (*.zip;*.mrpack)|*.zip;*.mrpack|{App.Localization["dialog.filter_all"]} (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            SetInputPath(dialog.FileName, readImmediately: false);
    }

    private async void ReadPack_Click(object sender, RoutedEventArgs e) => await ReadPackAsync();

    private async Task ReadPackAsync()
    {
        if (!EnsureIdle()) return;
        string input = InputPathBox.Text.Trim();
        if (!IsSupportedPackPath(input) || !File.Exists(input))
        {
            MessageBox.Show(App.Localization["dialog.invalid_pack"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        CleanupTemporaryDirectory();
        _pack = null;
        _parsedInputPath = string.Empty;
        _shownDependencyWarnings.Clear();
        OverviewBox.Text = string.Empty;
        _contentRows.Clear();
        ResetAnalysisDisplay();
        bool fetchLoaderAfterRead = false;
        _operationCts = new CancellationTokenSource();
        SetWorking(true, "status.reading", indeterminate: true);
        string nextTemporaryRoot = Path.Combine(Path.GetTempPath(), "MCModpackTool", Guid.NewGuid().ToString("N"));
        try
        {
            Log("INFO", App.Localization.Translate("log.read_pack", input));
            var pack = await PackParser.ParseAsync(input, cancellationToken: _operationCts.Token);
            string overrides = Path.Combine(nextTemporaryRoot, "overrides");
            await PackParser.ExtractOverridesAsync(input, overrides, cancellationToken: _operationCts.Token);
            pack.OverridesDirectory = overrides;

            CleanupTemporaryDirectory();
            _temporaryRoot = nextTemporaryRoot;
            _pack = pack;
            _parsedInputPath = Path.GetFullPath(input);
            _report = null;
            _analysisSnapshot = string.Empty;
            _shownDependencyWarnings.Clear();

            _suppressTargetEvents = true;
            if (!string.IsNullOrWhiteSpace(pack.LoaderType)) SelectLoader(pack.LoaderType);
            LoaderVersionBox.Text = string.Empty;
            _suppressTargetEvents = false;
            _lastTargetMinecraft = MinecraftBox.Text.Trim();
            _lastTargetLoader = SelectedLoader;
            App.Settings.TargetLoaderType = SelectedLoader;
            App.Settings.TargetLoaderVersion = string.Empty;
            fetchLoaderAfterRead = MinecraftBox.Text.Trim().Length > 0 && SelectedLoader.Length > 0;

            if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
                OutputDirectoryBox.Text = Path.GetDirectoryName(input) ?? string.Empty;
            RefreshAutomaticOutputName(force: true);
            RefreshOverview();
            RefreshContentRows();
            ResetAnalysisDisplay();
            AnalyzeButton.IsEnabled = true;
            SetStatus("status.read_ready");
            Log("INFO", App.Localization.Translate("log.read_complete", pack.FormatType, pack.Items.Count, pack.OverridePaths.Count));
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(nextTemporaryRoot);
            SetStatus("status.cancelled");
            Log("WARN", App.Localization["log.read_cancelled"]);
        }
        catch (Exception exception)
        {
            TryDeleteDirectory(nextTemporaryRoot);
            Log("ERROR", exception.ToString());
            MessageBox.Show(App.Localization["dialog.read_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetWorking(false);
        }
        if (fetchLoaderAfterRead && _pack is not null)
            await FetchLoaderVersionAsync(showFailure: false, finalStatusKey: "status.read_ready");
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureIdle()) return;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = App.Localization["dialog.choose_output"],
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(OutputDirectoryBox.Text) ? OutputDirectoryBox.Text : string.Empty
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputDirectoryBox.Text = dialog.SelectedPath;
    }

    private async void FetchLoader_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureIdle()) return;
        await FetchLoaderVersionAsync(showFailure: true, finalStatusKey: _pack is null ? "migration.ready" : "status.read_ready");
    }

    private async Task FetchLoaderVersionAsync(bool showFailure, string finalStatusKey)
    {
        if (_shutdownStarted) return;
        string minecraft = MinecraftBox.Text.Trim();
        string loader = SelectedLoader;
        string startingValue = LoaderVersionBox.Text.Trim();
        if (minecraft.Length == 0 || loader.Length == 0) return;

        long requestVersion = Interlocked.Increment(ref _loaderFetchVersion);
        _loaderFetchCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loaderFetchCts = cancellation;
        FetchLoaderButton.IsEnabled = false;
        SetStatus("status.loader_fetching");

        async Task FetchAsync()
        {
            try
            {
                string version = await _loaderVersions.FetchLatestAsync(loader, minecraft, cancellation.Token);
                if (requestVersion != _loaderFetchVersion
                    || !string.Equals(MinecraftBox.Text.Trim(), minecraft, StringComparison.Ordinal)
                    || !string.Equals(SelectedLoader, loader, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(LoaderVersionBox.Text.Trim(), startingValue, StringComparison.Ordinal))
                    return;
                if (version.Length > 0)
                {
                    LoaderVersionBox.Text = version;
                    Log("INFO", App.Localization.Translate("log.loader_latest", loader, minecraft, version));
                }
                else if (showFailure)
                {
                    MessageBox.Show(App.Localization["dialog.no_loader_version"], App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (requestVersion == _loaderFetchVersion)
                {
                    if (!_shutdownStarted)
                    {
                        SetStatus(finalStatusKey);
                        FetchLoaderButton.IsEnabled = !_working;
                    }
                    _loaderFetchCts = null;
                }
                cancellation.Dispose();
            }
        }

        Task fetchTask = FetchAsync();
        lock (_loaderFetchGate)
            _loaderFetchTasks.Add(fetchTask);
        try
        {
            await fetchTask;
        }
        finally
        {
            lock (_loaderFetchGate)
                _loaderFetchTasks.Remove(fetchTask);
        }
    }

    private void InputPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string current = InputPathBox.Text.Trim();
        if (_pack is not null && !PathsEqual(current, _parsedInputPath))
        {
            CleanupTemporaryDirectory();
            _pack = null;
            _parsedInputPath = string.Empty;
            _shownDependencyWarnings.Clear();
            OverviewBox.Text = string.Empty;
            _contentRows.Clear();
            ResetAnalysisDisplay();
            SetStatus("migration.ready");
        }
        RefreshAutomaticOutputName(force: false);
        AnalyzeButton.IsEnabled = _pack is not null && PathsEqual(current, _parsedInputPath);
    }

    private void Target_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTargetEvents) return;
        if (ReferenceEquals(sender, MinecraftBox)) ClearLoaderVersion();
        HandleTargetChanged();
    }

    private void Target_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTargetEvents || !IsLoaded) return;
        ClearLoaderVersion();
        HandleTargetChanged();
        _ = FetchLoaderVersionAsync(showFailure: false, finalStatusKey: _pack is null ? "migration.ready" : "status.read_ready");
    }

    private void HandleTargetChanged()
    {
        string minecraft = MinecraftBox.Text.Trim();
        string loader = SelectedLoader;
        bool environmentChanged = !string.Equals(_lastTargetMinecraft, minecraft, StringComparison.Ordinal)
            || !string.Equals(_lastTargetLoader, loader, StringComparison.OrdinalIgnoreCase);
        _lastTargetMinecraft = minecraft;
        _lastTargetLoader = loader;
        if (environmentChanged && _pack is not null)
        {
            foreach (ContentItem item in _pack.Items.Where(item => !item.Excluded && !item.Passthrough)) item.ResetTarget();
            RefreshContentRows();
        }
        RefreshAutomaticOutputName(force: false);
        App.Settings.TargetMinecraft = minecraft;
        App.Settings.TargetLoaderType = loader;
        App.Settings.TargetLoaderVersion = LoaderVersionBox.Text.Trim();
        InvalidateAnalysis();
        if (_pack is not null) SetStatus("status.target_changed");
    }

    private void ClearLoaderVersion()
    {
        Interlocked.Increment(ref _loaderFetchVersion);
        CancellationTokenSource? activeFetch = _loaderFetchCts;
        _loaderFetchCts = null;
        activeFetch?.Cancel();
        FetchLoaderButton.IsEnabled = !_working;
        if (_statusKey == "status.loader_fetching")
            SetStatus(_pack is null ? "migration.ready" : "status.target_changed");
        if (LoaderVersionBox.Text.Length == 0) return;
        _suppressTargetEvents = true;
        LoaderVersionBox.Clear();
        _suppressTargetEvents = false;
    }

    private async void MinecraftBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_working || MinecraftBox.Text.Trim().Length == 0 || SelectedLoader.Length == 0) return;
        await FetchLoaderVersionAsync(showFailure: false, finalStatusKey: _pack is null ? "migration.ready" : "status.read_ready");
    }

    private async void MinecraftBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _working) return;
        e.Handled = true;
        await FetchLoaderVersionAsync(showFailure: false, finalStatusKey: _pack is null ? "migration.ready" : "status.read_ready");
    }

    private void OutputNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOutputNameEvents) return;
        _outputNameEdited = !string.Equals(OutputNameBox.Text.Trim(), _lastAutomaticName, StringComparison.Ordinal);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e) => UpdateDropState(e);
    private void DropZone_DragOver(object sender, DragEventArgs e) => UpdateDropState(e);
    private void DropZone_DragLeave(object sender, DragEventArgs e) =>
        DropZone.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

    private void UpdateDropState(DragEventArgs e)
    {
        string? path = FirstDroppedFile(e.Data);
        e.Effects = !_working && IsSupportedPackPath(path) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropZone.SetResourceReference(
            Border.BorderBrushProperty,
            e.Effects == DragDropEffects.Copy ? "AccentBrush" : "DangerBrush");
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        e.Handled = true;
        if (_working)
        {
            MessageBox.Show(App.Localization["dialog.busy"], App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            string? path = FirstDroppedFile(e.Data);
            if (!IsSupportedPackPath(path))
            {
                MessageBox.Show(App.Localization["dialog.invalid_pack"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            SetInputPath(path!, readImmediately: false);
            await ReadPackAsync();
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
            MessageBox.Show(App.Localization["dialog.drop_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FilesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) is not DataGridRow row) return;
        row.IsSelected = true;
        FilesGrid.CurrentItem = row.Item;
        row.Focus();
    }

    private void FilesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _working || FilesGrid.SelectedItem is not ContentRow) return;
        e.Handled = true;
        ToggleExclude_Click(FilesGrid, e);
    }

    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => BrowseInput_Click(sender, e);

    private void SetInputPath(string path, bool readImmediately)
    {
        InputPathBox.Text = Path.GetFullPath(path);
        RefreshAutomaticOutputName(force: true);
        if (readImmediately) _ = ReadPackAsync();
    }

    private static string? FirstDroppedFile(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths)
                return null;
            return paths.Length == 1 ? paths[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedPackPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void RefreshAutomaticOutputName(bool force)
    {
        if (!force && _outputNameEdited) return;
        string manifestName = _pack is null ? string.Empty : RawString(_pack.RawData, "name", string.Empty);
        string sourceName = SelectSourcePackName(InputPathBox.Text, manifestName);
        string generated = GenerateOutputPackName(sourceName, _pack?.MinecraftVersion ?? string.Empty, MinecraftBox.Text.Trim());
        _suppressOutputNameEvents = true;
        OutputNameBox.Text = generated;
        _suppressOutputNameEvents = false;
        _lastAutomaticName = generated;
        _outputNameEdited = false;
    }

    public static string SelectSourcePackName(string? inputPath, string? manifestName)
    {
        try
        {
            string fileName = Path.GetFileNameWithoutExtension(inputPath?.Trim() ?? string.Empty).Trim();
            if (fileName.Length > 0) return fileName;
        }
        catch
        {
            // The text box may briefly contain an incomplete path while the user is typing.
        }
        return manifestName?.Trim() ?? string.Empty;
    }

    public static string GenerateOutputPackName(string sourceName, string sourceMinecraft, string targetMinecraft)
    {
        string name = (sourceName ?? string.Empty).Trim();
        string source = (sourceMinecraft ?? string.Empty).Trim();
        string target = (targetMinecraft ?? string.Empty).Trim();
        string newPack = App.Localization["migration.new_pack"];
        if (name.Length == 0) return target.Length == 0 ? newPack : $"{target} {newPack}";
        if (target.Length == 0) return name;

        string candidate = name;
        bool replaced = false;
        if (source.Length > 0)
        {
            var pattern = new Regex($@"(?<![\d.]){Regex.Escape(source)}(?![\d.])", RegexOptions.CultureInvariant);
            candidate = pattern.Replace(name, target, 1);
            replaced = !string.Equals(candidate, name, StringComparison.Ordinal);
        }
        else
        {
            var match = Regex.Match(name, @"^(?<prefix>\s*)(?<version>\d+\.\d+(?:\.\d+)?)(?=$|[\s_-])", RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var group = match.Groups["version"];
                candidate = name[..group.Index] + target + name[(group.Index + group.Length)..];
                replaced = true;
            }
        }
        if (!replaced) candidate = $"{target} {name}";
        return candidate.Equals(name, StringComparison.OrdinalIgnoreCase) ? name + App.Localization["migration.migrated_suffix"] : candidate;
    }

    private void RefreshOverview()
    {
        if (_pack is null) { OverviewBox.Text = string.Empty; return; }
        string name = RawString(_pack.RawData, "name", Path.GetFileNameWithoutExtension(_parsedInputPath));
        int mods = _pack.Items.Count(item => item.Category == "mod");
        int resources = _pack.Items.Count(item => item.Category == "resourcepack");
        int shaders = _pack.Items.Count(item => item.Category == "shaderpack");
        OverviewBox.Text = App.Localization.Translate(
            "migration.overview",
            name,
            _pack.FormatType,
            _pack.MinecraftVersion,
            _pack.LoaderType,
            _pack.LoaderVersion,
            mods,
            resources,
            shaders);
    }

    private void RefreshContentRows()
    {
        _contentRows.Clear();
        if (_pack is null) return;
        foreach (var item in _pack.Items)
            _contentRows.Add(ContentRow.From(item, App.Localization));
    }

    private void RefreshLocalizedContent()
    {
        RefreshOverview();
        RefreshContentRows();
        if (_report is not null) RenderCompatibilityReport(_report);
        else CompatibilitySummary.Text = App.Localization["migration.report_hint"];
        SetStatus(_statusKey, _statusArguments);
        RefreshAutomaticOutputName(force: false);
    }

    private void InvalidateAnalysis()
    {
        _analysisSnapshot = string.Empty;
        _report = null;
        _compatibilityRows.Clear();
        CompatibilityDetail.Visibility = Visibility.Collapsed;
        BuildButton.IsEnabled = false;
        RefreshAnalysisButton.IsEnabled = false;
        if (_pack is not null) CompatibilitySummary.Text = App.Localization["migration.report_hint"];
    }

    private void ResetAnalysisDisplay()
    {
        _compatibilityRows.Clear();
        CompatibilityDetail.Visibility = Visibility.Collapsed;
        CompatibilitySummary.Text = App.Localization["migration.report_hint"];
        RefreshAnalysisButton.IsEnabled = false;
        BuildButton.IsEnabled = false;
    }

    private string SelectedLoader => (LoaderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? string.Empty;

    private void SelectLoader(string loader)
    {
        foreach (ComboBoxItem item in LoaderCombo.Items)
        {
            if (string.Equals(item.Content?.ToString(), loader, StringComparison.OrdinalIgnoreCase))
            {
                LoaderCombo.SelectedItem = item;
                return;
            }
        }
        LoaderCombo.SelectedIndex = 0;
    }

    private bool EnsureIdle()
    {
        if (!_working) return true;
        MessageBox.Show(App.Localization["dialog.busy"], App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void SetWorking(bool working, string? status = null, bool indeterminate = false)
    {
        bool wasWorking = _working;
        if (working && !wasWorking)
            _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _working = working;

        InputPathBox.IsEnabled = !working;
        BrowseInputButton.IsEnabled = !working;
        DropZone.IsEnabled = !working;
        MinecraftBox.IsEnabled = !working;
        LoaderCombo.IsEnabled = !working;
        LoaderVersionBox.IsEnabled = !working;
        OutputDirectoryBox.IsEnabled = !working;
        BrowseOutputButton.IsEnabled = !working;
        OutputNameBox.IsEnabled = !working;
        FilesGrid.IsEnabled = !working;
        ReadButton.IsEnabled = !working;
        AnalyzeButton.IsEnabled = !working && _pack is not null && PathsEqual(InputPathBox.Text, _parsedInputPath);
        bool reportIsCurrent = !working
            && _pack is not null
            && _report is not null
            && _analysisSnapshot.Length > 0
            && PathsEqual(InputPathBox.Text, _parsedInputPath)
            && string.Equals(_analysisSnapshot, CurrentSnapshot(), StringComparison.Ordinal);
        RefreshAnalysisButton.IsEnabled = reportIsCurrent;
        BuildButton.IsEnabled = reportIsCurrent;
        FetchLoaderButton.IsEnabled = !working && _loaderFetchCts is null;
        CancelButton.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = working && _operationCts is not null && !_operationCts.IsCancellationRequested;
        OperationProgress.IsIndeterminate = working && indeterminate;
        if (!working)
        {
            OperationProgress.IsIndeterminate = false;
            OperationProgress.Value = 0;
        }
        if (!string.IsNullOrWhiteSpace(status)) SetStatus(status);
        if (!working && wasWorking)
        {
            TaskCompletionSource? completion = _operationCompletion;
            _operationCompletion = null;
            completion?.TrySetResult();
        }
    }

    private void Log(string level, string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    private void SetStatus(string key, params object[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        StatusText.Text = arguments.Length == 0 ? App.Localization[key] : App.Localization.Translate(key, arguments);
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string RawString(JsonObject data, string key, string fallback)
    {
        try { return data[key]?.GetValue<string>() is { Length: > 0 } value ? value : fallback; }
        catch { return fallback; }
    }

    private void CleanupTemporaryDirectory()
    {
        string path = _temporaryRoot;
        _temporaryRoot = string.Empty;
        TryDeleteDirectory(path);
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MCModpackTool")) + Path.DirectorySeparatorChar;
            if (full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ContentRow(ContentItem Item, string Name, string Source, string Category, string Status, string Action)
    {
        public static ContentRow From(ContentItem item, LocalizationService localization)
        {
            string categoryKey = $"category.{item.Category}";
            string category = localization[categoryKey];
            if (category == categoryKey) category = item.Category;

            string status = item.Excluded
                ? localization["item_status.excluded"]
                : LocalizeStatus(item.Status, localization);
            if (!item.Excluded && item.Disabled)
                status = $"{localization["item_status.disabled"]} · {status}";
            if (!item.Excluded && (item.Status is "warning" or "not_found") && !string.IsNullOrWhiteSpace(item.Note))
                status = $"{status} · {LocalizeNote(item.Note, localization)}";

            return new ContentRow(
                item,
                string.IsNullOrWhiteSpace(item.Name) ? item.FileName : item.Name,
                string.IsNullOrWhiteSpace(item.Source) ? "-" : item.Source,
                category,
                status,
                item.Excluded ? localization["action.restore"] : localization["action.exclude"]);
        }

        private static string LocalizeStatus(string status, LocalizationService localization)
        {
            string key = $"item_status.{status}";
            string value = localization[key];
            return value == key ? status : value;
        }

        private static string LocalizeNote(string note, LocalizationService localization)
        {
            var parts = note.Split('；', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return string.Join("; ", parts.Select(part => LocalizeNotePart(part, localization)));
        }

        private static string LocalizeNotePart(string note, LocalizationService localization)
        {
            if (note.StartsWith("仅 ", StringComparison.Ordinal) && note.EndsWith(" 版", StringComparison.Ordinal))
                return localization.Translate("note.prerelease", note[2..^2]);
            return note switch
            {
                "目标环境未变化，保留原文件" => localization["note.preserved"],
                "未配置 CurseForge API Key，已跳过 CurseForge 备用搜索" => localization["note.no_cf_key"],
                "平台项目或版本不存在" => localization["note.project_missing"],
                "CurseForge 项目 ID 无效" => localization["note.invalid_cf_id"],
                "原 CurseForge 项目没有目标版本" or "原 Modrinth 项目没有可用的目标版本"
                    or "哈希对应项目没有可用的目标版本" or "候选项目没有目标版本" => localization["note.no_target"],
                "无法验证原 CurseForge 文件身份" => localization["note.identity_unverified"],
                "没有高置信度的平台身份匹配" or "没有高置信度的 CurseForge 匹配" => localization["note.no_confident_match"],
                "目标版本缺少可用主文件" => localization["note.no_main_file"],
                _ => note
            };
        }
    }

    private sealed record CompatibilityRow(CompatibilityIssue Issue, string Severity, string Scope, string Item, string Message);
}
