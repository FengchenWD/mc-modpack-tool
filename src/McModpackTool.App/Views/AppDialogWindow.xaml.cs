using System.Windows;
using System.Windows.Controls;
using McModpackTool.App.Services;
using McModpackTool.App.UI;

namespace McModpackTool.App.Views;

public partial class AppDialogWindow : Window
{
    private readonly MessageBoxButton _buttons;

    public AppDialogWindow(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        _buttons = buttons;
        Result = DefaultCloseResult(buttons);
        InitializeComponent();
        DataContext = App.Localization;
        Title = caption;
        DialogMessage.Text = message;
        ConfigureIcon(image);
        ConfigureButtons(buttons, defaultResult);
        SourceInitialized += (_, _) =>
        {
            bool dark = App.Settings.Theme == "dark" || App.Settings.Theme == "system" && ThemeService.IsSystemDark();
            if (WindowEffects.Apply(this, dark))
                Background = System.Windows.Media.Brushes.Transparent;
            else
                SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        };
    }

    public MessageBoxResult Result { get; private set; }

    private void ConfigureButtons(MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        OkButton.Visibility = buttons == MessageBoxButton.OK || buttons == MessageBoxButton.OKCancel
            ? Visibility.Visible
            : Visibility.Collapsed;
        YesButton.Visibility = buttons == MessageBoxButton.YesNo || buttons == MessageBoxButton.YesNoCancel
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoButton.Visibility = YesButton.Visibility;
        CancelButton.Visibility = buttons == MessageBoxButton.OKCancel || buttons == MessageBoxButton.YesNoCancel
            ? Visibility.Visible
            : Visibility.Collapsed;
        OkButton.Margin = buttons == MessageBoxButton.OKCancel ? new Thickness(0, 0, 9, 0) : default;
        YesButton.Margin = YesButton.Visibility == Visibility.Visible ? new Thickness(0, 0, 9, 0) : default;
        NoButton.Margin = CancelButton.Visibility == Visibility.Visible ? new Thickness(0, 0, 9, 0) : default;

        Button? defaultButton = defaultResult switch
        {
            MessageBoxResult.OK => OkButton,
            MessageBoxResult.Yes => YesButton,
            MessageBoxResult.No => NoButton,
            MessageBoxResult.Cancel => CancelButton,
            _ => buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? YesButton : OkButton,
        };
        if (defaultButton.Visibility == Visibility.Visible)
        {
            defaultButton.IsDefault = true;
        }
        if (CancelButton.Visibility == Visibility.Visible)
        {
            CancelButton.IsCancel = true;
        }
        else if (NoButton.Visibility == Visibility.Visible)
        {
            NoButton.IsCancel = true;
        }
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        (string glyph, string brush) = image switch
        {
            MessageBoxImage.Error => ("\uEA39", "DangerBrush"),
            MessageBoxImage.Warning => ("\uE7BA", "WarningBrush"),
            MessageBoxImage.Question => ("\uE897", "AccentBrush"),
            _ => ("\uE946", "AccentBrush"),
        };
        DialogIcon.Text = glyph;
        DialogIcon.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private static MessageBoxResult DefaultCloseResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.YesNoCancel or MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.OK,
    };

    private void Complete(MessageBoxResult result)
    {
        Result = result;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Complete(MessageBoxResult.OK);
    private void Yes_Click(object sender, RoutedEventArgs e) => Complete(MessageBoxResult.Yes);
    private void No_Click(object sender, RoutedEventArgs e) => Complete(MessageBoxResult.No);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(MessageBoxResult.Cancel);
}
