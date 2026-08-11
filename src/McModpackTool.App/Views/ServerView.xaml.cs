using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using McModpackTool.App.Services;
using McModpackTool.Core.Compatibility;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.App.Views;

public partial class ServerView : UserControl
{
    private readonly CurseForgeClient _curseForge;
    private readonly ModrinthClient _modrinth;
    private readonly ServerModSupportResolver _supportResolver;
    private readonly CompatibilityAnalyzer _compatibilityAnalyzer = new();
    private readonly ServerArchiveSourceReader _archiveReader;
    private readonly ServerCoreService _coreService;
    private readonly ServerPackBuilder _builder;
    private readonly JavaRuntimeService _javaRuntimeService = new();
    private readonly ModeState _directoryState = new();
    private readonly ModeState _archiveState = new();
    private ModeState _activeState;
    private CancellationTokenSource? _operationCts;
    private TaskCompletionSource? _operationCompletion;
    private bool _working;
    private bool _suppressOutputNameChange;
    private bool _restoringMode;
    private bool _disposed;
    // A modal picker can return while the originating mouse event is still routing.
    // Keep the picker single-entry so that event replay cannot open it twice.
    private bool _inputPickerOpen;
    private bool _downloadActive;
    private double _downloadBytesPerSecond;

    private ObservableCollection<ServerModRow> _modRows => _activeState.ModRows;
    private ObservableCollection<CoreRow> _coreRows => _activeState.CoreRows;
    private ObservableCollection<WorldRow> _worldRows => _activeState.WorldRows;
    private ObservableCollection<JavaRuntimeInfo> _javaRuntimes => _activeState.JavaRuntimes;
    private ServerPackSource? _source { get => _activeState.Source; set => _activeState.Source = value; }
    private string _readPath { get => _activeState.ReadPath; set => _activeState.ReadPath = value; }
    private string _lastAutomaticName { get => _activeState.LastAutomaticName; set => _activeState.LastAutomaticName = value; }
    private string _preparedSnapshot { get => _activeState.PreparedSnapshot; set => _activeState.PreparedSnapshot = value; }
    private string _statusKey { get => _activeState.StatusKey; set => _activeState.StatusKey = value; }
    private bool _outputNameEdited { get => _activeState.OutputNameEdited; set => _activeState.OutputNameEdited = value; }
    private string _selectedJavaPath { get => _activeState.SelectedJavaPath; set => _activeState.SelectedJavaPath = value; }
    private int _recommendedJavaMajor { get => _activeState.RecommendedJavaMajor; set => _activeState.RecommendedJavaMajor = value; }
    private bool _suppressJavaSelection;
    private bool _suppressModSelectionRefresh;

    public ServerView()
    {
        _activeState = _directoryState;
        InitializeComponent();
        DataContext = App.Localization;
        ModsGrid.ItemsSource = _modRows;
        CoreCombo.ItemsSource = _coreRows;
        WorldCombo.ItemsSource = _worldRows;
        JavaCombo.ItemsSource = _javaRuntimes;

        _curseForge = new CurseForgeClient(BuildSecrets.CurseForgeApiKey);
        _modrinth = new ModrinthClient();
        _supportResolver = new ServerModSupportResolver(
            _modrinth,
            message => Dispatcher.Invoke(() => Log("WARN", message)));
        _archiveReader = new ServerArchiveSourceReader(_curseForge);
        _coreService = new ServerCoreService(logWarning: message => Dispatcher.Invoke(() => Log("WARN", message)));
        _builder = new ServerPackBuilder(_coreService);

        App.Localization.LanguageChanged += Localization_LanguageChanged;
        OutputNameBox.TextChanged += OutputNameBox_TextChanged;
        Unloaded += ServerView_Unloaded;
        ApplySourceMode();
        ApplyJavaLocalization();
        RefreshLocalizedRows();
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        App.Localization.LanguageChanged -= Localization_LanguageChanged;
        _operationCts?.Cancel();
        Task? pendingOperation = _operationCompletion?.Task;
        if (pendingOperation is not null)
        {
            await pendingOperation;
        }
        CleanupAllSources();
        _builder.Dispose();
        _coreService.Dispose();
        _curseForge.Dispose();
        _modrinth.Dispose();
    }

