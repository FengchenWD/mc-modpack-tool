using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using McModpackTool.App.Services;
using McModpackTool.App.UI;

namespace McModpackTool.App.Views;

public partial class ColorPickerWindow : Window
{
    private bool _syncing = true;

    private ColorPickerWindow(string initialHex)
    {
        InitializeComponent();
        DataContext = App.Localization;
        Color color = TryParseHex(initialHex, out var parsed) ? parsed : Color.FromRgb(22, 125, 106);
        SetColor(color);
        _syncing = false;
        SourceInitialized += (_, _) =>
        {
            bool dark = App.Settings.Theme == "dark" || App.Settings.Theme == "system" && ThemeService.IsSystemDark();
            if (WindowEffects.Apply(this, dark))
                Background = Brushes.Transparent;
            else
                SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        };
    }

    public string SelectedColorHex { get; private set; } = string.Empty;

    public static string? Pick(Window? owner, string initialHex)
    {
        var dialog = new ColorPickerWindow(initialHex);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dialog.ShowDialog() == true ? dialog.SelectedColorHex : null;
    }

    private void Rgb_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !TryByte(RedBox.Text, out byte red) ||
            !TryByte(GreenBox.Text, out byte green) || !TryByte(BlueBox.Text, out byte blue))
        {
            return;
        }
        UpdateFromRgb(Color.FromRgb(red, green, blue));
    }

    private void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !TryParseHex(HexBox.Text, out Color color))
        {
            return;
        }
        SetColor(color);
    }

    private void UpdateFromRgb(Color color)
    {
        _syncing = true;
        ClearInvalidBorders();
        HexBox.Text = ToHex(color);
        Preview.Background = new SolidColorBrush(color);
        SelectedColorHex = ToHex(color);
        _syncing = false;
    }

    private void SetColor(Color color)
    {
        _syncing = true;
        ClearInvalidBorders();
        RedBox.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GreenBox.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BlueBox.Text = color.B.ToString(CultureInfo.InvariantCulture);
        HexBox.Text = ToHex(color);
        Preview.Background = new SolidColorBrush(color);
        SelectedColorHex = ToHex(color);
        _syncing = false;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!TryByte(RedBox.Text, out byte red))
        {
            ShowInvalidRgb(RedBox, "R");
            return;
        }
        if (!TryByte(GreenBox.Text, out byte green))
        {
            ShowInvalidRgb(GreenBox, "G");
            return;
        }
        if (!TryByte(BlueBox.Text, out byte blue))
        {
            ShowInvalidRgb(BlueBox, "B");
            return;
        }
        if (!TryParseHex(HexBox.Text, out Color color))
        {
            HexBox.SetResourceReference(BorderBrushProperty, "DangerBrush");
            HexBox.Focus();
            HexBox.SelectAll();
            McModpackTool.App.MessageBox.Show(
                App.Localization["settings.color_hex_invalid"],
                App.Localization["settings.custom_color"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        SelectedColorHex = ToHex(color);
        DialogResult = true;
    }

    private void ShowInvalidRgb(TextBox box, string channel)
    {
        box.SetResourceReference(BorderBrushProperty, "DangerBrush");
        box.Focus();
        box.SelectAll();
        McModpackTool.App.MessageBox.Show(
            App.Localization.Translate("settings.color_rgb_invalid", channel),
            App.Localization["settings.custom_color"],
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void SystemPalette_Click(object sender, RoutedEventArgs e)
    {
        Color current = TryParseHex(SelectedColorHex, out var selected)
            ? selected
            : Color.FromRgb(22, 125, 106);
        using var picker = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };

        System.Windows.Forms.DialogResult result;
        IntPtr ownerHandle = new WindowInteropHelper(this).Handle;
        if (ownerHandle == IntPtr.Zero)
        {
            result = picker.ShowDialog();
        }
        else
        {
            result = picker.ShowDialog(new NativeWindowHandle(ownerHandle));
        }

        if (result == System.Windows.Forms.DialogResult.OK)
        {
            var color = picker.Color;
            SetColor(Color.FromRgb(color.R, color.G, color.B));
            ClearInvalidBorders();
        }
    }

    private void ClearInvalidBorders()
    {
        RedBox.ClearValue(BorderBrushProperty);
        GreenBox.ClearValue(BorderBrushProperty);
        BlueBox.ClearValue(BorderBrushProperty);
        HexBox.ClearValue(BorderBrushProperty);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryByte(string value, out byte result) =>
        byte.TryParse((value ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static bool TryParseHex(string value, out Color color)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (!normalized.StartsWith('#'))
        {
            normalized = "#" + normalized;
        }
        if (normalized.Length == 7 && uint.TryParse(normalized.AsSpan(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out uint rgb))
        {
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        color = default;
        return false;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private sealed class NativeWindowHandle : System.Windows.Forms.IWin32Window
    {
        public NativeWindowHandle(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
}
