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
        public const string StyleLabel = "指示样式";

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

            // 第二项：指示样式（条形 / 圆环）。
            List<KeyValuePair<string, string>> styles = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(AppSettings.PowerStyleBars, "条形指示图"),
                new KeyValuePair<string, string>(AppSettings.PowerStyleRings, "圆环")
            };
            DarkComboBox styleBox = new DarkComboBox();
            AutomationProperties.SetAutomationId(styleBox, "PowerCardStyle");
            AutomationProperties.SetName(styleBox, StyleLabel);
            styleBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            int styleIndex = string.Equals(settings == null ? null : settings.PowerCardStyle,
                AppSettings.PowerStyleRings, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            for (int i = 0; i < styles.Count; i++) styleBox.Items.Add(styles[i].Value);
            styleBox.SelectedIndex = styleIndex;

            // 两行都按通用设置的字段行排：标签在左、下拉在右，控件落在同一条竖线上。
            fields.Children.Add(FieldRow(ModeLabel, modeBox, "CpuPowerModeLabel"));
            fields.Children.Add(FieldRow(StyleLabel, styleBox, "PowerCardStyleLabel"));

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
            save.Click += (sender, e) => Save(modeBox, options, styleBox, styles);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            return panel;
        }

        /// <summary>
        /// 标签在左、控件在右的字段行，与通用设置里的下拉行同一套尺寸：
        /// 行高 34、标签 13 px 主文字、固定标签列宽让两行控件落在同一条竖线上。
        /// </summary>
        private const double LabelColumnWidth = 72;

        private static FrameworkElement FieldRow(string label, FrameworkElement control, string labelAutomationId)
        {
            Grid row = new Grid { Height = 34, Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock caption = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                // 间距放在标签的右边距里：控件带左外边距时下拉弹层会跟着偏到格子左边。
                Margin = new Thickness(0, 0, 20, 0)
            };
            AutomationProperties.SetAutomationId(caption, labelAutomationId);
            row.Children.Add(caption);

            control.VerticalAlignment = VerticalAlignment.Center;
            DarkComboBox combo = control as DarkComboBox;
            if (combo != null) combo.PopupOffset = 14;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private void Save(DarkComboBox box, List<KeyValuePair<string, string>> options,
                          DarkComboBox styleBox, List<KeyValuePair<string, string>> styles)
        {
            AppSettings edited = _window.CurrentSettings.Clone();
            int index = box.SelectedIndex;
            edited.CpuPowerMode = index >= 0 && index < options.Count ? options[index].Key : AppSettings.CpuPowerMtp;
            int styleIndex = styleBox.SelectedIndex;
            edited.PowerCardStyle = styleIndex >= 0 && styleIndex < styles.Count
                ? styles[styleIndex].Key
                : AppSettings.PowerStyleBars;
            edited.Normalize();

            _window.CloseOverlay();
            _window.SaveSettings(edited, BandLayout.TotalWidth(_window.CurrentSettings));
        }
    }
}
