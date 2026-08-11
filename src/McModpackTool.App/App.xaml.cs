using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using System.Diagnostics;
using McModpackTool.App.Services;
using McModpackTool.App.UI;
using McModpackTool.App.Views;

namespace McModpackTool.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static SettingsService SettingsStore { get; } = new();
    public static LocalizationService Localization { get; } = new();
    public static ThemeService Theme { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        Settings = await SettingsStore.LoadAsync();
        Localization.SetLanguage(Settings.Language, animate: false);
        Theme.Initialize(Resources, Settings);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        if (Settings.AcceptedAgreementVersion != AgreementContent.Version)
        {
            var agreement = new AgreementWindow(requireAcceptance: true);
            bool? accepted = agreement.ShowDialog();
            if (accepted != true)
            {
                Shutdown();
                return;
            }

            Settings.AcceptedAgreementVersion = AgreementContent.Version;
            try
            {
                await SettingsStore.SaveAsync(Settings);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    Localization.Translate("app.agreement_save_failed", exception.Message),
                    Localization["common.error"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }
        }

        mainWindow.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string message = Localization.Translate("app.unhandled_error", e.Exception.Message);
        try
        {
            MessageBox.Show(message, Localization["app.name"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception dialogException)
        {
            Debug.WriteLine($"Could not display the application error dialog: {dialogException}");
        }
        e.Handled = true;
        Application.Current?.Shutdown(-1);
    }

    private void Button_OnPress(object sender, MouseButtonEventArgs e) => PressAnimationBehavior.OnPress(sender, e);
    private void Button_OnRelease(object sender, RoutedEventArgs e) => PressAnimationBehavior.OnRelease(sender, e);
}
