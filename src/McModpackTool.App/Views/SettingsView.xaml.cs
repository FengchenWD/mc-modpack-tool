using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace McModpackTool.App.Views;

public partial class SettingsView : UserControl
{
    private const string DefaultFontFamily = "Microsoft YaHei UI";
    private bool _initializing = true;
    private bool _loadingFonts;
    private bool _fontsLoaded;
    public event Func<string, Task>? LanguageChangeRequested;

    public SettingsView()
    {
        InitializeComponent();
        DataContext = App.Localization;
        Loaded += SettingsView_Loaded;
        App.Localization.LanguageChanged += (_, _) => Dispatcher.Invoke(SyncLanguageSelection);
    }

    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            SyncLanguageSelection();
            LightTheme.IsChecked = App.Settings.Theme == "light";
            DarkTheme.IsChecked = App.Settings.Theme == "dark";
            SystemTheme.IsChecked = App.Settings.Theme == "system";
            _initializing = false;
        }

        if (_fontsLoaded || _loadingFonts) return;
        _loadingFonts = true;
        FontCombo.IsEnabled = false;
        DefaultFontButton.IsEnabled = false;
        try
        {
            string[] fontNames = await Task.Run(() =>
            {
                using var installedFonts = new System.Drawing.Text.InstalledFontCollection();
                return installedFonts.Families
                    .Select(font => font.Name)
                    .Append(DefaultFontFamily)
                    .Append(App.Settings.FontFamily)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            });
            FontCombo.ItemsSource = fontNames;
            FontCombo.SelectedItem = fontNames.FirstOrDefault(name => string.Equals(name, App.Settings.FontFamily, StringComparison.CurrentCultureIgnoreCase));
            _fontsLoaded = true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            MessageBox.Show(App.Localization["dialog.settings_fonts_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loadingFonts = false;
            FontCombo.IsEnabled = true;
            DefaultFontButton.IsEnabled = true;
        }
    }

    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        if (LanguageChangeRequested is { } handler)
            await handler(language);
        else
            App.Localization.SetLanguage(language);
        App.Settings.Language = language;
        await SaveAsync();
    }

    private async void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string theme }) return;
        App.Settings.Theme = theme;
        App.Theme.Apply(theme, App.Settings.AccentColor, App.Settings.FontFamily, animate: true);
        await SaveAsync();
    }

    private async void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color }) await ApplyAccentAsync(color);
    }

    private async void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.ColorTranslator.FromHtml(App.Settings.AccentColor)
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            await ApplyAccentAsync($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    private async void ResetColor_Click(object sender, RoutedEventArgs e) => await ApplyAccentAsync("#167D6A");

    private async Task ApplyAccentAsync(string color)
    {
        App.Settings.AccentColor = color;
        App.Theme.Apply(App.Settings.Theme, color, App.Settings.FontFamily, animate: true);
        await SaveAsync();
    }

    private async void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _loadingFonts || FontCombo.SelectedItem is not string font) return;
        App.Settings.FontFamily = font;
        App.Theme.Apply(App.Settings.Theme, App.Settings.AccentColor, font, animate: true);
        await SaveAsync();
    }

    private async void DefaultFont_Click(object sender, RoutedEventArgs e)
    {
        _loadingFonts = true;
        FontCombo.SelectedItem = FontCombo.Items.Cast<string>()
            .FirstOrDefault(name => string.Equals(name, DefaultFontFamily, StringComparison.CurrentCultureIgnoreCase));
        _loadingFonts = false;
        App.Settings.FontFamily = DefaultFontFamily;
        App.Theme.Apply(App.Settings.Theme, App.Settings.AccentColor, DefaultFontFamily, animate: true);
        await SaveAsync();
    }

    private static async Task SaveAsync()
    {
        try { await App.SettingsStore.SaveAsync(App.Settings); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            MessageBox.Show(App.Localization["dialog.settings_save_failed"], App.Localization["common.error"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SyncLanguageSelection()
    {
        bool restore = _initializing;
        _initializing = true;
        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if (!Equals(item.Tag, App.Localization.Language)) continue;
            LanguageCombo.SelectedItem = item;
            break;
        }
        _initializing = restore;
    }
}
