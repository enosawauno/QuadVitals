using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 风扇卡的设置弹窗（入口是卡标题行右侧的齿轮）。
    ///
    /// 只有一项：「显示风扇」下拉——自动（第一路有读数的通道），或者指定某一路（CPU_FAN / AIO_PUMP …）。
    /// 与网速卡、设备状态卡的齿轮弹窗同一块板与同一条保存路径：下拉只改草稿，点「保存」才写
    /// settings.json 并关闭；「取消」丢弃。
    /// </summary>
    internal sealed class FanSettingsDialog
    {
        public const double DialogWidth = NetworkSettingsDialog.DialogWidth;
        public const double DialogHeight = NetworkSettingsDialog.DialogHeight;

        /// <summary>下拉的名字：弹窗、断言、文档、截图统一用这四个字。</summary>
        public const string ChannelLabel = "显示风扇";

        private readonly SettingsWindow _window;
        private readonly FanCard _card;

        public FanSettingsDialog(SettingsWindow window, FanCard card)
        {
            _window = window;
            _card = card;
        }

        public FrameworkElement Build()
        {
            AppSettings settings = _window.CurrentSettings;

            Border panel = new Border
            {
                Width = DialogWidth,
                Height = DialogHeight,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true
            };
            AutomationProperties.SetAutomationId(panel, "FanSettingsDialog");

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "风扇设置",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(title, "FanSettingsTitle");
            grid.Children.Add(title);

            StackPanel fields = new StackPanel { Margin = new Thickness(0, 16, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            fields.Children.Add(new TextBlock
            {
                Text = ChannelLabel,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                Margin = new Thickness(0, 0, 0, 8)
            });

            List<KeyValuePair<int, string>> options = BuildOptions(settings);
            DarkComboBox channelBox = new DarkComboBox();
            AutomationProperties.SetAutomationId(channelBox, "FanChannel");
            AutomationProperties.SetName(channelBox, ChannelLabel);
            channelBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            int selected = 0;
            for (int i = 0; i < options.Count; i++)
            {
                channelBox.Items.Add(options[i].Value);
                if (options[i].Key == settings.FanChannel) selected = i;
            }
            channelBox.SelectedIndex = selected;
            fields.Children.Add(channelBox);


            Grid.SetRow(fields, 1);
            grid.Children.Add(fields);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            FlatButton cancel = new FlatButton("取消");
            AutomationProperties.SetAutomationId(cancel, "FanSettingsCancel");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(cancel);

            FlatButton save = new FlatButton("保存", true);
            AutomationProperties.SetAutomationId(save, "FanSettingsSave");
            save.Click += (sender, e) => Save(channelBox, options);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            return panel;
        }

        /// <summary>
        /// 自动 + 当前读到的每一路（按通道号排序）+ **已配置但这次没读到的那一路**。
        /// 最后一条是必需的：少了它，下拉会显示成“自动”，一次保存就把选择写没了。
        /// </summary>
        private List<KeyValuePair<int, string>> BuildOptions(AppSettings settings)
        {
            List<KeyValuePair<int, string>> options = new List<KeyValuePair<int, string>>();
            options.Add(new KeyValuePair<int, string>(-1, "自动（第一路）"));

            MetricsSnapshot.FanReading[] fans = _card == null ? null : _card.CurrentFans;
            if (fans != null)
            {
                List<MetricsSnapshot.FanReading> sorted = new List<MetricsSnapshot.FanReading>();
                for (int i = 0; i < fans.Length; i++)
                {
                    if (fans[i] != null && fans[i].Valid) sorted.Add(fans[i]);
                }
                // 通道号排序：LHM 的枚举顺序不是通道号顺序，“自动（第一路）”才有确定含义。
                sorted.Sort(delegate (MetricsSnapshot.FanReading a, MetricsSnapshot.FanReading b)
                {
                    return a.Channel.CompareTo(b.Channel);
                });
                for (int i = 0; i < sorted.Count; i++)
                {
                    MetricsSnapshot.FanReading fan = sorted[i];
                    // 这里只报读数：0 RPM 既可能是停转，也可能是这个风扇头没接，
                    // 选择列表不替人下这个结论（卡片上对“选中且为 0”才写停转）。
                    string label = FanLabels.For(fan.Kind, fan.Channel) + "  " + fan.Rpm.ToString("0") + " RPM";
                    options.Add(new KeyValuePair<int, string>(fan.Channel, label));
                }
            }

            int configured = settings == null ? -1 : settings.FanChannel;
            if (configured >= 0)
            {
                bool present = false;
                for (int i = 0; i < options.Count; i++)
                {
                    if (options[i].Key == configured) { present = true; break; }
                }
                if (!present)
                {
                    options.Add(new KeyValuePair<int, string>(configured, "通道 " + configured + "（未读数）"));
                }
            }
            return options;
        }

        private void Save(DarkComboBox box, List<KeyValuePair<int, string>> options)
        {
            AppSettings edited = _window.CurrentSettings.Clone();
            int index = box.SelectedIndex;
            edited.FanChannel = index >= 0 && index < options.Count ? options[index].Key : -1;
            edited.Normalize();

            _window.CloseOverlay();
            _window.SaveSettings(edited, BandLayout.TotalWidth(_window.CurrentSettings));
        }
    }
}
