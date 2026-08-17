using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using McModpackTool.App.Services;
using McModpackTool.App.UI;

namespace McModpackTool.App.Views;

public partial class AgreementWindow : Window
{
    private bool _initializing = true;
    private readonly bool _requireAcceptance;
    private CancellationTokenSource? _languageAnimation;

    public AgreementWindow(bool requireAcceptance)
    {
        _requireAcceptance = requireAcceptance;
        InitializeComponent();
        DataContext = App.Localization;
        VersionText.Text = AgreementContent.Version;
        DeclineButton.Visibility = requireAcceptance ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.Visibility = requireAcceptance ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = requireAcceptance ? Visibility.Collapsed : Visibility.Visible;
        foreach (ComboBoxItem item in LanguageCombo.Items)
            if (Equals(item.Tag, App.Localization.Language)) LanguageCombo.SelectedItem = item;
        AgreementText.Text = AgreementContent.Get(App.Localization.Language);
        _initializing = false;
        ContentRendered += (_, _) => ResetAgreementView();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.AttachWorkAreaMaximization(this);
            bool dark = App.Settings.Theme == "dark" || App.Settings.Theme == "system" && ThemeService.IsSystemDark();
            if (WindowEffects.Apply(this, dark))
                Background = System.Windows.Media.Brushes.Transparent;
            else
                SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        };
        Closed += (_, _) => _languageAnimation?.Cancel();
    }

    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        _languageAnimation?.Cancel();
        _languageAnimation?.Dispose();
        var animation = new CancellationTokenSource();
        _languageAnimation = animation;
        try
        {
            await AnimateOpacityAsync(AgreementText, 0.35, 65, animation.Token);
            animation.Token.ThrowIfCancellationRequested();
            App.Localization.SetLanguage(language);
            App.Settings.Language = language;
            AgreementText.Text = AgreementContent.Get(language);
            await Dispatcher.InvokeAsync(ResetAgreementView, System.Windows.Threading.DispatcherPriority.Loaded, animation.Token);
            await AnimateOpacityAsync(AgreementText, 1, 145, animation.Token);
            try { await App.SettingsStore.SaveAsync(App.Settings); }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
                MessageBox.Show(App.Localization["dialog.agreement_language_save_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_languageAnimation, animation))
            {
                AgreementText.BeginAnimation(OpacityProperty, null);
                AgreementText.Opacity = 1;
                _languageAnimation = null;
                animation.Dispose();
            }
        }
    }

    private void CodeLicense_Click(object sender, RoutedEventArgs e) => OpenLicense(AgreementContent.CodeLicenseUrl);

    private void AssetLicense_Click(object sender, RoutedEventArgs e) => OpenLicense(AgreementContent.AssetLicenseUrl);

    private void ThirdPartyNotices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MessageBox.Show(ThirdPartyNoticeContent.Get(), App.Localization["agreement.third_party_title"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLicense(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { MessageBox.Show(url, Title, MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ResetAgreementView()
    {
        AgreementScrollViewer.ScrollToTop();
        AgreementScrollViewer.ScrollToLeftEnd();
    }

    private static async Task AnimateOpacityAsync(UIElement element, double target, int durationMs, CancellationToken cancellationToken)
    {
        double from = element.Opacity;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = target;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        await Task.Delay(durationMs, cancellationToken);
        element.BeginAnimation(OpacityProperty, null);
    }
}
