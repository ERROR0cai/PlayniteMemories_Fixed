using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;

namespace SharpMemories
{
    public partial class SharpMemoriesSettingsView : System.Windows.Controls.UserControl
    {
        public SharpMemoriesSettingsView()
        {
            InitializeComponent();
            InstallAdvancedDiagnosticsPanel();
        }

        // Installed in code-behind so no unprovided XAML file needs modification.
        // A collapsed, bottom-docked section keeps the destructive-looking test
        // out of the normal settings workflow. The action requires confirmation.
        private void InstallAdvancedDiagnosticsPanel()
        {
            var originalContent = Content as System.Windows.UIElement;
            if (originalContent == null) return;

            Content = null;
            var dock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            var advanced = new System.Windows.Controls.Expander
            {
                Header = "高级诊断（一般无需操作）",
                IsExpanded = false,
                Margin = new Thickness(8, 4, 8, 4)
            };
            var panel = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(8, 6, 8, 8)
            };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "仅用于测试截图失败占位图；会在截图目录创建一张黑底白字 ERROR 图片，不改变截图计时。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var diagnosticButton = new System.Windows.Controls.Button
            {
                Content = "生成 ERROR 测试图片…",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(12, 5, 12, 5)
            };
            diagnosticButton.Click += GenerateErrorPlaceholder_Click;
            panel.Children.Add(diagnosticButton);
            advanced.Content = panel;
            System.Windows.Controls.DockPanel.SetDock(advanced, System.Windows.Controls.Dock.Bottom);
            dock.Children.Add(advanced);
            dock.Children.Add(originalContent);
            Content = dock;
        }

        private void GenerateErrorPlaceholder_Click(object sender, RoutedEventArgs e)
        {
            var answer = System.Windows.MessageBox.Show(
                "此操作会在截图目录生成一张黑底白字的 ERROR 测试图，并触发错误提示（如果可用）。\n\n不会真正截取游戏画面，也不会改变自动截图倒计时。\n\n确定继续？",
                "SharpMemories - 高级诊断确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                var vm = DataContext as SharpMemoriesSettingsViewModel;
                if (vm == null)
                {
                    System.Windows.MessageBox.Show("当前设置界面尚未绑定配置对象。", "SharpMemories");
                    return;
                }
                string path = vm.GenerateDiagnosticErrorPlaceholder();
                System.Windows.MessageBox.Show(
                    string.IsNullOrWhiteSpace(path)
                        ? "创建失败，请查看 Playnite 扩展日志（extensions.log）。"
                        : "已生成 ERROR 测试图片：\n" + path,
                    "SharpMemories - 诊断结果",
                    MessageBoxButton.OK,
                    string.IsNullOrWhiteSpace(path) ? MessageBoxImage.Error : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("生成失败：" + ex.Message,
                    "SharpMemories - 诊断错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RecordHotkey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var vm = DataContext as SharpMemoriesSettingsViewModel;
                if (vm == null) return;

                // Toggle recording state
                vm.IsRecordingHotkey = !vm.IsRecordingHotkey;

                if (vm.IsRecordingHotkey)
                {
                    // Start listening for key presses
                    this.PreviewKeyDown += OnRecordingKeyDown;
                    this.Focus();
                }
                else
                {
                    // Stop listening
                    this.PreviewKeyDown -= OnRecordingKeyDown;
                }
            }
            catch (Exception)
            {
                // ignore UI errors
            }
        }

        private void OnRecordingKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            try
            {
                var vm = DataContext as SharpMemoriesSettingsViewModel;
                if (vm == null || !vm.IsRecordingHotkey) return;

                // Get the actual key (not modifier keys)
                var key = e.Key == Key.System ? e.SystemKey : e.Key;

                // Ignore modifier keys by themselves
                if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                    key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift ||
                    key == Key.LWin || key == Key.RWin)
                {
                    e.Handled = true;
                    return;
                }

                // Get modifier states
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

                // Update the hotkey
                vm.UpdateHotkey(key, ctrl, alt, shift);

                // Stop recording
                vm.IsRecordingHotkey = false;
                this.PreviewKeyDown -= OnRecordingKeyDown;

                e.Handled = true;
            }
            catch (Exception)
            {
                // ignore UI errors
            }
        }

