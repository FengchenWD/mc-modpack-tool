namespace McModpackTool.App.Services;

public sealed class AppSettings
{
    public string TargetMinecraft { get; set; } = "1.21.1";
    public string TargetLoaderType { get; set; } = "fabric";
    public string TargetLoaderVersion { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string Language { get; set; } = "zh_CN";
    public string Theme { get; set; } = "light";
    public string AccentColor { get; set; } = "#167D6A";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public string AcceptedAgreementVersion { get; set; } = "";
}
