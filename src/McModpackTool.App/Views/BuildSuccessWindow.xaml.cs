using System.Diagnostics;
using System.Windows;
using McModpackTool.App.Services;
using McModpackTool.App.UI;

namespace McModpackTool.App.Views;

public partial class BuildSuccessWindow : Window
{
    private readonly string _outputPath;

    public BuildSuccessWindow(string message, string outputPath)
    {
        InitializeComponent();
        DataContext = App.Localization;
        MessageBox.Text = message;
        _outputPath = outputPath;
        SourceInitialized += (_, _) =>
        {
            bool dark = App.Settings.Theme == "dark" || App.Settings.Theme == "system" && ThemeService.IsSystemDark();
            if (WindowEffects.Apply(this, dark))
                Background = System.Windows.Media.Brushes.Transparent;
            else
                SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(_outputPath)}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            System.Windows.MessageBox.Show(App.Localization["dialog.open_folder_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
