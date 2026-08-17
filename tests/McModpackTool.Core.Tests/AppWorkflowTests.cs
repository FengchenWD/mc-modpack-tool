using McModpackTool.App.Services;
using McModpackTool.App.UI;
using McModpackTool.App.Views;
using McModpackTool.Core.Models;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace McModpackTool.Core.Tests;

internal static class AppWorkflowTests
{
    public static async Task RunAllAsync()
    {
        OutputNamesTrackTargetVersion();
        SourcePackNamesPreferInputFilesAndHandleIncompletePaths();
        TargetValidationRejectsMalformedMinecraftVersions();
        AgreementContainsRequiredTermsInEveryLanguage();
        LocalizationCatalogCoversVisibleKeys();
        LocalizationCatalogsHaveIdenticalKeySets();
        ThemeApplicationHandlesCompiledResources();
        await ConcurrentSettingsSavesAreSerializedAsync();
        await LegacySettingsRoundTripPreservesAgreementAsync();
    }

    private static void OutputNamesTrackTargetVersion()
    {
        Assert(MigrationView.GenerateOutputPackName("1.21.1 录制", "1.21.1", "1.21.11") == "1.21.11 录制", "Source MC version should be replaced.");
        Assert(MigrationView.GenerateOutputPackName("1.21.10 录制", "1.21.1", "1.21.11") == "1.21.11 录制", "A leading version in the source name must not be duplicated when manifest metadata differs.");
        Assert(MigrationView.GenerateOutputPackName("1.21.11 录制", "1.21.1", "1.21.11") == "1.21.11 录制", "An already-targeted source name must remain stable.");
        Assert(MigrationView.GenerateOutputPackName("录制", "1.21.1", "1.21.11") == "1.21.11 录制", "Target should prefix a name without a source version.");
    }

