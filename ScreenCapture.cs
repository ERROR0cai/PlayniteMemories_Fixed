﻿using System;
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
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

        // Screen and session state: no window-title guessing. Lock detection
        // checks the desktop actually receiving user input; display power is
        // tracked through Windows power-setting broadcasts.
        private const int UOI_NAME = 2;
        private const uint DESKTOP_READOBJECTS = 0x0001;
        private const int SPI_GETSCREENSAVERRUNNING = 0x0072;
        private static readonly object monitorGate = new object();
        private static DisplayStateListener displayListener;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetUserObjectInformation(IntPtr obj, int index,
            System.Text.StringBuilder buffer, int length, out int needed);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param,
            out int value, uint update);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient,
            ref Guid setting, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        private const uint WM_CLOSE = 0x0010;
        private static readonly Guid GUID_CONSOLE_DISPLAY_STATE =
            new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerBroadcastSetting
        {
            public Guid PowerSetting;
            public uint DataLength;
            public uint Data;
        }

        private sealed class DisplayStateListener : System.Windows.Forms.NativeWindow
        {
            private readonly System.Threading.ManualResetEventSlim ready =
                new System.Threading.ManualResetEventSlim(false);
            private readonly System.Threading.Thread thread;
            private IntPtr notification;
            private volatile int displayState = -1; // -1 unknown, 0 off, 1 on, 2 dim
            public int State => displayState;
            public DisplayStateListener()
            {
                thread = new System.Threading.Thread(Run)
                { IsBackground = true, Name = "SharpMemories.DisplayPower" };
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                if (!ready.Wait(3000)) logger.Warn("Display monitor startup timed out");
            }
            private void Run()
            {
                try
                {
                    var parameters = new System.Windows.Forms.CreateParams
                    { Caption = "SharpMemories.PowerMonitor", Parent = new IntPtr(-3) };
                    CreateHandle(parameters);
                    Guid guid = GUID_CONSOLE_DISPLAY_STATE;
                    notification = RegisterPowerSettingNotification(Handle, ref guid,
                        DEVICE_NOTIFY_WINDOW_HANDLE);
                    if (notification == IntPtr.Zero)
                        logger.Warn("Display power notification registration failed");
                    ready.Set();
                    System.Windows.Forms.Application.Run();
                }
                catch (Exception ex) { logger.Warn(ex, "Display monitor failed"); ready.Set(); }
                finally
                {
                    if (notification != IntPtr.Zero) UnregisterPowerSettingNotification(notification);
                    if (Handle != IntPtr.Zero) DestroyHandle();
                }
            }
            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt64() == PBT_POWERSETTINGCHANGE &&
                    m.LParam != IntPtr.Zero)
                {
                    try
                    {
                        var update = (PowerBroadcastSetting)Marshal.PtrToStructure(m.LParam,
                            typeof(PowerBroadcastSetting));
                        if (update.PowerSetting == GUID_CONSOLE_DISPLAY_STATE &&
                            update.DataLength >= sizeof(uint) && update.Data <= 2)
                        {
                            displayState = (int)update.Data;
                            logger.Info($"Display power state changed: {displayState}");
                        }
                    }
                    catch (Exception ex) { logger.Warn(ex, "Display state message failed"); }
                }
                if (m.Msg == WM_CLOSE)
                {
                    System.Windows.Forms.Application.ExitThread();
                    return;
                }
                base.WndProc(ref m);
            }
            public void Stop()
            {
                if (Handle != IntPtr.Zero) PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public static void StartSystemStateMonitor()
        {
            lock (monitorGate)
                if (displayListener == null) displayListener = new DisplayStateListener();
        }

        public static void StopSystemStateMonitor()
        {
            lock (monitorGate)
            {
                var old = displayListener;
                displayListener = null;
                old?.Stop();
            }
        }

        public static bool IsScreenLocked()
        {
            IntPtr desktop = IntPtr.Zero;
            try
            {
                desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
                if (desktop == IntPtr.Zero)
                {
                    logger.Debug("Input desktop inaccessible; pausing capture for safety");
                    return true;
                }
                var name = new System.Text.StringBuilder(256);
                int needed;
                if (!GetUserObjectInformation(desktop, UOI_NAME, name,
                    name.Capacity * sizeof(char), out needed))
                {
                    logger.Debug("Input desktop name unavailable; pausing capture for safety");
                    return true;
                }
                return !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Input desktop check failed");
                return true;
            }
            finally { if (desktop != IntPtr.Zero) CloseDesktop(desktop); }
        }

        public static bool IsScreenSaverOrSleeping()
        {
            int running;
            if (!SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, out running, 0))
            {
                logger.Debug("Screen saver status unavailable; pausing capture for safety");
                return true;
            }
            if (running != 0) return true;
            lock (monitorGate)
                return displayListener != null && displayListener.State == 0;
        }

        public static bool CanTakeScreenshot()
        {
            bool locked = IsScreenLocked();
            bool unavailable = IsScreenSaverOrSleeping();
            logger.Debug($"System capture status: locked={locked}, screenOffOrSaver={unavailable}");
            return !locked && !unavailable;
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
            if (bitmap == null || bitmap.Width < 2 || bitmap.Height < 2) return false;
            // PrintWindow can succeed but return a solid black/white frame for
            // some Direct3D games. Such frames are treated as capture failures.
            // Sampling keeps this check cheap even for 4K screenshots.
            try
            {
                int first = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).ToArgb();
                bool uniform = true;
                for (int y = 1; y <= 7 && uniform; y++)
                    for (int x = 1; x <= 7; x++)
                        if (bitmap.GetPixel(x * (bitmap.Width - 1) / 8,
                                y * (bitmap.Height - 1) / 8).ToArgb() != first)
                        { uniform = false; break; }
                return !uniform;
            }
            catch { return false; }
        }

        public static Bitmap CaptureScreen()
        {
            var hWnd = GetDesktopWindow();
            return CaptureWindow(hWnd);
        }

        /// <summary>
        /// Capture only a verified target window. An on-screen region fallback is
        /// permitted solely for the *actual foreground* HWND, never in background.
        /// This does NOT grant permission to capture an unrelated full desktop.
        /// </summary>
        public static Bitmap CaptureWindow(IntPtr hWnd, bool allowBitBlt = true)
        {
            if (hWnd == IntPtr.Zero) return null;
            string problem;
            Bitmap result = CaptureWithPrintWindow(hWnd, PW_RENDERFULLCONTENT, out problem);
            if (AcceptFrame(result, "PrintWindow(full)", hWnd, problem, out result)) return result;

            result = CaptureWithPrintWindow(hWnd, PW_CLIENTONLY, out problem);
            if (AcceptFrame(result, "PrintWindow(client)", hWnd, problem, out result)) return result;

            // Background BitBlt risks capturing a covering application.
            if (allowBitBlt && GetForegroundWindow() == hWnd)
            {
                result = CaptureWithBitBlt(hWnd, out problem);
                if (AcceptFrame(result, "BitBlt(window DC)", hWnd, problem, out result)) return result;

                // Some OpenGL/Java games do not expose current pixels through
                // PrintWindow/GetWindowDC. Capture ONLY the verified foreground
                // window's on-screen bounds, and only if fully visible on screen.
                result = CaptureForegroundWindowRegion(hWnd, out problem);
                if (AcceptFrame(result, "CopyFromScreen(window bounds)", hWnd, problem, out result)) return result;
            }
            else
            {
                logger.Debug($"Window capture HWND={hWnd}: foreground-only fallbacks disabled " +
                    $"(allowBitBlt={allowBitBlt}, isForeground={GetForegroundWindow() == hWnd})");
            }
            logger.Warn($"Window capture failed for HWND={hWnd}: all permitted capture methods failed");
            return null;
        }

        private static bool AcceptFrame(Bitmap image, string method, IntPtr hwnd,
            string failure, out Bitmap accepted)
        {
            if (IsValidScreenshot(image))
            {
                logger.Debug($"Window capture succeeded: method={method}, HWND={hwnd}, " +
                    $"size={image.Width}x{image.Height}");
                accepted = image;
                return true;
            }
            logger.Debug($"Window capture rejected: method={method}, HWND={hwnd}, " +
                $"reason={(image == null ? (failure ?? "no bitmap") : "uniform/invalid frame")}");
            image?.Dispose();
            accepted = null;
            return false;
        }

        private static Bitmap CaptureWithPrintWindow(IntPtr hwnd, uint flags, out string failure)
        {
            failure = null;
            IntPtr source = IntPtr.Zero, dest = IntPtr.Zero, dib = IntPtr.Zero, original = IntPtr.Zero;
            try
            {
                Rectangle rect = GetWindowRectangle(hwnd);
                if (rect.Width <= 0 || rect.Height <= 0) { failure = "invalid bounds"; return null; }
                source = GetWindowDC(hwnd);
                if (source == IntPtr.Zero) { failure = "GetWindowDC failed, win32=" + Marshal.GetLastWin32Error(); return null; }
                dest = CreateCompatibleDC(source);
                if (dest == IntPtr.Zero) { failure = "CreateCompatibleDC failed"; return null; }
                dib = CreateCompatibleBitmap(source, rect.Width, rect.Height);
                if (dib == IntPtr.Zero) { failure = "CreateCompatibleBitmap failed"; return null; }
                original = SelectObject(dest, dib);
                if (original == IntPtr.Zero || original == new IntPtr(-1))
                { failure = "SelectObject failed"; return null; }
                if (!PrintWindow(hwnd, dest, flags))
                { failure = "PrintWindow returned false, win32=" + Marshal.GetLastWin32Error(); return null; }
                using (var image = Image.FromHbitmap(dib)) return new Bitmap(image);
            }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; return null; }
            finally
            {
                if (original != IntPtr.Zero && original != new IntPtr(-1)) SelectObject(dest, original);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (dest != IntPtr.Zero) DeleteDC(dest);
                if (source != IntPtr.Zero) ReleaseDC(hwnd, source);
            }
        }

        private static Bitmap CaptureWithBitBlt(IntPtr hwnd, out string failure)
        {
            failure = null;
            IntPtr source = IntPtr.Zero, dest = IntPtr.Zero, dib = IntPtr.Zero, original = IntPtr.Zero;
            try
            {
                Rectangle rect = GetWindowRectangle(hwnd);
                if (rect.Width <= 0 || rect.Height <= 0) { failure = "invalid bounds"; return null; }
                source = GetWindowDC(hwnd);
                if (source == IntPtr.Zero) { failure = "GetWindowDC failed, win32=" + Marshal.GetLastWin32Error(); return null; }
                dest = CreateCompatibleDC(source);
                if (dest == IntPtr.Zero) { failure = "CreateCompatibleDC failed"; return null; }
                dib = CreateCompatibleBitmap(source, rect.Width, rect.Height);
                if (dib == IntPtr.Zero) { failure = "CreateCompatibleBitmap failed"; return null; }
                original = SelectObject(dest, dib);
                if (original == IntPtr.Zero || original == new IntPtr(-1))
                { failure = "SelectObject failed"; return null; }
                if (!BitBlt(dest, 0, 0, rect.Width, rect.Height, source, 0, 0, SRCCOPY))
                { failure = "BitBlt returned false, win32=" + Marshal.GetLastWin32Error(); return null; }
                using (var image = Image.FromHbitmap(dib)) return new Bitmap(image);
            }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; return null; }
            finally
            {
                if (original != IntPtr.Zero && original != new IntPtr(-1)) SelectObject(dest, original);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (dest != IntPtr.Zero) DeleteDC(dest);
                if (source != IntPtr.Zero) ReleaseDC(hwnd, source);
            }
        }

        // Screen-space region only; never use PrimaryScreen.Bounds as a fallback
        // for an unknown HWND. Avoid partially offscreen windows and size errors.
        private static Bitmap CaptureForegroundWindowRegion(IntPtr hwnd, out string failure)
        {
            failure = null;
            if (hwnd == IntPtr.Zero || GetForegroundWindow() != hwnd)
            { failure = "window is not foreground"; return null; }
            try
            {
                RECT native;
                if (!GetWindowRect(hwnd, out native))
                { failure = "GetWindowRect failed, win32=" + Marshal.GetLastWin32Error(); return null; }
                var rect = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
                Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
                if (rect.Width < 2 || rect.Height < 2 || !virtualScreen.Contains(rect))
                { failure = "window outside visible virtual-screen bounds"; return null; }
                Bitmap image = new Bitmap(rect.Width, rect.Height);
                try
                {
                    using (Graphics g = Graphics.FromImage(image))
                        g.CopyFromScreen(rect.Location, Point.Empty, rect.Size, CopyPixelOperation.SourceCopy);
                    if (GetForegroundWindow() != hwnd)
                    { failure = "foreground changed during region capture"; image.Dispose(); return null; }
                    return image;
                }
                catch { image.Dispose(); throw; }
            }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; return null; }
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