using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace McModpackTool.Installer;

public partial class MainWindow : Window
{
    private const string ProductName = "MC整合包工具";
    private const string ProductVersion = "1.0.0-beta.6.1";
    private string _installedExecutable = string.Empty;
    private bool _installCompleted;

    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = GetDefaultInstallPath();
        SourceInitialized += (_, _) => ApplyWindowChrome();
    }

    private void ApplyWindowChrome()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        try
        {
            int corners = 2;
            DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
            int dark = 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static string GetDefaultInstallPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", ProductName);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int value,
        int valueSize);

    private void BrowseInstall_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择 MC整合包工具 的安装目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(InstallPathBox.Text) ? InstallPathBox.Text : GetDefaultInstallPath(),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            InstallPathBox.Text = dialog.SelectedPath;
    }

    private void PolyForm_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://polyformproject.org/licenses/noncommercial/1.0.0/");

    private void CreativeCommons_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://creativecommons.org/licenses/by-nc-sa/4.0/");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (!AgreementCheckBox.IsChecked.GetValueOrDefault())
        {
            ShowError("请先阅读并同意用户协议及许可说明。");
            return;
        }

        string installDirectory = InstallPathBox.Text.Trim();
        if (!ValidateInstallPath(installDirectory))
            return;

        Stream? payload = GetPayloadStream();
        if (payload is null)
        {
            ShowError("安装包中缺少软件文件。请使用构建脚本传入 InstallerPayload 后重新生成安装程序。");
            return;
        }

        SetInstallingState(true);
        try
        {
            Directory.CreateDirectory(installDirectory);
            string target = Path.Combine(installDirectory, ProductName + ".exe");
            string tempTarget = target + ".installing";
            if (File.Exists(tempTarget))
                File.Delete(tempTarget);

            StatusText.Text = "正在复制软件文件...";
            StatusText.Visibility = Visibility.Visible;
            DetailText.Visibility = Visibility.Visible;
            InstallProgress.Visibility = Visibility.Visible;
            await CopyPayloadAsync(payload, tempTarget);
            payload.Dispose();
            File.Move(tempTarget, target, true);

            WriteUninstaller(installDirectory);
            RegisterUninstall(installDirectory);
            if (StartMenuCheckBox.IsChecked == true)
                CreateShortcut(GetStartMenuShortcutPath(), target, "MC整合包工具");
            if (DesktopCheckBox.IsChecked == true)
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    ProductName + ".lnk"), target, ProductName);

            _installedExecutable = target;
            _installCompleted = true;
            InstallProgress.Value = 100;
            StatusText.Text = "安装完成";
            DetailText.Text = "可从开始菜单或桌面快捷方式启动 MC整合包工具。";
            InstallButton.Content = "已安装";
            LaunchButton.IsEnabled = true;
            LaunchButton.Focus();
        }
        catch (Exception ex)
        {
            ShowError("安装失败：" + ex.Message);
            StatusText.Visibility = Visibility.Collapsed;
            DetailText.Visibility = Visibility.Collapsed;
            InstallProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            payload.Dispose();
            SetInstallingState(false);
        }
    }

    private async Task CopyPayloadAsync(Stream payload, string destination)
    {
        const int bufferSize = 1024 * 1024;
        byte[] buffer = new byte[bufferSize];
        long total = payload.CanSeek ? payload.Length : 0;
        long copied = 0;
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        int read;
        while ((read = await payload.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            copied += read;
            if (total > 0)
            {
                InstallProgress.Value = Math.Min(100, copied * 100d / total);
                DetailText.Text = $"{copied / 1024d / 1024d:0.0} / {total / 1024d / 1024d:0.0} MB";
            }
        }
        await output.FlushAsync();
    }

    private Stream? GetPayloadStream()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        return assembly.GetManifestResourceStream("McModpackTool.Installer.Payload.exe")
            ?? FindPayloadResource(assembly);
    }

    private static Stream? FindPayloadResource(Assembly assembly)
    {
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(".Payload.exe", StringComparison.OrdinalIgnoreCase))
                return assembly.GetManifestResourceStream(name);
        }
        return null;
    }

    private bool ValidateInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowError("请选择安装目录。");
            return false;
        }
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                ShowError("不能直接安装到磁盘根目录，请选择一个子目录。");
                return false;
            }
            InstallPathBox.Text = fullPath;
            return true;
        }
        catch
        {
            ShowError("安装目录路径无效，请重新选择。");
            return false;
        }
    }

    private void SetInstallingState(bool installing)
    {
        InstallButton.IsEnabled = !installing && !_installCompleted;
        BrowseInstallButton.IsEnabled = !installing;
        StartMenuCheckBox.IsEnabled = !installing;
        DesktopCheckBox.IsEnabled = !installing;
        AgreementCheckBox.IsEnabled = !installing;
        Mouse.OverrideCursor = installing ? Cursors.Wait : null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_installedExecutable) || !File.Exists(_installedExecutable))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(_installedExecutable) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            ShowError("启动软件失败：" + ex.Message);
        }
    }

    private static string GetStartMenuShortcutPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ProductName + ".lnk");

    private static void CreateShortcut(string shortcutPath, string targetPath, string description)
    {
        string? directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return;
        object? shell = Activator.CreateInstance(shellType);
        object? shortcut = shellType.InvokeMember("CreateShortcut",
            BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
        if (shortcut is null)
            return;
        Type shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
            new object[] { targetPath });
        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
            new object[] { Path.GetDirectoryName(targetPath) ?? string.Empty });
        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
            new object[] { description });
        shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
            new object[] { targetPath + ",0" });
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    private static void WriteUninstaller(string installDirectory)
    {
        string scriptPath = Path.Combine(installDirectory, "uninstall.ps1");
        string cmdPath = Path.Combine(installDirectory, "uninstall.cmd");
        string escaped = installDirectory.Replace("'", "''");
        string script = $@"$ErrorActionPreference = 'SilentlyContinue'
Start-Sleep -Milliseconds 500
$installPath = '{escaped}'
$shortcutPaths = @(
    [Environment]::GetFolderPath('Desktop') + '\{ProductName}.lnk',
    [Environment]::GetFolderPath('StartMenu') + '\Programs\{ProductName}.lnk'
)
foreach ($shortcut in $shortcutPaths) {{ Remove-Item -LiteralPath $shortcut -Force }}
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MCModpackTool' -Recurse -Force
Remove-Item -LiteralPath $installPath -Recurse -Force
";
        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(false));
        string cmd = "@echo off\r\npowershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%~dp0uninstall.ps1\"\r\n";
        File.WriteAllText(cmdPath, cmd, new System.Text.UTF8Encoding(false));
    }

    private static void RegisterUninstall(string installDirectory)
    {
        string uninstallCmd = Path.Combine(installDirectory, "uninstall.cmd");
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MCModpackTool");
        key?.SetValue("DisplayName", ProductName);
        key?.SetValue("DisplayVersion", ProductVersion);
        key?.SetValue("Publisher", "风尘WD");
        key?.SetValue("InstallLocation", installDirectory);
        key?.SetValue("UninstallString", "\"" + uninstallCmd + "\"");
        key?.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key?.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

}
