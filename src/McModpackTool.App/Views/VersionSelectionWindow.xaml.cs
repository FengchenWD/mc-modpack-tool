using System.Windows;
using McModpackTool.App.Services;
using McModpackTool.App.UI;
using McModpackTool.Core.Models;

namespace McModpackTool.App.Views;

public partial class VersionSelectionWindow : Window
{
    private VersionSelectionWindow(IReadOnlyList<ServerVersionCandidate> candidates)
    {
        InitializeComponent();
        DataContext = App.Localization;
        VersionCombo.ItemsSource = candidates;
        VersionCombo.SelectedIndex = 0;
        SourceInitialized += (_, _) =>
        {
            bool dark = App.Settings.Theme == "dark" || App.Settings.Theme == "system" && ThemeService.IsSystemDark();
            if (WindowEffects.Apply(this, dark))
                Background = System.Windows.Media.Brushes.Transparent;
            else
                SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        };
    }

    public ServerVersionCandidate? SelectedCandidate { get; private set; }

    public static ServerVersionCandidate? Select(
        Window? owner,
        IReadOnlyList<ServerVersionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return null;
        }
        var dialog = new VersionSelectionWindow(candidates) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedCandidate : null;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        SelectedCandidate = VersionCombo.SelectedItem as ServerVersionCandidate;
        if (SelectedCandidate is null)
        {
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

}
