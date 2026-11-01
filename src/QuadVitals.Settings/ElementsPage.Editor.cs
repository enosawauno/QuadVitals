using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;
using WpfButton = System.Windows.Controls.Button;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Element editor: the参数卡 (metric / type / size / colours) for the selected element.
    /// Split out of ElementsPage.cs to keep both files under the per-file line budget.
    /// </summary>
    internal sealed partial class ElementsPage
    {
        // ---- editor -------------------------------------------------------------------

        private void BuildEditor()
        {
            _editor.Children.Clear();
            if (_selectedIndex < 0 || _selectedIndex >= _draft.Items.Count)
            {
                _editor.Children.Add(new TextBlock
                {
                    Text = "左边选一个元素来编辑。",
                    Foreground = Theme.TextTertiaryBrush,
                    FontSize = 12.5
                });
                return;
            }

            BandElement element = _draft.Items[_selectedIndex];

            _editor.Children.Add(new TextBlock
            {
                Text = "元素 " + (_selectedIndex + 1) + " · " + DescribeMetric(element),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                Margin = new Thickness(0, 0, 0, 14)
            });

            // 指标在前、类型在后：指标是“要展示什么数据”，类型是它的表现方式（由指标给出默认值）。
            _editor.Children.Add(MetricRow(element));

            _editor.Children.Add(ComboRow("类型", TypeNames, element.Type, value =>
            {
                bool wasPie = element.IsType(BandElementType.Pie);
                element.Type = value;
                // 从柱/文本切到环形时直接给「渐变环」：任务栏上现有的环（显存/内存）都是这个形态，
                // 留着柱元素上那个无意义的 style 会切出一个白描边实心环。切回柱/文本再切回来仍可再选。
                if (!wasPie && element.IsType(BandElementType.Pie)) element.Style = PieStyle.Gradient;
                EnsureDefaultsFor(element);
                RefreshList();
                BuildEditor();
            }, "ElementTypeCombo"));

            if (element.IsType(BandElementType.Bar))
            {
                // 列数不再是手填参数：由 CPU 决定，下拉里选「核心数量 / 线程数量」。
                _editor.Children.Add(ComboRow("列数", new Dictionary<string, string>
                {
                    { ColumnModes.Cores, "核心数量" },
                    { ColumnModes.Threads, "线程数量" },
                    { ColumnModes.Custom, "自定义" }
                }, ColumnModes.Canonical(element.ColumnMode), value =>
                {
                    element.ColumnMode = ColumnModes.Canonical(value);
                    element.HistoryColumns = BandLayout.ColumnsFor(element,
                        HardwareInfo.PhysicalCores, HardwareInfo.LogicalProcessors);
                    RefreshList();
                    BuildEditor();
                }));

                // 自定义才给输入框，范围 1..线程数（不提供核心数时按 64 兜底）
                if (ColumnModes.Canonical(element.ColumnMode) == ColumnModes.Custom)
                {
                    int maxColumns = HardwareInfo.LogicalProcessors > 0 ? HardwareInfo.LogicalProcessors : 64;
                    // 输入框读写 CustomColumns（持久保存），同时同步生效值 HistoryColumns。
                    int shown = element.CustomColumns > 0 ? element.CustomColumns : element.HistoryColumns;
                    _editor.Children.Add(NumberRow("自定义列数", shown, 1, maxColumns, value =>
                    {
                        element.CustomColumns = value;
                        element.HistoryColumns = value;
                    }));
                }

                // 刷新率放在外观项之前。
                _editor.Children.Add(RefreshRow(element));

                // 柱宽 / 柱间距 / 高度是写死的外观常量（AppSettings.BarCellWidth/Gap/Height），
                // 不提供输入框；颜色（用户态 / 内核态 / 外边框）可配。
                // 先画内核色、再用用户态色覆盖，
                // 所以看得见的只有“内核超过用户态”的那一段。
                _editor.Children.Add(ColorRow("用户态", ColorRoles.Fill, () => element.Colors.Fill, value => element.Colors.Fill = value));
                _editor.Children.Add(ColorRow("系统内核", ColorRoles.Kernel, () => element.Colors.Kernel, value => element.Colors.Kernel = value));
                _editor.Children.Add(ColorRow("外边框", ColorRoles.Outline, () => element.Colors.Outline, value => element.Colors.Outline = value));
            }
            else if (element.IsType(BandElementType.Pie))
            {
                // 直径是固定外观常量（照 XMeters 模具 25 px），不在 UI 暴露，也不信任文件里的值。
                _editor.Children.Add(RefreshRow(element));
                _editor.Children.Add(ComboRow("样式", new Dictionary<string, string>
                {
                    { PieStyle.Solid, "实心扇形" },
                    { PieStyle.Gradient, "渐变环" }
                }, PieStyle.Canonical(element.Style), value =>
                {
                    element.Style = value;
                    RefreshList();
                    BuildEditor();
                }, "ElementPieStyleCombo"));
                if (PieStyle.Canonical(element.Style) == PieStyle.Gradient)
                {
                    // 渐变环的颜色不归编辑器调：它和温度一样按占用分档，取色器留着只会误导。
                    _editor.Children.Add(ZoneHintRow("颜色", "随占用分档：< 80 绿 · 80–89 黄 · ≥ 90 红"));
                }
                else
                {
                    _editor.Children.Add(ColorRow("已用色", ColorRoles.Fill, () => element.Colors.Fill, value => element.Colors.Fill = value));
                    _editor.Children.Add(ColorRow("描边色", ColorRoles.Outline, () => element.Colors.Outline, value => element.Colors.Outline = value));
                }
            }
            else if (element.IsType(BandElementType.Text))
            {
                bool temperaturePair = IsTemperaturePair(element);
                bool networkPair = IsNetworkPair(element);
                bool diskIoPair = IsDiskIoPair(element);
                if (temperaturePair) EnsureTemperaturePairLines(element);
                if (diskIoPair) EnsureDiskIoPairLines(element);
                if (diskIoPair)
                {
                    // 逐盘采样："" = 系统合计；其它候选只列本地固定盘与可移动盘（网络盘不参与，
                    // SMB 客户端计数是按共享而不是按盘符的）。
                    _editor.Children.Add(ComboRow("驱动器", DriveOptions(element.Drive), DriveOptionKey(element.Drive), value =>
                    {
                        element.Drive = AppSettings.CanonicalDrive(value);
                    }, "ElementDiskDriveCombo"));
                }
                _editor.Children.Add(RefreshRow(element));
                if (element.Lines == null) element.Lines = new List<TextLine>();
                for (int i = 0; i < element.Lines.Count; i++)
                {
                    int index = i;
                    TextLine line = element.Lines[i];
                    if (temperaturePair)
                    {
                        _editor.Children.Add(TemperatureLineRow(line, index, () => RefreshList()));
                    }
                    else if (!networkPair && !diskIoPair && element.Lines.Count > 1)
                    {
                        // 网络上下行固定是上行/下行；磁盘 I/O 固定是读/写；单行元素（单速率等）的指标
                        // 由上面的元素级「指标」决定——这三种情况行内再选一次只是重复。
                        _editor.Children.Add(TextLineMetricRow(line, () => RefreshList()));
                    }
                    // 温度行没有颜色可调（颜色由数值分档决定），也不再写一行说明：说了也没地方改。
                    if (!IsTemperatureMetric(line.Metric))
                    {
                        _editor.Children.Add(ColorRow("颜色", ColorRoles.Line, () => line.Color, value => line.Color = value));
                    }
                }
            }
        }

        /// <summary>
        /// 元素级刷新率。三个类型共用一行，位置固定在“尺寸之后、颜色之前”。
        /// </summary>
        private FrameworkElement RefreshRow(BandElement element)
        {
            return NumberRow("刷新率", element.RefreshMs,
                AppSettings.MinElementRefreshMs, AppSettings.MaxElementRefreshMs,
                value => element.RefreshMs = value, suffix: "毫秒");
        }

        private void EnsureDefaultsFor(BandElement element)
        {
            if (element.Colors == null)
            {
                element.Colors = new ElementColors
                {
                    Fill = AppSettings.DefaultGreen,
                    Outline = AppSettings.DefaultWhite,
                    Kernel = AppSettings.DefaultSystemRed
                };
            }
            if (element.RefreshMs < AppSettings.MinElementRefreshMs ||
                element.RefreshMs > AppSettings.MaxElementRefreshMs)
            {
                element.RefreshMs = _draft.RefreshMs;
            }
            if (element.IsType(BandElementType.Text))
            {
                if (element.Lines == null || element.Lines.Count == 0)
                {
                    element.Lines = new List<TextLine>
                    {
                        new TextLine { Metric = "cpu", Color = "#FFFFFFFF", Indicator = Indicator.None }
                    };
                }
                if (element.Width < AppSettings.MinTextWidth) element.Width = AppSettings.MinTextWidth;
            }
            else if (element.IsType(BandElementType.Bar))
            {
                if (string.IsNullOrEmpty(element.Metric)) element.Metric = "cpu";
                if (element.HistoryColumns < 1) element.HistoryColumns = 16;
            }
            else if (element.IsType(BandElementType.Pie))
            {
                if (string.IsNullOrEmpty(element.Metric)) element.Metric = "memory";
                // 直径 = 柱高（同一个内容带），用 Core 的常量而不是另一个 25。
                if (element.Diameter != AppSettings.BarHeight) element.Diameter = AppSettings.BarHeight;
                // 新建/切换成环形时默认给「渐变环」：现在盘上的环形（显存/内存）都是这个形态，
                // 旧配置里没写 style 的仍然按实心扇形读（Core 的默认值没变）。
                element.Style = string.IsNullOrEmpty(element.Style) ? PieStyle.Gradient : PieStyle.Canonical(element.Style);
            }
        }

        /// <summary>
        /// 指标下拉：目录里的各项 + 一个“网络上下行”伪项（一个元素同时显示上行/下行两行，
        /// 而不是拆成两个指标各建一个元素）。
        /// </summary>
        private FrameworkElement MetricRow(BandElement element)
        {
            Dictionary<string, string> options = new Dictionary<string, string>();
            options[NetworkMetricKey] = "网络上下行";
            options[TemperaturePairKey] = "温度";
            options[DiskIoPairKey] = "磁盘 I/O";
            foreach (MetricInfo info in MetricCatalog.All)
            {
                if (string.Equals(info.Key, "netUp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.Key, "netDown", StringComparison.OrdinalIgnoreCase)) continue;
                // 两个温度已经合并成上面的“温度”（两行各选一个），不再单列。
                if (IsTemperatureMetric(info.Key)) continue;
                // 磁盘 I/O 的读/写与合并值都走上面的两行项，不再单列。
                if (IsDiskIoMetric(info.Key)) continue;
                options[info.Key] = info.DisplayName;
            }
            return ComboRow("指标", options, CurrentMetricKey(element), value =>
            {
                ApplyMetric(element, value);
                RefreshList();
                BuildEditor();
            }, "ElementMetricCombo");
        }

        /// <summary>元素当前对应哪个下拉项：文本元素着的两行 netUp+netDown 算一个“网络上下行”。</summary>
        private static string CurrentMetricKey(BandElement element)
        {
            if (element.IsType(BandElementType.Text))
            {
                if (element.Lines == null || element.Lines.Count == 0) return null;
                if (element.Lines.Count == 2 &&
                    IsMetric(element.Lines[0].Metric, "netUp") &&
                    IsMetric(element.Lines[1].Metric, "netDown")) return NetworkMetricKey;
                bool anyTemperature = false;
                bool allTemperature = element.Lines.Count > 0;
                foreach (TextLine line in element.Lines)
                {
                    if (IsTemperatureMetric(line.Metric)) anyTemperature = true;
                    else allTemperature = false;
                }
                if (anyTemperature && allTemperature) return TemperaturePairKey;
                if (IsDiskIoPair(element)) return DiskIoPairKey;
                if (element.Lines.Count == 1) return element.Lines[0].Metric;
                return null;
            }
            return element.Metric;
        }

        private static bool IsMetric(string value, string expected)
        {
            return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>CPU 温度与 GPU 温度是同一个下拉项的两行，所以两侧都要用同一份判定。</summary>
        private static bool IsTemperatureMetric(string metricKey)
        {
            return IsMetric(metricKey, "temperature") || IsMetric(metricKey, "gpuTemp");
        }

        private static bool IsDiskIoMetric(string metricKey)
        {
            return IsMetric(metricKey, "diskIo") || IsMetric(metricKey, "diskIoRead") ||
                   IsMetric(metricKey, "diskIoWrite");
        }

        /// <summary>磁盘 I/O 元素：所有行都是磁盘指标（旧配置只有一行合并值，也算）。</summary>
        private static bool IsDiskIoPair(BandElement element)
        {
            if (element == null || !element.IsType(BandElementType.Text)) return false;
            if (element.Lines == null || element.Lines.Count == 0) return false;
            foreach (TextLine line in element.Lines)
            {
                if (!IsDiskIoMetric(line.Metric)) return false;
            }
            return true;
        }

        private static string DriveOptionKey(string drive)
        {
            return AppSettings.CanonicalDrive(drive);
        }

        /// <summary>
        /// 驱动器下拉的候选：空串 = 全部磁盘（系统合计），其余只列本地固定盘与可移动盘。
        /// 配置文件里的盘符如果当前不在线（拔掉的 USB、断开的映射），也保留一项并标“未连接”，
        /// 否则下拉会显示成“全部磁盘”，看起来像设置丢了。
        /// </summary>
        private static Dictionary<string, string> DriveOptions(string current)
        {
            Dictionary<string, string> options = new Dictionary<string, string>();
            options[string.Empty] = "全部磁盘";
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                    try
                    {
                        if (!drive.IsReady) continue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    string name = drive.Name;
                    if (name == null || name.Length < 2 || name[1] != ':') continue;
                    string key = name.Substring(0, 2).ToUpperInvariant();
                    string kind = drive.DriveType == DriveType.Removable ? "可移动磁盘" : "本地磁盘";
                    string label;
                    try { label = drive.VolumeLabel; }
                    catch (Exception) { label = null; }
                    // 卷标经常就是“本地磁盘/新加卷”这类和类型同名的词；再拼一次就成了
                    // “E: 本地磁盘 · 本地磁盘”。同名时不再拼。
                    bool showLabel = !string.IsNullOrEmpty(label) &&
                                     !string.Equals(label.Trim(), kind, StringComparison.OrdinalIgnoreCase);
                    options[key] = key + "  " + kind + (showLabel ? " · " + label.Trim() : string.Empty);
                }
            }
            catch (Exception)
            {
                // 枚举失败时只剩“全部磁盘”，元素仍然可用
            }
            string canonical = AppSettings.CanonicalDrive(current);
            if (!string.IsNullOrEmpty(canonical) && !options.ContainsKey(canonical))
            {
                options[canonical] = canonical + "  未连接";
            }
            return options;
        }

        /// <summary>
        /// 旧配置里的磁盘 I/O 只有一行（读 + 写合并）。打开编辑器时补成读 / 写两行：
        /// 第一行沿用元素现有颜色，第二行给默认色；保存后任务栏就分成两行了。
        /// </summary>
        private static void EnsureDiskIoPairLines(BandElement element)
        {
            if (element.Lines == null) element.Lines = new List<TextLine>();
            if (element.Lines.Count == 2 &&
                ((IsMetric(element.Lines[0].Metric, "diskIoRead") && IsMetric(element.Lines[1].Metric, "diskIoWrite")) ||
                 (IsMetric(element.Lines[0].Metric, "diskIoWrite") && IsMetric(element.Lines[1].Metric, "diskIoRead"))))
            {
                return;
            }
            string readColour = AppSettings.DefaultBlue;
            string writeColour = AppSettings.DefaultGreenSoft;
            if (element.Lines.Count > 0 && !string.IsNullOrEmpty(element.Lines[0].Color)) readColour = element.Lines[0].Color;
            element.Lines = new List<TextLine>
            {
                new TextLine { Metric = "diskIoRead", Color = readColour, Indicator = Indicator.None },
                new TextLine { Metric = "diskIoWrite", Color = writeColour, Indicator = Indicator.None }
            };
        }

        /// <summary>
        /// 指标 → 默认图形：占用率看历史柱（CPU）、看环形（内存/温度），网速看两行文本。
        /// 换指标时自动切到这个默认类型；切完仍可在「类型」里改回来。
        /// </summary>
        private static string DefaultTypeFor(string metric)
        {
            switch ((metric ?? string.Empty).ToLowerInvariant())
            {
                case "cpu": return BandElementType.Bar;
                case "memory":
                case "temperature":
                case "gpu":
                case "gpumemory":
                case "gputemp":
                case "disk": return BandElementType.Pie;
                case "netup":
                case "netdown":
                // 速率型指标放柱/环里没有意义（百分比域），默认给文本元素。
                case "diskio":
                case "diskioread":
                case "diskiowrite": return BandElementType.Text;
                default: return null;
            }
        }

        /// <summary>
        /// 选指标时同步把类型切到默认图形，并把指标/行落到元素上：
        /// “网络上下行” → 文本两行（上 ▲ 蓝 / 下 ▼ 绿）；其余 → 柱状/环形 + 单指标。
        /// </summary>
        private static void ApplyMetric(BandElement element, string metric)
        {
            // 换指标就丢掉上一个指标的附加参数（比如磁盘元素的盘符），避免把 C: 带到别的元素上。
            element.Drive = string.Empty;
            if (string.Equals(metric, NetworkMetricKey, StringComparison.OrdinalIgnoreCase))
            {
                element.Type = BandElementType.Text;
                if (element.Width < AppSettings.MinTextWidth) element.Width = AppSettings.MinTextWidth;
                element.Lines = new List<TextLine>
                {
                    new TextLine { Metric = "netUp", Color = AppSettings.DefaultBlue, Indicator = Indicator.Up },
                    new TextLine { Metric = "netDown", Color = AppSettings.DefaultGreenSoft, Indicator = Indicator.Down }
                };
                return;
            }

            if (string.Equals(metric, TemperaturePairKey, StringComparison.OrdinalIgnoreCase))
            {
                // 一行 CPU、一行 GPU：两个数值上下排，行内还能对调。
                element.Type = BandElementType.Text;
                element.Metric = null;
                if (element.Width < AppSettings.MinTextWidth) element.Width = AppSettings.MinTextWidth;
                element.Lines = new List<TextLine>
                {
                    NewTemperatureLine("temperature"),
                    NewTemperatureLine("gpuTemp")
                };
                return;
            }

            if (string.Equals(metric, DiskIoPairKey, StringComparison.OrdinalIgnoreCase))
            {
                // 读 / 写上下两行、各自一个颜色，结构与网络上下行一致，只是没有方向三角。
                element.Type = BandElementType.Text;
                element.Metric = null;
                if (element.Width < AppSettings.MinTextWidth) element.Width = AppSettings.MinTextWidth;
                element.Lines = new List<TextLine>
                {
                    new TextLine { Metric = "diskIoRead", Color = AppSettings.DefaultBlue, Indicator = Indicator.None },
                    new TextLine { Metric = "diskIoWrite", Color = AppSettings.DefaultGreenSoft, Indicator = Indicator.None }
                };
                return;
            }

            element.Metric = metric;
            string type = DefaultTypeFor(metric);
            if (!string.IsNullOrEmpty(type)) element.Type = type;

            if (element.IsType(BandElementType.Text))
            {
                string indicator = Indicator.None;
                string color = "#FFFFFFFF";
                element.Lines = new List<TextLine>
                {
                    new TextLine { Metric = metric, Color = color, Indicator = indicator }
                };
            }
        }

        private FrameworkElement TextLineMetricRow(TextLine line, Action onChange)
        {
            Dictionary<string, string> options = new Dictionary<string, string>();
            foreach (MetricInfo info in MetricCatalog.All) options[info.Key] = info.DisplayName;
            return ComboRow("指标", options, line.Metric, value =>
            {
                line.Metric = value;
                onChange();
            });
        }

        /// <summary>
        /// “温度”元素的行内选择：只有 CPU / GPU 两个温度可选，不再给整份指标目录。
        /// 两行选同一个也没关系（比如 CPU 的 Package 与最高核），渲染照常上下排。
        /// </summary>
        private FrameworkElement TemperatureLineRow(TextLine line, int index, Action onChange)
        {
            Dictionary<string, string> options = new Dictionary<string, string>
            {
                { "temperature", "CPU 温度" },
                { "gpuTemp", "GPU 温度" }
            };
            string current = IsTemperatureMetric(line.Metric) ? line.Metric : "temperature";
            return ComboRow("传感器", options, current, value =>
            {
                line.Metric = value;
                onChange();
            }, "ElementTempLine" + index);
        }

        private static bool IsNetworkPair(BandElement element)
        {
            if (element == null || !element.IsType(BandElementType.Text)) return false;
            if (element.Lines == null || element.Lines.Count != 2) return false;
            return IsMetric(element.Lines[0].Metric, "netUp") && IsMetric(element.Lines[1].Metric, "netDown");
        }

        private static bool IsTemperaturePair(BandElement element)
        {
            if (element == null || !element.IsType(BandElementType.Text)) return false;
            if (element.Lines == null || element.Lines.Count == 0) return false;
            foreach (TextLine line in element.Lines)
            {
                if (!IsTemperatureMetric(line.Metric)) return false;
            }
            return true;
        }

        /// <summary>
        /// “温度”元素在编辑器里始终给两行：只选了一行时补上另一个传感器，
        /// 否则第二行没有入口，无法把 CPU/GPU 上下摆到一起。
        /// </summary>
        private static void EnsureTemperaturePairLines(BandElement element)
        {
            if (element.Lines == null) element.Lines = new List<TextLine>();
            if (element.Lines.Count > 2) element.Lines.RemoveRange(2, element.Lines.Count - 2);
            if (element.Lines.Count == 0) element.Lines.Add(NewTemperatureLine("temperature"));
            if (element.Lines.Count == 1)
            {
                string missing = IsMetric(element.Lines[0].Metric, "temperature") ? "gpuTemp" : "temperature";
                element.Lines.Add(NewTemperatureLine(missing));
            }
        }

        private static TextLine NewTemperatureLine(string metric)
        {
            return new TextLine
            {
                Metric = metric,
                Color = AppSettings.DefaultOrange,
                Indicator = Indicator.None
            };
        }

        private FrameworkElement ComboRow(string label, Dictionary<string, string> options, string current, Action<string> onChange,
                                        string automationId = null)
        {
            Grid grid = FieldRow(label);
            ComboBox combo = new DarkComboBox();
            if (!string.IsNullOrEmpty(automationId)) AutomationProperties.SetAutomationId(combo, automationId);

            string selectedKey = null;
            foreach (KeyValuePair<string, string> option in options)
            {
                ComboBoxItem item = new ComboBoxItem
                {
                    Content = option.Value,
                    Tag = option.Key,
                    Background = Theme.SurfaceHoverBrush,
                    Foreground = Theme.TextPrimaryBrush
                };
                combo.Items.Add(item);
                if (string.Equals(option.Key, current, StringComparison.OrdinalIgnoreCase)) selectedKey = option.Key;
            }
            if (selectedKey != null) combo.SelectedIndex = IndexOfKey(combo, selectedKey);

            combo.SelectionChanged += (sender, e) =>
            {
                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null) return;
                string key = (string)item.Tag;
                if (string.Equals(key, current, StringComparison.OrdinalIgnoreCase)) return;
                current = key;
                onChange(key);
            };

            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
            return grid;
        }

        private static int IndexOfKey(ComboBox combo, string key)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                ComboBoxItem item = combo.Items[i] as ComboBoxItem;
                if (item != null && string.Equals((string)item.Tag, key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }

        private FrameworkElement NumberRow(string label, int value, int min, int max, Action<int> onChange,
                                          string suffix = null)
        {
            Grid grid = FieldRow(label);
            TextBox box = new DarkTextBox { Text = value.ToString() };
            // 左内边距 4：模板的 Border 与 TextBox 内容宿主各算一次 Padding（1 px 变 2 px），
            // 所以实际缩进 8 px；数字字形左侧还有约 2 px 空白，最终墨迹左边缘与下拉文字
            // （Padding 10 + 1 px 边框）落在同一条竖线上，实测偏差 0–1 px。
            box.Padding = new Thickness(4, 2, string.IsNullOrEmpty(suffix) ? 10 : 46, 2);
            box.LostFocus += (sender, e) => Commit();
            box.KeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter) Commit();
            };

            void Commit()
            {
                int parsed;
                if (!int.TryParse(box.Text, out parsed))
                {
                    box.Text = value.ToString();
                    return;
                }
                int clamped = Math.Max(min, Math.Min(max, parsed));
                box.Text = clamped.ToString();
                onChange(clamped);
                RefreshList();
            }

            if (string.IsNullOrEmpty(suffix))
            {
                Grid.SetColumn(box, 1);
                grid.Children.Add(box);
                return grid;
            }

            Grid holder = new Grid();
            holder.Children.Add(box);
            holder.Children.Add(new TextBlock
            {
                Text = suffix,
                FontSize = 11.5,
                Foreground = Theme.TextTertiaryBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                IsHitTestVisible = false
            });
            Grid.SetColumn(holder, 1);
            grid.Children.Add(holder);
            return grid;
        }

        /// <summary>只读说明行（与温度分档那行同口径）：左边标签、右边一句话，没有可交互控件。</summary>
        private static Grid ZoneHintRow(string label, string text)
        {
            Grid grid = FieldRow(label);
            TextBlock hint = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hint, 1);
            grid.Children.Add(hint);
            return grid;
        }

        private static Grid FieldRow(string label)
        {
            Grid grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock text = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
            return grid;
        }
    }
}
