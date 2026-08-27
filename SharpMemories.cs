using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;      // 👈 新增
using System.Linq;              // 👈 新增
using System.Reflection;        // 👈 新增
using System;
using System.Windows.Controls;

namespace SharpMemories
{
    public class SharpMemories : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();


        private static readonly Guid ScreenshotsVisualizerId = Guid.Parse("c6c8276f-91bf-48e5-a1d1-4bee0b493488");// 👇 新增：ScreenshotsVisualizer GUID（与 GameSnap 保持一致）

        private SharpMemoriesSettingsViewModel settings { get; set; }

        // Manager classes
        private ScreenshotCaptureManager screenshotCapture;
        private FolderMonitorManager folderMonitor;
        private KeyboardHookManager keyboardHook;

        public override Guid Id { get; } = Guid.Parse("f6e5e286-47b0-4fa9-bc5d-2c17587d215d");

        public SharpMemories(IPlayniteAPI api) : base(api)
        {
            logger.Info("SharpMemories plugin initialized");
            settings = new SharpMemoriesSettingsViewModel(this);
            screenshotCapture = new ScreenshotCaptureManager(settings, this);
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

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            try
            {
                var gameName = args?.Game?.Name ?? "Unknown";
                logger.Info($"OnGameStarted event received for game: {gameName}");

                if (settings?.Settings == null || !settings.Settings.Enabled)
                {
                    logger.Info("Plugin is disabled or settings are null, skipping screenshot capture");
                    return;
                }

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
                    screenshotCapture.StartCaptureForProcess(pid, title);
                }
                else
                {
                    logger.Warn("Output folder is not configured, skipping screenshot capture");
                }

                // Start monitoring the monitor folder if enabled
                if (settings.Settings.EnableMonitoring && !string.IsNullOrWhiteSpace(settings.Settings.MonitorFolder))
                {
                    folderMonitor.StartMonitoring(gameName);
                }

                // Register keyboard hotkey if enabled for this game's library
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
                        () => screenshotCapture.CaptureOnDemand(pid, title)
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