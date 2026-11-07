using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
        public const double DialogWidth = 613;
        /// <summary>面板高度：当前三行只占一半，余下留给后加的设置项。</summary>
        public const double DialogHeight = 377;
        /// <summary>面板内边距；行宽与按钮位置都从这里算。</summary>
        public const double PanelPadding = 24;
        /// <summary>行左侧标签列宽：四个字 + 余量，保证各行控件从同一条竖线起。</summary>
        public const double LabelColumnWidth = 96;

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
                // 三个设置项的短列表：宽度只给到“标签 + 控件”需要的量，高度随内容收，
                // 不再用 640×427 那块表单板把内容摊成半张空桌子。
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                // 比内容高一截：多出来的地方是给后来要加的设置项留的，按钮贴底不动。
                Height = DialogHeight,
                Padding = new Thickness(PanelPadding),
                SnapsToDevicePixels = true
            };
            AutomationProperties.SetAutomationId(panel, "GeneralSettingsPanel");

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "通用设置",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(title, "GeneralSettingsTitle");
            grid.Children.Add(title);

            StackPanel body = new StackPanel
            {
                Margin = new Thickness(0, 14, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };

            // 设置项按组排：组标题四个字；开关行把勾选框放最前，紧接四字标签，后面补一句说明；
            // 下拉行是四字标签 + 撑满的下拉。
            body.Children.Add(GroupHeader("启动设置", "GeneralGroupStart", false));

            CheckBox autostart = new DarkCheckBox
            {
                IsChecked = settings.StartAgentWithWindows
            };
            AutomationProperties.SetAutomationId(autostart, "GeneralAutostart");
            AutomationProperties.SetName(autostart, "开机自启");
            body.Children.Add(CheckRow("开机自启", autostart, "登录系统后自动启动传感器", "GeneralAutostartLabel", "GeneralAutostartHint"));

            body.Children.Add(GroupHeader("性能设置", "GeneralGroupPerformance", true));

            CheckBox battery = new DarkCheckBox
            {
                IsChecked = settings.PauseAnimationsOnBattery != false
            };
            AutomationProperties.SetAutomationId(battery, "GeneralBatteryAnimations");
            AutomationProperties.SetName(battery, "电池暂停");
            body.Children.Add(CheckRow("电池暂停", battery, "电池供电时暂停动画，降低占用", "GeneralBatteryAnimationsLabel", "GeneralBatteryAnimationsHint"));

            // 渲染时机是独立的一档：它不改变风扇的 hover/always 动效，只决定仪表盘在失焦或
            // 前台全屏时是否继续逐帧重绘。
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
            body.Children.Add(FieldRow("渲染策略", renderPolicy, "GeneralRenderPolicyLabel"));

            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
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

        /// <summary>
        /// 组的标题：四个字，字号比行标签小一号但字重更重，右侧一根细线把这一组和上面隔开。
        /// 只用留白分组时，两组在缩小看时连成一片；线条给出边界，又不占额外一行的位置。
        /// </summary>
        private static FrameworkElement GroupHeader(string text, string automationId, bool separated)
        {
            Grid header = new Grid
            {
                Margin = new Thickness(0, separated ? 16 : 0, 0, 8)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock caption = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                // 三级灰在 #303030 上只有 2.6:1，小字号读不清；用二级灰（4.1:1），
                // 和面板里其他说明行同一个灰度。
                Foreground = Theme.TextSecondaryBrush
            };
            AutomationProperties.SetAutomationId(caption, automationId);
            header.Children.Add(caption);

            Border rule = new Border
            {
                Height = 1,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Theme.BorderSubtleBrush,
                SnapsToDevicePixels = true
            };
            AutomationProperties.SetAutomationId(rule, automationId + "Rule");
            Grid.SetColumn(rule, 1);
            header.Children.Add(rule);
            return header;
        }

        /// <summary>勾选框列的宽度：勾选框在最左，标签与下拉行都从这条竖线之后开始。</summary>
        private const double CheckColumnWidth = 22;
        /// <summary>勾选框与标签之间的间距。</summary>
        private const double CheckGap = 10;

        /// <summary>
        /// 开关行：勾选框在前、四字标签紧跟、后面一句说明。
        /// 勾选框放最前是为了让“能点的东西”对齐成一条竖线；说明放在同一行末尾，
        /// 不另起一行——三行设置多出三行解释会把这张小单子撑成一段散文。
        /// </summary>
        private static FrameworkElement CheckRow(string label, CheckBox check, string hint,
                                                 string labelAutomationId, string hintAutomationId)
        {
            Grid row = new Grid { Height = 34, Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CheckColumnWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            check.VerticalAlignment = VerticalAlignment.Center;
            check.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(check);

            TextBlock caption = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(CheckGap, 0, 0, 0)
            };
            AutomationProperties.SetAutomationId(caption, labelAutomationId);
            Grid.SetColumn(caption, 1);
            row.Children.Add(caption);

            TextBlock note = new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            AutomationProperties.SetAutomationId(note, hintAutomationId);
            Grid.SetColumn(note, 2);
            row.Children.Add(note);
            return row;
        }

        /// <summary>下拉行：四字标签从勾选框列之后开始，控件撑满余下宽度。</summary>
        private static FrameworkElement FieldRow(string label, FrameworkElement control, string labelAutomationId)
        {
            Grid row = new Grid { Height = 34, Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CheckColumnWidth + CheckGap) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock caption = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(caption, labelAutomationId);
            Grid.SetColumn(caption, 1);
            row.Children.Add(caption);

            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(20, 0, 0, 0);
            Grid.SetColumn(control, 2);
            row.Children.Add(control);
            return row;
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
