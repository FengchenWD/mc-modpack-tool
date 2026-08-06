using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly LoaderVersionService _loaderVersions = new();
    private readonly ContentTargetResolver _targetResolver;
    private readonly CompatibilityAnalyzer _compatibilityAnalyzer = new();
    private readonly ServerArchiveSourceReader _archiveReader;
    private readonly ServerCoreService _coreService;
    private readonly ServerPackBuilder _builder;
    private readonly ObservableCollection<ServerModRow> _modRows = [];
    private readonly ObservableCollection<CoreRow> _coreRows = [];
    private readonly ObservableCollection<WorldRow> _worldRows = [];
    private CancellationTokenSource? _operationCts;
    private TaskCompletionSource? _operationCompletion;
    private ServerPackSource? _source;
    private string _readPath = string.Empty;
    private string _lastAutomaticName = string.Empty;
    private string _preparedSnapshot = string.Empty;
    private string _statusKey = "server.ready";
    private bool _working;
    private bool _outputNameEdited;
    private bool _suppressOutputNameChange;
    private bool _disposed;

    public ServerView()
    {
        InitializeComponent();
        DataContext = App.Localization;
        ModsGrid.ItemsSource = _modRows;
        CoreCombo.ItemsSource = _coreRows;
        WorldCombo.ItemsSource = _worldRows;

        _curseForge = new CurseForgeClient(BuildSecrets.CurseForgeApiKey);
        _modrinth = new ModrinthClient();
        _targetResolver = new ContentTargetResolver(_curseForge, _modrinth);
        _archiveReader = new ServerArchiveSourceReader(_curseForge);
        _coreService = new ServerCoreService(logWarning: message => Dispatcher.Invoke(() => Log("WARN", message)));
        _builder = new ServerPackBuilder(_coreService);

        App.Localization.LanguageChanged += Localization_LanguageChanged;
        OutputNameBox.TextChanged += OutputNameBox_TextChanged;
        Unloaded += ServerView_Unloaded;
        ApplySourceMode();
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
        CleanupSource();
        _loaderVersions.Dispose();
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
        ApplySourceMode();
        if (_source is not null && !_working)
        {
            ClearLoadedState(clearInput: true);
        }
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
        VersionModeHint.Text = App.Localization[ArchiveMode ? "server.archive_version_hint" : "server.directory_version_hint"];
        TargetVersionBox.IsReadOnly = !ArchiveMode;
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        if (_working)
        {
            return;
        }
        if (ArchiveMode)
        {
            var dialog = new OpenFileDialog
            {
                Title = App.Localization["dialog.choose_pack"],
                Filter = $"{App.Localization["dialog.filter_modpacks"]}|*.zip;*.mrpack|{App.Localization["dialog.filter_all"]}|*.*",
                CheckFileExists = true,
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
                    return;
                }
                if (discovery.VersionCandidates.Count == 0)
                {
                    MessageBox.Show(App.Localization["server.dialog.no_version"], App.Localization["common.error"],
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                ServerVersionCandidate? candidate = discovery.VersionCandidates.Count == 1
                    ? discovery.VersionCandidates[0]
                    : VersionSelectionWindow.Select(Window.GetWindow(this), discovery.VersionCandidates);
                if (candidate is null)
                {
                    return;
                }
                source = await GameDirectoryScanner.ReadAsync(path, candidate, cancellationToken);
            }

            if (SearchMatcher.NormalizeLoaderName(source.LoaderType) == "quilt")
            {
                DisposeSource(source);
                MessageBox.Show(App.Localization["server.dialog.quilt"], App.Localization["common.error"],
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ApplySource(source, path);
            Log("INFO", $"Source ready: Minecraft {source.MinecraftVersion}, {source.LoaderType} {source.LoaderVersion}, mods={source.Mods.Count}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("server.cancelled");
        }
        catch (Exception exception)
        {
            Log("ERROR", exception.ToString());
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
        _readPath = Path.GetFullPath(inputPath);
        InputPathBox.Text = _readPath;
        TargetVersionBox.Text = source.MinecraftVersion;
        LoaderBox.Text = source.LoaderType;
        LoaderVersionBox.Text = source.LoaderVersion;
        OutputDirectoryBox.Text = source.InputKind == ServerInputKinds.Directory
            ? source.SourcePath
            : Path.GetDirectoryName(source.SourcePath) ?? string.Empty;

        _modRows.Clear();
        foreach (ServerModEntry mod in source.Mods)
        {
            _modRows.Add(new ServerModRow(mod, InvalidatePreparation));
        }
        _worldRows.Clear();
        _worldRows.Add(new WorldRow(null, App.Localization["server.no_world"]));
        foreach (ServerWorldEntry world in source.Worlds)
        {
            _worldRows.Add(new WorldRow(world, world.Name));
        }
        WorldCombo.SelectedIndex = 0;
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
        checkBox.Opacity = available ? 1 : 0.5;
    }

    private void InputPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_source is null)
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

    private void TargetVersionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_source is null)
        {
            return;
        }
        InvalidatePreparation();
        _coreRows.Clear();
        LoaderVersionBox.Text = TargetVersionBox.Text.Trim().Equals(_source.MinecraftVersion, StringComparison.Ordinal)
            ? _source.LoaderVersion
            : string.Empty;
        UpdateAutomaticOutputName(force: false);
    }

    private void OutputNameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_suppressOutputNameChange && !OutputNameBox.Text.Equals(_lastAutomaticName, StringComparison.Ordinal))
        {
            _outputNameEdited = true;
        }
    }

    private void SelectAllMods_Click(object sender, RoutedEventArgs e)
    {
        foreach (ServerModRow row in _modRows)
        {
            row.Selected = !row.Entry.Disabled && row.Entry.ServerSupport != ServerSupportKinds.Unsupported;
        }
    }

    private void ClearAllMods_Click(object sender, RoutedEventArgs e)
    {
        foreach (ServerModRow row in _modRows)
        {
            row.Selected = false;
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
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
        }
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

    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => BrowseInput_Click(sender, e);

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
        TargetVersionBox.IsEnabled = !working;
        CoreCombo.IsEnabled = !working;
        RefreshCoresButton.IsEnabled = !working && _source is not null;
        ModsGrid.IsEnabled = !working;
        SelectAllModsButton.IsEnabled = !working;
        ClearAllModsButton.IsEnabled = !working;
        PrepareButton.IsEnabled = !working && _source is not null && PathsEqual(InputPathBox.Text, _readPath);
        BuildButton.IsEnabled = !working && PreparationIsCurrent();
        CancelButton.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = working;
        OperationProgress.IsIndeterminate = working && indeterminate;
        OperationProgress.Value = working ? 0 : 1;
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
        TargetVersionBox.Text.Trim(),
        LoaderBox.Text.Trim(),
        LoaderVersionBox.Text.Trim(),
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
            TargetVersionBox.Text.Trim());
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
        RefreshLocalizedRows();
        RefreshOverview();
        StatusText.Text = App.Localization[_statusKey];
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

    private void ClearLoadedState(bool clearInput)
    {
        CleanupSource();
        _source = null;
        _readPath = string.Empty;
        _modRows.Clear();
        _coreRows.Clear();
        _worldRows.Clear();
        OverviewBox.Clear();
        TargetVersionBox.Clear();
        LoaderBox.Clear();
        LoaderVersionBox.Clear();
        if (clearInput)
        {
            InputPathBox.Clear();
        }
        PrepareButton.IsEnabled = false;
        InvalidatePreparation();
    }

    private void CleanupSource()
    {
        if (_source is not null)
        {
            DisposeSource(_source);
        }
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
        public string RelativePath => Entry.ContentItem?.TargetFileName is { Length: > 0 } target
            ? target
            : Entry.RelativePath.Length > 0
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
