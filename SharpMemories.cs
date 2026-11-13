using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;
using System.Linq;
using System.Reflection;
using System;
using System.Windows.Controls;
using System.Threading;
using System.IO;
using System.Collections.Generic;

namespace SharpMemories
{
    public class SharpMemories : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private static readonly Guid ScreenshotsVisualizerId = Guid.Parse("c6c8276f-91bf-48e5-a1d1-4bee0b493488");

        private SharpMemoriesSettingsViewModel settings { get; set; }

        // Manager classes
        private ScreenshotCaptureManager screenshotCapture;
        private FolderMonitorManager folderMonitor;
        private KeyboardHookManager keyboardHook;

        // 通知处理器
        private MessagesHandler messagesHandler;
        private readonly object sessionGate = new object();
        private Game activeGame;
        private GameAction activeSourceAction;
        private int activeProcessId;
        private string activeTitle;


        public override Guid Id { get; } = Guid.Parse("f6e5e286-47b0-4fa9-bc5d-2c17587d215d");

        // 公共属性供其他类访问
        public MessagesHandler MessagesHandler => messagesHandler;

        public SharpMemories(IPlayniteAPI api) : base(api)
        {
            logger.Info("SharpMemories plugin initialized");
            GameFingerprintStore.Initialize(GetPluginUserDataPath());
            settings = new SharpMemoriesSettingsViewModel(this);

            // 初始化 MessagesHandler
            messagesHandler = new MessagesHandler(
                PlayniteApi,
                settings.Settings,
                () => settings.Settings);

            // 传递 messagesHandler 给 ScreenshotCaptureManager
            screenshotCapture = new ScreenshotCaptureManager(settings, this, messagesHandler);
            folderMonitor = new FolderMonitorManager(settings);
            keyboardHook = new KeyboardHookManager();

            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
        }

        private bool ShouldEnableHotkeyForGame(Playnite.SDK.Models.Game game)
        {
            if (!settings?.Settings?.EnableHotkey ?? true)
            {
                return false;
            }

            var pluginId = game?.PluginId ?? Guid.Empty;

            // Use the helper method to check if hotkey is enabled for this library
            // Defaults to true if the library is not in the dictionary
            return settings.Settings.IsHotkeyEnabledForLibrary(pluginId);
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            // Add code to be executed when game is finished installing.
        }


