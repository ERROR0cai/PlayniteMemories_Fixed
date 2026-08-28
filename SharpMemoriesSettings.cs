using Playnite.SDK;
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
        private int intervalMinutes = 15;
        private string outputFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Playnite");
        private string monitorFolder = string.Empty;
        private bool enableMonitoring = true;

        //  新增：重命名模式
        private string _renamePattern = "{game}_{date}_{time}_Gamesnap";
        public string RenamePattern
        {
            get => _renamePattern;
            set => SetValue(ref _renamePattern, value);
        }

        // 👇 新增：是否启用 ScreenshotsVisualizer 刷新
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
        private bool hotkeySuppressKey = true; // Prevent the key from being passed to the application

        // Per-library hotkey enable flags - dynamic dictionary keyed by library plugin ID
        private Dictionary<Guid, bool> hotkeyEnabledByLibrary = new Dictionary<Guid, bool>();

        public bool Enabled { get => enabled; set => SetValue(ref enabled, value); }
        public int IntervalMinutes { get => intervalMinutes; set => SetValue(ref intervalMinutes, value); }
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
            // If we have an explicit setting for this library, use it
            if (hotkeyEnabledByLibrary.TryGetValue(libraryId, out bool enabled))
            {
                return enabled;
            }

            // Steam library ID (CB91DFC9-B977-43BF-8E70-55F46E410FAB) - disable hotkey by default
            // Other libraries default to enabled
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

        // ========== 新增：截图后缀设置 ==========

        private string _autoScreenshotSuffix = "";   // 自动截图后缀（默认为空）
        private string _manualScreenshotSuffix = ""; // 手动截图后缀（默认为空）

        /// <summary>
        /// 自动截图额外后缀（例如 "_Auto"），默认为空
        /// </summary>
        public string AutoScreenshotSuffix
        {
            get => _autoScreenshotSuffix;
            set => SetValue(ref _autoScreenshotSuffix, value ?? "");  // null 转换为空字符串
        }

        /// <summary>
        /// 手动截图额外后缀（例如 "_Manual"），默认为空
        /// </summary>
        public string ManualScreenshotSuffix
        {
            get => _manualScreenshotSuffix;
            set => SetValue(ref _manualScreenshotSuffix, value ?? "");
        }

        // ========== 新增：通知设置 ==========

        private bool _enableNotifications = true;
        private bool _enableAutoScreenshotNotification = true;  // 👈 新增
        private bool _enableManualScreenshotNotification = true; // 👈 新增
        private NotificationStyles _notificationStyle = NotificationStyles.Toast;

        public bool EnableNotifications
        {
            get => _enableNotifications;
            set => SetValue(ref _enableNotifications, value);
        }

        // 👇 新增：自动截图通知开关
        public bool EnableAutoScreenshotNotification
        {
            get => _enableAutoScreenshotNotification;
            set => SetValue(ref _enableAutoScreenshotNotification, value);
        }

        // 👇 新增：手动截图通知开关
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

        // 👇 添加 [DontSerialize] 属性，防止被保存到 JSON
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

        public SharpMemoriesSettingsViewModel(SharpMemories plugin)
        {
            // Injecting your plugin instance is required for Save/Load method because Playnite saves data to a location based on what plugin requested the operation.
            this.plugin = plugin;

            // Load saved settings.
            var savedSettings = plugin.LoadPluginSettings<SharpMemoriesSettings>();

            // LoadPluginSettings returns null if no saved data is available.
            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                Settings = new SharpMemoriesSettings();
                // Set default monitor folder to Steam screenshot folder if available
                var steamFolder = SteamHelpers.GetSteamScreenshotFolder();
                Settings.MonitorFolder = steamFolder ?? string.Empty;
            }
        }

        public void BeginEdit()
        {
            // Code executed when settings view is opened and user starts editing values.
            editingClone = Serialization.GetClone(Settings);

            // Populate library plugins list from Playnite API
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
                // Log error and provide empty list as fallback
                LogManager.GetLogger().Error(ex, "Failed to enumerate library plugins");
                LibraryPlugins = new List<LibraryPluginInfo>();
            }
        }

        public void CancelEdit()
        {
            // Code executed when user decides to cancel any changes made since BeginEdit was called.
            // This method should revert any changes made to Option1 and Option2.
            Settings = editingClone;
        }

        public void EndEdit()
        {
            // Code executed when user decides to confirm changes made since BeginEdit was called.
            // Save the library plugin settings back to the dictionary
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
            // Code execute when user decides to confirm changes made since BeginEdit was called.
            // Executed before EndEdit is called and EndEdit is not called if false is returned.
            // List of errors is presented to user if verification fails.
            errors = new List<string>();
            return true;
        }

        // Update hotkey settings and refresh the display
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