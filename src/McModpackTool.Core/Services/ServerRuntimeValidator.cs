using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace McModpackTool.Core.Services;

public sealed record ServerRuntimeValidationResult(
    bool Succeeded,
    bool TimedOut,
    int? ExitCode,
    IReadOnlyList<string> LogTail,
    string Error);

/// <summary>
/// Starts the staged server in an isolated copy and waits for Minecraft's explicit
/// successful-start signal. The caller must obtain user consent before invoking it.
/// </summary>
public sealed class ServerRuntimeValidator
{
    private const int MaximumLogLines = 200;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    public async Task<ServerRuntimeValidationResult> ValidateAsync(
        string stagedServerDirectory,
        string launchCommand,
        string javaExecutable,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedServerDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchCommand);
        string validationRoot = Path.Combine(
            Path.GetTempPath(),
            $"mc-modpack-tool-runtime-check-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(validationRoot);
            await ArchiveSafety.CopyDirectoryAsync(
                stagedServerDirectory,
                validationRoot,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(validationRoot, "eula.txt"),
                "eula=true" + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(validationRoot, "server.properties"),
                string.Join(
                    Environment.NewLine,
                    "server-ip=127.0.0.1",
                    "server-port=0",
                    "online-mode=false",
                    "level-name=validation-world",
                    "motd=MC Modpack Tool Validation",
                    "max-tick-time=-1",
                    string.Empty),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            string commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = commandProcessor,
                Arguments = $"/D /S /C \"{launchCommand}\"",
                WorkingDirectory = validationRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            ConfigureJava(startInfo, javaExecutable);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var log = new ConcurrentQueue<string>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Capture(string? line)
            {
                if (line is null) return;
                log.Enqueue(line);
                while (log.Count > MaximumLogLines)
                    log.TryDequeue(out _);
                if (line.Contains("Done (", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("For help", StringComparison.OrdinalIgnoreCase))
                    started.TrySetResult();
            }
            process.OutputDataReceived += (_, eventArgs) => Capture(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => Capture(eventArgs.Data);
            if (!process.Start())
                return Failure("The validation server process did not start.", log);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);
            Task exited = process.WaitForExitAsync(CancellationToken.None);
            Task completed;
            try
            {
                completed = await Task.WhenAny(
                    started.Task,
                    exited,
                    Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw;
            }

            if (started.Task.IsCompletedSuccessfully)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) { }
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                }
                return new ServerRuntimeValidationResult(
                    true,
                    false,
                    process.HasExited ? process.ExitCode : null,
                    log.ToArray(),
                    string.Empty);
            }

            if (completed == exited || process.HasExited)
            {
                return new ServerRuntimeValidationResult(
                    false,
                    false,
                    process.HasExited ? process.ExitCode : null,
                    log.ToArray(),
                    "The server exited before reporting a successful startup.");
            }

            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            return new ServerRuntimeValidationResult(
                false,
                true,
                null,
                log.ToArray(),
                "The server did not report a successful startup within the validation timeout.");
        }
        finally
        {
            TryDeleteDirectory(validationRoot);
        }
    }

    private static void ConfigureJava(ProcessStartInfo startInfo, string javaExecutable)
    {
        string path = (javaExecutable ?? string.Empty).Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return;
        path = Path.GetFullPath(path);
        string? javaBin = Path.GetDirectoryName(path);
        string? javaHome = javaBin is null ? null : Path.GetDirectoryName(javaBin);
        if (javaHome is not null)
            startInfo.Environment["JAVA_HOME"] = javaHome;
        if (javaBin is not null)
        {
            startInfo.Environment.TryGetValue("PATH", out string? existingPath);
            startInfo.Environment["PATH"] = javaBin + Path.PathSeparator + (existingPath ?? string.Empty);
        }
    }

    private static ServerRuntimeValidationResult Failure(
        string error,
        ConcurrentQueue<string> log) =>
        new(false, false, null, log.ToArray(), error);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
