using Microsoft.Toolkit.Uwp.Notifications;
using Playnite.SDK;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;  // 👈 新增

namespace SharpMemories
{
    public class MessagesHandler
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI playniteApi;
        private readonly SharpMemoriesSettings settings;

        public MessagesHandler(IPlayniteAPI playniteApi, SharpMemoriesSettings settings)
        {
            this.playniteApi = playniteApi;
            this.settings = settings;
        }

        /// <summary>
        /// 显示截图成功通知
        /// </summary>
        public void ShowScreenshotNotification(string gameName, string screenshotPath, bool isAutoCapture = true)
        {
            try
            {
                // 👇 添加详细日志
                logger.Info($"🔔 ShowScreenshotNotification called:");
                logger.Info($"   ├─ gameName: '{gameName}'");
                logger.Info($"   ├─ isAutoCapture: {isAutoCapture}");
                logger.Info($"   ├─ screenshotPath: '{screenshotPath}'");
                logger.Info($"   ├─ EnableNotifications: {settings.EnableNotifications}");
                logger.Info($"   ├─ EnableAutoScreenshotNotification: {settings.EnableAutoScreenshotNotification}");
                logger.Info($"   └─ EnableManualScreenshotNotification: {settings.EnableManualScreenshotNotification}");
                if (!settings.EnableNotifications)
                    return;

                // 👇 新增：根据截图类型判断是否启用通知
                if (isAutoCapture && !settings.EnableAutoScreenshotNotification)
                    return;

                if (!isAutoCapture && !settings.EnableManualScreenshotNotification)
                    return;

                if (settings.NotificationStyle == NotificationStyles.Toast && IsWindows10Or11())
                {
                    // 👇 使用 Task.Run 在后台线程显示 Toast 通知
                    Task.Run(() => ShowToastNotification(gameName, screenshotPath, isAutoCapture));
                }
                else
                {
                    ShowPlayniteNotification(gameName, screenshotPath, isAutoCapture);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to show screenshot notification");
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

                toastBuilder.Show();
                logger.Info($"Toast notification shown for: {gameName}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to show toast notification");
            }
        }

        /// <summary>
        /// 显示 Playnite 内置通知（备用方案）
        /// </summary>
        private void ShowPlayniteNotification(string gameName, string screenshotPath, bool isAutoCapture)
        {
            var actionText = isAutoCapture ? "自动截图" : "手动截图";
            var message = $"{gameName}: 截图已保存 [{actionText}]";

            playniteApi.Notifications.Add(
                new NotificationMessage(
                    Guid.NewGuid().ToString(),
                    message,
                    NotificationType.Info
                )
            );
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