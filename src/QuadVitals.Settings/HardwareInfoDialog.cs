using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuadVitals.Core;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 「硬件信息」操作弹窗：九个页签（概览 / CPU / 主板 / 内存 / 显卡 / 硬盘 / 显示器 / 网络 / 音频），
    /// 每个字段一行「灰标签 + 白值」。
    ///
    /// 采集在后台线程：WMI 冷启动要几百毫秒，主线程不能等。弹窗先画骨架（全部 `--`），
    /// 报告到了再填；「重新检测」重跑一次，旧值保留到新值到达，避免整页闪成空。
    /// 序列号与 MAC 默认掩码——它们是能定位到具体设备的标识，展开只是改显示，不重新读数据。
    /// </summary>
    internal sealed class HardwareInfoDialog
    {
        public const double DialogWidth = 760;
        public const double DialogHeight = 620;

        private readonly SettingsWindow _window;
        private readonly StackPanel _tabs = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly StackPanel _body = new StackPanel();
        private readonly TextBlock _status = new TextBlock
        {
            FontSize = 11,
            Foreground = Theme.TextTertiaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        private HardwareReport _report;
        private int _selected;

        public HardwareInfoDialog(SettingsWindow window)
        {
            _window = window;
        }

        public FrameworkElement Build()
        {
            Border panel = new Border
            {
                Width = DialogWidth,
                Height = DialogHeight,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true
            };
            AutomationProperties.SetAutomationId(panel, "HardwareInfoDialog");

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 标题行：标题 + 状态（动作按钮都在底部）
            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "硬件信息",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(title, "HardwareInfoTitle");
            header.Children.Add(title);

            StackPanel statusHost = new StackPanel { Orientation = Orientation.Horizontal };
            statusHost.Children.Add(_status);
            Grid.SetColumn(statusHost, 1);
            header.Children.Add(statusHost);
            grid.Children.Add(header);

            // 页签 + 内容。必须用 Grid 而不是 StackPanel：竖直 StackPanel 会用无限高度测量子元素，
            // ScrollViewer 永远拿不到可视高度，内容超出时就只是被裁掉、根本不会出现滚动条。
            Grid mid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            mid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mid.Children.Add(_tabs);

            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 12, 0, 0),
                Content = _body
            };
            // 弹窗里的滚动条要自己带一套深色样式：窗口级隐式样式到不了这几个页签里的 ScrollBar，
            // 默认模板的 Track 没有 Thumb，结果就是「能滚但看不见滚动条」（实测宽 17 px、Thumb 为空）。
            scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), DarkScrollBars.Create());
            AutomationProperties.SetAutomationId(scroll, "HwContent");
            Grid.SetRow(scroll, 1);
            mid.Children.Add(scroll);
            Grid.SetRow(mid, 1);
            grid.Children.Add(mid);

            // 底部：左侧两个动作，右侧关闭
            Grid footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel footerActions = new StackPanel { Orientation = Orientation.Horizontal };
            FlatButton reload = new FlatButton("重新检测");
            AutomationProperties.SetAutomationId(reload, "HwReload");
            reload.MinWidth = 88;
            reload.Margin = new Thickness(0, 0, 8, 0);
            reload.Click += (sender, e) => Reload();
            footerActions.Children.Add(reload);

            FlatButton export = new FlatButton("导出报告");
            AutomationProperties.SetAutomationId(export, "HwExport");
            export.MinWidth = 88;
            export.Click += (sender, e) => ExportReport();
            footerActions.Children.Add(export);
            footer.Children.Add(footerActions);

            FlatButton close = new FlatButton("关闭", true);
            AutomationProperties.SetAutomationId(close, "HwClose");
            close.MinWidth = 96;
            close.Click += (sender, e) => _window.CloseOverlay();
            Grid.SetColumn(close, 2);
            footer.Children.Add(close);
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);

            panel.Child = grid;

            RenderTabs(SectionTitles());
            RenderBody();
            _status.Text = "正在读取…";
            LoadInBackground();

            return panel;
        }

        private static string[] SectionTitles()
        {
            return new[] { "概览", "CPU", "主板", "内存", "显卡", "硬盘", "显示器", "网络", "音频" };
        }

        private void LoadInBackground()
        {
            Dispatcher dispatcher = _window.Dispatcher;
            System.Threading.ThreadPool.QueueUserWorkItem(state =>
            {
                HardwareReport report = HardwareReport.Load();
                if (dispatcher == null) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _report = report;
                    RenderTabs(SectionTitles());
                    RenderBody();
                    _status.Text = report.Error != null
                        ? report.Error
                        : "读取于 " + report.LoadedUtc.ToLocalTime().ToString("HH:mm:ss");
                }));
            });
        }

        private void Reload()
        {
            _status.Text = "正在重新读取…";
            LoadInBackground();
        }

        /// <summary>
        /// 导出报告：默认 Markdown，文件名按「yyyyMMdd-硬件检测报告_QuadVitals」+ 当天递增序号，
        /// 具体落在哪个目录由系统保存对话框决定（默认打开桌面）。
        /// </summary>
        private void ExportReport()
        {
            if (_report == null)
            {
                _window.ShowNotice("还没有读到硬件信息");
                return;
            }

            DateTime now = DateTime.Now;
            string folder = SafeFolder();
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出硬件检测报告",
                Filter = "Markdown (*.md)|*.md|文本文件 (*.txt)|*.txt",
                FilterIndex = 1,
                DefaultExt = ".md",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (!string.IsNullOrEmpty(folder)) dialog.InitialDirectory = folder;
            dialog.FileName = System.IO.Path.GetFileName(ReportNaming.NextFileName(folder, now, ".md"));

            bool? result = dialog.ShowDialog(_window);
            if (result != true) return;

            string path = dialog.FileName;
            bool plain = string.Equals(System.IO.Path.GetExtension(path), ".txt",
                StringComparison.OrdinalIgnoreCase);
            string text = plain ? _report.PlainText() : _report.ToMarkdown(now);

            try
            {
                // 带 BOM：报告要在记事本/WPS/Word 里直接打开，无 BOM 的 UTF-8 在部分老工具里会乱码。
                System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
                _window.ShowNotice("已导出到 " + System.IO.Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                _window.ShowNotice("导出失败：" + ex.GetType().Name, true);
            }
        }

        private static string SafeFolder()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop)) return desktop;
            }
            catch (Exception)
            {
                // 取不到桌面就用系统默认目录
            }
            return string.Empty;
        }

        private void RenderTabs(string[] titles)
        {
            _tabs.Children.Clear();
            for (int i = 0; i < titles.Length; i++)
            {
                int index = i;
                FlatButton tab = new FlatButton(titles[i]);
                AutomationProperties.SetAutomationId(tab, "HwTab" + i);
                tab.MinWidth = 56;
                tab.Margin = new Thickness(0, 0, 6, 0);
                tab.Padding = new Thickness(10, 6, 10, 6);
                TabStyle selected = index == _selected ? SelectedTab() : UnselectedTab();
                tab.Background = selected.Background;
                tab.Foreground = selected.Foreground;
                tab.Click += (sender, e) =>
                {
                    _selected = index;
                    RenderTabs(titles);
                    RenderBody();
                };
                _tabs.Children.Add(tab);
            }
        }

        private sealed class TabStyle
        {
            public Brush Background;
            public Brush Foreground;
        }

        private static TabStyle SelectedTab()
        {
            return new TabStyle { Background = Theme.SurfaceHoverBrush, Foreground = Theme.TextPrimaryBrush };
        }

        private static TabStyle UnselectedTab()
        {
            return new TabStyle { Background = Brushes.Transparent, Foreground = Theme.TextSecondaryBrush };
        }

        private void RenderBody()
        {
            _body.Children.Clear();
            if (_report == null || _report.Sections.Count == 0)
            {
                _body.Children.Add(Hint("正在读取硬件信息…"));
                return;
            }

            HwSection section = _report.Sections[Math.Min(_selected, _report.Sections.Count - 1)];
            bool anyValue = false;

            foreach (HwBlock block in section.Blocks)
            {
                bool blockHasValue = false;
                foreach (HwField field in block.Fields) if (field.HasValue) blockHasValue = true;
                if (!blockHasValue && !string.IsNullOrEmpty(block.Title) && section.Blocks.Count > 1) continue;

                if (!string.IsNullOrEmpty(block.Title))
                {
                    _body.Children.Add(new TextBlock
                    {
                        Text = block.Title,
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Theme.TextSecondaryBrush,
                        Margin = new Thickness(0, _body.Children.Count == 0 ? 0 : 14, 0, 8)
                    });
                }

                foreach (HwField field in block.Fields)
                {
                    _body.Children.Add(FieldRow(field));
                    if (field.HasValue) anyValue = true;
                }
            }

            if (!anyValue)
            {
                _body.Children.Add(Hint("没有读到这一类的信息。"));
            }
        }

        private static FrameworkElement Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                Margin = new Thickness(0, 8, 0, 0)
            };
        }

        private static FrameworkElement FieldRow(HwField field)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock label = new TextBlock
            {
                Text = field.Label,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush
            };
            row.Children.Add(label);

            TextBlock value = new TextBlock
            {
                Text = field.Value,
                FontSize = 12.5,
                Foreground = field.HasValue ? Theme.TextPrimaryBrush : Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap
            };
            AutomationProperties.SetAutomationId(value, "HwValue_" + field.Label);
            Grid.SetColumn(value, 1);
            row.Children.Add(value);

            return row;
        }
    }
}
