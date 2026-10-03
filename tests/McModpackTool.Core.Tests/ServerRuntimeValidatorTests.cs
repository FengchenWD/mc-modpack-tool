using System.Text;
using McModpackTool.Core.Services;

namespace McModpackTool.Core.Tests;

public static class ServerRuntimeValidatorTests
{
    public static async Task RunAllAsync()
    {
        await DetectsExplicitSuccessfulStartupAsync();
        await RejectsExitBeforeStartupAsync();
    }

    private static async Task DetectsExplicitSuccessfulStartupAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string script = Path.Combine(root, "fake-server.bat");
            await File.WriteAllTextAsync(
                script,
                "@echo off\r\necho [Server thread/INFO]: Done (0.123s)! For help, type \"help\"\r\nset /p command=\r\nexit /b 0\r\n",
                Encoding.ASCII);
            var validator = new ServerRuntimeValidator();
            ServerRuntimeValidationResult result = await validator.ValidateAsync(
                root,
                "call fake-server.bat",
                "java",
                TimeSpan.FromSeconds(10));
            True(result.Succeeded, "The explicit successful-start signal was not accepted.");
            True(result.LogTail.Any(line => line.Contains("Done (", StringComparison.Ordinal)),
                "The validation log did not retain the successful-start line.");
        });
    }

    private static async Task RejectsExitBeforeStartupAsync()
    {
        await WithTemporaryDirectoryAsync(async root =>
        {
            string script = Path.Combine(root, "fake-failure.bat");
            await File.WriteAllTextAsync(
                script,
                "@echo off\r\necho Incompatible mods found 1>&2\r\nexit /b 3\r\n",
                Encoding.ASCII);
            var validator = new ServerRuntimeValidator();
            ServerRuntimeValidationResult result = await validator.ValidateAsync(
                root,
                "call fake-failure.bat",
                "java",
                TimeSpan.FromSeconds(10));
            True(!result.Succeeded, "A server that exited before startup was accepted.");
            True(result.ExitCode == 3, "The failed validation did not preserve the exit code.");
            True(result.LogTail.Any(line => line.Contains("Incompatible mods", StringComparison.OrdinalIgnoreCase)),
                "The failed validation did not retain its diagnostic log.");
        });
    }

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "mc-modpack-tool-runtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await action(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
