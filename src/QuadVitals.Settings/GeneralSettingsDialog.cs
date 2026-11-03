using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 登录自启开关。挂在标题栏齿轮上（反馈气泡左边），和网速卡、设备状态卡的齿轮一样是就地弹窗。
    ///
    /// 这一页原本是侧栏里的「通用设置」，只剩一个开关，占一整页就为了一个复选框。弹窗里
    /// 改动即写盘：没有保存按钮，也没有草稿，关掉面板不会留下没提交的改动。
    /// </summary>
    internal sealed class GeneralSettingsDialog
    {
        public const double DialogWidth = 640;
        public const double DialogHeight = 427;

        private readonly SettingsWindow _window;

        public GeneralSettingsDialog(SettingsWindow window)
        {
            _window = window;
        }

        public FrameworkElement Build()
        {
            AppSettings settings = _window.CurrentSettings.Clone();

            Border panel = new Border
            {
                Width = DialogWidth,
                // 与网速卡、设备状态卡的齿轮弹窗同一块板（640×427，3:2）：后面还要往这里加设置项，
                // 高度一开始就留够，不再一项一项把面板拉长。
                Height = DialogHeight,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true
            };
            AutomationProperties.SetAutomationId(panel, "GeneralSettingsPanel");

            // 标题贴顶、按钮贴底，中间整块留白给后续的开关/下拉。
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "通用设置",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(title, "GeneralSettingsTitle");
            grid.Children.Add(title);

            StackPanel body = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

            CheckBox autostart = new DarkCheckBox
            {
                Content = "登录时自动启动传感器（计划任务，最高权限）",
                IsChecked = settings.StartAgentWithWindows
            };
            AutomationProperties.SetAutomationId(autostart, "GeneralAutostart");
            // Subscribe after the initial value: assigning IsChecked above must not write a file.
            autostart.Checked += (sender, e) => SaveStartWithWindows(true);
            autostart.Unchecked += (sender, e) => SaveStartWithWindows(false);
            body.Children.Add(autostart);

            body.Children.Add(new TextBlock
            {
                Text = "仅保存偏好；计划任务需执行 scripts\\install-agent-task.ps1 创建（需一次管理员授权）。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                // Aligned under the checkbox caption (18 px square + 9 px gap + 1 px), not the square.
                Margin = new Thickness(28, 6, 0, 0)
            });
            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            FlatButton close = new FlatButton("关闭");
            AutomationProperties.SetAutomationId(close, "GeneralClose");
            close.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            return panel;
        }

        /// <summary>
        /// Writes just this field onto the latest settings (adapter / refresh rate / elements may
        /// have changed elsewhere since the panel was built) and saves silently.
        /// </summary>
        private void SaveStartWithWindows(bool value)
        {
            AppSettings latest = _window.CurrentSettings.Clone();
            if (latest.StartAgentWithWindows == value) return;
            latest.StartAgentWithWindows = value;
            latest.Normalize();
            _window.SaveSettingsQuietly(latest);
        }
    }
}
