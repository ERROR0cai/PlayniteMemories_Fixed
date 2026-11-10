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
        /// Captures a window using multiple methods with fallback logic
        /// </summary>
        public static Bitmap CaptureWindow(IntPtr hWnd, bool allowBitBlt = true)
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

            // BitBlt of a covered/background window may include another app.
            if (allowBitBlt)
            {
                result = CaptureWithBitBlt(hWnd);
                if (IsValidScreenshot(result)) return result;
                result?.Dispose();
            }

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
                if (hdcDest == IntPtr.Zero) return null;
                hBitmap = CreateCompatibleBitmap(hdcSrc, rect.Width, rect.Height);
                if (hBitmap == IntPtr.Zero) return null;
                hOld = SelectObject(hdcDest, hBitmap);

                bool success = PrintWindow(hWnd, hdcDest, flags);
                if (!success) return null;

                using (var img = Image.FromHbitmap(hBitmap))
                {
                    return new Bitmap(img);
                }
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
                if (hdcDest == IntPtr.Zero) return null;
                hBitmap = CreateCompatibleBitmap(hdcSrc, rect.Width, rect.Height);
                if (hBitmap == IntPtr.Zero) return null;
                hOld = SelectObject(hdcDest, hBitmap);

                var success = BitBlt(hdcDest, 0, 0, rect.Width, rect.Height, hdcSrc, 0, 0, SRCCOPY);
                if (!success) return null;

                using (var img = Image.FromHbitmap(hBitmap))
                {
                    return new Bitmap(img);
                }
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