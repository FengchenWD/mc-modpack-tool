using System.Text.Json;
using System.Text.Json.Nodes;

namespace McModpackTool.App.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public string SettingsPath { get; } = ResolveSettingsPath();

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            string json = await File.ReadAllTextAsync(SettingsPath, cancellationToken);
            if (JsonNode.Parse(json) is not JsonObject root) return new AppSettings();
            var preferences = root["ui_preferences"] as JsonObject;
            var settings = new AppSettings
            {
                TargetMinecraft = Text(root, "target_mc", "targetMinecraft", "1.21.1"),
                TargetLoaderType = Text(root, "target_loader_type", "targetLoaderType", "fabric"),
                TargetLoaderVersion = Text(root, "target_loader_version", "targetLoaderVersion", ""),
                OutputDirectory = Text(root, "output_dir", "outputDirectory", ""),
                Language = preferences is null ? Text(root, "language", "language", "zh_CN") : Text(preferences, "language", "language", "zh_CN"),
                Theme = preferences is null ? Text(root, "theme", "theme", "light") : Text(preferences, "theme", "theme", "light"),
                AccentColor = preferences is null ? Text(root, "accent_color", "accentColor", "#167D6A") : Text(preferences, "accent_color", "accentColor", "#167D6A"),
                FontFamily = preferences is null ? Text(root, "font_family", "fontFamily", "Microsoft YaHei UI") : Text(preferences, "font_family", "fontFamily", "Microsoft YaHei UI"),
                AcceptedAgreementVersion = Text(root, "accepted_agreement_version", "acceptedAgreementVersion", "")
            };
            return Normalize(settings);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            settings = Normalize(settings);
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            JsonObject root = await ReadExistingAsync(cancellationToken);
            root["target_mc"] = settings.TargetMinecraft;
            root["target_loader_type"] = settings.TargetLoaderType;
            root["target_loader_version"] = settings.TargetLoaderVersion;
            root["output_dir"] = settings.OutputDirectory;
            root["ui_preferences"] = new JsonObject
            {
                ["schema_version"] = 1,
                ["language"] = settings.Language,
                ["theme"] = settings.Theme,
                ["accent_color"] = settings.AccentColor,
                ["font_family"] = settings.FontFamily
            };
            if (!string.IsNullOrWhiteSpace(settings.AcceptedAgreementVersion))
                root["accepted_agreement_version"] = settings.AcceptedAgreementVersion;

            string temporaryPath = SettingsPath + "." + Environment.ProcessId + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(JsonOptions), cancellationToken);
                File.Move(temporaryPath, SettingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<JsonObject> ReadExistingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SettingsPath)) return [];
        try
        {
            string json = await File.ReadAllTextAsync(SettingsPath, cancellationToken);
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    private static string Text(JsonObject root, string primary, string alternate, string fallback)
    {
        foreach (string key in new[] { primary, alternate })
        {
            try
            {
                if (root[key]?.GetValue<string>() is { } value) return value;
            }
            catch (InvalidOperationException) { }
        }
        return fallback;
    }

    private static AppSettings Normalize(AppSettings value)
    {
        string[] languages = ["zh_CN", "zh_HK", "en_US"];
        string[] themes = ["light", "dark", "system"];
        if (!languages.Contains(value.Language)) value.Language = "zh_CN";
        if (!themes.Contains(value.Theme)) value.Theme = "light";
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.AccentColor ?? "", "^#[0-9A-Fa-f]{6}$")) value.AccentColor = "#167D6A";
        value.FontFamily = string.IsNullOrWhiteSpace(value.FontFamily) ? "Microsoft YaHei UI" : value.FontFamily.Trim();
        return value;
    }

    private static string ResolveSettingsPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("MC_PACK_MIGRATOR_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mc_pack_migrator_config.json");
    }
}