        private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var vm = DataContext as SharpMemoriesSettingsViewModel;
                if (vm == null) return;

                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Select output folder for screenshots";
                    if (!string.IsNullOrWhiteSpace(vm.Settings.OutputFolder))
                    {
                        try { dlg.SelectedPath = vm.Settings.OutputFolder; } catch { }
                    }

                    var result = dlg.ShowDialog();
                    if (result == DialogResult.OK || result == DialogResult.Yes)
                    {
                        vm.Settings.OutputFolder = dlg.SelectedPath;
                    }
                }
            }
            catch (Exception)
            {
                // ignore UI errors
            }
        }

        private void BrowseMonitorFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var vm = DataContext as SharpMemoriesSettingsViewModel;
                if (vm == null) return;

                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Select folder to monitor for screenshots";
                    if (!string.IsNullOrWhiteSpace(vm.Settings.MonitorFolder))
                    {
                        try { dlg.SelectedPath = vm.Settings.MonitorFolder; } catch { }
                    }

                    var result = dlg.ShowDialog();
                    if (result == DialogResult.OK || result == DialogResult.Yes)
                    {
                        vm.Settings.MonitorFolder = dlg.SelectedPath;
                    }
                }
            }
            catch (Exception)
            {
                // ignore UI errors
            }
        }


        // ========== 自动截图黑名单 ==========

        private void AddAutoBlacklist_Click(
            object sender,
            RoutedEventArgs e)
        {
            var vm = DataContext as SharpMemoriesSettingsViewModel;

            if (vm == null)
                return;

            string input = AutoBlacklistInput.Text;

            string normalized =
                SharpMemoriesSettings.NormalizeProcessName(input);

            if (string.IsNullOrEmpty(normalized))
                return;

            vm.AddAutoBlacklistItem(normalized);

            AutoBlacklistInput.Clear();
        }

        private void RemoveAutoBlacklist_Click(
            object sender,
            RoutedEventArgs e)
        {
            var vm = DataContext as SharpMemoriesSettingsViewModel;

            if (vm == null)
                return;

            string selected =
                AutoBlacklistList.SelectedItem as string;

            if (string.IsNullOrEmpty(selected))
                return;

            vm.AutoBlacklistItems.Remove(selected);
        }

        // ========== 手动截图黑名单 ==========

        private void AddManualBlacklist_Click(
            object sender,
            RoutedEventArgs e)
        {
            var vm = DataContext as SharpMemoriesSettingsViewModel;

            if (vm == null)
                return;

            string input = ManualBlacklistInput.Text;

            string normalized =
                SharpMemoriesSettings.NormalizeProcessName(input);

            if (string.IsNullOrEmpty(normalized))
                return;

            vm.AddManualBlacklistItem(normalized);

            ManualBlacklistInput.Clear();
        }

        private void RemoveManualBlacklist_Click(
            object sender,
            RoutedEventArgs e)
        {
            var vm = DataContext as SharpMemoriesSettingsViewModel;

            if (vm == null)
                return;

            string selected =
                ManualBlacklistList.SelectedItem as string;

            if (string.IsNullOrEmpty(selected))
                return;

            vm.ManualBlacklistItems.Remove(selected);
        }
        private void AddAutoNotification_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SharpMemoriesSettingsViewModel;
            if (vm == null) return;
            vm.AddAutoNotificationItem(AutoNotificationInput.Text);
            AutoNotificationInput.Clear();
        }

        private void RemoveAutoNotification_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SharpMemoriesSettingsViewModel;
            var name = AutoNotificationList.SelectedItem as string;
            if (vm != null && name != null) vm.AutoNotificationItems.Remove(name);
        }

        private void AddManualNotification_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SharpMemoriesSettingsViewModel;
            if (vm == null) return;
            vm.AddManualNotificationItem(ManualNotificationInput.Text);
            ManualNotificationInput.Clear();
        }

        private void RemoveManualNotification_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SharpMemoriesSettingsViewModel;
            var name = ManualNotificationList.SelectedItem as string;
            if (vm != null && name != null) vm.ManualNotificationItems.Remove(name);
        }
    }
}