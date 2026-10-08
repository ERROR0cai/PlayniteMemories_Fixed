using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpMemories
{
    public class ScreenshotCaptureManager
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly SharpMemoriesSettingsViewModel settings;
        private readonly SharpMemories plugin;
        private readonly MessagesHandler messagesHandler;  // 新增：通知处理器
        private CancellationTokenSource captureCts;
        private Task captureTask;
        private int currentGameProcessId = 0;
        private string currentGameTitle = null;
        private readonly object captureLock = new object();

        // ========== 进程黑名单识别 ==========

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr hWnd,
            out uint processId);

        /// <summary>
        /// 根据 PID 获取进程可执行文件名。
        /// 例如 playnite.desktopapp.exe。
        /// </summary>
        private static string GetProcessFileName(int processId)
        {
            if (processId <= 0)
                return null;

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    // ProcessName 不带 .exe 后缀。
                    return SharpMemoriesSettings.NormalizeProcessName(
                        process.ProcessName);
                }
            }
            catch (Exception ex)
            {
                logger.Debug(
                    $"Unable to resolve process {processId}: {ex.Message}");

                return null;
            }
        }

        /// <summary>
        /// 获取当前前台窗口所属进程的可执行文件名。
        /// </summary>
        private static string GetForegroundProcessFileName()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();

                if (hWnd == IntPtr.Zero)
                    return null;

                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);

                if (processId == 0 || processId > int.MaxValue)
                    return null;

                return GetProcessFileName((int)processId);
            }
            catch (Exception ex)
            {
                logger.Debug(
                    $"Unable to resolve foreground process: {ex.Message}");

                return null;
            }
        }

        /// <summary>
        /// 自动截图黑名单：检查 Playnite 追踪的游戏 PID。
        /// </summary>
        private bool IsAutoScreenshotBlocked(int processId)
        {
            string processName = GetProcessFileName(processId);

            if (string.IsNullOrEmpty(processName))
            {
                logger.Warn(
                    $"Auto capture skipped: unable to identify PID {processId}");

                return true;
            }

            var currentSettings = settings?.Settings;

            if (currentSettings == null)
            {
                logger.Warn(
                    "Auto screenshot skipped: settings unavailable");

                return true;
            }

            bool blocked =
                currentSettings.IsAutoScreenshotProcessBlocked(processName);

            if (blocked)
            {
                string modeName =
                    currentSettings.AutoScreenshotProcessMode == 1
                        ? "Whitelist"
                        : "Blacklist";

                logger.Info(
                    $"Auto screenshot blocked by {modeName} mode: {processName}");
            }

            return blocked;
        }

        /// <summary>
        /// 手动截图黑名单：检查当前前台窗口所属进程。
        /// </summary>
        private bool IsManualScreenshotBlocked(string processName)
        {

            if (string.IsNullOrEmpty(processName))
            {
                logger.Warn(
                    "Manual capture skipped: foreground process unknown");

                return true;
            }

            var currentSettings = settings?.Settings;

            if (currentSettings == null)
            {
                logger.Warn(
                    "Manual screenshot skipped: settings unavailable");

                return true;
            }

            bool blocked =
                currentSettings.IsManualScreenshotProcessBlocked(processName);

            if (blocked)
            {
                string modeName =
                    currentSettings.ManualScreenshotProcessMode == 1
                        ? "Whitelist"
                        : "Blacklist";

                logger.Info(
                    $"Manual screenshot blocked by {modeName} mode: {processName}");
            }

            return blocked;
        }

        // 构造函数（修改）
        public ScreenshotCaptureManager(SharpMemoriesSettingsViewModel settings, SharpMemories plugin, MessagesHandler messagesHandler)
        {
            this.settings = settings;
            this.plugin = plugin;
            this.messagesHandler = messagesHandler;  // 新增
        }

        public void StartCaptureForProcess(int processId, string gameTitle)
        {
            lock (captureLock)
            {
                // stop any existing capture
                if (captureCts != null)
                {
                    logger.Debug("Stopping existing capture before starting new one");
                }
                StopCapture();

                logger.Debug($"Initializing capture for process {processId}, game: {gameTitle}");
                currentGameProcessId = processId;
                currentGameTitle = gameTitle;
                captureCts = new CancellationTokenSource();
                captureTask = Task.Run(() => CaptureLoop(processId, gameTitle, captureCts.Token));
            }
        }

        public void StopCapture()
        {
            lock (captureLock)
            {
                try
                {
                    if (captureCts != null)
                    {
                        logger.Info($"Stopping capture loop for game: {currentGameTitle ?? "Unknown"}");
                        captureCts.Cancel();
                        try { captureTask?.Wait(2000); } catch { }
                        captureCts.Dispose();
                        captureCts = null;
                        logger.Debug("Capture task cancelled and disposed");
                    }
                    else
                    {
                        logger.Debug("No active capture to stop");
                    }
                }
                finally
                {
                    captureTask = null;
                    currentGameProcessId = 0;
                    currentGameTitle = null;
                }
            }
        }

        public void CaptureOnDemand(int processId, string gameTitle)
        {
            logger.Info(
                $"On-demand screenshot capture triggered for '{gameTitle}'");
            messagesHandler?.TraceNotification(
                "ManualCapture.Triggered",
                false);

            // 在按键时固定前台进程名，截图和通知使用同一份结果。
            string foregroundProcessName = GetForegroundProcessFileName();
            if (IsManualScreenshotBlocked(foregroundProcessName))
            {
                messagesHandler?.TraceNotification(
                    "ManualCapture.BlockedByBlacklist",
                    false);

                logger.Debug(
                    $"Manual screenshot skipped by process mode: {gameTitle}");

                return;
            }

            string savedPath =
                CaptureOnce(processId, gameTitle, false);

            messagesHandler?.TraceNotification(
                string.IsNullOrEmpty(savedPath)
                    ? "ManualCapture.NoSavedFile"
                    : "ManualCapture.FileSaved",
                false);

            // 截图失败时不显示成功反馈。
            if (string.IsNullOrEmpty(savedPath))
                return;

            if (messagesHandler != null)
            {
                try
                {
                    messagesHandler.TraceNotification(
                        "ManualCapture.BeforeNotificationCall",
                        false);

                    messagesHandler.ShowScreenshotNotification(
                        gameTitle,
                        savedPath,
                        false,
                        foregroundProcessName);
                }
                catch (Exception ex)
                {
                    logger.Error(
                        ex,
                        "Failed to show notification for manual screenshot");
                }
            }

            try
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Error playing sound on screenshot capture");
            }
        }

        private async Task CaptureLoop(int processId, string gameTitle, CancellationToken token)
        {
            try
            {
                var intervalMinutes = settings?.Settings?.IntervalMinutes ?? 30;
                TimeSpan interval;

                // 👇 测试模式：当间隔为 0 时，每 10 秒截图一次
                if (intervalMinutes <= 0)
                {
                    interval = TimeSpan.FromSeconds(10);
                    logger.Info($"🔬 TEST MODE: Capture loop started for '{gameTitle}' with interval: 10 seconds");
                }
                else
                {
                    interval = TimeSpan.FromMinutes(intervalMinutes);
                    logger.Info($"Capture loop started for '{gameTitle}' with interval: {intervalMinutes} minutes");
                }

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (intervalMinutes <= 0)
                        {
                            logger.Debug($"🔬 Test mode: Waiting 10 seconds before next capture");
                        }
                        else
                        {
                            logger.Debug($"Waiting {intervalMinutes} minutes before next capture");
                        }
                        await Task.Delay(interval, token);
                    }
                    catch (TaskCanceledException)
                    {
                        logger.Debug("Capture loop cancelled during delay");
                        break;
                    }

                    if (token.IsCancellationRequested) break;

                    await Task.Run(() =>
                    {
                        if (!CanPerformAutoScreenshot(processId, gameTitle))
                        {
                            messagesHandler?.TraceNotification(
                                "AutoCapture.ConditionsNotMet",
                                true);

                            logger.Debug(
                                $"Screenshot skipped for '{gameTitle}' - conditions not met");

                            return;
                        }
                        string savedPath = CaptureOnce(processId, gameTitle, true);

                        messagesHandler?.TraceNotification(
                            string.IsNullOrEmpty(savedPath)
                                ? "AutoCapture.NoSavedFile"
                                : "AutoCapture.FileSaved",
                            true);

                        if (!string.IsNullOrEmpty(savedPath) && messagesHandler != null)
                        {
                            try
                            {
                                messagesHandler.TraceNotification(
                                    "AutoCapture.BeforeNotificationCall",
                                    true);
                                messagesHandler.ShowScreenshotNotification(
                                    gameTitle, savedPath, true, GetProcessFileName(processId));
                            }
                            catch (Exception ex)
                            {
                                logger.Error(ex, "Failed to show notification for auto screenshot");
                            }
                        }
                    });
                }

                logger.Info($"Capture loop ended for '{gameTitle}'");
            }
            catch (Exception e)
            {
                logger.Error(e, "Error in CaptureLoop");
            }
        }

        // 👇 检查是否允许自动截图
        private bool CanPerformAutoScreenshot(int processId, string gameTitle)
        {
            logger.Debug($"🔍 CanPerformAutoScreenshot() for '{gameTitle}' (PID: {processId})");

            // 优先检查自动截图黑名单。
            // 匹配 Playnite 正在追踪的游戏进程。
            if (IsAutoScreenshotBlocked(processId))
            {
                logger.Debug(
                    $"Auto screenshot skipped by process mode: {gameTitle}");

                return false;
            }

            // 1. 检查系统是否锁屏或息屏
            bool canTake = ScreenCapture.CanTakeScreenshot();
            logger.Debug($"   ├─ ScreenCapture.CanTakeScreenshot(): {canTake}");

            if (!canTake)
            {
                logger.Debug($"   └─ ⛔ System is locked or sleeping, skipping screenshot for '{gameTitle}'");
                return false;
            }

            // 2. 检查后台截图是否允许
            bool allowBackground = settings?.Settings?.AllowBackgroundScreenshot ?? false;
            logger.Debug($"   ├─ AllowBackgroundScreenshot: {allowBackground}");

            if (!allowBackground)
            {
                // 如果不允许后台截图，检查游戏窗口是否在前台
                try
                {
                    if (processId > 0)
                    {
                        var proc = System.Diagnostics.Process.GetProcessById(processId);
                        if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
                        {
                            bool isForeground = ScreenCapture.IsWindowForeground(proc.MainWindowHandle);
                            logger.Debug($"   ├─ Game window foreground: {isForeground}");

                            if (!isForeground)
                            {
                                logger.Debug($"   └─ ⛔ Game '{gameTitle}' is not in foreground and background screenshot is disabled");
                                return false;
                            }
                        }
                        else
                        {
                            logger.Debug($"   └─ ⛔ Process {processId} not found or has no window handle");
                            return false;
                        }
                    }
                    else
                    {
                        logger.Debug($"   ├─ No process ID available, cannot check foreground status");
                        // 没有进程ID时，保守处理：如果是自动截图，返回 false
                        // 但如果是手动截图，应该允许（因为用户主动触发）
                        // 这里假设是自动截图调用，返回 false
                        logger.Debug($"   └─ ⛔ No process ID, cannot verify foreground - skipping");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug($"   └─ ⛔ Failed to check foreground status: {ex.Message}");
                    return false;
                }
            }
            else
            {
                logger.Debug($"   ├─ Background screenshot is allowed for '{gameTitle}'");
            }

            logger.Debug($"   └─ ✅ All conditions met, allowing screenshot");
            return true;
        }

        // 修改：返回保存的文件路径
        private string CaptureOnce(int processId, string gameTitle, bool isAutoCapture)
        {
            string savedPath = null;

            // 自动截图再次确认黑名单，防止进入截图流程前
            // 黑名单发生变化，或其他调用路径绕过条件检查。
            if (isAutoCapture && IsAutoScreenshotBlocked(processId))
            {
                return null;
            }

            try
            {
                logger.Debug($"Starting screenshot capture for '{gameTitle}' (PID: {processId})");
                Bitmap bmp = null;

                if (processId > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(processId);
                        if (proc != null)
                        {
                            var h = proc.MainWindowHandle;
                            if (h != IntPtr.Zero)
                            {
                                logger.Debug($"Attempting to capture window for process {processId}");
                                bmp = ScreenCapture.CaptureWindow(h);
                                if (bmp != null)
                                {
                                    logger.Debug("Window capture successful");
                                }
                                else
                                {
                                    logger.Debug("Window capture returned null, will fallback to screen capture");
                                }
                            }
                            else
                            {
                                logger.Debug("Process has no main window handle, will use screen capture");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Debug($"Failed to capture window: {ex.Message}");
                    }
                }
                else
                {
                    logger.Debug("No process ID available, using screen capture");
                }

                if (bmp == null)
                {
                    logger.Debug("Performing full screen capture");
                    bmp = ScreenCapture.CaptureScreen();
                }

                if (bmp == null)
                {
                    logger.Warn("Capture returned null bitmap");
                    return null;
                }

                var outFolder = settings?.Settings?.OutputFolder;
                if (string.IsNullOrWhiteSpace(outFolder))
                {
                    outFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite", "Plugins", "SharpMemories", "Screenshots");
                    logger.Debug($"Using default output folder: {outFolder}");
                }

                var safeTitle = FileHelpers.MakeSafeFilename(gameTitle ?? "game");

                outFolder = Path.Combine(outFolder, safeTitle);

                try { Directory.CreateDirectory(outFolder); } catch (Exception ex) { logger.Error(ex, "Failed to create output folder"); }

                var now = DateTime.Now;
                var pattern = settings?.Settings?.RenamePattern;

                if (string.IsNullOrWhiteSpace(pattern))
                {
                    pattern = "{game}_{datetime}";
                }

                var result = pattern
                    .Replace("{game}", safeTitle)
                    .Replace("{date}", now.ToString("yyyy-MM-dd"))
                    .Replace("{time}", now.ToString("HH_mm_ss"))
                    .Replace("{datetime}", now.ToString("yyyy-MM-dd_HH_mm_ss"))
                    .Replace("{original}", safeTitle);

                result = string.Concat(result.Split(Path.GetInvalidFileNameChars()));

                // 👇 新增：根据截图类型添加后缀
                string suffix = "";
                if (isAutoCapture)
                {
                    suffix = settings?.Settings?.AutoScreenshotSuffix ?? "";
                }
                else
                {
                    suffix = settings?.Settings?.ManualScreenshotSuffix ?? "";
                }

                // 如果后缀不为空，直接添加到 result 后面（不加额外下划线）
                if (!string.IsNullOrEmpty(suffix))
                {
                    // 直接追加用户输入的内容，不做任何修改
                    result = result + suffix;
                }

                var filename = Path.Combine(outFolder, result + ".png");

                bmp.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
                bmp.Dispose();
                savedPath = filename;
                logger.Info($"Saved screenshot: {filename}");

                // 刷新 ScreenshotsVisualizer
                if (plugin != null && !string.IsNullOrEmpty(gameTitle))
                {
                    plugin.NotifyScreenshotsVisualizerRefresh(gameTitle);
                }

                return savedPath;
            }
            catch (Exception e)
            {
                logger.Error(e, "Error taking screenshot");

                // 截图失败时发送错误通知
                if (messagesHandler != null)
                {
                    try
                    {
                        messagesHandler.ShowErrorNotification(gameTitle ?? "Unknown", e.Message);
                    }
                    catch { }
                }

                return null;
            }
        }
    }
}