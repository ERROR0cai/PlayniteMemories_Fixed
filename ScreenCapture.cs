using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Playnite.SDK;

namespace SharpMemories
{
    internal static class ScreenCapture
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
            IntPtr hdcSrc, int nXSrc, int nYSrc, System.Int32 dwRop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        // Windows 系统状态检测
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        /// <summary>
        /// 获取窗口类名
        /// </summary>
        private static string GetWindowClassName(IntPtr hWnd)
        {
            try
            {
                var sb = new System.Text.StringBuilder(256);
                if (GetClassName(hWnd, sb, sb.Capacity) > 0)
                {
                    return sb.ToString();
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int SRCCOPY = 0x00CC0020;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const uint PW_CLIENTONLY = 0x00000001;

        /// <summary>
        /// 检测电脑是否处于锁屏状态
        /// </summary>
        public static bool IsScreenLocked()
        {
            try
            {
                logger.Debug($"🔍 IsScreenLocked() - Checking lock screen status...");
                string title = string.Empty;
                IntPtr foreground = IntPtr.Zero;

                // 方法1：检测锁屏窗口是否存在
                IntPtr lockScreenHwnd = FindWindow("LockScreen", null);
                bool lockScreenExists = lockScreenHwnd != IntPtr.Zero && IsWindowVisible(lockScreenHwnd);
                logger.Debug($"   ├─ LockScreen window exists: {lockScreenExists} (Handle: {lockScreenHwnd})");

                if (lockScreenExists)
                {
                    logger.Debug($"   └─ ✅ Lock screen detected via LockScreen window");
                    return true;
                }

                // 方法2：检测 LogonUI 进程窗口
                IntPtr logonHwnd = FindWindow("LogonUI", null);
                bool logonExists = logonHwnd != IntPtr.Zero && IsWindowVisible(logonHwnd);
                logger.Debug($"   ├─ LogonUI window exists: {logonExists} (Handle: {logonHwnd})");

                if (logonExists)
                {
                    logger.Debug($"   └─ ✅ Lock screen detected via LogonUI window");
                    return true;
                }

                // 方法3：检测 "Windows 默认锁屏界面" (Win11 中文锁屏)
                IntPtr defaultLockHwnd = FindWindow("Windows.UI.Core.CoreWindow", null);
                if (defaultLockHwnd != IntPtr.Zero)
                {
                    var sb = new System.Text.StringBuilder(256);
                    GetWindowText(defaultLockHwnd, sb, sb.Capacity);
                    string windowTitle = sb.ToString();
                    if (!string.IsNullOrEmpty(windowTitle) && (windowTitle.Contains("锁屏") || windowTitle.Contains("Lock")))
                    {
                        logger.Debug($"   └─ ✅ Lock screen detected via Windows.UI.Core.CoreWindow: '{windowTitle}'");
                        return true;
                    }
                }

                // 方法4：检查前台窗口是否为锁屏相关
                foreground = GetForegroundWindow();
                logger.Debug($"   ├─ Foreground window handle: {foreground}");

                if (foreground != IntPtr.Zero)
                {
                    var sb = new System.Text.StringBuilder(256);
                    GetWindowText(foreground, sb, sb.Capacity);
                    title = sb.ToString();
                    string titleLower = title.ToLowerInvariant();
                    logger.Debug($"   ├─ Foreground window title: '{title}'");

                    // 检测常见的锁屏窗口标题（中英文完整列表）
                    bool isLockTitle =
                        // 英文
                        titleLower.Contains("lock") ||
                        titleLower.Contains("logon") ||
                        titleLower.Contains("welcome") ||
                        titleLower.Contains("credential") ||
                        titleLower.Contains("sign in") ||
                        titleLower.Contains("sign-in") ||
                        // 中文
                        title.Contains("默认锁屏") ||
                        title.Contains("锁屏界面") ||
                        title.Contains("锁屏") ||
                        title.Contains("登录") ||
                        title.Contains("登入") ||
                        title.Contains("账户") ||
                        // 其他常见锁屏相关
                        titleLower.Contains("screen saver") ||
                        titleLower.Contains("unlock");

                    if (isLockTitle)
                    {
                        logger.Debug($"   └─ ✅ Lock screen detected via window title: '{title}'");
                        return true;
                    }
                }

                // 方法5：检查是否有全屏覆盖窗口（锁屏/登录界面）
                try
                {
                    if (foreground != IntPtr.Zero)
                    {
                        IntPtr desktopHwnd = GetDesktopWindow();
                        if (foreground != desktopHwnd)
                        {
                            RECT foregroundRect;
                            if (GetWindowRect(foreground, out foregroundRect))
                            {
                                int screenWidth = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                                int screenHeight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
                                int fgWidth = foregroundRect.Right - foregroundRect.Left;
                                int fgHeight = foregroundRect.Bottom - foregroundRect.Top;

                                // 如果前台窗口几乎覆盖整个屏幕，且不是桌面，可能是锁屏
                                if (fgWidth >= screenWidth * 0.9 && fgHeight >= screenHeight * 0.9)
                                {
                                    var className = GetWindowClassName(foreground);
                                    logger.Debug($"   ├─ Foreground window class: '{className}'");

                                    if (!string.IsNullOrEmpty(className) &&
                                        (className.Contains("Lock") ||
                                        className.Contains("Logon") ||
                                        className.Contains("Credential") ||
                                        className.Contains("Fullscreen")))
                                    {
                                        logger.Debug($"   └─ ✅ Lock screen detected via full-screen window (class: {className})");
                                        return true;
                                    }

                                    if (title.Contains("默认锁屏") || title.Contains("锁屏"))
                                    {
                                        logger.Debug($"   └─ ✅ Lock screen detected via full-screen window with lock title");
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug($"   ├─ Full-screen check failed: {ex.Message}");
                }

                logger.Debug($"   └─ ❌ No lock screen detected");
                return false;
            }
            catch (Exception ex)
            {
                logger.Error($"IsScreenLocked() exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检测系统是否处于息屏/睡眠状态
        /// </summary>
        public static bool IsScreenSaverOrSleeping()
        {
            try
            {
                logger.Debug($"🔍 IsScreenSaverOrSleeping() - Checking screen saver/sleep status...");

                // 检查屏保是否运行
                IntPtr screenSaverHwnd = FindWindow("WindowsScreenSaverClass", null);
                bool screenSaverExists = screenSaverHwnd != IntPtr.Zero && IsWindowVisible(screenSaverHwnd);
                logger.Debug($"   ├─ Screen saver window exists: {screenSaverExists} (Handle: {screenSaverHwnd})");

                if (screenSaverExists)
                {
                    logger.Debug($"   └─ ✅ Screen saver detected");
                    return true;
                }

                // 检查锁屏窗口是否存在
                IntPtr lockScreenHwnd = FindWindow("LockScreen", null);
                if (lockScreenHwnd != IntPtr.Zero && IsWindowVisible(lockScreenHwnd))
                {
                    logger.Debug($"   └─ ✅ Lock screen detected in sleep check");
                    return true;
                }

                // 检查前台窗口是否存在
                IntPtr foreground = GetForegroundWindow();
                logger.Debug($"   ├─ Foreground window handle: {foreground}");

                if (foreground == IntPtr.Zero)
                {
                    logger.Debug($"   └─ ✅ No foreground window - system may be sleeping");
                    return true;
                }

                // 检查前台窗口是否可见
                bool isVisible = IsWindowVisible(foreground);
                logger.Debug($"   ├─ Foreground window visible: {isVisible}");

                if (!isVisible)
                {
                    logger.Debug($"   └─ ✅ Foreground window not visible - system may be sleeping");
                    return true;
                }

                // 检查桌面窗口是否可见
                IntPtr desktopHwnd = GetDesktopWindow();
                bool desktopVisible = IsWindowVisible(desktopHwnd);
                logger.Debug($"   ├─ Desktop window visible: {desktopVisible}");

                // 检查是否处于登出/切换用户状态
                IntPtr switchUserHwnd = FindWindow("SwitchUser", null);
                bool switchUserExists = switchUserHwnd != IntPtr.Zero && IsWindowVisible(switchUserHwnd);
                logger.Debug($"   ├─ SwitchUser window exists: {switchUserExists}");

                if (switchUserExists)
                {
                    logger.Debug($"   └─ ✅ Switch user screen detected");
                    return true;
                }

                // 检查前台窗口标题是否包含锁屏关键词
                if (foreground != IntPtr.Zero)
                {
                    var sb = new System.Text.StringBuilder(256);
                    GetWindowText(foreground, sb, sb.Capacity);
                    string title = sb.ToString();
                    string titleLower = title.ToLowerInvariant();
                    logger.Debug($"   ├─ Foreground window title: '{title}'");

                    if (title.Contains("默认锁屏") ||
                        title.Contains("锁屏") ||
                        title.Contains("登录") ||
                        titleLower.Contains("lock") ||
                        titleLower.Contains("logon") ||
                        titleLower.Contains("credential") ||
                        titleLower.Contains("sign in"))
                    {
                        logger.Debug($"   └─ ✅ Lock screen detected via title in sleep check");
                        return true;
                    }
                }

                logger.Debug($"   └─ ❌ No screen saver or sleep detected");
                return false;
            }
            catch (Exception ex)
            {
                logger.Error($"IsScreenSaverOrSleeping() exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检测是否可以截图（未锁屏、未息屏）
        /// </summary>
        public static bool CanTakeScreenshot()
        {
            logger.Debug($"🔍 CanTakeScreenshot() - Starting check...");

            bool isLocked = IsScreenLocked();
            bool isSleeping = IsScreenSaverOrSleeping();

            logger.Debug($"   ├─ IsScreenLocked: {isLocked}");
            logger.Debug($"   ├─ IsScreenSaverOrSleeping: {isSleeping}");

            bool canTake = !isLocked && !isSleeping;
            logger.Debug($"   └─ CanTakeScreenshot: {canTake}");

            return canTake;
        }

        /// <summary>
        /// 检测指定窗口是否为前台窗口（用于检测游戏是否在前台）
        /// </summary>
        public static bool IsWindowForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                logger.Debug($"IsWindowForeground: hWnd is Zero, returning false");
                return false;
            }

            IntPtr foreground = GetForegroundWindow();
            bool isForeground = foreground == hWnd;
            logger.Debug($"IsWindowForeground: Foreground={foreground}, Target={hWnd}, IsForeground={isForeground}");
            return isForeground;
        }

        /// <summary>
        /// Validates if a bitmap is usable
        /// </summary>
        private static bool IsValidScreenshot(Bitmap bitmap)
        {
            if (bitmap == null) return false;
            return true;
        }

        public static Bitmap CaptureScreen()
        {
            var hWnd = GetDesktopWindow();
            return CaptureWindow(hWnd);
        }

        /// <summary>
        /// Captures a window using multiple methods with fallback logic
        /// </summary>
        public static Bitmap CaptureWindow(IntPtr hWnd)
        {
            Bitmap result = null;

            result = CaptureWithPrintWindow(hWnd, PW_RENDERFULLCONTENT);
            if (IsValidScreenshot(result))
                return result;
            result?.Dispose();

            result = CaptureWithPrintWindow(hWnd, PW_CLIENTONLY);
            if (IsValidScreenshot(result))
                return result;
            result?.Dispose();

            result = CaptureWithBitBlt(hWnd);
            if (IsValidScreenshot(result))
                return result;
            result?.Dispose();

            return null;
        }

        /// <summary>
        /// Captures window using PrintWindow API
        /// </summary>
        private static Bitmap CaptureWithPrintWindow(IntPtr hWnd, uint flags)
        {
            IntPtr hdcSrc = IntPtr.Zero;
            IntPtr hdcDest = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;

            try
            {
                Rectangle rect = GetWindowRectangle(hWnd);
                if (rect.Width <= 0 || rect.Height <= 0) return null;

                hdcSrc = GetWindowDC(hWnd);
                if (hdcSrc == IntPtr.Zero) return null;

                hdcDest = CreateCompatibleDC(hdcSrc);
                hBitmap = CreateCompatibleBitmap(hdcSrc, rect.Width, rect.Height);
                hOld = SelectObject(hdcDest, hBitmap);

                bool success = PrintWindow(hWnd, hdcDest, flags);
                if (!success) return null;

                var img = Image.FromHbitmap(hBitmap);
                return new Bitmap(img);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hOld != IntPtr.Zero) SelectObject(hdcDest, hOld);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (hdcDest != IntPtr.Zero) DeleteDC(hdcDest);
                if (hdcSrc != IntPtr.Zero) ReleaseDC(hWnd, hdcSrc);
            }
        }

        /// <summary>
        /// Captures window using BitBlt
        /// </summary>
        private static Bitmap CaptureWithBitBlt(IntPtr hWnd)
        {
            IntPtr hdcSrc = IntPtr.Zero;
            IntPtr hdcDest = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;

            try
            {
                hdcSrc = GetWindowDC(hWnd);
                if (hdcSrc == IntPtr.Zero) return null;

                Rectangle rect = GetWindowRectangle(hWnd);
                if (rect.Width <= 0 || rect.Height <= 0) return null;

                hdcDest = CreateCompatibleDC(hdcSrc);
                hBitmap = CreateCompatibleBitmap(hdcSrc, rect.Width, rect.Height);
                hOld = SelectObject(hdcDest, hBitmap);

                var success = BitBlt(hdcDest, 0, 0, rect.Width, rect.Height, hdcSrc, 0, 0, SRCCOPY);
                if (!success) return null;

                var img = Image.FromHbitmap(hBitmap);
                return new Bitmap(img);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hOld != IntPtr.Zero) SelectObject(hdcDest, hOld);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (hdcDest != IntPtr.Zero) DeleteDC(hdcDest);
                if (hdcSrc != IntPtr.Zero) ReleaseDC(hWnd, hdcSrc);
            }
        }

        /// <summary>
        /// Gets window rectangle, falling back to screen bounds if needed
        /// </summary>
        private static Rectangle GetWindowRectangle(IntPtr hWnd)
        {
            if (hWnd == GetDesktopWindow())
            {
                return System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            }

            RECT rect;
            if (GetWindowRect(hWnd, out rect))
            {
                return new Rectangle(
                    0,
                    0,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top
                );
            }

            return System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        }
    }
}