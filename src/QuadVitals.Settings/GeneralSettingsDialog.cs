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
            // 面板里的改动要先点「保存」才生效，与网速卡/风扇卡/设备卡一致。
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
                Content = "登录时自动启动传感器",
                IsChecked = settings.StartAgentWithWindows
            };
            AutomationProperties.SetAutomationId(autostart, "GeneralAutostart");
            // 只是草稿：勾选框本身不写盘。
            body.Children.Add(autostart);

            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            FlatButton cancel = new FlatButton("取消");
            AutomationProperties.SetAutomationId(cancel, "GeneralClose");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(cancel);

            FlatButton save = new FlatButton("保存", true);
            AutomationProperties.SetAutomationId(save, "GeneralSave");
            save.Click += (sender, e) => SavePanel(autostart);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            return panel;
        }

        /// <summary>
        /// Writes just this field onto the latest settings (adapter / refresh rate / elements may
        /// have changed elsewhere since the panel was built) and saves silently.
        /// </summary>
        /// <summary>把面板里的草稿写回最新设置（别处可能已经改过网卡/刷新等字段）。</summary>
        private void SavePanel(CheckBox autostart)
        {
            AppSettings latest = _window.CurrentSettings.Clone();
            bool wanted = autostart.IsChecked == true;
            _window.CloseOverlay();
            if (latest.StartAgentWithWindows == wanted) return;
            latest.StartAgentWithWindows = wanted;
            latest.Normalize();
            _window.SaveSettingsQuietly(latest);
        }
    }
}
