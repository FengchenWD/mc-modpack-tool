using System.Windows;
using System.Windows.Controls;

namespace McModpackTool.App.UI;

public partial class AppTitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AppTitleBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MinimizeVisibilityProperty = DependencyProperty.Register(
        nameof(MinimizeVisibility), typeof(Visibility), typeof(AppTitleBar), new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty MaximizeVisibilityProperty = DependencyProperty.Register(
        nameof(MaximizeVisibility), typeof(Visibility), typeof(AppTitleBar), new PropertyMetadata(Visibility.Collapsed));

    public AppTitleBar() => InitializeComponent();

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public Visibility MinimizeVisibility
    {
        get => (Visibility)GetValue(MinimizeVisibilityProperty);
        set => SetValue(MinimizeVisibilityProperty, value);
    }

    public Visibility MaximizeVisibility
    {
        get => (Visibility)GetValue(MaximizeVisibilityProperty);
        set => SetValue(MaximizeVisibilityProperty, value);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