    private static void SourcePackNamesPreferInputFilesAndHandleIncompletePaths()
    {
        string inputPath = Path.Combine("D:\\packs", "1.21.1 Recording.zip");
        Assert(
            MigrationView.SelectSourcePackName(inputPath, "Manifest Pack") == "1.21.1 Recording",
            "The input archive file name must take precedence over the manifest name.");

        Assert(
            MigrationView.SelectSourcePackName(string.Empty, "  Manifest Pack  ") == "Manifest Pack",
            "An empty input path must fall back to the trimmed manifest name.");
        Assert(
            MigrationView.SelectSourcePackName(null, "Manifest Pack") == "Manifest Pack",
            "A null input path must fall back to the manifest name.");
        Assert(
            MigrationView.SelectSourcePackName("   ", null) == string.Empty,
            "Missing input and manifest names must produce an empty source name.");
        Assert(
            MigrationView.SelectSourcePackName("C:\\packs\\", "Manifest Pack") == "Manifest Pack",
            "A directory-only input must fall back to the manifest name.");

        foreach (string incompletePath in new[] { "\0", "C:\\", "C:\\packs\\...", "\"unfinished" })
        {
            try
            {
                _ = MigrationView.SelectSourcePackName(incompletePath, "Manifest Pack");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"An incomplete or malformed input path escaped the source-name helper: {exception.Message}",
                    exception);
            }
        }
    }

    private static void TargetValidationRejectsMalformedMinecraftVersions()
    {
        Type inputsType = typeof(MigrationView).GetNestedType(
            "AnalysisInputs",
            BindingFlags.NonPublic) ?? throw new InvalidOperationException("AnalysisInputs type was not found.");
        MethodInfo validator = typeof(MigrationView).GetMethod(
            "TargetIsComplete",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("TargetIsComplete validator was not found.");

        bool IsComplete(string minecraft)
        {
            object inputs = Activator.CreateInstance(
                inputsType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [string.Empty, minecraft, "fabric", "0.16.14", string.Empty],
                culture: null) ?? throw new InvalidOperationException("Could not create analysis inputs.");
            return validator.Invoke(null, [inputs]) as bool? == true;
        }

        foreach (string valid in new[] { "1.21", "1.21.1", " 1.21.1 ".Trim() })
            Assert(IsComplete(valid), $"Valid Minecraft target was rejected: {valid}");

        foreach (string invalid in new[] { "", "abc", "1", "1.21.1x", "1.21.1.1", "1..21", "v1.21.1" })
            Assert(!IsComplete(invalid), $"Malformed Minecraft target was accepted: {invalid}");
    }

    private static void AgreementContainsRequiredTermsInEveryLanguage()
    {
        foreach (string language in new[] { "zh_CN", "zh_HK", "en_US" })
        {
            string text = AgreementContent.Get(language);
            Assert(text.Contains("PolyForm Noncommercial License 1.0.0", StringComparison.Ordinal), $"Code license missing for {language}.");
            Assert(text.Contains("https://polyformproject.org/licenses/noncommercial/1.0.0", StringComparison.Ordinal), $"Code license URL missing for {language}.");
            Assert(text.Contains("CC BY-NC-SA 4.0", StringComparison.Ordinal), $"Agreement license missing for {language}.");
            Assert(text.Contains("https://creativecommons.org/licenses/by-nc-sa/4.0/", StringComparison.Ordinal), $"Agreement URL missing for {language}.");
            Assert(text.Contains("dual license", StringComparison.OrdinalIgnoreCase) || text.Contains("dual-license", StringComparison.OrdinalIgnoreCase), $"No-dual-license statement missing for {language}.");
            Assert(text.Contains("third-party", StringComparison.OrdinalIgnoreCase) || text.Contains("第三方", StringComparison.Ordinal), $"Third-party license boundary missing for {language}.");
            Assert(text.Contains("Required Notice:", StringComparison.Ordinal), $"PolyForm required notice missing for {language}.");
            Assert(text.Contains("AI", StringComparison.OrdinalIgnoreCase), $"AI disclosure missing for {language}.");
            Assert(text.Contains("Minecraft", StringComparison.OrdinalIgnoreCase), $"Minecraft disclaimer missing for {language}.");
            Assert(text.Contains("CurseForge", StringComparison.OrdinalIgnoreCase), $"Network disclosure missing for {language}.");
        }

        string thirdPartyNotices = ThirdPartyNoticeContent.Get();
        Assert(thirdPartyNotices.Contains("MIT License", StringComparison.Ordinal), "Embedded .NET MIT license missing.");
        Assert(thirdPartyNotices.Contains(".NET", StringComparison.OrdinalIgnoreCase), "Embedded .NET third-party notices missing.");
    }

    private static void LocalizationCatalogCoversVisibleKeys()
    {
        string[] keys =
        [
            "app.name", "nav.home", "nav.migration", "nav.client_pack", "nav.server", "nav.settings",
            "home.migration", "home.client_pack", "home.server", "home.agreement", "migration.title",
            "migration.drop_title", "migration.check", "migration.build", "settings.title",
            "settings.language", "settings.appearance", "settings.light", "settings.dark",
            "settings.system", "settings.accent", "settings.font", "agreement.title",
            "agreement.code_license", "agreement.asset_license", "agreement.third_party", "agreement.third_party_title",
            "agreement.accept", "agreement.decline", "build.open_folder",
            "window.minimize", "window.maximize_restore", "window.close",
            "build.incomplete", "build.incomplete_status", "status.canceling",
            "status.cancelled", "status.analysis_failed", "dialog.drop_failed",
            "dialog.read_failed", "dialog.analysis_failed", "dialog.settings_save_failed",
            "compat.scope.content", "compat.issue.item_not_found",
            "compat.issue.loader_dependency_mismatch", "deps.entry",
            "migration.subtitle", "deps.body", "server.dialog.incompatible",
            "server.building_core", "server.building_mods_copy", "server.building_mods_download",
            "server.building_config", "server.building_world", "server.building_files",
            "server.building_archive", "server.support.optional", "server.build_launch_hint",
            "client.title", "client.subtitle", "client.source_hint", "client.content",
            "client.group.mod", "client.group.world", "client.group.other", "client.build",
            "client.dialog.format_required", "client.dialog.reselect_instance"
        ];
        var localization = new LocalizationService();
        foreach (string language in new[] { "zh_CN", "zh_HK", "en_US" })
        {
            localization.SetLanguage(language, animate: false);
            foreach (string key in keys) Assert(localization[key] != key, $"Missing {language} localization key: {key}");
        }
        localization.SetLanguage("zh_CN", animate: false);
        Assert(localization["migration.subtitle"] == "CF/MR整合包目标游戏版本自动转换",
            "The migration subtitle does not match the requested Simplified Chinese text.");
        Assert(localization["deps.body"].Contains("仅供参考", StringComparison.Ordinal),
            "The missing-dependency notice must say that platform metadata is for reference only.");
        Assert(localization["server.dialog.incompatible"].Contains("保留该模组并继续导出", StringComparison.Ordinal),
            "The server compatibility prompt must explain that No keeps the mod and continues export.");
        foreach (string language in new[] { "zh_CN", "zh_HK", "en_US" })
        {
            localization.SetLanguage(language, animate: false);
            Assert(localization["server.build_launch_hint"].Contains("start.bat", StringComparison.Ordinal)
                   && localization["server.build_launch_hint"].Contains("server-console.ps1", StringComparison.Ordinal),
                $"The server launch hint does not name both launch scripts for {language}.");
        }
    }

    private static void LocalizationCatalogsHaveIdenticalKeySets()
    {
        FieldInfo field = typeof(LocalizationService).GetField(
            "Tables",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Localization tables were not found.");
        var tables = field.GetValue(null) as IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            ?? throw new InvalidOperationException("Localization tables have an unexpected type.");

        string[] languages = ["zh_CN", "zh_HK", "en_US"];
        Assert(languages.All(tables.ContainsKey), "One or more supported language tables are missing.");
        HashSet<string> baseline = tables["zh_CN"].Keys.ToHashSet(StringComparer.Ordinal);
        foreach (string language in languages.Skip(1))
        {
            HashSet<string> actual = tables[language].Keys.ToHashSet(StringComparer.Ordinal);
            string[] missing = baseline.Except(actual).Order().ToArray();
            string[] extra = actual.Except(baseline).Order().ToArray();
            Assert(missing.Length == 0 && extra.Length == 0,
                $"Localization key set differs for {language}. Missing: [{string.Join(", ", missing)}]; extra: [{string.Join(", ", extra)}].");
        }
    }

    private static void ThemeApplicationHandlesCompiledResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new McModpackTool.App.App();
                application.InitializeComponent();
                var settings = new AppSettings
                {
                    Theme = "light",
                    AccentColor = "#167D6A",
                    FontFamily = "Microsoft YaHei UI"
                };
                var theme = new ThemeService();

                theme.Initialize(application.Resources, settings);
                theme.Apply("dark", "#2563EB", "Segoe UI", animate: true);
                Assert(application.Resources["AppBackgroundBrush"] is SolidColorBrush,
                    "Compiled application theme resources must remain available after replacement.");

                var button = new System.Windows.Controls.Button
                {
                    Style = (Style)application.FindResource("FluentButtonStyle")
                };
                button.ApplyTemplate();
                PressAnimationBehavior.OnPress(
                    button,
                    new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount,
                        System.Windows.Input.MouseButton.Left));
                Assert(
                    button.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue &&
                    button.RenderTransform is ScaleTransform { IsFrozen: false, HasAnimatedProperties: true },
                    "Styled button press must create an animated writable local transform.");

                var frozenTransform = new ScaleTransform(1, 1);
                frozenTransform.Freeze();
                button.RenderTransform = frozenTransform;
                PressAnimationBehavior.OnRelease(button, new RoutedEventArgs());
                Assert(button.RenderTransform is ScaleTransform { IsFrozen: false },
                    "Press animation must replace a frozen transform with a writable local transform.");

                var mainWindow = new McModpackTool.App.MainWindow();
                mainWindow.ApplyTemplate();
                var agreementWindow = new AgreementWindow(requireAcceptance: false);
                var buildSuccessWindow = new BuildSuccessWindow("ok", Path.GetTempPath());
                var appDialogWindow = new AppDialogWindow(
                    "message", "title", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.OK);
                Assert(mainWindow.FontFamily.Source == "Segoe UI",
                    "The selected application font must reach the main window and its content.");
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(mainWindow);
                Assert(mainWindow.WindowStyle == WindowStyle.None && chrome?.CaptionHeight == 40,
                    "Main window must use the custom WindowChrome title bar.");
                Assert(mainWindow.MinWidth == 1120 && mainWindow.MinHeight == 600,
                    "Main window must retain a usable minimum size without exceeding common scaled work areas.");
                VerifyNativeMinimumTrackSize(mainWindow);
                var contentViewport = (System.Windows.Controls.ScrollViewer)mainWindow.FindName("ContentViewport");
                Assert(
                    contentViewport.HorizontalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Auto &&
                    contentViewport.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Disabled &&
                    contentViewport.HorizontalContentAlignment == HorizontalAlignment.Stretch &&
                    contentViewport.VerticalContentAlignment == VerticalAlignment.Stretch,
                    "Main window content must stretch and provide horizontal overflow instead of clipping pages.");
                var sidebarToggle = (System.Windows.Controls.Button)mainWindow.FindName("SidebarToggleButton");
                var sidebarToggleIcon = (System.Windows.Controls.TextBlock)mainWindow.FindName("SidebarToggleIcon");
                var sidebarColumn = (System.Windows.Controls.ColumnDefinition)mainWindow.FindName("SidebarColumn");
                Assert(
                    sidebarToggle.Width == 40 && sidebarToggle.Height == 40 &&
                    sidebarToggle.MinWidth == 40 && sidebarToggle.MinHeight == 40 &&
                    sidebarToggle.MaxWidth == 40 && sidebarToggle.MaxHeight == 40 &&
                    sidebarToggle.Padding == new Thickness(0) &&
                    sidebarToggle.HorizontalContentAlignment == HorizontalAlignment.Center &&
                    sidebarToggle.VerticalContentAlignment == VerticalAlignment.Center &&
                    sidebarToggleIcon.FontFamily.Source == "Segoe Fluent Icons" &&
                    sidebarToggleIcon.TextAlignment == TextAlignment.Center,
                    "Sidebar toggle geometry and icon font must remain stable across layout and theme changes.");
                sidebarToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert(sidebarColumn.Width.Value == 64 && sidebarToggleIcon.Text == "\uE76C",
                    "Collapsing the sidebar must use the fixed expand glyph and compact width.");
                sidebarToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert(sidebarColumn.Width.Value == 176 && sidebarToggleIcon.Text == "\uE76B",
                    "Expanding the sidebar must restore the fixed collapse glyph and width.");
                var mainTitleBar = (AppTitleBar)mainWindow.FindName("MainTitleBar");
                var clientNavigation = (System.Windows.Controls.RadioButton)mainWindow.FindName("ClientPackNav");
                Assert(clientNavigation.CommandParameter as string == "client_pack",
                    "The client modpack page must have its own sidebar navigation entry.");
                FieldInfo pagesField = typeof(McModpackTool.App.MainWindow).GetField(
                    "_pages", BindingFlags.Instance | BindingFlags.NonPublic) ??
                    throw new InvalidOperationException("Main window page catalog was not found.");
                var pages = pagesField.GetValue(mainWindow) as IReadOnlyDictionary<string, FrameworkElement>
                    ?? throw new InvalidOperationException("Main window page catalog has an unexpected type.");
                Assert(pages.TryGetValue("client_pack", out FrameworkElement? clientPage) && clientPage is ClientPackView,
                    "The client modpack page must be created once and retained by the main window.");
                var clientView = (ClientPackView)clientPage!;
                var modrinthFormat = (System.Windows.Controls.CheckBox)clientView.FindName("ModrinthFormatCheckBox");
                var curseForgeFormat = (System.Windows.Controls.CheckBox)clientView.FindName("CurseForgeFormatCheckBox");
                Assert(modrinthFormat.IsChecked == true && curseForgeFormat.IsChecked != true,
                    "Modrinth must be the default client pack format and CurseForge must remain available.");
                Assert(clientView.FindName("DropZone") is null,
                    "The folder-only client pack workflow must not expose a drag-and-drop target.");
                var homePage = (HomeView)pages["home"];
                Assert(homePage.FindName("MigrationHomeButton") is System.Windows.Controls.Button &&
                       homePage.FindName("ClientPackHomeButton") is System.Windows.Controls.Button &&
                       homePage.FindName("ServerHomeButton") is System.Windows.Controls.Button,
                    "The home page must expose all three module entries.");
                VerifyClientPackWorkflow(clientView);
                Assert(mainTitleBar.FindName("MinimizeButton") is System.Windows.Controls.Button &&
                       mainTitleBar.FindName("MaximizeButton") is System.Windows.Controls.Button &&
                       mainTitleBar.FindName("CloseButton") is System.Windows.Controls.Button,
                    "Custom title bar buttons were not created.");
                Assert(System.Windows.Shell.WindowChrome.GetWindowChrome(agreementWindow)?.CaptionHeight == 40 &&
                       System.Windows.Shell.WindowChrome.GetWindowChrome(buildSuccessWindow)?.CaptionHeight == 40 &&
                       System.Windows.Shell.WindowChrome.GetWindowChrome(appDialogWindow)?.CaptionHeight == 40 &&
                       agreementWindow.WindowStyle == WindowStyle.None && buildSuccessWindow.WindowStyle == WindowStyle.None &&
                       appDialogWindow.WindowStyle == WindowStyle.None,
                    "Application dialogs must share the custom WindowChrome title bar.");
                theme.Apply("dark", "#2563EB", "Microsoft YaHei UI", animate: false);
                var homeNavigation = (System.Windows.Controls.RadioButton)mainWindow.FindName("HomeNav");
                var maximizeButton = (System.Windows.Controls.Button)mainTitleBar.FindName("MaximizeButton");
                Assert(mainWindow.FontFamily.Source == "Microsoft YaHei UI" &&
                       homeNavigation.FontFamily.Source == "Microsoft YaHei UI" &&
                       agreementWindow.FontFamily.Source == "Microsoft YaHei UI" &&
                       buildSuccessWindow.FontFamily.Source == "Microsoft YaHei UI",
                    "Changing the application font must update existing windows and their content immediately.");
                Assert(maximizeButton.FontFamily.Source == "Segoe Fluent Icons",
                    "Changing the application font must not replace the title bar icon font.");

                VerifyServerModesKeepIndependentState();

                var settingsView = new SettingsView();
                var languageCombo = (System.Windows.Controls.ComboBox)settingsView.FindName("LanguageCombo");
                languageCombo.ApplyTemplate();
                Assert(languageCombo.Template.FindName("PART_Popup", languageCombo) is System.Windows.Controls.Primitives.Popup,
                    "The custom ComboBox popup template was not applied.");

                string resourceAssembly = Uri.EscapeDataString(
                    typeof(McModpackTool.App.App).Assembly.GetName().Name ?? "McModpackTool.App");
                Uri avatarUri = new(
                    $"pack://application:,,,/{resourceAssembly};component/Assets/fengchenwd_avatar.png",
                    UriKind.Absolute);
                System.Windows.Resources.StreamResourceInfo? avatar = Application.GetResourceStream(avatarUri);
                Assert(avatar is not null, "The creator avatar was not embedded as an application resource.");
                avatar?.Stream.Dispose();
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new InvalidOperationException("Theme application failed for compiled App.xaml resources.", failure);
    }

    private static void VerifyServerModesKeepIndependentState()
    {
        var view = new ServerView();
        try
        {
            var directoryMode = (System.Windows.Controls.RadioButton)view.FindName("DirectoryModeButton");
            var archiveMode = (System.Windows.Controls.RadioButton)view.FindName("ArchiveModeButton");
            var input = (System.Windows.Controls.TextBox)view.FindName("InputPathBox");
            var version = (System.Windows.Controls.TextBox)view.FindName("MinecraftVersionBox");
            var outputDirectory = (System.Windows.Controls.TextBox)view.FindName("OutputDirectoryBox");
            var output = (System.Windows.Controls.TextBox)view.FindName("OutputNameBox");
            var log = (System.Windows.Controls.TextBox)view.FindName("LogBox");
            var mods = (System.Windows.Controls.DataGrid)view.FindName("ModsGrid");
            var config = (System.Windows.Controls.CheckBox)view.FindName("ConfigCheckBox");

            input.Text = "D:\\directory-instance";
            version.Text = "1.21.1";
            outputDirectory.Text = "D:\\directory-output";
            output.Text = "directory-server";
            log.Text = "directory log";
            config.IsChecked = true;
            object directoryRows = mods.ItemsSource;

            archiveMode.IsChecked = true;
            object archiveRows = mods.ItemsSource;
            Assert(!ReferenceEquals(directoryRows, archiveRows),
                "Folder and modpack modes must not share the same mod collection.");
            input.Text = "D:\\archive-pack.mrpack";
            version.Text = "1.21.1";
            outputDirectory.Text = "D:\\archive-output";
            output.Text = "archive-server";
            log.Text = "archive log";
            config.IsChecked = false;

            directoryMode.IsChecked = true;
            Assert(input.Text == "D:\\directory-instance" && version.Text == "1.21.1" && version.IsReadOnly &&
                   outputDirectory.Text == "D:\\directory-output" && output.Text == "directory-server" && log.Text == "directory log" &&
                   config.IsChecked == true && ReferenceEquals(mods.ItemsSource, directoryRows),
                "Switching back to folder mode did not restore its independent state.");

            archiveMode.IsChecked = true;
            Assert(input.Text == "D:\\archive-pack.mrpack" && version.Text == "1.21.1" && version.IsReadOnly &&
                   outputDirectory.Text == "D:\\archive-output" && output.Text == "archive-server" && log.Text == "archive log" &&
                   config.IsChecked == false && ReferenceEquals(mods.ItemsSource, archiveRows),
                "Switching back to modpack mode did not restore its independent state.");

            MethodInfo applySource = typeof(ServerView).GetMethod(
                "ApplySource", BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("Server source application method was not found.");
            applySource.Invoke(view,
            [
                new ServerPackSource
                {
                    InputKind = ServerInputKinds.Modrinth,
                    DisplayName = "Example Pack",
                    MinecraftVersion = "1.21.1",
                    LoaderType = "fabric",
                    LoaderVersion = "0.16.10",
                },
                "D:\\archive-pack.mrpack",
            ]);
            Assert(outputDirectory.Text == "D:\\archive-output",
                "Reading content must not replace a user-selected output directory.");
        }
        finally
        {
            view.ShutdownAsync().GetAwaiter().GetResult();
        }
    }

    private static void VerifyNativeMinimumTrackSize(Window window)
    {
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        int structureSize = Marshal.SizeOf<NativeMinMaxInfo>();
        nint buffer = Marshal.AllocHGlobal(structureSize);
        try
        {
            Marshal.StructureToPtr(new NativeMinMaxInfo(), buffer, false);
            SendMessage(handle, WmGetMinMaxInfo, 0, buffer);
            NativeMinMaxInfo info = Marshal.PtrToStructure<NativeMinMaxInfo>(buffer);
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            Assert(info.MinTrackSize.X >= Math.Ceiling(window.MinWidth * dpi.DpiScaleX) &&
                   info.MinTrackSize.Y >= Math.Ceiling(window.MinHeight * dpi.DpiScaleY),
                "The native resize boundary must enforce the WPF minimum window size at the current DPI.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int WmGetMinMaxInfo = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    private static void VerifyClientPackWorkflow(ClientPackView view)
    {
        var input = (System.Windows.Controls.TextBox)view.FindName("InputPathBox");
        var minecraft = (System.Windows.Controls.TextBox)view.FindName("MinecraftVersionBox");
        var loader = (System.Windows.Controls.TextBox)view.FindName("LoaderBox");
        var loaderVersion = (System.Windows.Controls.TextBox)view.FindName("LoaderVersionBox");
        var outputDirectory = (System.Windows.Controls.TextBox)view.FindName("OutputDirectoryBox");
        var outputName = (System.Windows.Controls.TextBox)view.FindName("OutputNameBox");
        var groups = (System.Windows.Controls.ItemsControl)view.FindName("ContentGroupsControl");
        var build = (System.Windows.Controls.Button)view.FindName("BuildButton");
        outputDirectory.Text = "D:\\chosen-output";

        MethodInfo applySource = typeof(ClientPackView).GetMethod(
            "ApplySource", BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Client source application method was not found.");
        applySource.Invoke(view,
        [
            new ClientPackSource
            {
                SourcePath = "D:\\client-instance",
                ContentRoot = "D:\\client-instance",
                DisplayName = "1.21.1 Example",
                MinecraftVersion = "1.21.1",
                LoaderType = "fabric",
                LoaderVersion = "0.16.14",
                Items =
                [
                    new ClientContentEntry
                    {
                        Name = "fabric-api.jar",
                        RelativePath = "mods/fabric-api.jar",
                        SourcePath = "D:\\client-instance\\mods\\fabric-api.jar",
                        Kind = ClientContentKinds.Mod,
                        TotalBytes = 1024,
                        Selected = true,
                    },
                    new ClientContentEntry
                    {
                        Name = "World",
                        RelativePath = "saves/World",
                        SourcePath = "D:\\client-instance\\saves\\World",
                        Kind = ClientContentKinds.World,
                        IsDirectory = true,
                        TotalBytes = 2048,
                        Selected = false,
                    },
                    new ClientContentEntry
                    {
                        Name = "XaeroWaypoints",
                        RelativePath = "XaeroWaypoints",
                        SourcePath = "D:\\client-instance\\XaeroWaypoints",
                        Kind = ClientContentKinds.ModData,
                        IsDirectory = true,
                        TotalBytes = 512,
                        Selected = false,
                    },
                    new ClientContentEntry
                    {
                        Name = "UnknownModData",
                        RelativePath = "UnknownModData",
                        SourcePath = "D:\\client-instance\\UnknownModData",
                        Kind = ClientContentKinds.Other,
                        IsDirectory = true,
                        TotalBytes = 256,
                        Selected = false,
                    },
                ],
            },
            "D:\\client-instance",
        ]);

        Assert(input.Text == "D:\\client-instance" && minecraft.Text == "1.21.1" &&
               loader.Text == "fabric" && loaderVersion.Text == "0.16.14" &&
               minecraft.IsReadOnly && loader.IsReadOnly && loaderVersion.IsReadOnly,
            "Client environment values must be populated from the source and remain read-only.");
        Assert(outputDirectory.Text == "D:\\chosen-output",
            "Reading a client directory must not replace a user-selected output folder.");
        Assert(outputName.Text == "1.21.1 Example" && groups.Items.Count == 3 && build.IsEnabled,
            "Client content grouping or automatic output naming was not applied.");

        object modGroup = groups.Items.Cast<object>().Single(group =>
            Equals(group.GetType().GetProperty("Kind")?.GetValue(group), ClientContentKinds.Mod));
        object worldGroup = groups.Items.Cast<object>().Single(group =>
            Equals(group.GetType().GetProperty("Kind")?.GetValue(group), ClientContentKinds.World));
        object modDataGroup = groups.Items.Cast<object>().Single(group =>
            Equals(group.GetType().GetProperty("Kind")?.GetValue(group), ClientContentKinds.ModData));
        object mergedRows = modDataGroup.GetType().GetProperty("Items")?.GetValue(modDataGroup) ??
            throw new InvalidOperationException("Merged mod data rows were not found.");
        Assert(((System.Collections.IEnumerable)mergedRows).Cast<object>().Count() == 2 &&
               groups.Items.Cast<object>().All(group =>
                   !Equals(group.GetType().GetProperty("Kind")?.GetValue(group), ClientContentKinds.Other)),
            "Other mod data must be merged into the minimap, world map, and mod data group.");
        PropertyInfo expandedProperty = modGroup.GetType().GetProperty("IsExpanded") ??
            throw new InvalidOperationException("Client content group expansion state was not found.");
        Assert((bool)expandedProperty.GetValue(modGroup)! && !(bool)expandedProperty.GetValue(worldGroup)!,
            "Selected client groups must start expanded while fully unselected groups start collapsed.");
        expandedProperty.SetValue(modGroup, false);
        Assert(sourceItemSelected(groups, ClientContentKinds.Mod) && !(bool)expandedProperty.GetValue(modGroup)!,
            "Collapsing a client content group must not clear its selected items.");

        input.Text = "D:\\another-instance";
        Assert(!build.IsEnabled && groups.Items.Count == 0,
            "Changing the client input path must invalidate previously read content.");

        static bool sourceItemSelected(System.Windows.Controls.ItemsControl groupControl, string kind)
        {
            object group = groupControl.Items.Cast<object>().Single(item =>
                Equals(item.GetType().GetProperty("Kind")?.GetValue(item), kind));
            object rows = group.GetType().GetProperty("Items")?.GetValue(group) ??
                throw new InvalidOperationException("Client content group rows were not found.");
            return ((System.Collections.IEnumerable)rows).Cast<object>().Any(row =>
                Equals(row.GetType().GetProperty("Selected")?.GetValue(row), true));
        }
    }

    private static async Task LegacySettingsRoundTripPreservesAgreementAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "McModpackToolSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string? oldOverride = Environment.GetEnvironmentVariable("MC_PACK_MIGRATOR_CONFIG_PATH");
        try
        {
            await File.WriteAllTextAsync(path, """
            {
              "target_mc": "1.20.1",
              "target_loader_type": "forge",
              "target_loader_version": "47.3.0",
              "output_dir": "C:\\packs",
              "ui_preferences": {
                "language": "zh_HK",
                "theme": "dark",
                "accent_color": "#2563EB",
                "font_family": "Segoe UI"
              },
              "accepted_agreement_version": "2026-08-05-v3",
              "unknown_future_value": 42
            }
            """);
            Environment.SetEnvironmentVariable("MC_PACK_MIGRATOR_CONFIG_PATH", path);
            var service = new SettingsService();
            AppSettings loaded = await service.LoadAsync();
            Assert(loaded.TargetMinecraft == "1.20.1" && loaded.Theme == "dark" && loaded.Language == "zh_HK", "Legacy settings were not loaded.");

            loaded.AcceptedAgreementVersion = string.Empty;
            loaded.Theme = "system";
            await service.SaveAsync(loaded);
            string saved = await File.ReadAllTextAsync(path);
            Assert(saved.Contains("\"accepted_agreement_version\": \"2026-08-05-v3\"", StringComparison.Ordinal), "A concurrent/current agreement marker must be preserved.");
            Assert(saved.Contains("\"unknown_future_value\": 42", StringComparison.Ordinal), "Unknown settings must be preserved.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MC_PACK_MIGRATOR_CONFIG_PATH", oldOverride);
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task ConcurrentSettingsSavesAreSerializedAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "McModpackToolConcurrentSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string? oldOverride = Environment.GetEnvironmentVariable("MC_PACK_MIGRATOR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("MC_PACK_MIGRATOR_CONFIG_PATH", path);
            var service = new SettingsService();
            Task[] saves = Enumerable.Range(0, 24)
                .Select(index => service.SaveAsync(new AppSettings
                {
                    TargetMinecraft = $"1.21.{index}",
                    TargetLoaderType = "fabric",
                    TargetLoaderVersion = "0.16.14",
                    Language = index % 2 == 0 ? "zh_CN" : "en_US",
                    Theme = index % 3 == 0 ? "system" : "dark",
                    AccentColor = "#2563EB",
                    FontFamily = "Microsoft YaHei UI",
                    AcceptedAgreementVersion = AgreementContent.Version
                }))
                .ToArray();
            await Task.WhenAll(saves);

            string saved = await File.ReadAllTextAsync(path);
            using var document = System.Text.Json.JsonDocument.Parse(saved);
            string target = document.RootElement.GetProperty("target_mc").GetString() ?? string.Empty;
            Assert(target.StartsWith("1.21.", StringComparison.Ordinal), "Concurrent settings saves produced invalid JSON or lost the target value.");
            Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "A concurrent settings save left a temporary file behind.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MC_PACK_MIGRATOR_CONFIG_PATH", oldOverride);
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
