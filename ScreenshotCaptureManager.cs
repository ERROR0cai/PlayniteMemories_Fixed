using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
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
        private string currentGameInstallDirectory = null;
        private readonly object captureLock = new object();
        private readonly object screenshotSaveLock = new object();
        // Countdown is measured using monotonic Stopwatch ticks, not wall-clock time.
        private TimeSpan remainingCaptureTime;
        private TimeSpan configuredInterval;
        private long captureGeneration;
        private bool captureRunning;
        // Validated game window. Recheck the handle/PID on every reuse and
        // revalidate the executable directory periodically (PID reuse protection).
        private readonly object windowCacheGate = new object();
        private IntPtr cachedGameWindow = IntPtr.Zero;
        private int cachedGameWindowPid;
        private int cachedLaunchPid;
        private string cachedGameDirectory;
        private long cachedVerifiedAt;
        private static readonly long WindowReverifyTicks = Stopwatch.Frequency * 30L;


        private TimeSpan GetInterval()
        {
            int minutes = settings?.Settings?.IntervalMinutes ?? 30;
            return minutes <= 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(minutes);
        }

        public bool IsRunningFor(int processId, string gameTitle)
        {
            lock (captureLock)
                return captureRunning && currentGameProcessId == processId &&
                    string.Equals(currentGameTitle, gameTitle, StringComparison.Ordinal);
        }

        // Only change the countdown if the interval itself changed. An unrelated
        // settings save must not silently discard the elapsed active time.
        public void UpdateInterval()
        {
            lock (captureLock)
            {
                if (!captureRunning) return;
                TimeSpan updated = GetInterval();
                if (updated == configuredInterval) return;
                configuredInterval = updated;
                remainingCaptureTime = updated;
                captureGeneration++;
                logger.Info($"Capture interval changed: remaining={remainingCaptureTime}");
            }
        }

        // Game window resolution. A Playnite launch PID is not always the process
        // owning the real game window (launchers and intermediary processes exist).
        // Never equate an arbitrary foreground window with the game.
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        private const uint GW_OWNER = 4;

        // Unlike Process.MainModule.FileName, this works when 32-bit Playnite
        // needs the executable path of a 64-bit process.
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
            StringBuilder buffer, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private static string GetProcessImagePath(int pid)
        {
            if (pid <= 0) return null;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(32768);
                uint size = (uint)buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size)
                    ? buffer.ToString(0, (int)size) : null;
            }
            finally { CloseHandle(handle); }
        }

        private static string NormalizeDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try
            {
                var normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')));
                return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            }
            catch { return null; }
        }

        private static bool IsExeInsideDirectory(string exePath, string directory)
        {
            var root = NormalizeDirectory(directory);
            if (root == null || string.IsNullOrWhiteSpace(exePath)) return false;
            try
            {
                var full = Path.GetFullPath(exePath);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string ReadWindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            if (hwnd != IntPtr.Zero) GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static int GetWindowProcessId(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid > int.MaxValue ? 0 : (int)pid;
        }

        // A window is verified only when its PID equals the launch PID or its
        // executable resides inside the explicit game directory. Do not match
        // translated game titles or unrelated foreground processes by title.
        private static bool IsVerifiedGameProcess(int windowPid, int launchPid, string gameDir)
        {
            if (windowPid <= 0) return false;
            if (windowPid == launchPid) return true;
            return IsExeInsideDirectory(GetProcessImagePath(windowPid), gameDir);
        }

        private void ClearWindowCache()
        {
            lock (windowCacheGate)
            {
                cachedGameWindow = IntPtr.Zero;
                cachedGameWindowPid = 0;
                cachedLaunchPid = 0;
                cachedGameDirectory = null;
                cachedVerifiedAt = 0;
            }
        }

        private void CacheGameWindow(IntPtr hwnd, int pid, int launchPid, string gameDir)
        {
            lock (windowCacheGate)
            {
                cachedGameWindow = hwnd;
                cachedGameWindowPid = pid;
                cachedLaunchPid = launchPid;
                cachedGameDirectory = NormalizeDirectory(gameDir);
                cachedVerifiedAt = Stopwatch.GetTimestamp();
            }
        }

        private IntPtr GetCachedGameWindow(int launchPid, string gameDir)
        {
            lock (windowCacheGate)
            {
                IntPtr hwnd = cachedGameWindow;
                if (hwnd == IntPtr.Zero) return IntPtr.Zero;
                string normalizedDir = NormalizeDirectory(gameDir);
                int pid = GetWindowProcessId(hwnd);
                if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) ||
                    GetWindow(hwnd, GW_OWNER) != IntPtr.Zero ||
                    pid == 0 || pid != cachedGameWindowPid ||
                    cachedLaunchPid != launchPid ||
                    !string.Equals(cachedGameDirectory, normalizedDir, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(ReadWindowTitle(hwnd)))
                {
                    cachedGameWindow = IntPtr.Zero;
                    return IntPtr.Zero;
                }
                long now = Stopwatch.GetTimestamp();
                if (now - cachedVerifiedAt >= WindowReverifyTicks)
                {
                    if (!IsVerifiedGameProcess(pid, launchPid, gameDir))
                    {
                        cachedGameWindow = IntPtr.Zero;
                        return IntPtr.Zero;
                    }
                    cachedVerifiedAt = now;
                }
                return hwnd;
            }
        }

        private IntPtr ResolveGameWindow(int launchPid, string gameDir, bool preferForeground)
        {
            IntPtr foreground = GetForegroundWindow();
            IntPtr cached = GetCachedGameWindow(launchPid, gameDir);
            if (foreground != IntPtr.Zero && foreground == cached)
                return foreground;

            // Foreground-only mode must never return a cached background window.
            // Check only this foreground PID; don't enumerate unrelated windows.
            int foregroundPid = GetWindowProcessId(foreground);
            if (foreground != IntPtr.Zero && IsWindowVisible(foreground) &&
                !IsIconic(foreground) &&
                IsVerifiedGameProcess(foregroundPid, launchPid, gameDir))
            {
                CacheGameWindow(foreground, foregroundPid, launchPid, gameDir);
                logger.Debug($"WindowResolver: foreground verified, PID={foregroundPid}, " +
                    $"HWND={foreground}, title='{ReadWindowTitle(foreground)}'");
                return foreground;
            }
            if (preferForeground) return IntPtr.Zero;
            if (cached != IntPtr.Zero) return cached;

            // Full window enumeration only on cache miss or invalidation.
            IntPtr result = IntPtr.Zero;
            int selectedPid = 0;
            EnumWindows((hwnd, _) =>
            {
                if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) ||
                    GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                if (string.IsNullOrWhiteSpace(ReadWindowTitle(hwnd))) return true;
                int ownerPid = GetWindowProcessId(hwnd);
                if (!IsVerifiedGameProcess(ownerPid, launchPid, gameDir)) return true;
                result = hwnd;
                selectedPid = ownerPid;
                return false;
            }, IntPtr.Zero);
            if (result != IntPtr.Zero)
            {
                CacheGameWindow(result, selectedPid, launchPid, gameDir);
                logger.Debug($"WindowResolver: enumerated game window PID={selectedPid}, " +
                    $"HWND={result}, title='{ReadWindowTitle(result)}'");
            }
            else logger.Debug($"WindowResolver: no verified visible game window " +
                $"for launch PID={launchPid}, directory='{gameDir ?? "(unknown)"}'");
            return result;
        }

        private string GetCurrentGameDirectory(int processId, string title)
        {
            lock (captureLock)
            {
                return captureRunning && currentGameProcessId == processId &&
                    string.Equals(currentGameTitle, title, StringComparison.Ordinal)
                    ? currentGameInstallDirectory : null;
            }
        }

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

        public void StartCaptureForProcess(int processId, string gameTitle, string gameInstallDirectory = null)
        {
            StopCapture();
            ClearWindowCache();
            lock (captureLock)
            {
                currentGameProcessId = processId;
                currentGameTitle = gameTitle;
                currentGameInstallDirectory = gameInstallDirectory;
                captureRunning = true;
                ScreenCapture.StartSystemStateMonitor();
                configuredInterval = GetInterval();
                remainingCaptureTime = configuredInterval;
                captureGeneration++;
                var cts = new CancellationTokenSource();
                captureCts = cts;
                captureTask = Task.Run(() => CaptureLoop(processId, gameTitle, cts.Token));
                logger.Info($"Capture loop started for {gameTitle}, interval {GetInterval()}");
            }
        }

        public void StopCapture()
        {
            CancellationTokenSource oldCts;
            Task oldTask;
            lock (captureLock)
            {
                oldCts = captureCts;
                oldTask = captureTask;
                captureCts = null;
                captureTask = null;
                captureRunning = false;
                currentGameTitle = null;
                currentGameInstallDirectory = null;
                currentGameProcessId = 0;
                captureGeneration++;
                if (oldCts != null) oldCts.Cancel();
            }
            ClearWindowCache();
            ScreenCapture.StopSystemStateMonitor();
            // Never block the Playnite UI thread. Dispose after the old worker has exited.
            if (oldCts != null)
            {
                if (oldTask != null)
                    oldTask.ContinueWith(_ => oldCts.Dispose(), TaskScheduler.Default);
                else oldCts.Dispose();
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
            // Single monotonic clock, one sequential worker, and no catch-up queue.
            // A condition probe runs at most once per 2 seconds (or at deadline).
            // Paused time and time spent capturing are excluded from the interval.
            bool wasActive = false;
            long lastTick = Stopwatch.GetTimestamp();
            long seenGeneration = -1;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool active = false;
                    try { active = CanPerformAutoScreenshot(processId, gameTitle); }
                    catch (Exception ex) { logger.Warn(ex, "Eligibility probe failed; pausing countdown"); }
                    long now = Stopwatch.GetTimestamp();
                    bool due = false;
                    TimeSpan remaining;
                    lock (captureLock)
                    {
                        if (token.IsCancellationRequested || !captureRunning ||
                            currentGameProcessId != processId ||
                            !string.Equals(currentGameTitle, gameTitle, StringComparison.Ordinal))
                            break;
                        if (seenGeneration != captureGeneration)
                        {
                            seenGeneration = captureGeneration;
                            lastTick = now;
                            wasActive = false;
                        }
                        if (active && wasActive)
                        {
                            double seconds = (now - lastTick) / (double)Stopwatch.Frequency;
                            if (seconds > 0 && seconds <= 6.0)
                                remainingCaptureTime -= TimeSpan.FromSeconds(seconds);
                            else if (seconds > 6.0)
                                logger.Info($"Countdown gap ({seconds:F1}s); probable sleep or stall, time not charged");
                        }
                        lastTick = now;
                        if (active && remainingCaptureTime <= TimeSpan.Zero)
                        {
                            // At most one shot; reset at completion to exclude capture time.
                            due = true;
                            remainingCaptureTime = configuredInterval;
                        }
                        remaining = remainingCaptureTime;
                    }
                    if (active != wasActive)
                        logger.Info($"Capture countdown {(active ? "resumed" : "paused")} for '{gameTitle}', remaining={remaining}");
                    wasActive = active;
                    if (due && !token.IsCancellationRequested)
                    {
                        // Recheck immediately; window/lock state may have changed.
                        bool stillEligible = false;
                        try { stillEligible = CanPerformAutoScreenshot(processId, gameTitle); }
                        catch (Exception ex) { logger.Warn(ex, "Pre-capture eligibility check failed"); }
                        if (!stillEligible)
                        {
                            lock (captureLock)
                            {
                                if (captureRunning && currentGameProcessId == processId &&
                                    string.Equals(currentGameTitle, gameTitle, StringComparison.Ordinal))
                                    remainingCaptureTime = TimeSpan.Zero;
                            }
                            wasActive = false;
                        }
                        else
                        {
                            string savedPath = CaptureOnce(processId, gameTitle, true, token);
                            if (token.IsCancellationRequested) break;
                            if (!string.IsNullOrEmpty(savedPath))
                            {
                                try
                                {
                                    messagesHandler?.ShowScreenshotNotification(gameTitle,
                                        savedPath, true, GetProcessFileName(processId));
                                }
                                catch (Exception ex) { logger.Error(ex, "Auto screenshot notification failed"); }
                            }
                            lastTick = Stopwatch.GetTimestamp();
                            // Stay active; do not waste an entire polling period resetting
                            // the baseline after every capture.
                            wasActive = true;
                        }
                    }
                    if (token.IsCancellationRequested) break;
                    // Poll less frequently while paused; approach active deadlines
                    // precisely rather than rounding every shot to a 4-second tick.
                    double waitMs = active ? Math.Min(2000.0,
                        Math.Max(1.0, remaining.TotalMilliseconds)) : 2000.0;
                    await Task.Delay(TimeSpan.FromMilliseconds(waitMs), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.Error(ex, "Error in CaptureLoop"); }
            finally { logger.Info($"Capture loop ended for '{gameTitle}'"); }
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

            string gameDir = GetCurrentGameDirectory(processId, gameTitle);
            IntPtr gameWindow = ResolveGameWindow(processId, gameDir, !allowBackground);
            if (gameWindow == IntPtr.Zero)
            {
                // Normal pause (e.g. Alt+Tab), not a failure. CaptureLoop
                // records the paused/resumed state once per transition.
                return false;
            }
            logger.Debug($"   ├─ Verified game window HWND={gameWindow}, " +
                $"PID={GetWindowProcessId(gameWindow)}, title='{ReadWindowTitle(gameWindow)}'");

            logger.Debug($"   └─ ✅ All conditions met, allowing screenshot");
            return true;
        }

        // Revalidate right before and after rendering: a window may be destroyed
        // between a timer probe and the actual capture (notably during game exit).
        // A disappearing window is a cancelled attempt, NOT a renderer failure.
        private bool IsAutoCaptureWindowCurrent(IntPtr hwnd, int expectedPid,
            int launchPid, string gameTitle, CancellationToken token, bool allowBackground)
        {
            if (token.IsCancellationRequested || hwnd == IntPtr.Zero || expectedPid <= 0)
                return false;
            lock (captureLock)
            {
                if (!captureRunning || currentGameProcessId != launchPid ||
                    !string.Equals(currentGameTitle, gameTitle, StringComparison.Ordinal))
                    return false;
            }
            if (!IsWindow(hwnd) || GetWindowProcessId(hwnd) != expectedPid ||
                !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return false;
            if (!allowBackground && GetForegroundWindow() != hwnd)
                return false;
            try
            {
                using (var process = Process.GetProcessById(expectedPid))
                    return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // 修改：返回保存的文件路径
        private string CaptureOnce(int processId, string gameTitle, bool isAutoCapture, CancellationToken token = default(CancellationToken))
        {
            lock (screenshotSaveLock)
                return CaptureOnceCore(processId, gameTitle, isAutoCapture, token);
        }

        private string CaptureOnceCore(int processId, string gameTitle, bool isAutoCapture, CancellationToken token)
        {
            string savedPath = null;
            Bitmap bmp = null;
            bool attempted = false;
            IntPtr renderingWindow = IntPtr.Zero;
            int renderingWindowPid = 0;
            bool renderingBackgroundAllowed = false;
            bool rendererEntered = false;

            // 自动截图再次确认黑名单，防止进入截图流程前
            // 黑名单发生变化，或其他调用路径绕过条件检查。
            if (isAutoCapture && IsAutoScreenshotBlocked(processId))
            {
                return null;
            }

            try
            {
                if (token.IsCancellationRequested) return null;
                attempted = true;
                logger.Debug($"Starting screenshot capture for '{gameTitle}' (PID: {processId})");
                if (isAutoCapture)
                {
                    // Never silently capture the desktop for an automatic capture:
                    // it might contain the browser, private messages, etc.
                    string dir = GetCurrentGameDirectory(processId, gameTitle);
                    bool backgroundAllowed = settings?.Settings?.AllowBackgroundScreenshot ?? false;
                    IntPtr window = ResolveGameWindow(processId, dir, !backgroundAllowed);
                    if (window == IntPtr.Zero)
                    {
                        // The foreground/window condition changed after the timer
                        // probe. This is a pause, not a failed screenshot.
                        logger.Debug($"Auto capture cancelled: game window unavailable " +
                            $"before capture for '{gameTitle}'");
                        return null;
                    }
                    int ownerPid = GetWindowProcessId(window);
                    if (!IsAutoCaptureWindowCurrent(window, ownerPid, processId,
                        gameTitle, token, backgroundAllowed))
                    {
                        logger.Debug($"Auto capture cancelled: game window/session no longer " +
                            $"valid before render for '{gameTitle}'");
                        return null;
                    }
                    renderingWindow = window;
                    renderingWindowPid = ownerPid;
                    renderingBackgroundAllowed = backgroundAllowed;
                    rendererEntered = true;
                    bool isActuallyForeground = ScreenCapture.IsWindowForeground(window);
                    bmp = ScreenCapture.CaptureWindow(window, isActuallyForeground);
                    // Windows can destroy/recreate the HWND during PrintWindow.
                    // Never report this ordinary game-exit race as ERROR, and
                    // never save an image taken after the window switched away.
                    if (!IsAutoCaptureWindowCurrent(window, ownerPid, processId,
                        gameTitle, token, backgroundAllowed))
                    {
                        logger.Debug($"Auto capture cancelled: game window/session changed " +
                            $"during render for '{gameTitle}'");
                        return null;
                    }
                    if (bmp == null)
                    {
                        // The same live game window is still present: this is a
                        // real renderer failure and should produce an ERROR image.
                        logger.Warn($"Auto capture failed: unable to capture verified game window " +
                            $"HWND={window}, PID={ownerPid}; desktop fallback disabled");
                        return SaveErrorImage(gameTitle, isAutoCapture, "Unable to render game window", token);
                    }
                }
                else
                {
                    // Preserve the original manual screenshot behavior.
                    if (processId > 0)
                    {
                        try
                        {
                            using (var proc = Process.GetProcessById(processId))
                            {
                                var h = proc.MainWindowHandle;
                                if (h != IntPtr.Zero)
                                {
                                    logger.Debug($"Attempting manual window capture for process {processId}");
                                    bmp = ScreenCapture.CaptureWindow(h);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Debug($"Failed to capture manual game window: {ex.Message}");
                        }
                    }
                }

                if (bmp == null && !isAutoCapture)
                {
                    logger.Debug("Performing full screen capture");
                    bmp = ScreenCapture.CaptureScreen();
                }

                if (bmp == null)
                {
                    logger.Warn("Capture returned null bitmap");
                    return SaveErrorImage(gameTitle, isAutoCapture, "Capture returned no bitmap", token);
                }
                if (token.IsCancellationRequested) return null;

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
                    result = result + FileHelpers.MakeSafeFilename(suffix);
                }

                if (token.IsCancellationRequested) return null;
                // CreateNew prevents overwriting existing screenshots or racing with other apps.
                var filename = FileHelpers.GetAvailablePath(outFolder, result, ".png");
                try
                {
                    using (var stream = new FileStream(filename, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        bmp.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                }
                catch
                {
                    // Delete only the incomplete file created by this attempt.
                    try { if (File.Exists(filename)) File.Delete(filename); } catch { }
                    throw;
                }
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
                // A renderer may throw while its window is being destroyed.
                // That is session teardown, not a capture error to report.
                if (isAutoCapture && rendererEntered &&
                    !IsAutoCaptureWindowCurrent(renderingWindow, renderingWindowPid,
                        processId, gameTitle, token, renderingBackgroundAllowed))
                {
                    logger.Debug($"Auto capture cancelled during window teardown: {e.Message}");
                    return null;
                }
                logger.Error(e, "Error taking screenshot");

                if (attempted && !token.IsCancellationRequested)
                    return SaveErrorImage(gameTitle, isAutoCapture, e.Message, token);
                return null;
            }
            finally
            {
                bmp?.Dispose();
            }
        }
        // Manual diagnostics: use the identical ERROR image writer as real failures.
        // Does not touch captureGeneration, the active timer, or window resolution.
        public string CreateDiagnosticErrorPlaceholder(string gameTitle)
        {
            lock (screenshotSaveLock)
            {
                return WriteErrorImage(gameTitle, "Manual ERROR placeholder diagnostic", CancellationToken.None);
            }
        }

        // A failed *attempt* gets a visible placeholder; ordinary pauses and
        // policy denials do not create a flood of ERROR images.
        private string SaveErrorImage(string gameTitle, bool isAutoCapture,
            string reason, CancellationToken token)
        {
            WriteErrorImage(gameTitle, reason, token);
            // Failure placeholders must NOT be counted as successful screenshots.
            return null;
        }

        // Shared production and manual diagnostic writer. Returns the file path
        // only to the diagnostic entry point, never to the capture success path.
        private string WriteErrorImage(string gameTitle, string reason, CancellationToken token)
        {
            if (token.IsCancellationRequested) return null;
            var bases = new[] {
                settings?.Settings?.OutputFolder,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Playnite", "Plugins", "SharpMemories", "Screenshots")
            };
            foreach (string basePath in bases)
            {
                if (string.IsNullOrWhiteSpace(basePath)) continue;
                try
                {
                    string folder = Path.Combine(basePath,
                        FileHelpers.MakeSafeFilename(gameTitle ?? "game"));
                    Directory.CreateDirectory(folder);
                    string stem = $"{FileHelpers.MakeSafeFilename(gameTitle ?? "game")}_" +
                        DateTime.Now.ToString("yyyy-MM-dd_HH_mm_ss") + "_ERROR";
                    string path = FileHelpers.GetAvailablePath(folder, stem, ".png");
                    using (var image = new Bitmap(960, 540))
                    using (Graphics graphics = Graphics.FromImage(image))
                    using (var font = new Font("Arial", 96, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.White))
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        graphics.Clear(Color.Black);
                        graphics.DrawString("ERROR", font, brush,
                            new RectangleF(0, 0, 960, 540), new StringFormat {
                                Alignment = StringAlignment.Center,
                                LineAlignment = StringAlignment.Center
                            });
                        image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    logger.Warn($"Saved ERROR placeholder: {path}; reason={reason}");
                    try { plugin?.NotifyScreenshotsVisualizerRefresh(gameTitle); }
                    catch (Exception ex) { logger.Warn(ex, "ERROR image refresh failed"); }
                    try { messagesHandler?.ShowErrorNotification(gameTitle ?? "Unknown", reason); }
                    catch (Exception ex) { logger.Warn(ex, "ERROR notification failed"); }
                    // A placeholder is not a successful screenshot: never send
                    // the normal screenshot-success toast or success sound.
                    return path;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Could not save ERROR placeholder under '{basePath}'");
                }
            }
            logger.Error($"Unable to save ERROR placeholder anywhere: {reason}");
            return null;
        }
    }
}