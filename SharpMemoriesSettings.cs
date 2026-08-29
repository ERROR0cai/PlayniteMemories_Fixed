﻿using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace SharpMemories
{
    // Helper class for UI binding to library plugin settings
    public class LibraryPluginInfo : ObservableObject
    {
        private bool isHotkeyEnabled;

        public Guid Id { get; set; }
        public string Name { get; set; }
        public bool IsHotkeyEnabled
        {
            get => isHotkeyEnabled;
            set => SetValue(ref isHotkeyEnabled, value);
        }
    }

    public class SharpMemoriesSettings : ObservableObject
    {
        private bool enabled = true;
        private int intervalMinutes = 10;
        private string outputFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Playnite");
        private string monitorFolder = string.Empty;
        private bool enableMonitoring = true;

        // 重命名模式
        private string _renamePattern = "{game}_{date}_{time}_Gamesnap";
        public string RenamePattern
        {
            get => _renamePattern;
            set => SetValue(ref _renamePattern, value);
        }

        // 是否启用 ScreenshotsVisualizer 刷新
        private bool _enableSVRefresh = false;
        public bool EnableSVRefresh
        {
            get => _enableSVRefresh;
            set => SetValue(ref _enableSVRefresh, value);
        }

        // Hotkey settings
        private bool enableHotkey = true;
        private Key hotkeyKey = Key.F12;
        private bool hotkeyCtrl = false;
        private bool hotkeyAlt = false;
        private bool hotkeyShift = false;
        private bool hotkeySuppressKey = true;

        // Per-library hotkey enable flags - dynamic dictionary keyed by library plugin ID
        private Dictionary<Guid, bool> hotkeyEnabledByLibrary = new Dictionary<Guid, bool>();

        public bool Enabled { get => enabled; set => SetValue(ref enabled, value); }

        public int IntervalMinutes
        {
            get => intervalMinutes;
            set
            {
                if (value < 0) value = 0;
                SetValue(ref intervalMinutes, value);
            }
        }

        public string OutputFolder { get => outputFolder; set => SetValue(ref outputFolder, value); }
        public string MonitorFolder { get => monitorFolder; set => SetValue(ref monitorFolder, value); }
        public bool EnableMonitoring { get => enableMonitoring; set => SetValue(ref enableMonitoring, value); }

        // Hotkey properties
        public bool EnableHotkey { get => enableHotkey; set => SetValue(ref enableHotkey, value); }
        public Key HotkeyKey { get => hotkeyKey; set => SetValue(ref hotkeyKey, value); }
        public bool HotkeyCtrl { get => hotkeyCtrl; set => SetValue(ref hotkeyCtrl, value); }
        public bool HotkeyAlt { get => hotkeyAlt; set => SetValue(ref hotkeyAlt, value); }
        public bool HotkeyShift { get => hotkeyShift; set => SetValue(ref hotkeyShift, value); }
        public bool HotkeySuppressKey { get => hotkeySuppressKey; set => SetValue(ref hotkeySuppressKey, value); }

        // Per-library hotkey enable property - exposed as dictionary
        public Dictionary<Guid, bool> HotkeyEnabledByLibrary
        {
            get => hotkeyEnabledByLibrary;
            set => SetValue(ref hotkeyEnabledByLibrary, value);
        }

        // Helper method to check if hotkey is enabled for a specific library
        public bool IsHotkeyEnabledForLibrary(Guid libraryId)
        {
            if (hotkeyEnabledByLibrary.TryGetValue(libraryId, out bool enabled))
            {
                return enabled;
            }
            return libraryId != Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
        }

        // Helper method to set hotkey enabled state for a specific library
        public void SetHotkeyEnabledForLibrary(Guid libraryId, bool enabled)
        {
            hotkeyEnabledByLibrary[libraryId] = enabled;
            OnPropertyChanged(nameof(HotkeyEnabledByLibrary));
        }

        // Helper method to get a formatted display string for the current hotkey
        public string GetHotkeyDisplayString()
        {
            var parts = new List<string>();
            if (hotkeyCtrl) parts.Add("Ctrl");
            if (hotkeyAlt) parts.Add("Alt");
            if (hotkeyShift) parts.Add("Shift");
            parts.Add(hotkeyKey.ToString());
            return string.Join(" + ", parts);
        }

        // ========== 截图条件设置 ==========

        private bool _allowBackgroundScreenshot = false;

        /// <summary>
        /// 是否允许游戏在后台时自动截图（默认禁用）
        /// </summary>
        public bool AllowBackgroundScreenshot
        {
            get => _allowBackgroundScreenshot;
            set => SetValue(ref _allowBackgroundScreenshot, value);
        }

        // ========== 截图后缀设置 ==========

        private string _autoScreenshotSuffix = "";
        private string _manualScreenshotSuffix = "";

        /// <summary>
        /// 自动截图额外后缀，默认为空
        /// </summary>
        public string AutoScreenshotSuffix
        {
            get => _autoScreenshotSuffix;
            set => SetValue(ref _autoScreenshotSuffix, value ?? "");
        }

        /// <summary>
        /// 手动截图额外后缀，默认为空
        /// </summary>
        public string ManualScreenshotSuffix
        {
            get => _manualScreenshotSuffix;
            set => SetValue(ref _manualScreenshotSuffix, value ?? "");
        }

        // ========== 通知设置 ==========

        private bool _enableNotifications = true;
        private bool _enableAutoScreenshotNotification = true;
        private bool _enableManualScreenshotNotification = true;
        private NotificationStyles _notificationStyle = NotificationStyles.Toast;

        public bool EnableNotifications
        {
            get => _enableNotifications;
            set => SetValue(ref _enableNotifications, value);
        }

        public bool EnableAutoScreenshotNotification
        {
            get => _enableAutoScreenshotNotification;
            set => SetValue(ref _enableAutoScreenshotNotification, value);
        }

        public bool EnableManualScreenshotNotification
        {
            get => _enableManualScreenshotNotification;
            set => SetValue(ref _enableManualScreenshotNotification, value);
        }

        public NotificationStyles NotificationStyle
        {
            get => _notificationStyle;
            set => SetValue(ref _notificationStyle, value);
        }

        [DontSerialize]
        public System.Collections.Generic.List<NotificationStyleItem> NotificationStyleOptions { get; } = new System.Collections.Generic.List<NotificationStyleItem>
        {
            new NotificationStyleItem { Value = NotificationStyles.Toast, DisplayName = "Windows 通知" },
            new NotificationStyleItem { Value = NotificationStyles.Playnite, DisplayName = "Playnite 通知" }
        };
    }

    // 通知样式项类
    public class NotificationStyleItem
    {
        public NotificationStyles Value { get; set; }
        public string DisplayName { get; set; }
    }

    public class SharpMemoriesSettingsViewModel : ObservableObject, ISettings
    {
        private readonly SharpMemories plugin;
        private SharpMemoriesSettings editingClone { get; set; }

        private SharpMemoriesSettings settings;
        public SharpMemoriesSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HotkeyDisplayString));
                OnPropertyChanged(nameof(IsTestMode));
            }
        }

        private List<LibraryPluginInfo> libraryPlugins;
        public List<LibraryPluginInfo> LibraryPlugins
        {
            get => libraryPlugins;
            set
            {
                libraryPlugins = value;
                OnPropertyChanged();
            }
        }

        private bool isRecordingHotkey = false;
        public bool IsRecordingHotkey
        {
            get => isRecordingHotkey;
            set
            {
                isRecordingHotkey = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RecordButtonText));
            }
        }

        public string RecordButtonText => IsRecordingHotkey ? "Press a key combination..." : "Record Hotkey";

        public string HotkeyDisplayString => Settings?.GetHotkeyDisplayString() ?? "None";

        /// <summary>
        /// 是否为测试模式（间隔为 0 时进入测试模式）
        /// </summary>
        public bool IsTestMode => Settings?.IntervalMinutes == 0;

        public SharpMemoriesSettingsViewModel(SharpMemories plugin)
        {
            this.plugin = plugin;

            var savedSettings = plugin.LoadPluginSettings<SharpMemoriesSettings>();

            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                Settings = new SharpMemoriesSettings();
                var steamFolder = SteamHelpers.GetSteamScreenshotFolder();
                Settings.MonitorFolder = steamFolder ?? string.Empty;
            }

            // 👇 订阅 PropertyChanged 事件，当 IntervalMinutes 变化时更新 IsTestMode
            Settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Settings.IntervalMinutes))
                {
                    OnPropertyChanged(nameof(IsTestMode));
                }
            };
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);

            try
            {
                var plugins = plugin.PlayniteApi.Addons.Plugins.OfType<LibraryPlugin>().ToList();
                LibraryPlugins = plugins
                    .OrderBy(p => p.Name)
                    .Select(p => new LibraryPluginInfo
                    {
                        Id = p.Id,
                        Name = p.Name,
                        IsHotkeyEnabled = Settings.IsHotkeyEnabledForLibrary(p.Id)
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Error(ex, "Failed to enumerate library plugins");
                LibraryPlugins = new List<LibraryPluginInfo>();
            }
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            if (LibraryPlugins != null)
            {
                foreach (var libraryPlugin in LibraryPlugins)
                {
                    Settings.SetHotkeyEnabledForLibrary(libraryPlugin.Id, libraryPlugin.IsHotkeyEnabled);
                }
            }
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }

        public void UpdateHotkey(Key key, bool ctrl, bool alt, bool shift)
        {
            Settings.HotkeyKey = key;
            Settings.HotkeyCtrl = ctrl;
            Settings.HotkeyAlt = alt;
            Settings.HotkeyShift = shift;
            OnPropertyChanged(nameof(HotkeyDisplayString));
        }
    }
}