    private bool ArchiveMode => ArchiveModeButton?.IsChecked == true;

    private void SourceMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && InputPathBox is null)
        {
            return;
        }
        if (_working)
        {
            return;
        }
        ModeState next = ArchiveMode ? _archiveState : _directoryState;
        if (!ReferenceEquals(next, _activeState))
        {
            CaptureActiveMode();
            _activeState = next;
            RestoreActiveMode();
        }
        ApplySourceMode();
    }

    private void ApplySourceMode()
    {
        if (InputLabel is null)
        {
            return;
        }
        InputLabel.Text = App.Localization[ArchiveMode ? "server.pack_path" : "server.source_path"];
        BrowseInputButton.Content = App.Localization[ArchiveMode ? "server.choose_pack" : "server.choose_directory"];
        DropZone.Visibility = ArchiveMode ? Visibility.Visible : Visibility.Collapsed;
        VersionModeHint.Text = App.Localization["server.version_hint"];
        MinecraftVersionBox.IsReadOnly = true;
    }

    private void ApplyJavaLocalization()
    {
        if (ChooseJavaButton is null)
        {
            return;
        }
        ChooseJavaButton.Content = App.Localization["server.browse"];
        RefreshJavaHint();
    }

    private void RefreshJavaHint()
    {
        if (JavaHintText is null)
        {
            return;
        }
        int recommended = _recommendedJavaMajor <= 0
            ? JavaRuntimeService.RecommendedMajorVersion(MinecraftVersionBox?.Text)
            : _recommendedJavaMajor;
        JavaRuntimeInfo? selected = JavaCombo?.SelectedItem as JavaRuntimeInfo;
        if (selected is null)
        {
            JavaHintText.Text = _source is null || _javaRuntimes.Count > 0
                ? App.Localization["server.java_hint"]
                : $"{App.Localization["server.java_hint"]}\n{App.Localization.Translate("server.dialog.java_none", recommended)}";
            return;
        }
        bool exact = selected.MajorVersion == recommended;
        JavaHintText.Text = exact
            ? $"{App.Localization["server.java_hint"]}\nJava {selected.MajorVersion}"
            : App.Localization.Translate("server.dialog.java_incompatible",
                MinecraftVersionBox?.Text.Trim() ?? string.Empty, recommended, selected.MajorVersion);
    }

    private async Task RefreshJavaRuntimesAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> requirements = SelectedJavaRequirements();
        JavaRuntimeDiscoveryResult result = await _javaRuntimeService.DiscoverAsync(
            minecraftVersion, requirements, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _recommendedJavaMajor = result.RecommendedMajorVersion;
        _javaRuntimes.Clear();
        foreach (JavaRuntimeInfo runtime in result.Runtimes)
        {
            _javaRuntimes.Add(runtime);
        }

        JavaRuntimeInfo? selected = !string.IsNullOrWhiteSpace(_selectedJavaPath)
            ? _javaRuntimes.FirstOrDefault(runtime => runtime.ExecutablePath.Equals(
                _selectedJavaPath, StringComparison.OrdinalIgnoreCase))
            : null;
        selected ??= result.Recommended;
        selected ??= JavaRuntimeService.SelectBest(_javaRuntimes, result.RecommendedMajorVersion);
        _selectedJavaPath = selected?.ExecutablePath ?? string.Empty;
        _suppressJavaSelection = true;
        try
        {
            JavaCombo.ItemsSource = _javaRuntimes;
            JavaCombo.SelectedItem = selected;
        }
        finally
        {
            _suppressJavaSelection = false;
        }
        RefreshJavaHint();
        if (result.Warning.Length > 0)
        {
            Log("WARN", result.Warning);
        }
        Log("INFO", $"Java runtimes detected: {_javaRuntimes.Count}; recommended={result.RecommendedMajorVersion}; mod requirements={string.Join(", ", requirements)}");
    }

    private IReadOnlyList<string> SelectedJavaRequirements() => _source?.Mods
        .Where(entry => entry.Selected && !entry.Disabled)
        .SelectMany(entry => entry.JavaVersionRequirements)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    private void RefreshJavaRecommendation()
    {
        if (_source is null)
        {
            return;
        }

        _recommendedJavaMajor = JavaRuntimeService.RecommendedMajorVersion(
            _source.MinecraftVersion,
            SelectedJavaRequirements());
        JavaRuntimeInfo? selected = JavaCombo.SelectedItem as JavaRuntimeInfo;
        if (selected?.MajorVersion != _recommendedJavaMajor)
        {
            selected = JavaRuntimeService.SelectBest(_javaRuntimes, _recommendedJavaMajor);
            _suppressJavaSelection = true;
            try
            {
                JavaCombo.SelectedItem = selected;
            }
            finally
            {
                _suppressJavaSelection = false;
            }
            _selectedJavaPath = selected?.ExecutablePath ?? string.Empty;
        }
        RefreshJavaHint();
    }

    private void ModSelectionChanged()
    {
        if (_suppressModSelectionRefresh)
        {
            return;
        }
        RefreshJavaRecommendation();
        InvalidatePreparation();
    }

    private void JavaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressJavaSelection)
        {
            return;
        }
        _selectedJavaPath = (JavaCombo.SelectedItem as JavaRuntimeInfo)?.ExecutablePath ?? string.Empty;
        RefreshJavaHint();
        // The selected runtime is part of the preparation snapshot. A changed
        // selection must require preparation again before export.
        if (_source is not null)
        {
            InvalidatePreparation();
        }
    }

    private async void ChooseJava_Click(object sender, RoutedEventArgs e)
    {
        if (_working)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = App.Localization["server.dialog.choose_java"],
            Filter = "Java executable (java.exe)|java.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        JavaRuntimeInfo? runtime = await _javaRuntimeService.ProbeExecutableAsync(dialog.FileName);
        if (runtime is null)
        {
            MessageBox.Show(App.Localization["server.dialog.java_required"], App.Localization["common.warning"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
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
        InvalidatePreparation();
        RefreshJavaHint();
    }

    private void CaptureActiveMode()
    {
        if (InputPathBox is null)
        {
            return;
        }
        _activeState.InputPath = InputPathBox.Text;
        _activeState.MinecraftVersion = MinecraftVersionBox.Text;
        _activeState.Loader = LoaderBox.Text;
        _activeState.LoaderVersion = LoaderVersionBox.Text;
        _activeState.OutputDirectory = OutputDirectoryBox.Text;
        _activeState.OutputName = OutputNameBox.Text;
        _activeState.IncludeConfig = ConfigCheckBox.IsChecked == true;
        _activeState.IncludeDefaultConfigs = DefaultConfigsCheckBox.IsChecked == true;
        _activeState.IncludeKubeJs = KubeJsCheckBox.IsChecked == true;
        _activeState.IncludeScripts = ScriptsCheckBox.IsChecked == true;
        _activeState.SelectedCore = CoreCombo.SelectedItem as CoreRow;
        _activeState.SelectedWorld = WorldCombo.SelectedItem as WorldRow;
        _activeState.SelectedJavaPath = (JavaCombo.SelectedItem as JavaRuntimeInfo)?.ExecutablePath
            ?? _activeState.SelectedJavaPath;
        _activeState.LogText = LogBox.Text;
    }

    private void RestoreActiveMode()
    {
        _restoringMode = true;
        _suppressOutputNameChange = true;
        try
        {
            ModsGrid.ItemsSource = _modRows;
            CoreCombo.ItemsSource = _coreRows;
            WorldCombo.ItemsSource = _worldRows;
            JavaCombo.ItemsSource = _javaRuntimes;
            InputPathBox.Text = _activeState.InputPath;
            MinecraftVersionBox.Text = _activeState.MinecraftVersion;
            LoaderBox.Text = _activeState.Loader;
            LoaderVersionBox.Text = _activeState.LoaderVersion;
            OutputDirectoryBox.Text = _activeState.OutputDirectory;
            OutputNameBox.Text = _activeState.OutputName;
            ConfigCheckBox.IsChecked = _activeState.IncludeConfig;
            DefaultConfigsCheckBox.IsChecked = _activeState.IncludeDefaultConfigs;
            KubeJsCheckBox.IsChecked = _activeState.IncludeKubeJs;
            ScriptsCheckBox.IsChecked = _activeState.IncludeScripts;
            CoreCombo.SelectedItem = _activeState.SelectedCore is not null && _coreRows.Contains(_activeState.SelectedCore)
                ? _activeState.SelectedCore
                : _coreRows.FirstOrDefault();
            WorldCombo.SelectedItem = _activeState.SelectedWorld is not null && _worldRows.Contains(_activeState.SelectedWorld)
                ? _activeState.SelectedWorld
                : _worldRows.FirstOrDefault();
            _suppressJavaSelection = true;
            try
            {
                JavaCombo.SelectedItem = _javaRuntimes.FirstOrDefault(runtime =>
                    runtime.ExecutablePath.Equals(_activeState.SelectedJavaPath, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressJavaSelection = false;
            }
            RefreshJavaHint();
            LogBox.Text = _activeState.LogText;
            LogBox.ScrollToEnd();
            RefreshLocalizedRows();
            RefreshOverview();
            StatusText.Text = App.Localization[_statusKey];
        }
        finally
        {
            _suppressOutputNameChange = false;
            _restoringMode = false;
        }
        SetWorking(false);
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_working || _inputPickerOpen)
        {
            return;
        }
        _inputPickerOpen = true;
        try
        {
            if (ArchiveMode)
            {
                var dialog = new OpenFileDialog
                {
                    Title = App.Localization["dialog.choose_pack"],
                    Filter = $"{App.Localization["dialog.filter_modpacks"]}|*.zip;*.mrpack|{App.Localization["dialog.filter_all"]}|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                };
                if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                {
                    InputPathBox.Text = dialog.FileName;
                }
                return;
            }

            using var folderDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = App.Localization["server.dialog.choose_directory"],
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(InputPathBox.Text) ? InputPathBox.Text : string.Empty,
            };
            if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                InputPathBox.Text = folderDialog.SelectedPath;
            }
        }
        finally
        {
            // Release after the current input message has completely unwound.
            // A modal dialog can otherwise let the same mouse event re-enter
            // this handler immediately after ShowDialog returns.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => _inputPickerOpen = false));
        }
    }

    private async void Read_Click(object sender, RoutedEventArgs e) => await ReadSourceAsync();

    private async Task ReadSourceAsync()
    {
        if (_working)
        {
            return;
        }
        string path = InputPathBox.Text.Trim().Trim('"');
        if (ArchiveMode && !IsSupportedArchive(path) || !ArchiveMode && !Directory.Exists(path))
        {
            MessageBox.Show(
                ArchiveMode ? App.Localization["dialog.invalid_pack"] : App.Localization["server.dialog.read_failed"],
                App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetWorking(true, "server.reading", indeterminate: true);
        CancellationToken cancellationToken = BeginOperation();
        try
        {
            Log("INFO", $"Read source: {path}");
            ServerPackSource source;
            if (ArchiveMode)
            {
                string temporaryRoot = Path.Combine(Path.GetTempPath(), $"mc-modpack-tool-source-{Guid.NewGuid():N}");
                try
                {
                    source = await _archiveReader.ReadAsync(path, temporaryRoot, cancellationToken);
                }
                catch
                {
                    TryDeleteTemporaryRoot(temporaryRoot);
                    throw;
                }
            }
            else
            {
                GameDirectoryDiscovery discovery = await GameDirectoryScanner.DiscoverAsync(path, cancellationToken);
                if (discovery.RequiresInstanceDirectory)
                {
                    MessageBox.Show(App.Localization["server.dialog.reselect_instance"], App.Localization["common.warning"],
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetStatus("server.ready");
                    return;
                }
                if (discovery.VersionCandidates.Count == 0)
                {
                    MessageBox.Show(App.Localization["server.dialog.no_version"], App.Localization["common.error"],
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    SetStatus("server.ready");
                    return;
                }
                ServerVersionCandidate? candidate = discovery.VersionCandidates.Count == 1
                    ? discovery.VersionCandidates[0]
                    : VersionSelectionWindow.Select(Window.GetWindow(this), discovery.VersionCandidates);
                if (candidate is null)
                {
                    SetStatus("server.ready");
                    return;
                }
                source = await GameDirectoryScanner.ReadAsync(path, candidate, cancellationToken);
            }

            if (SearchMatcher.NormalizeLoaderName(source.LoaderType) == "quilt")
            {
                DisposeSource(source);
                MessageBox.Show(App.Localization["server.dialog.quilt"], App.Localization["common.error"],
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("server.ready");
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _supportResolver.ResolveAsync(source, cancellationToken);
            }
            catch
            {
                DisposeSource(source);
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ApplySource(source, path);
            Log("INFO", $"Source ready: Minecraft {source.MinecraftVersion}, {source.LoaderType} {source.LoaderVersion}, mods={source.Mods.Count}");
            try
            {
                Log("INFO", "Scanning installed Java runtimes...");
                await RefreshJavaRuntimesAsync(source.MinecraftVersion, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log("WARN", $"Could not scan Java runtimes: {exception.Message}");
                RefreshJavaHint();
            }
            try
            {
                Log("INFO", "Refreshing available server cores...");
                await RefreshCoreOptionsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log("WARN", $"Could not refresh server cores automatically: {exception.Message}");
                MessageBox.Show($"{App.Localization["server.core_none"]}\n\n{exception.Message}",
                    App.Localization["common.warning"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("server.cancelled");
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
            SetStatus("server.ready");
            MessageBox.Show($"{App.Localization["server.dialog.read_failed"]}\n\n{exception.Message}",
                App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
            EndOperation();
        }
    }

    private void ApplySource(ServerPackSource source, string inputPath)
    {
        CleanupSource();
        _source = source;
        _javaRuntimes.Clear();
        _selectedJavaPath = string.Empty;
        _recommendedJavaMajor = JavaRuntimeService.RecommendedMajorVersion(
            source.MinecraftVersion,
            source.Mods.Where(entry => entry.Selected && !entry.Disabled)
                .SelectMany(entry => entry.JavaVersionRequirements));
        _readPath = Path.GetFullPath(inputPath);
        _coreRows.Clear();
        CoreCombo.SelectedItem = null;
        InputPathBox.Text = _readPath;
        MinecraftVersionBox.Text = source.MinecraftVersion;
        LoaderBox.Text = source.LoaderType;
        LoaderVersionBox.Text = source.LoaderVersion;
        _modRows.Clear();
        foreach (ServerModEntry mod in source.Mods)
        {
            _modRows.Add(new ServerModRow(mod, ModSelectionChanged));
        }
        _worldRows.Clear();
        _worldRows.Add(new WorldRow(null, App.Localization["server.no_world"]));
        foreach (ServerWorldEntry world in source.Worlds)
        {
            _worldRows.Add(new WorldRow(world, world.Name));
        }
        WorldCombo.SelectedIndex = 0;
        JavaCombo.ItemsSource = _javaRuntimes;
        JavaCombo.SelectedItem = null;
        RefreshJavaHint();
        ConfigureOptionalDirectoryCheckBox(ConfigCheckBox, "config");
        ConfigureOptionalDirectoryCheckBox(DefaultConfigsCheckBox, "defaultconfigs");
        ConfigureOptionalDirectoryCheckBox(KubeJsCheckBox, "kubejs");
        ConfigureOptionalDirectoryCheckBox(ScriptsCheckBox, "scripts");
        RefreshOverview();
        UpdateAutomaticOutputName(force: true);
        InvalidatePreparation();
        PrepareButton.IsEnabled = true;
        SetStatus("server.ready");
    }

    private void ConfigureOptionalDirectoryCheckBox(CheckBox checkBox, string key)
    {
        bool available = _source?.OptionalDirectories.ContainsKey(key) == true;
        checkBox.IsEnabled = available;
        checkBox.IsChecked = available;
    }

    private void InputPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_restoringMode || _source is null)
        {
            return;
        }
        try
        {
            if (!Path.GetFullPath(InputPathBox.Text.Trim().Trim('"')).Equals(_readPath, StringComparison.OrdinalIgnoreCase))
            {
                InvalidatePreparation();
                PrepareButton.IsEnabled = false;
            }
            else
            {
                PrepareButton.IsEnabled = !_working;
            }
        }
        catch
        {
            InvalidatePreparation();
            PrepareButton.IsEnabled = false;
        }
    }

    private void OutputNameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_restoringMode && !_suppressOutputNameChange &&
            !OutputNameBox.Text.Equals(_lastAutomaticName, StringComparison.Ordinal))
        {
            _outputNameEdited = true;
        }
    }

    private void SelectAllMods_Click(object sender, RoutedEventArgs e)
    {
        _suppressModSelectionRefresh = true;
        try
        {
            foreach (ServerModRow row in _modRows)
            {
                row.Selected = !row.Entry.Disabled && row.Entry.ServerSupport != ServerSupportKinds.Unsupported;
            }
        }
        finally
        {
            _suppressModSelectionRefresh = false;
        }
        ModSelectionChanged();
    }

    private void ClearAllMods_Click(object sender, RoutedEventArgs e)
    {
        _suppressModSelectionRefresh = true;
        try
        {
            foreach (ServerModRow row in _modRows)
            {
                row.Selected = false;
            }
        }
        finally
        {
            _suppressModSelectionRefresh = false;
        }
        ModSelectionChanged();
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
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            OutputDirectoryBox.Text = dialog.SelectedPath;
            return true;
        }
        return false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        SetStatus("status.canceling");
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e) => UpdateDropState(e);
    private void DropZone_DragOver(object sender, DragEventArgs e) => UpdateDropState(e);
    private void DropZone_DragLeave(object sender, DragEventArgs e) => DropZone.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

    private void UpdateDropState(DragEventArgs e)
    {
        string? file = FirstDroppedFile(e.Data);
        e.Effects = ArchiveMode && !_working && IsSupportedArchive(file) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropZone.SetResourceReference(Border.BorderBrushProperty, e.Effects == DragDropEffects.Copy ? "AccentBrush" : "DangerBrush");
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropZone.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        string? file = FirstDroppedFile(e.Data);
        if (!ArchiveMode || !IsSupportedArchive(file))
        {
            MessageBox.Show(App.Localization["dialog.invalid_pack"], App.Localization["common.error"],
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        InputPathBox.Text = file!;
        await ReadSourceAsync();
    }

    private static string? FirstDroppedFile(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
                ? paths[0]
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedArchive(string? path) =>
        path is not null && File.Exists(path) &&
        (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".mrpack", StringComparison.OrdinalIgnoreCase));

    private void SetWorking(bool working, string? statusKey = null, bool indeterminate = false)
    {
        _working = working;
        DirectoryModeButton.IsEnabled = !working;
        ArchiveModeButton.IsEnabled = !working;
        InputPathBox.IsEnabled = !working;
        BrowseInputButton.IsEnabled = !working;
        ReadButton.IsEnabled = !working;
        DropZone.IsEnabled = !working;
        MinecraftVersionBox.IsEnabled = !working;
        CoreCombo.IsEnabled = !working;
        RefreshCoresButton.IsEnabled = !working && _source is not null;
        JavaCombo.IsEnabled = !working && _javaRuntimes.Count > 0;
        ChooseJavaButton.IsEnabled = !working;
        ModsGrid.IsEnabled = !working;
        SelectAllModsButton.IsEnabled = !working;
        ClearAllModsButton.IsEnabled = !working;
        ConfigCheckBox.IsEnabled = !working && _source?.OptionalDirectories.ContainsKey("config") == true;
        DefaultConfigsCheckBox.IsEnabled = !working && _source?.OptionalDirectories.ContainsKey("defaultconfigs") == true;
        KubeJsCheckBox.IsEnabled = !working && _source?.OptionalDirectories.ContainsKey("kubejs") == true;
        ScriptsCheckBox.IsEnabled = !working && _source?.OptionalDirectories.ContainsKey("scripts") == true;
        WorldCombo.IsEnabled = !working && _source is not null;
        BrowseOutputButton.IsEnabled = !working;
        OutputDirectoryBox.IsEnabled = !working;
        OutputNameBox.IsEnabled = !working;
        PrepareButton.IsEnabled = !working && _source is not null && PathsEqual(InputPathBox.Text, _readPath);
        BuildButton.IsEnabled = !working && PreparationIsCurrent();
        CancelButton.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = working;
        OperationProgress.IsIndeterminate = working && indeterminate;
        OperationProgress.Value = working ? 0 : 1;
        if (!working)
        {
            HideDownloadSpeed();
        }
        if (statusKey is not null)
        {
            SetStatus(statusKey);
        }
    }

    private void SetStatus(string key)
    {
        _statusKey = key;
        StatusText.Text = App.Localization[key];
    }

    private void UpdateDownloadSpeed(DownloadTransferProgress progress)
    {
        _downloadActive = _working && progress.IsActive &&
            double.IsFinite(progress.BytesPerSecond) && progress.BytesPerSecond > 0;
        _downloadBytesPerSecond = _downloadActive ? progress.BytesPerSecond : 0;
        RefreshDownloadSpeed();
    }

    private void HideDownloadSpeed()
    {
        _downloadActive = false;
        _downloadBytesPerSecond = 0;
        RefreshDownloadSpeed();
    }

    private void RefreshDownloadSpeed()
    {
        if (!_downloadActive)
        {
            DownloadSpeedText.Text = string.Empty;
            DownloadSpeedText.Visibility = Visibility.Hidden;
            return;
        }

        DownloadSpeedText.Text = App.Localization.Translate(
            "server.download_speed", FormatDownloadSpeed(_downloadBytesPerSecond));
        DownloadSpeedText.Visibility = Visibility.Visible;
    }

    private static string FormatDownloadSpeed(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        double value = Math.Max(0, bytesPerSecond);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string format = value >= 100 ? "0" : "0.0";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
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

    private void InvalidatePreparation()
    {
        _preparedSnapshot = string.Empty;
        BuildButton.IsEnabled = false;
    }

    private string CurrentSnapshot() => string.Join('\u001f',
        _readPath,
        _source?.MinecraftVersion ?? string.Empty,
        _source?.LoaderType ?? string.Empty,
        _source?.LoaderVersion ?? string.Empty,
        _selectedJavaPath,
        string.Join(',', _modRows.Select(ModSnapshot)));

    private static string ModSnapshot(ServerModRow row)
    {
        if (row.Entry.Origin != ServerModOrigins.Local || string.IsNullOrWhiteSpace(row.Entry.SourcePath))
        {
            return row.Selected ? "1" : "0";
        }
        try
        {
            var file = new FileInfo(row.Entry.SourcePath);
            return $"{(row.Selected ? 1 : 0)}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"{(row.Selected ? 1 : 0)}:unavailable";
        }
    }

    private bool PreparationIsCurrent() => _preparedSnapshot.Length > 0 && _preparedSnapshot == CurrentSnapshot();

    private void UpdateAutomaticOutputName(bool force)
    {
        if (_source is null || !force && _outputNameEdited)
        {
            return;
        }
        string generated = MigrationView.GenerateOutputPackName(
            _source.DisplayName,
            _source.MinecraftVersion,
            _source.MinecraftVersion);
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
            OverviewBox.Clear();
            return;
        }
        string sourceKind = _source.InputKind switch
        {
            ServerInputKinds.CurseForge => "CurseForge",
            ServerInputKinds.Modrinth => "Modrinth",
            _ => App.Localization["server.directory"],
        };
        OverviewBox.Text = App.Localization.Translate(
            "server.overview", _source.DisplayName, sourceKind, _source.MinecraftVersion,
            _source.LoaderType, _source.LoaderVersion, _source.Mods.Count, _source.Worlds.Count);
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        ApplySourceMode();
        ApplyJavaLocalization();
        RefreshLocalizedRows();
        RefreshOverview();
        StatusText.Text = App.Localization[_statusKey];
        RefreshDownloadSpeed();
    }

    private void RefreshLocalizedRows()
    {
        foreach (ServerModRow row in _modRows)
        {
            row.RefreshText();
        }
        if (_worldRows.Count > 0 && _worldRows[0].World is null)
        {
            _worldRows[0].DisplayName = App.Localization["server.no_world"];
        }
    }

    private void CleanupSource()
    {
        if (_source is not null)
        {
            DisposeSource(_source);
        }
    }

    private void CleanupAllSources()
    {
        ServerPackSource? directorySource = _directoryState.Source;
        ServerPackSource? archiveSource = _archiveState.Source;
        if (directorySource is not null)
        {
            DisposeSource(directorySource);
        }
        if (archiveSource is not null && !ReferenceEquals(archiveSource, directorySource))
        {
            DisposeSource(archiveSource);
        }
        _directoryState.Source = null;
        _archiveState.Source = null;
    }

    private static void DisposeSource(ServerPackSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.TemporaryRoot))
        {
            TryDeleteTemporaryRoot(source.TemporaryRoot);
        }
    }

    private static void TryDeleteTemporaryRoot(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // Temporary cleanup is best effort.
        }
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
        catch
        {
            return false;
        }
    }

    private async void ServerView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow is null)
        {
            await ShutdownAsync();
        }
    }

    private sealed class ModeState
    {
        public ObservableCollection<ServerModRow> ModRows { get; } = [];
        public ObservableCollection<CoreRow> CoreRows { get; } = [];
        public ObservableCollection<WorldRow> WorldRows { get; } = [];
        public ObservableCollection<JavaRuntimeInfo> JavaRuntimes { get; } = [];
        public ServerPackSource? Source { get; set; }
        public string ReadPath { get; set; } = string.Empty;
        public string InputPath { get; set; } = string.Empty;
        public string MinecraftVersion { get; set; } = string.Empty;
        public string Loader { get; set; } = string.Empty;
        public string LoaderVersion { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string OutputName { get; set; } = string.Empty;
        public string LastAutomaticName { get; set; } = string.Empty;
        public string PreparedSnapshot { get; set; } = string.Empty;
        public string StatusKey { get; set; } = "server.ready";
        public string LogText { get; set; } = string.Empty;
        public bool OutputNameEdited { get; set; }
        public bool IncludeConfig { get; set; }
        public bool IncludeDefaultConfigs { get; set; }
        public bool IncludeKubeJs { get; set; }
        public bool IncludeScripts { get; set; }
        public CoreRow? SelectedCore { get; set; }
        public WorldRow? SelectedWorld { get; set; }
        public string SelectedJavaPath { get; set; } = string.Empty;
        public int RecommendedJavaMajor { get; set; } = 21;
    }

    private sealed class ServerModRow : INotifyPropertyChanged
    {
        private readonly Action _changed;
        private bool _selected;
        private string _support = string.Empty;
        private string _source = string.Empty;

        public ServerModRow(ServerModEntry entry, Action changed)
        {
            Entry = entry;
            _changed = changed;
            _selected = entry.Selected;
            RefreshText();
        }

        public ServerModEntry Entry { get; }
        public string Name => Entry.Name;
        public string RelativePath => Entry.RelativePath.Length > 0
                ? Entry.RelativePath
                : Entry.ContentItem?.FileName ?? string.Empty;
        public string Support { get => _support; private set => Set(ref _support, value); }
        public string Source { get => _source; private set => Set(ref _source, value); }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value))
                {
                    return;
                }
                Entry.Selected = value;
                if (Entry.ContentItem is not null)
                {
                    Entry.ContentItem.Excluded = !value;
                }
                _changed();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void RefreshText()
        {
            Support = App.Localization[$"server.support.{Entry.ServerSupport}"];
            Source = App.Localization[$"server.origin.{Entry.Origin}"];
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }

    private sealed class CoreRow
    {
        public CoreRow(ServerCoreOption option)
        {
            Option = option;
        }

        public ServerCoreOption Option { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(Option.CoreVersion)
            ? Option.Name
            : $"{Option.Name}  {Option.CoreVersion}";
    }

    private sealed class WorldRow : INotifyPropertyChanged
    {
        private string _displayName;
        public WorldRow(ServerWorldEntry? world, string displayName)
        {
            World = world;
            _displayName = displayName;
        }
        public ServerWorldEntry? World { get; }
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName == value) return;
                _displayName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
