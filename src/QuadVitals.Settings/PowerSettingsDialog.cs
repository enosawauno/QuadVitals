using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 功耗卡右上角齿轮：CPU 功耗用哪个上限做分母。两个选项对应处理器规格里的两个名字，
    /// 基础功耗（PBP，13900K 为 125 W）与最大睿频功耗（MTP，同型号 253 W）。
    /// 用基础功耗当分母时，持续负载下的读数会一直顶满，所以默认给 MTP。
    /// </summary>
    internal sealed class PowerSettingsDialog
    {
        public const double DialogWidth = NetworkSettingsDialog.DialogWidth;
        public const double DialogHeight = NetworkSettingsDialog.DialogHeight;

        /// <summary>下拉的名字：弹窗、断言、文档、截图统一用这四个字。</summary>
        public const string ModeLabel = "CPU 功耗";

        private readonly SettingsWindow _window;

        public PowerSettingsDialog(SettingsWindow window)
        {
            _window = window;
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
            AutomationProperties.SetAutomationId(panel, "PowerSettingsDialog");

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "功耗设置",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(title, "PowerSettingsTitle");
            grid.Children.Add(title);

            StackPanel fields = new StackPanel { Margin = new Thickness(0, 16, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            fields.Children.Add(new TextBlock
            {
                Text = ModeLabel,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                Margin = new Thickness(0, 0, 0, 8)
            });

            List<KeyValuePair<string, string>> options = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(AppSettings.CpuPowerTdp, "TDP（基础功耗）"),
                new KeyValuePair<string, string>(AppSettings.CpuPowerMtp, "MTP（最大睿频功耗）")
            };

            DarkComboBox modeBox = new DarkComboBox();
            AutomationProperties.SetAutomationId(modeBox, "CpuPowerMode");
            AutomationProperties.SetName(modeBox, ModeLabel);
            modeBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            int selected = AppSettings.CpuPowerMtp == (settings == null ? null : settings.CpuPowerMode) ? 1 : 0;
            for (int i = 0; i < options.Count; i++) modeBox.Items.Add(options[i].Value);
            modeBox.SelectedIndex = selected;
            fields.Children.Add(modeBox);

            // 一句话说明两者差在哪：一个按规格基础功耗，一个按睿频上限。
            fields.Children.Add(new TextBlock
            {
                Text = "分母取处理器规格：基础功耗决定条形比例偏向保守，最大睿频功耗贴近满载读数。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 8, 0, 0)
            });

            Grid.SetRow(fields, 1);
            grid.Children.Add(fields);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            FlatButton cancel = new FlatButton("取消");
            AutomationProperties.SetAutomationId(cancel, "PowerSettingsCancel");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(cancel);

            FlatButton save = new FlatButton("保存", true);
            AutomationProperties.SetAutomationId(save, "PowerSettingsSave");
            save.Click += (sender, e) => Save(modeBox, options);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            return panel;
        }

        private void Save(DarkComboBox box, List<KeyValuePair<string, string>> options)
        {
            AppSettings edited = _window.CurrentSettings.Clone();
            int index = box.SelectedIndex;
            edited.CpuPowerMode = index >= 0 && index < options.Count ? options[index].Key : AppSettings.CpuPowerMtp;
            edited.Normalize();

            _window.CloseOverlay();
            _window.SaveSettings(edited, BandLayout.TotalWidth(_window.CurrentSettings));
        }
    }
}
