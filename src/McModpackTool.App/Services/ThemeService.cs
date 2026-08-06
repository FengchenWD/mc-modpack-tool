using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace McModpackTool.App.Services;

public sealed class ThemeService
{
    private ResourceDictionary? _resources;
    private readonly DispatcherTimer _systemThemeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private AppSettings? _settings;
    private bool _lastSystemDark;

    public event EventHandler? ThemeChanged;

    public void Initialize(ResourceDictionary resources, AppSettings settings)
    {
        _resources = resources;
        _settings = settings;
        _lastSystemDark = IsSystemDark();
        Apply(settings.Theme, settings.AccentColor, settings.FontFamily, animate: false);
        _systemThemeTimer.Tick += (_, _) =>
        {
            bool current = IsSystemDark();
            if (current == _lastSystemDark) return;
            _lastSystemDark = current;
            if (_settings?.Theme == "system")
                Apply("system", _settings.AccentColor, _settings.FontFamily, animate: true);
        };
        _systemThemeTimer.Start();
    }

    public void Apply(string theme, string accentHex, string fontFamily, bool animate = true)
    {
        if (_resources is null) return;
        bool dark = theme == "dark" || (theme == "system" && IsSystemDark());
        Color accent = ParseColor(accentHex, Color.FromRgb(22, 125, 106));
        var colors = BuildPalette(dark, accent);
        var duration = animate ? TimeSpan.FromMilliseconds(240) : TimeSpan.Zero;

        foreach ((string key, Color color) in colors)
        {
            Color visibleColor = _resources[key] is SolidColorBrush current ? current.Color : color;
            var replacement = new SolidColorBrush(color);
            if (duration != TimeSpan.Zero && visibleColor != color)
            {
                // Configure the animation before the brush enters Application.Resources;
                // WPF may make an unanimated resource brush read-only immediately.
                replacement.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(visibleColor, color, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
            }
            _resources[key] = replacement;
        }

        try { _resources["AppFontFamily"] = new FontFamily(fontFamily); }
        catch (ArgumentException) { _resources["AppFontFamily"] = new FontFamily("Microsoft YaHei UI"); }
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool IsSystemDark()
    {
        try
        {
            object? raw = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return Convert.ToInt32(raw) == 0;
        }
        catch { return false; }
    }

    private static Dictionary<string, Color> BuildPalette(bool dark, Color accent)
    {
        Color app = Alpha(dark ? C("#171A19") : C("#F4F7F6"), 232);
        Color surface = Alpha(dark ? C("#222625") : Colors.White, 245);
        Color surfaceAlt = Alpha(dark ? C("#2A302E") : C("#F0F5F3"), 232);
        Color text = dark ? C("#F2F5F4") : C("#17211F");
        Color muted = dark ? C("#AAB5B1") : C("#66736F");
        Color border = dark ? C("#3A423F") : C("#DDE6E2");
        Color accentSoft = Mix(accent, surface, dark ? 0.72 : 0.84);
        Color sidebar = Alpha(Mix(accent, app, dark ? 0.84 : 0.90), 238);
        Color accentText = Contrast(accent, Colors.White) >= 4.5 ? Colors.White : C("#101513");
        return new()
        {
            ["AppBackgroundBrush"] = app,
            ["SurfaceBrush"] = surface,
            ["SurfaceAltBrush"] = surfaceAlt,
            ["TextBrush"] = text,
            ["MutedTextBrush"] = muted,
            ["BorderBrush"] = border,
            ["AccentBrush"] = accent,
            ["AccentHoverBrush"] = Mix(accent, dark ? Colors.White : Colors.Black, 0.12),
            ["AccentSoftBrush"] = accentSoft,
            ["AccentForegroundBrush"] = accentText,
            ["SidebarBrush"] = sidebar,
            ["SidebarHoverBrush"] = Mix(accent, sidebar, 0.76),
            ["DangerBrush"] = dark ? C("#FF8686") : C("#C23B3B"),
            ["DangerSoftBrush"] = dark ? C("#4A2929") : C("#FDECEC"),
            ["WarningBrush"] = dark ? C("#F3B761") : C("#A96000"),
            ["SuccessBrush"] = dark ? C("#61C98D") : C("#18864B"),
            ["InputBrush"] = dark ? C("#1D211F") : C("#FBFDFC"),
            ["SelectionBrush"] = accentSoft,
            ["OverlayBrush"] = dark ? C("#B8000000") : C("#92000000")
        };
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value ?? "")!; }
        catch { return fallback; }
    }

    private static Color C(string value) => (Color)ColorConverter.ConvertFromString(value)!;
    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double F(byte value)
            {
                double x = value / 255d;
                return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
        }
        double l1 = L(a), l2 = L(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
