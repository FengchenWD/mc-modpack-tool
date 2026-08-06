using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using McModpackTool.App.Services;
using McModpackTool.App.UI;
using McModpackTool.App.Views;

namespace McModpackTool.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages;
    private string _currentPage = "home";
    private CancellationTokenSource? _navigationAnimation;
    private CancellationTokenSource? _languageAnimation;
    private bool _closeInProgress;
    private bool _closeCommitted;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Localization;

        var home = new HomeView();
        var migration = new MigrationView();
        var settings = new SettingsView();
        home.NavigationRequested += (_, destination) => _ = NavigateAsync(destination);
        settings.LanguageChangeRequested += ChangeLanguageAsync;

        _pages = new Dictionary<string, FrameworkElement>
        {
            ["home"] = home,
            ["migration"] = migration,
            ["server"] = new ServerView(),
            ["settings"] = settings
        };
        ContentHost.Content = home;

        SourceInitialized += (_, _) => ApplyWindowMaterial();
        App.Theme.ThemeChanged += (_, _) => Dispatcher.Invoke(ApplyWindowMaterial);
        Closing += MainWindow_Closing;
    }

    private void ApplyWindowMaterial()
    {
        bool dark = App.Settings.Theme == "dark" || App.Settings.Theme == "system" && ThemeService.IsSystemDark();
        bool backdrop = WindowEffects.Apply(this, dark);
        if (backdrop)
            Background = Brushes.Transparent;
        else
            SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private async void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { CommandParameter: string page })
            await NavigateAsync(page);
    }

    public async Task NavigateAsync(string page)
    {
        if (!_pages.ContainsKey(page) || page == _currentPage) return;
        CancellationTokenSource animation = ReplaceAnimation(ref _navigationAnimation);
        CancellationToken cancellationToken = animation.Token;
        var transform = (TranslateTransform)ContentHost.RenderTransform;
        UpdateNavigationSelection(page);
        _currentPage = page;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContentHost.Content = _pages[page];
            ContentHost.Opacity = 0.82;
            transform.X = 6;
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded, cancellationToken);

            await Task.WhenAll(
                AnimateAsync(ContentHost, UIElement.OpacityProperty, 1, 145, cancellationToken),
                AnimateAsync(transform, TranslateTransform.XProperty, 0, 155, cancellationToken));
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_navigationAnimation, animation))
            {
                ContentHost.BeginAnimation(UIElement.OpacityProperty, null);
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                ContentHost.Opacity = 1;
                transform.X = 0;
                _navigationAnimation = null;
                animation.Dispose();
            }
        }
    }

    public async Task ChangeLanguageAsync(string language)
    {
        if (language == App.Localization.Language) return;
        CancellationTokenSource animation = ReplaceAnimation(ref _languageAnimation);
        CancellationToken cancellationToken = animation.Token;
        try
        {
            await AnimateAsync(UiRoot, UIElement.OpacityProperty, 0.72, 65, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            App.Localization.SetLanguage(language);
            App.Settings.Language = language;
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded, cancellationToken);
            await AnimateAsync(UiRoot, UIElement.OpacityProperty, 1, 155, cancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_languageAnimation, animation))
            {
                UiRoot.BeginAnimation(UIElement.OpacityProperty, null);
                UiRoot.Opacity = 1;
                _languageAnimation = null;
                animation.Dispose();
            }
        }
    }

    private void UpdateNavigationSelection(string page)
    {
        HomeNav.IsChecked = page == "home";
        MigrationNav.IsChecked = page == "migration";
        ServerNav.IsChecked = page == "server";
        SettingsNav.IsChecked = page == "settings";
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeCommitted) return;
        e.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        IsEnabled = false;
        _navigationAnimation?.Cancel();
        _languageAnimation?.Cancel();
        try
        {
            if (_pages.TryGetValue("migration", out FrameworkElement? page) && page is MigrationView migration)
                await migration.ShutdownAsync();
            try { await App.SettingsStore.SaveAsync(App.Settings); }
            catch { }
        }
        finally
        {
            _closeCommitted = true;
            Application.Current.Shutdown();
        }
    }

    private static async Task AnimateAsync(UIElement target, DependencyProperty property, double to, int durationMs, CancellationToken cancellationToken)
    {
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
        var animation = CreateAnimation(from, to, durationMs);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        await Task.Delay(durationMs, cancellationToken);
        target.BeginAnimation(property, null);
    }

    private static async Task AnimateAsync(Animatable target, DependencyProperty property, double to, int durationMs, CancellationToken cancellationToken)
    {
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
        var animation = CreateAnimation(from, to, durationMs);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        await Task.Delay(durationMs, cancellationToken);
        target.BeginAnimation(property, null);
    }

    private static DoubleAnimation CreateAnimation(double from, double to, int durationMs) => new(from, to, TimeSpan.FromMilliseconds(durationMs))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };

    private static CancellationTokenSource ReplaceAnimation(ref CancellationTokenSource? current)
    {
        current?.Cancel();
        current?.Dispose();
        current = new CancellationTokenSource();
        return current;
    }
}
