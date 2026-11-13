using System;
using System.Linq;
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
        // Resolve localization keys from the same resource dictionaries used by XAML.
        // Fallback strings keep diagnostics available if a language pack is missing a key.
        private string Localized(string key, string fallback)
        {
            return TryFindResource(key) as string ?? fallback;
        }

        private void InstallAdvancedDiagnosticsPanel()
        {
            var originalContent = Content as System.Windows.UIElement;
            if (originalContent == null) return;

            Content = null;
            var dock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            var advanced = new System.Windows.Controls.Expander
            {
                Header = Localized("LOCSharpMemories_Diagnostics_Header", "高级诊断（一般无需操作）"),
                IsExpanded = false,
                Margin = new Thickness(8, 4, 8, 4)
            };
            var panel = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(8, 6, 8, 8)
            };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Localized("LOCSharpMemories_Diagnostics_Description", "仅用于测试截图失败占位图；会在截图目录创建一张黑底白字 ERROR 图片，不改变截图计时。"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var diagnosticButton = new System.Windows.Controls.Button
            {
                Content = Localized("LOCSharpMemories_Diagnostics_GenerateButton", "生成 ERROR 测试图片…"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(12, 5, 12, 5)
            };
            diagnosticButton.Click += GenerateErrorPlaceholder_Click;
            panel.Children.Add(diagnosticButton);
            advanced.Content = panel;
            System.Windows.Controls.DockPanel.SetDock(advanced, System.Windows.Controls.Dock.Bottom);
            dock.Children.Add(advanced);
            InstallFingerprintSettings(dock);
            dock.Children.Add(originalContent);
            Content = dock;
        }


        private System.Windows.Controls.ListBox fingerprintGameList;
        private System.Windows.Controls.TextBox fingerprintSearchBox;
        private System.Windows.Controls.ComboBox fingerprintModeBox;
        private System.Windows.Controls.ListBox fingerprintObservationList;
        private System.Windows.Controls.TextBox fingerprintExeText;
        private System.Windows.Controls.TextBox fingerprintPreferredTitleText;
        private System.Windows.Controls.TextBox fingerprintExcludedTitleText;
        private System.Windows.Controls.TextBox fingerprintClassText;
        private System.Windows.Controls.TextBlock fingerprintSaveStatus;
        private System.Collections.Generic.List<GameWindowFingerprint> fingerprintGames =
            new System.Collections.Generic.List<GameWindowFingerprint>();

        private System.Windows.Controls.TextBlock MakeFingerprintLabel(string key, string fallback)
        {
            return new System.Windows.Controls.TextBlock
            {
                Text = Localized(key, fallback), Margin = new Thickness(0, 8, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
        }
        private void InstallFingerprintSettings(System.Windows.Controls.DockPanel dock)
        {
            var expander = new System.Windows.Controls.Expander
            {
                Header = Localized("LOCSharpMemories_FP_Header", "每个游戏的进程和窗口指纹"),
                IsExpanded = false, Margin = new Thickness(8, 4, 8, 4)
            };
            var layout = new System.Windows.Controls.Grid { Margin = new Thickness(8) };
            layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(195), MinWidth = 145
            });
            layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(8)
            });
            layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star), MinWidth = 220
            });

            // Left: searchable, independently scrollable game list.
            var left = new System.Windows.Controls.DockPanel { LastChildFill = true };
            var leftTop = new System.Windows.Controls.StackPanel();
            leftTop.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Localized("LOCSharpMemories_FP_Games", "游戏列表"),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4)
            });
            fingerprintSearchBox = new System.Windows.Controls.TextBox
            {
                Margin = new Thickness(0, 0, 0, 6),
                ToolTip = Localized("LOCSharpMemories_FP_Search", "搜索游戏名称或 ID")
            };
            fingerprintSearchBox.TextChanged += (sender, e) => FilterFingerprintGames();
            leftTop.Children.Add(fingerprintSearchBox);
            var reloadButton = new System.Windows.Controls.Button
            {
                Content = Localized("LOCSharpMemories_FP_Refresh", "刷新游戏列表"),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            reloadButton.Click += (sender, e) => RefreshFingerprintList();
            leftTop.Children.Add(reloadButton);
            System.Windows.Controls.DockPanel.SetDock(leftTop, System.Windows.Controls.Dock.Top);
            left.Children.Add(leftTop);
            fingerprintGameList = new System.Windows.Controls.ListBox
            {
                MinHeight = 200,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            System.Windows.Controls.ScrollViewer.SetVerticalScrollBarVisibility(
                fingerprintGameList, System.Windows.Controls.ScrollBarVisibility.Auto);
            fingerprintGameList.SelectionChanged += (sender, e) => LoadSelectedFingerprint();
            left.Children.Add(fingerprintGameList);
            var leftBorder = new System.Windows.Controls.Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6), Child = left
            };
            System.Windows.Controls.Grid.SetColumn(leftBorder, 0);
            layout.Children.Add(leftBorder);

            // Right: mode is always the first setting, followed by selected-game rules.
            var right = new System.Windows.Controls.StackPanel { Margin = new Thickness(2, 0, 2, 4) };
            right.Children.Add(MakeFingerprintLabel("LOCSharpMemories_FP_Mode", "匹配模式"));
            fingerprintModeBox = new System.Windows.Controls.ComboBox
            {
                SelectedValuePath = "Tag", Margin = new Thickness(0, 2, 0, 6)
            };
            fingerprintModeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = Localized("LOCSharpMemories_FP_ModeNone", "无（普通自动识别）"), Tag = 0
            });
            fingerprintModeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = Localized("LOCSharpMemories_FP_ModePreferred", "优先模式（有匹配窗口则优先；否则自动识别）"), Tag = 1
            });
            fingerprintModeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = Localized("LOCSharpMemories_FP_ModeForced", "强制模式（只使用匹配窗口）"), Tag = 2
            });
            fingerprintModeBox.SelectedIndex = 0;
            right.Children.Add(fingerprintModeBox);
            right.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Localized("LOCSharpMemories_FP_ModeHint", "优先模式若匹配窗口存在但不在前台，将等待该窗口；只有不存在匹配窗口时才回退自动识别。"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 6)
            });
            right.Children.Add(MakeFingerprintLabel("LOCSharpMemories_FP_Observations", "已记录窗口（可选中并设为首选）"));
            fingerprintObservationList = new System.Windows.Controls.ListBox { Height = 112 };
            // Samples belong to the right-hand settings scroller, not a separate
            // nested scroll area. The left game list remains independently scrollable.
            System.Windows.Controls.ScrollViewer.SetVerticalScrollBarVisibility(
                fingerprintObservationList, System.Windows.Controls.ScrollBarVisibility.Disabled);
            right.Children.Add(fingerprintObservationList);
            var useSample = new System.Windows.Controls.Button
            {
                Content = Localized("LOCSharpMemories_FP_UseSample", "使用选中窗口作为首选"),
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            useSample.Click += (sender, e) =>
            {
                var sample = fingerprintObservationList.SelectedItem as GameWindowObservation;
                if (sample == null) return;
                fingerprintExeText.Text = sample.ExecutablePath ?? "";
                fingerprintPreferredTitleText.Text = sample.WindowTitle ?? "";
                fingerprintClassText.Text = sample.WindowClass ?? "";
            };
            right.Children.Add(useSample);
            var excludeSample = new System.Windows.Controls.Button
            {
                Content = Localized("LOCSharpMemories_FP_ExcludeSample", "将选中窗口标记为启动器并排除"),
                Margin = new Thickness(0, 1, 0, 5),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            excludeSample.Click += (sender, e) =>
            {
                var sample = fingerprintObservationList.SelectedItem as GameWindowObservation;
                if (sample != null) fingerprintExcludedTitleText.Text = sample.WindowTitle ?? "";
            };
            right.Children.Add(excludeSample);
            right.Children.Add(MakeFingerprintLabel("LOCSharpMemories_FP_Exe", "首选可执行文件完整路径（可留空）"));
            fingerprintExeText = new System.Windows.Controls.TextBox(); right.Children.Add(fingerprintExeText);
            right.Children.Add(MakeFingerprintLabel("LOCSharpMemories_FP_Title", "正式游戏窗口标题包含（推荐填写）"));
            fingerprintPreferredTitleText = new System.Windows.Controls.TextBox(); right.Children.Add(fingerprintPreferredTitleText);
            right.Children.Add(MakeFingerprintLabel("LOCSharpMemories_FP_Exclude", "排除的启动器窗口标题包含（可选）"));
            fingerprintExcludedTitleText = new System.Windows.Controls.TextBox(); right.Children.Add(fingerprintExcludedTitleText);
            right.Children.Add(MakeFingerprintLabel("LOCSharpMemories_FP_Class", "首选窗口类名（可选）"));
            fingerprintClassText = new System.Windows.Controls.TextBox(); right.Children.Add(fingerprintClassText);
            var saveButton = new System.Windows.Controls.Button
            {
                Content = Localized("LOCSharpMemories_FP_Save", "保存该游戏的指纹规则"),
                Margin = new Thickness(0, 12, 0, 4),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(8, 3, 8, 3)
            };
            saveButton.Click += (sender, e) => SaveSelectedFingerprint();
            right.Children.Add(saveButton);
            fingerprintSaveStatus = new System.Windows.Controls.TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            right.Children.Add(fingerprintSaveStatus);
            right.Children.Add(MakeFingerprintLabel("LOCSharpMemories_FP_File",
                "指纹保存为插件数据目录中的 GameWindowFingerprints.xml，可手动备份或编辑。"));
            var rightScroll = new System.Windows.Controls.ScrollViewer
            {
                Content = right, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            // WPF ListBox handles MouseWheel internally even when it cannot scroll.
            // Forward the wheel to the parent once, so hovering the sample list
            // scrolls the entire right-hand fingerprint settings panel.
            fingerprintObservationList.PreviewMouseWheel += (sender, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                var forwarded = new System.Windows.Input.MouseWheelEventArgs(
                    e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = System.Windows.UIElement.MouseWheelEvent,
                    Source = rightScroll
                };
                rightScroll.RaiseEvent(forwarded);
            };
            var rightBorder = new System.Windows.Controls.Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7), Child = rightScroll
            };
            System.Windows.Controls.Grid.SetColumn(rightBorder, 2);
            layout.Children.Add(rightBorder);
            expander.Content = new System.Windows.Controls.Border
            {
                Child = layout, Height = 400
            };
            System.Windows.Controls.DockPanel.SetDock(expander, System.Windows.Controls.Dock.Bottom);
            dock.Children.Add(expander);
            expander.Expanded += (sender, e) => RefreshFingerprintList();
        }
        private void RefreshFingerprintList()
        {
            if (fingerprintGameList == null) return;
            fingerprintGames = GameFingerprintStore.Current?.GetAll() ??
                new System.Collections.Generic.List<GameWindowFingerprint>();
            FilterFingerprintGames();
        }
        private void FilterFingerprintGames()
        {
            if (fingerprintGameList == null) return;
            string selectedId = (fingerprintGameList.SelectedItem as GameWindowFingerprint)?.GameId;
            string query = fingerprintSearchBox?.Text?.Trim() ?? "";
            var filtered = fingerprintGames.Where(x => string.IsNullOrWhiteSpace(query) ||
                (x.GameName ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (x.GameId ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            fingerprintGameList.ItemsSource = filtered;
            fingerprintGameList.SelectedItem = filtered.FirstOrDefault(x => x.GameId == selectedId) ??
                filtered.FirstOrDefault();
        }
        private void LoadSelectedFingerprint()
        {
            var rule = fingerprintGameList?.SelectedItem as GameWindowFingerprint;
            if (fingerprintPreferredTitleText == null) return;
            fingerprintExeText.Text = rule?.PreferredExecutablePath ?? "";
            fingerprintPreferredTitleText.Text = rule?.PreferredTitleContains ?? "";
            fingerprintExcludedTitleText.Text = rule?.ExcludedTitleContains ?? "";
            fingerprintClassText.Text = rule?.PreferredWindowClass ?? "";
            fingerprintModeBox.SelectedIndex = rule == null ? 0 : Math.Max(0, Math.Min(2, rule.MatchMode));
            fingerprintObservationList.ItemsSource = rule?.Observations;
            if (fingerprintSaveStatus != null) fingerprintSaveStatus.Text = "";
        }
        private void SaveSelectedFingerprint()
        {
            var rule = fingerprintGameList?.SelectedItem as GameWindowFingerprint;
            if (rule == null) return;
            // Do not modify the selected clone until input passes validation.
            int mode = fingerprintModeBox.SelectedIndex;
            if (mode != 0 && string.IsNullOrWhiteSpace(fingerprintPreferredTitleText.Text) &&
                string.IsNullOrWhiteSpace(fingerprintClassText.Text))
            {
                fingerprintSaveStatus.Text = Localized("LOCSharpMemories_FP_NeedTitle",
                    "优先或强制模式需要填写正式游戏窗口标题或窗口类名。");
                return;
            }
            rule.PreferredExecutablePath = fingerprintExeText.Text;
            rule.PreferredTitleContains = fingerprintPreferredTitleText.Text;
            rule.ExcludedTitleContains = fingerprintExcludedTitleText.Text;
            rule.PreferredWindowClass = fingerprintClassText.Text;
            rule.MatchMode = mode;
            bool saved = GameFingerprintStore.Current?.UpdateRules(rule) == true;
            fingerprintSaveStatus.Text = saved
                ? Localized("LOCSharpMemories_FP_Saved", "已保存。识别规则立即生效。")
                : Localized("LOCSharpMemories_FP_SaveFailed", "保存失败，请检查插件数据目录写入权限。");
            if (saved)
            {
                string selectedId = rule.GameId;
                fingerprintGames = GameFingerprintStore.Current.GetAll();
                var selected = fingerprintGames.FirstOrDefault(x => x.GameId == selectedId);
                // Keep the scroll position and selected game; do not rebind the list.
                if (selected != null)
                {
                    rule.MatchMode = selected.MatchMode;
                }
            }
        }

        private void GenerateErrorPlaceholder_Click(object sender, RoutedEventArgs e)
        {
            var answer = System.Windows.MessageBox.Show(
                Localized("LOCSharpMemories_Diagnostics_Confirmation", "此操作会在截图目录生成一张黑底白字的 ERROR 测试图，并触发错误提示（如果可用）。\n\n不会真正截取游戏画面，也不会改变自动截图倒计时。\n\n确定继续？"),
                Localized("LOCSharpMemories_Diagnostics_ConfirmationTitle", "SharpMemories - 高级诊断确认"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                var vm = DataContext as SharpMemoriesSettingsViewModel;
                if (vm == null)
                {
                    System.Windows.MessageBox.Show(Localized("LOCSharpMemories_Diagnostics_NoViewModel", "当前设置界面尚未绑定配置对象。"), "SharpMemories");
                    return;
                }
                string path = vm.GenerateDiagnosticErrorPlaceholder();
                System.Windows.MessageBox.Show(
                    string.IsNullOrWhiteSpace(path)
                        ? Localized("LOCSharpMemories_Diagnostics_Failed", "创建失败，请查看 Playnite 扩展日志（extensions.log）。")
                        : Localized("LOCSharpMemories_Diagnostics_Success", "已生成 ERROR 测试图片：\n") + path,
                    Localized("LOCSharpMemories_Diagnostics_ResultTitle", "SharpMemories - 诊断结果"),
                    MessageBoxButton.OK,
                    string.IsNullOrWhiteSpace(path) ? MessageBoxImage.Error : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(Localized("LOCSharpMemories_Diagnostics_Exception", "生成失败：") + ex.Message,
                    Localized("LOCSharpMemories_Diagnostics_ErrorTitle", "SharpMemories - 诊断错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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