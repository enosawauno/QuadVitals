using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 登录自启与电池动画偏好。挂在标题栏齿轮上（反馈气泡左边），和网速卡、设备状态卡的齿轮一样是就地弹窗。
    ///
    /// 面板用草稿 + 保存：勾选只改控件状态，点「保存」才写盘，取消/Esc 丢弃。
    /// </summary>
    internal sealed class GeneralSettingsDialog
    {
        public const double DialogWidth = 640;
        public const double DialogHeight = 427;

        private static readonly string[] RenderPolicyLabels =
        {
            "全局重绘", "失焦停止动画", "全屏停止动画"
        };

        private static readonly string[] RenderPolicyKeys =
        {
            AppSettings.RenderPolicyAlways, AppSettings.RenderPolicyUnfocused,
            AppSettings.RenderPolicyFullscreen
        };

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

            CheckBox battery = new DarkCheckBox
            {
                Content = "电池模式下暂停动画",
                IsChecked = settings.PauseAnimationsOnBattery != false,
                Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetAutomationId(battery, "GeneralBatteryAnimations");
            body.Children.Add(battery);

            // 渲染时机是独立的一档：它不改变风扇的 hover/always 动效，只决定仪表盘在失焦或
            // 前台全屏时是否继续逐帧重绘。
            TextBlock renderLabel = new TextBlock
            {
                Text = "渲染策略",
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                Margin = new Thickness(0, 16, 0, 8)
            };
            AutomationProperties.SetAutomationId(renderLabel, "GeneralRenderPolicyLabel");
            body.Children.Add(renderLabel);

            DarkComboBox renderPolicy = new DarkComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(renderPolicy, "GeneralRenderPolicy");
            AutomationProperties.SetName(renderPolicy, "渲染策略");
            int selectedPolicy = 0;
            for (int i = 0; i < RenderPolicyLabels.Length; i++)
            {
                renderPolicy.Items.Add(RenderPolicyLabels[i]);
                if (string.Equals(RenderPolicyKeys[i], settings.RenderPolicy,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    selectedPolicy = i;
                }
            }
            renderPolicy.SelectedIndex = selectedPolicy;
            body.Children.Add(renderPolicy);

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
            save.Click += (sender, e) => SavePanel(autostart, battery, renderPolicy);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            return panel;
        }

        /// <summary>把面板里的草稿写回最新设置（别处可能已经改过网卡/刷新等字段）。</summary>
        private void SavePanel(CheckBox autostart, CheckBox battery, DarkComboBox renderPolicy)
        {
            AppSettings latest = _window.CurrentSettings.Clone();
            bool wantedAutostart = autostart.IsChecked == true;
            bool wantedBatteryPause = battery.IsChecked == true;
            string wantedPolicy = RenderPolicyKeyAt(renderPolicy.SelectedIndex);
            _window.CloseOverlay();
            if (latest.StartAgentWithWindows == wantedAutostart &&
                latest.PauseAnimationsOnBattery == wantedBatteryPause &&
                string.Equals(latest.RenderPolicy, wantedPolicy, StringComparison.OrdinalIgnoreCase)) return;
            latest.StartAgentWithWindows = wantedAutostart;
            latest.PauseAnimationsOnBattery = wantedBatteryPause;
            latest.RenderPolicy = wantedPolicy;
            latest.Normalize();
            _window.SaveSettingsQuietly(latest);
        }

        private static string RenderPolicyKeyAt(int index)
        {
            if (index < 0 || index >= RenderPolicyKeys.Length) return AppSettings.RenderPolicyAlways;
            return RenderPolicyKeys[index];
        }
    }
}
