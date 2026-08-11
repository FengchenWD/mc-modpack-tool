using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using McModpackTool.App.Services;
using McModpackTool.Core.Models;
using McModpackTool.Core.Services;

namespace McModpackTool.App.Views;

public partial class ClientPackView : UserControl
{
    private readonly ObservableCollection<ClientContentGroup> _groups = [];
    private readonly Dictionary<ClientContentEntry, bool> _defaultSelections = [];
    private readonly CurseForgeClient _curseForge;
    private readonly ModrinthClient _modrinth;
    private readonly ClientPackBuilder _builder;
    private ClientPackSource? _source;
    private CancellationTokenSource? _operationCts;
    private TaskCompletionSource? _operationCompletion;
    private string _readPath = string.Empty;
    private string _lastAutomaticName = string.Empty;
    private string _statusKey = "client.ready";
    private bool _working;
    private bool _outputNameEdited;
    private bool _suppressOutputNameChange;
    private bool _inputPickerOpen;
    private bool _updatingFormats;
    private bool _applyingSource;
    private bool _batchSelection;
    private bool _disposed;

    public ClientPackView()
    {
        InitializeComponent();
        DataContext = App.Localization;
        ContentGroupsControl.ItemsSource = _groups;
        _curseForge = new CurseForgeClient(BuildSecrets.CurseForgeApiKey);
        _modrinth = new ModrinthClient();
        _builder = new ClientPackBuilder(_modrinth, _curseForge);
        App.Localization.LanguageChanged += Localization_LanguageChanged;
        Unloaded += ClientPackView_Unloaded;
    }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true;
        App.Localization.LanguageChanged -= Localization_LanguageChanged;
        _operationCts?.Cancel();
        if (_operationCompletion?.Task is Task pending) await pending;
        _builder.Dispose();
        _curseForge.Dispose();
        _modrinth.Dispose();
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        foreach (ClientContentGroup group in _groups) group.RefreshText();
        RefreshOverview();
        StatusText.Text = App.Localization[_statusKey];
    }

    private async void ClientPackView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow is null) await ShutdownAsync();
    }

    private sealed class ClientContentRow : INotifyPropertyChanged
    {
        private readonly Action _changed;
        private bool _selected;

        public ClientContentRow(ClientContentEntry entry, Action changed)
        {
            Entry = entry;
            _changed = changed;
            _selected = entry.Selected;
        }

        public ClientContentEntry Entry { get; }
        public string Name => Entry.Name;
        public string RelativePath => Entry.RelativePath;
        public string SizeText => FormatSize(Entry.TotalBytes);
        public bool CanSelect => true;
        public bool Selected
        {
            get => _selected;
            set
            {
                bool next = value && CanSelect;
                if (_selected == next) return;
                _selected = next;
                Entry.Selected = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
                _changed();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ClientContentGroup : INotifyPropertyChanged
    {
        private bool _updating;
        private bool _isExpanded;

        public ClientContentGroup(string kind, IEnumerable<ClientContentEntry> entries, Action changed)
        {
            Kind = kind;
            Items = new ObservableCollection<ClientContentRow>();
            foreach (ClientContentEntry entry in entries)
                Items.Add(new ClientContentRow(entry, SelectionChanged));
            _changed = changed;
            _isExpanded = Items.Any(item => item.Selected);
            RefreshText();
        }

        private readonly Action _changed;
        public string Kind { get; }
        public ObservableCollection<ClientContentRow> Items { get; }
        public string DisplayName { get; private set; } = string.Empty;
        public string Summary { get; private set; } = string.Empty;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ItemsVisibility));
                OnPropertyChanged(nameof(ToggleGlyph));
                OnPropertyChanged(nameof(ToggleHint));
            }
        }
        public Visibility ItemsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        public string ToggleGlyph => IsExpanded ? "\uE70D" : "\uE76C";
        public string ToggleHint => App.Localization[IsExpanded ? "client.group.collapse" : "client.group.expand"];

        public bool? SelectionState
        {
            get
            {
                int selectable = Items.Count(item => item.CanSelect);
                int selected = Items.Count(item => item.CanSelect && item.Selected);
                return selected == 0 ? false : selected == selectable ? true : null;
            }
            set
            {
                if (_updating) return;
                SetSelections(_ => value == true);
            }
        }

        public void SetSelections(Func<ClientContentRow, bool> selector)
        {
            _updating = true;
            try
            {
                foreach (ClientContentRow item in Items.Where(item => item.CanSelect))
                    item.Selected = selector(item);
            }
            finally { _updating = false; }
            SelectionChanged();
        }

        public void RefreshText()
        {
            DisplayName = App.Localization[$"client.group.{Kind}"];
            Summary = App.Localization.Translate("client.group_summary", Items.Count, FormatSize(Items.Sum(item => item.Entry.TotalBytes)));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(ToggleHint));
        }

        private void SelectionChanged()
        {
            if (_updating) return;
            OnPropertyChanged(nameof(SelectionState));
            _changed();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ClientContentGroup group })
            group.IsExpanded = !group.IsExpanded;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
