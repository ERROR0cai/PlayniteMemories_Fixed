using Microsoft.Toolkit.Uwp.Notifications;
using Playnite.SDK;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;  // 👈 新增

namespace SharpMemories
{
    public class MessagesHandler
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI playniteApi;
        // 保留初始化时的设置对象，仅用于调试对比。
        private readonly SharpMemoriesSettings capturedSettings;

        // 始终获取当前正在使用的设置对象。
        private readonly Func<SharpMemoriesSettings> currentSettingsProvider;

        // 通知判断统一读取最新的设置对象。
        private SharpMemoriesSettings settings
        {
            get
            {
                return currentSettingsProvider();
            }
        }

        public MessagesHandler(
            IPlayniteAPI playniteApi,
            SharpMemoriesSettings settings,
            Func<SharpMemoriesSettings> currentSettingsProvider)
        {
            this.playniteApi = playniteApi;

            // 旧设置仅用于诊断。
            this.capturedSettings = settings;

            // 正式通知逻辑通过此委托读取最新设置。
            this.currentSettingsProvider =
                currentSettingsProvider
                ?? throw new ArgumentNullException(
                    nameof(currentSettingsProvider));
        }


        /// <summary>
        /// 通知诊断入口。
        /// 只记录信息，不改变通知判断和发送行为。
        /// </summary>
        public void TraceNotification(
            string stage,
            bool? isAutoCapture = null)
        {
            try
            {
                SharpMemoriesSettings current =
                    currentSettingsProvider?.Invoke();

                // 调试开关以当前 ViewModel 为准。
                if (current?.EnableNotificationDebug != true)
                    return;

                var captured = capturedSettings;

                int capturedId = captured == null
                    ? 0
                    : RuntimeHelpers.GetHashCode(captured);

                int currentId = current == null
                    ? 0
                    : RuntimeHelpers.GetHashCode(current);

                logger.Info(
                    $"[NOTIFY-DEBUG] Stage={stage} | " +
                    $"Time={DateTime.Now:HH:mm:ss.fff} | " +
                    $"Thread={Thread.CurrentThread.ManagedThreadId} | " +
                    $"Type={(isAutoCapture.HasValue ? (isAutoCapture.Value ? "AUTO" : "MANUAL") : "OTHER")}");

                logger.Info(
                    $"[NOTIFY-DEBUG] " +
                    $"CapturedSettingsId={capturedId} | " +
                    $"CurrentSettingsId={currentId} | " +
                    $"SameReference={object.ReferenceEquals(captured, current)}");

                logger.Info(
                    $"[NOTIFY-DEBUG] Captured | " +
                    $"Global={captured?.EnableNotifications} | " +
                    $"Auto={captured?.EnableAutoScreenshotNotification} | " +
                    $"Manual={captured?.EnableManualScreenshotNotification} | " +
                    $"Style={captured?.NotificationStyle}");

                logger.Info(
                    $"[NOTIFY-DEBUG] Current  | " +
                    $"Global={current?.EnableNotifications} | " +
                    $"Auto={current?.EnableAutoScreenshotNotification} | " +
                    $"Manual={current?.EnableManualScreenshotNotification} | " +
                    $"Style={current?.NotificationStyle}");
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "[NOTIFY-DEBUG] Diagnostic logging failed");
            }
        }

        /// <summary>
        /// 记录通知流程中的分支选择。
        /// </summary>
        private void TraceBranch(string message)
        {
            try
            {
                if (currentSettingsProvider?.Invoke()?.EnableNotificationDebug == true)
                {
                    logger.Info("[NOTIFY-DEBUG] " + message);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "[NOTIFY-DEBUG] Branch logging failed");
            }
        }

        /// <summary>
        /// 显示截图成功通知
        /// </summary>
        public void ShowScreenshotNotification(
            string gameName,
            string screenshotPath,
            bool isAutoCapture = true,
            string processName = null)
        {
            TraceNotification("Notification.Entry", isAutoCapture);

            try
            {
                if (!settings.EnableNotifications)
                {
                    TraceBranch(
                        "Notification.Blocked: Global notification disabled");
                    return;
                }

                if (isAutoCapture &&
                    !settings.EnableAutoScreenshotNotification)
                {
                    TraceBranch(
                        "Notification.Blocked: Auto notification disabled");
                    return;
                }

                if (!isAutoCapture &&
                    !settings.EnableManualScreenshotNotification)
                {
                    TraceBranch(
                        "Notification.Blocked: Manual notification disabled");
                    return;
                }

                // 只过滤截图成功通知，不影响截图文件保存。
                var current = settings;
                if (current == null ||
                    current.IsScreenshotNotificationProcessBlocked(processName, isAutoCapture))
                {
                    TraceBranch(
                        $"Notification.Blocked: ProcessMode=" +
                        $"{(isAutoCapture ? current?.AutoNotificationProcessMode : current?.ManualNotificationProcessMode)} " +
                        $"Process={processName ?? "(unknown)"} " +
                        $"Type={(isAutoCapture ? "AUTO" : "MANUAL")}");
                    return;
                }

                TraceBranch(
                    $"Notification.Allowed | Game={gameName} | " +
                    $"Type={(isAutoCapture ? "AUTO" : "MANUAL")}");

                if (settings.NotificationStyle == NotificationStyles.Toast &&
                    IsWindows10Or11())
                {
                    TraceBranch("Notification.Dispatch: Windows Toast");

                    Task.Run(() =>
                    {
                        TraceBranch("Notification.ToastTask.Start");

                        ShowToastNotification(
                            gameName,
                            screenshotPath,
                            isAutoCapture);

                        TraceBranch("Notification.ToastTask.End");
                    });
                }
                else
                {
                    TraceBranch("Notification.Dispatch: Playnite");

                    ShowPlayniteNotification(
                        gameName,
                        screenshotPath,
                        isAutoCapture);

                    TraceBranch("Notification.PlayniteCall.Returned");
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to show screenshot notification");

                TraceBranch(
                    $"Notification.Exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示 Windows Toast 通知
        /// </summary>
        private void ShowToastNotification(string gameName, string screenshotPath, bool isAutoCapture)
        {
            try
            {
                var fileName = Path.GetFileName(screenshotPath);
                var actionText = isAutoCapture ? "自动截图" : "手动截图";

                var toastBuilder = new ToastContentBuilder()
                    .AddText($"📸 {gameName}")
                    .AddText($"截图已保存: {fileName}")
                    .AddText($"方式: {actionText} | 位置: {Path.GetDirectoryName(screenshotPath)}");

                TraceBranch("WindowsToast.Show: Calling");

                toastBuilder.Show();

                TraceBranch("WindowsToast.Show: ReturnedSuccessfully");

                logger.Info($"Toast notification shown for: {gameName}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to show toast notification");

                TraceBranch(
                    $"WindowsToast.Show: Exception={ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示 Playnite 内置通知（备用方案）
        /// </summary>
        private void ShowPlayniteNotification(string gameName, string screenshotPath, bool isAutoCapture)
        {
            var actionText = isAutoCapture ? "自动截图" : "手动截图";
            var message = $"{gameName}: 截图已保存 [{actionText}]";

            TraceBranch("PlayniteNotification.Add: Calling");

            playniteApi.Notifications.Add(
                new NotificationMessage(
                    Guid.NewGuid().ToString(),
                    message,
                    NotificationType.Info
                )
            );
            TraceBranch("PlayniteNotification.Add: ReturnedSuccessfully");
            logger.Info($"Playnite notification shown for: {gameName}");
        }

        /// <summary>
        /// 显示错误通知
        /// </summary>
        public void ShowErrorNotification(string gameName, string errorMessage)
        {
            try
            {
                if (!settings.EnableNotifications)
                    return;

                if (settings.NotificationStyle == NotificationStyles.Toast && IsWindows10Or11())
                {
                    // 👇 使用 Task.Run 在后台线程显示 Toast 通知
                    Task.Run(() =>
                    {
                        try
                        {
                            new ToastContentBuilder()
                                .AddText($"❌ {gameName}")
                                .AddText($"截图失败: {errorMessage}")
                                .Show();
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Failed to show error toast notification");
                        }
                    });
                }
                else
                {
                    playniteApi.Notifications.Add(
                        new NotificationMessage(
                            Guid.NewGuid().ToString(),
                            $"❌ {gameName}: 截图失败\n{errorMessage}",
                            NotificationType.Error
                        )
                    );
                }
                logger.Info($"Error notification shown for: {gameName}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to show error notification");
            }
        }

        /// <summary>
        /// 显示通用通知（用于错误等）
        /// </summary>
        public void ShowGenericNotification(string title, string message, NotificationType type = NotificationType.Info)
        {
            try
            {
                if (!settings.EnableNotifications)
                    return;

                if (settings.NotificationStyle == NotificationStyles.Toast && IsWindows10Or11())
                {
                    // 👇 使用 Task.Run 在后台线程显示 Toast 通知
                    Task.Run(() =>
                    {
                        try
                        {
                            new ToastContentBuilder()
                                .AddText(title)
                                .AddText(message)
                                .Show();
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Failed to show generic toast notification");
                        }
                    });
                }
                else
                {
                    playniteApi.Notifications.Add(
                        new NotificationMessage(Guid.NewGuid().ToString(), $"{title}: {message}", type)
                    );
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to show generic notification");
            }
        }

        /// <summary>
        /// 检测是否为 Windows 10/11
        /// </summary>
        private bool IsWindows10Or11()
        {
            var version = Environment.OSVersion.Version;
            return version.Major >= 10;
        }
    }

    /// <summary>
    /// 通知样式枚举
    /// </summary>
    public enum NotificationStyles
    {
        Toast,      // Windows 原生通知
        Playnite    // Playnite 内置通知
    }
}