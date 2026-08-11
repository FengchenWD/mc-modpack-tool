using System.Windows;
using McModpackTool.App.Views;

namespace McModpackTool.App;

/// <summary>Application-themed replacement with the same call shape as WPF MessageBox.</summary>
internal static class MessageBox
{
    public static MessageBoxResult Show(string messageBoxText) =>
        Show(messageBoxText, App.Localization["app.name"], MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        Show(messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        var dialog = new AppDialogWindow(messageBoxText, caption, button, icon, defaultResult);
        Window? owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsVisible && window.IsActive);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
        return dialog.Result;
    }
}
