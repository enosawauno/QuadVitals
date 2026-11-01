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
    /// 设备状态卡的设置弹窗（入口是那张卡标题行右侧的齿轮）。
    ///
    /// 第一项是指示器样式：「进度样式」下拉在 16 段小分段与连成一体的胶囊条之间切换；
    /// 与网速卡的「网络设置」弹窗一致：**只改草稿，点「保存」才写入并生效**，点「取消」丢弃。
    /// </summary>
    internal sealed class DeviceSettingsDialog
    {
        /// <summary>与网速卡弹窗同尺寸，三个弹窗并排看不会一个大一个小。</summary>
        public const double DialogWidth = NetworkSettingsDialog.DialogWidth;
        public const double DialogHeight = NetworkSettingsDialog.DialogHeight;

        /// <summary>下拉的名字：弹窗、断言、文档、截图统一用这四个字。</summary>
        public const string IndicatorLabel = "进度样式";

        private static readonly KeyValuePair<string, string>[] Styles =
        {
            new KeyValuePair<string, string>(AppSettings.IndicatorSegments, "分段条"),
            new KeyValuePair<string, string>(AppSettings.IndicatorCapsule, "胶囊条")
        };

        private readonly SettingsWindow _window;

        public DeviceSettingsDialog(SettingsWindow window)
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
            AutomationProperties.SetAutomationId(panel, "DeviceSettingsDialog");

            // 三行：标题贴顶、中间是设置项（以后继续往下加）、按钮贴底。
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "设备状态设置",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(title, "DeviceSettingsTitle");
            grid.Children.Add(title);

            StackPanel fields = new StackPanel { Margin = new Thickness(0, 16, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            fields.Children.Add(new TextBlock
            {
                Text = IndicatorLabel,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                Margin = new Thickness(0, 0, 0, 8)
            });

            DarkComboBox styleBox = new DarkComboBox();
            AutomationProperties.SetAutomationId(styleBox, "DeviceSettingsIndicator");
            AutomationProperties.SetName(styleBox, IndicatorLabel);
            // 不给固定宽度：和网速弹窗的网卡 / 刷新间隔下拉一样铺满字段列（弹窗内宽），
            // 两个弹窗的下拉框看着才是同一个宽。
            styleBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            int selected = 0;
            for (int i = 0; i < Styles.Length; i++)
            {
                styleBox.Items.Add(Styles[i].Value);
                if (string.Equals(Styles[i].Key, settings.IndicatorStyle, StringComparison.OrdinalIgnoreCase)) selected = i;
            }
            styleBox.SelectedIndex = selected;
            fields.Children.Add(styleBox);

            // 四个硬件各一个下拉：每个格子显示哪个指标。两列排，免得弹窗被拉长。
            Grid metrics = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            DarkComboBox cpuBox = AddMetricPicker(metrics, 0, 0, "CPU", "cpu", DeviceMetricChoices.CpuOptions, settings.DeviceMetrics.Cpu);
            DarkComboBox memoryBox = AddMetricPicker(metrics, 2, 0, "内存", "memory", DeviceMetricChoices.MemoryOptions, settings.DeviceMetrics.MemoryUsed);
            DarkComboBox gpuBox = AddMetricPicker(metrics, 0, 1, "显卡", "gpu", DeviceMetricChoices.GpuOptions, settings.DeviceMetrics.Gpu);
            DarkComboBox diskBox = AddMetricPicker(metrics, 2, 1, "硬盘", "disk", DeviceMetricChoices.DiskOptions, settings.DeviceMetrics.Disk);

            fields.Children.Add(metrics);

            Grid.SetRow(fields, 1);
            grid.Children.Add(fields);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            FlatButton cancel = new FlatButton("取消");
            AutomationProperties.SetAutomationId(cancel, "DeviceSettingsCancel");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(cancel);

            FlatButton save = new FlatButton("保存", true);
            AutomationProperties.SetAutomationId(save, "DeviceSettingsSave");
            save.Click += (sender, e) => Save(styleBox, cpuBox, memoryBox, gpuBox, diskBox);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            return panel;
        }

        /// <summary>
        /// 一个硬件的「显示指标」下拉：标签（CPU / 内存 / 显卡 / 硬盘）+ 选项（占用 / 温度 / 频率 / …）。
        /// 选项的中文名只在弹窗里出现，写回 settings.json 的仍然是稳定 key。
        /// </summary>
        private static DarkComboBox AddMetricPicker(Grid host, int column, int row, string label, string hardware,
                                     string[] options, string current)
        {
            StackPanel block = new StackPanel();
            block.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                Margin = new Thickness(0, 0, 0, 8)
            });

            DarkComboBox box = new DarkComboBox();
            AutomationProperties.SetAutomationId(box, "DeviceMetric_" + hardware);
            AutomationProperties.SetName(box, label);
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            int selected = 0;
            for (int i = 0; i < options.Length; i++)
            {
                box.Items.Add(DeviceMetricChoices.DisplayName(options[i]));
                if (string.Equals(options[i], current, StringComparison.OrdinalIgnoreCase)) selected = i;
            }
            box.SelectedIndex = selected;
            block.Children.Add(box);

            Grid.SetColumn(block, column);
            Grid.SetRow(block, row);
            host.Children.Add(block);
            return box;
        }

        /// <summary>
        /// 保存：从当前 live 设置克隆一份（期间可能改了别的页），只覆盖本弹窗的字段，
        /// 再走与网速弹窗同一条保存路径；取消 / 关遮罩都不写文件。
        /// </summary>
        private void Save(DarkComboBox styleBox, DarkComboBox cpuBox, DarkComboBox memoryBox,
                          DarkComboBox gpuBox, DarkComboBox diskBox)
        {
            AppSettings edited = _window.CurrentSettings.Clone();

            int styleIndex = styleBox.SelectedIndex;
            if (styleIndex >= 0 && styleIndex < Styles.Length) edited.IndicatorStyle = Styles[styleIndex].Key;

            DeviceMetricChoices metrics = new DeviceMetricChoices
            {
                Cpu = OptionAt(cpuBox, DeviceMetricChoices.CpuOptions),
                MemoryUsed = OptionAt(memoryBox, DeviceMetricChoices.MemoryOptions),
                Gpu = OptionAt(gpuBox, DeviceMetricChoices.GpuOptions),
                Disk = OptionAt(diskBox, DeviceMetricChoices.DiskOptions)
            };
            metrics.Normalize();
            edited.DeviceMetrics = metrics;
            edited.Normalize();

            _window.CloseOverlay();
            _window.SaveSettings(edited, BandLayout.TotalWidth(_window.CurrentSettings));
        }

        private static string OptionAt(DarkComboBox box, string[] options)
        {
            int index = box.SelectedIndex;
            return index >= 0 && index < options.Length ? options[index] : options[0];
        }
    }
}
