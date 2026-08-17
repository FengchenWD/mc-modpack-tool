using System.Reflection;

namespace McModpackTool.App.Services;

public static class ThirdPartyNoticeContent
{
    private const string LicenseResource = "McModpackTool.Legal.DotNetLicense.txt";
    private const string WindowsDesktopLicenseResource = "McModpackTool.Legal.DotNetWindowsDesktopLicense.txt";
    private const string NoticesResource = "McModpackTool.Legal.DotNetThirdPartyNotices.txt";

    public static string Get() =>
        Read(LicenseResource) + Environment.NewLine + Environment.NewLine +
        Read(WindowsDesktopLicenseResource) + Environment.NewLine + Environment.NewLine +
        Read(NoticesResource);

    private static string Read(string resourceName)
    {
        Assembly assembly = typeof(ThirdPartyNoticeContent).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded legal resource: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