        // PlayState-style detection hints: the source launch action is often more
        // accurate than Game.InstallDirectory (e.g. relocated Java games).
        private List<string> GetGameProcessDirectories(Game game, GameAction sourceAction)
        {
            var directories = new List<string>();
            Action<string> addDirectory = value =>
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                try
                {
                    string candidate = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                    if (!Path.IsPathRooted(candidate)) return;
                    candidate = Path.GetFullPath(candidate);
                    if (File.Exists(candidate) ||
                        (Path.HasExtension(candidate) && !Directory.Exists(candidate)))
                        candidate = Path.GetDirectoryName(candidate);
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        !directories.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase)))
                        directories.Add(candidate);
                }
                catch (Exception ex) { logger.Debug($"Game process path hint ignored: {ex.Message}"); }
            };

            // Prefer the Playnite action actually used for this session.
            Action<GameAction> addAction = action =>
            {
                if (action == null) return;
                try
                {
                    var expanded = PlayniteApi.ExpandGameVariables(game, action);
                    if (expanded == null) return;
                    addDirectory(expanded.TrackingPath);
                    if (!string.IsNullOrWhiteSpace(expanded.Path))
                    {
                        string path = expanded.Path;
                        if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(expanded.WorkingDir))
                            path = Path.Combine(expanded.WorkingDir, path);
                        addDirectory(path);
                    }
                    // Avoid accepting the entire parent library as a game directory.
                    // WorkingDir is only a fallback when neither tracking nor executable
                    // path yielded a directory for this action.
                    if (directories.Count == 0) addDirectory(expanded.WorkingDir);
                }
                catch (Exception ex) { logger.Debug($"Unable to expand game action for process discovery: {ex.Message}"); }
            };
            addAction(sourceAction);
            // Game has no PlayAction property in the supported Playnite SDK.
            // The actual session action is supplied through OnGameStarted.SourceAction.
            addDirectory(game?.InstallDirectory);
            return directories;
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            try
            {
                var gameName = args?.Game?.Name ?? "Unknown";
                logger.Info($"OnGameStarted event received for game: {gameName}");
                lock (sessionGate)
                {
                    activeGame = args?.Game;
                    activeSourceAction = args?.SourceAction;
                    activeProcessId = args?.StartedProcessId ?? 0;
                    activeTitle = gameName;
                }

                if (args?.Game != null) GameFingerprintStore.Current?.Ensure(args.Game.Id.ToString(), gameName);
                var detectionDirectories = GetGameProcessDirectories(args?.Game, args?.SourceAction);
                string preferredGameDirectory = detectionDirectories.FirstOrDefault() ?? args?.Game?.InstallDirectory;

                // ============================================================
                // 1. 自动截图功能 - 由 Enabled CheckBox 控制
                // ============================================================
                if (settings?.Settings != null && settings.Settings.Enabled)
                {
                    if (settings.Settings.OutputFolder != null)
                    {
                        var pid = 0;
                        try
                        {
                            pid = args?.StartedProcessId ?? 0;
                        }
                        catch { pid = 0; }

                        var title = args?.Game?.Name ?? "unknown";
                        logger.Info($"Starting capture loop for '{title}' (pid={pid})");
                        screenshotCapture.StartCaptureForProcess(pid, title, preferredGameDirectory);
                    }
                    else
                    {
                        logger.Warn("Output folder is not configured, skipping screenshot capture");
                    }
                }
                else
                {
                    logger.Info("Auto screenshot is disabled, skipping screenshot capture");
                }

                screenshotCapture.SetGameDirectories(detectionDirectories);
                screenshotCapture.SetFingerprintGameId(args?.Game?.Id.ToString());

                // ============================================================
                // 2. 文件夹监控 - 由 EnableMonitoring 独立控制
                // ============================================================
                if (settings.Settings.EnableMonitoring && !string.IsNullOrWhiteSpace(settings.Settings.MonitorFolder))
                {
                    folderMonitor.StartMonitoring(gameName);
                }

                // ============================================================
                // 3. 热键注册 - 由 EnableHotkey 独立控制（不受 Enabled 影响）
                // ============================================================
                if (ShouldEnableHotkeyForGame(args?.Game))
                {
                    var pid = 0;
                    try
                    {
                        pid = args?.StartedProcessId ?? 0;
                    }
                    catch { pid = 0; }

                    var title = args?.Game?.Name ?? "unknown";
                    logger.Info($"Registering hotkey for '{title}'");

                    keyboardHook.RegisterHotkey(
                        settings.Settings.HotkeyKey,
                        settings.Settings.HotkeyCtrl,
                        settings.Settings.HotkeyAlt,
                        settings.Settings.HotkeyShift,
                        settings.Settings.HotkeySuppressKey,
                        () => screenshotCapture.CaptureOnDemand(pid, title, preferredGameDirectory)
                    );
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Error in OnGameStarted");
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            // Add code to be executed when game is preparing to be started.
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            var gameName = args?.Game?.Name ?? "Unknown";
            logger.Info($"OnGameStopped event received for game: {gameName}");
            lock (sessionGate)
            {
                if (activeGame != null && args?.Game != null && activeGame.Id != args.Game.Id)
                    return; // unrelated game stop must not terminate the current session
                activeGame = null;
                activeSourceAction = null;
                activeTitle = null;
                activeProcessId = 0;
            }

            try
            {
                screenshotCapture.StopCapture();
            }
            catch (Exception e)
            {
                logger.Error(e, "Error stopping capture loop in OnGameStopped");
            }

            try
            {
                folderMonitor.StopMonitoring();
            }
            catch (Exception e)
            {
                logger.Error(e, "Error stopping folder monitor in OnGameStopped");
            }

            try
            {
                keyboardHook.UnregisterHotkey();
            }
            catch (Exception e)
            {
                logger.Error(e, "Error unregistering hotkey in OnGameStopped");
            }
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
            // Add code to be executed when game is uninstalled.
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            //logger.Info("Playnite application started");
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            logger.Info("Playnite application stopping, cleaning up resources");
            // Ensure capture loop is stopped when Playnite exits.
            try
            {
                screenshotCapture.StopCapture();
                folderMonitor?.StopMonitoring();
                keyboardHook?.Dispose();
                logger.Info("Cleanup completed successfully");
            }
            catch (Exception e)
            {
                logger.Error(e, "Error in OnApplicationStopped");
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            // Add code to be executed when library is updated.
        }

        // Applied only after the settings dialog is saved, never during editing.
        public void ApplyRuntimeSettings()
        {
            Game game;
            GameAction sourceAction;
            int pid;
            string title;
            lock (sessionGate)
            {
                game = activeGame;
                sourceAction = activeSourceAction;
                pid = activeProcessId;
                title = activeTitle;
            }
            if (game == null || string.IsNullOrEmpty(title)) return;

            try
            {
                var current = settings.Settings;
                var detectionDirectories = GetGameProcessDirectories(game, sourceAction);
                string preferredGameDirectory = detectionDirectories.FirstOrDefault() ?? game.InstallDirectory;
                if (current.Enabled)
                {
                    if (screenshotCapture.IsRunningFor(pid, title))
                        screenshotCapture.UpdateInterval();
                    else
                        screenshotCapture.StartCaptureForProcess(pid, title, preferredGameDirectory);
                }
                else screenshotCapture.StopCapture();
                screenshotCapture.SetGameDirectories(detectionDirectories);
                screenshotCapture.SetFingerprintGameId(game.Id.ToString());

                if (current.EnableMonitoring && !string.IsNullOrWhiteSpace(current.MonitorFolder))
                    folderMonitor.EnsureMonitoring(title);
                else folderMonitor.StopMonitoring();

                keyboardHook.UnregisterHotkey();
                if (ShouldEnableHotkeyForGame(game))
                    keyboardHook.RegisterHotkey(current.HotkeyKey, current.HotkeyCtrl,
                        current.HotkeyAlt, current.HotkeyShift, current.HotkeySuppressKey,
                        () => screenshotCapture.CaptureOnDemand(pid, title, preferredGameDirectory));
                logger.Info("Runtime settings applied to active game");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to apply runtime settings");
            }
        }

        // Invoked only after explicit confirmation in the advanced settings UI.
        // Works even when no game is currently running.
        public string GenerateDiagnosticErrorPlaceholder()
        {
            string gameTitle;
            lock (sessionGate)
                gameTitle = activeTitle;
            if (string.IsNullOrWhiteSpace(gameTitle))
                gameTitle = "SharpMemories-Diagnostics";
            return screenshotCapture.CreateDiagnosticErrorPlaceholder(gameTitle);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SharpMemoriesSettingsView();
        }

        /// <summary>
        /// 通知 ScreenshotsVisualizer 刷新指定游戏的截图
        /// 直接调用 SV 的 RefreshGameByName 方法
        /// </summary>
        public void NotifyScreenshotsVisualizerRefresh(string gameName)
        {
            try
            {
                // 检查是否启用 SV 刷新
                if (settings?.Settings == null || !settings.Settings.EnableSVRefresh)
                    return;

                var sv = PlayniteApi.Addons.Plugins
                    .FirstOrDefault(p => p.Id == ScreenshotsVisualizerId);
                if (sv == null)
                {
                    logger.Debug("ScreenshotsVisualizer plugin not found, skipping refresh");
                    return;
                }

                // 直接调用 SV 的 RefreshGameByName 方法
                var method = sv.GetType().GetMethod("RefreshGameByName",
                    BindingFlags.Public | BindingFlags.Instance);
                if (method != null)
                {
                    method.Invoke(sv, new object[] { gameName });
                    logger.Info($"ScreenshotsVisualizer refresh called for: {gameName}");
                }
                else
                {
                    logger.Debug("RefreshGameByName method not found in SV");
                }
            }
            catch (Exception ex)
            {
                logger.Debug($"ScreenshotsVisualizer refresh skipped: {ex.Message}");
            }
        }
    }
}