using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace McModpackTool.App.Views;

public partial class HomeView : UserControl
{
    public event EventHandler<string>? NavigationRequested;

    public HomeView()
    {
        InitializeComponent();
        DataContext = App.Localization;
    }

    private void Migration_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke(this, "migration");
    private void Server_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke(this, "server");
    private void Agreement_Click(object sender, RoutedEventArgs e) => new AgreementWindow(requireAcceptance: false) { Owner = Window.GetWindow(this) }.ShowDialog();
    private void Bilibili_Click(object sender, RoutedEventArgs e) => OpenUrl("https://space.bilibili.com/1003434667");
    private void Github_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/FengchenWD/mc-modpack-tool");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { MessageBox.Show(url, App.Localization["app.name"], MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
