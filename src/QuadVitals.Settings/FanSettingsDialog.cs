using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
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
        private bool _updatingMotion;

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

            // 风扇动效：只控制这张卡的「常驻动画 / 悬停播放」。磁盘 I/O 的走马是读数的一部分，
            // 一直是常驻的，不在这里。
            TextBlock motionLabel = new TextBlock
            {
                Text = "风扇动效",
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                Margin = new Thickness(0, 24, 0, 8)
            };
            fields.Children.Add(motionLabel);

            DarkComboBox motionBox = new DarkComboBox();
            AutomationProperties.SetAutomationId(motionBox, "FanAnimationMode");
            AutomationProperties.SetName(motionBox, "风扇动效");
            motionBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            motionBox.Items.Add("悬停播放（默认）");
            motionBox.Items.Add("常驻动画");
            bool always = string.Equals(settings.AnimationMode, AppSettings.AnimationAlways, StringComparison.OrdinalIgnoreCase);
            motionBox.SelectedIndex = always ? 1 : 0;
            motionBox.SelectionChanged += (sender, e) => OnMotionChanged(motionBox);
            fields.Children.Add(motionBox);



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

        /// <summary>
        /// 切到「常驻动画」前先确认：这不是偏好题，是明确的资源代价，切换前必须提示。
        /// 取消则把下拉拨回原档，且不写盘。
        /// </summary>
        private void OnMotionChanged(DarkComboBox box)
        {
            if (_updatingMotion) return;
            AppSettings latest = _window.CurrentSettings.Clone();
            bool wasAlways = string.Equals(latest.AnimationMode, AppSettings.AnimationAlways, StringComparison.OrdinalIgnoreCase);
            bool wantAlways = box.SelectedIndex == 1;
            if (wantAlways == wasAlways) return;

            if (!wantAlways)
            {
                ApplyMotion(false);
                return;
            }

            // 下拉先停在新档位（看得见自己选了什么），**取消时才拨回**：
            // 在 SelectionChanged 里直接改 SelectedIndex 会被 ComboBox 自己覆盖，取消回调是唯一可靠时机。
            Action rollback = delegate
            {
                _updatingMotion = true;
                box.SelectedIndex = 0;
                _updatingMotion = false;
            };

            // 文案按确认过的版本照用；排版按 ui-design 分三层：
            // 结论（Body 白）→ 实测数据（小标题 Caption 11 三级灰 + 带项目符号的数据行，悬挂缩进、
            // 表格数字）→ 问句（Body 白，单独一段）。全都同字号同颜色堆在一起就是一段散文。
            _window.ShowConfirmContent(
                "开启常驻动画",
                BuildMotionCostBody(),
                "开启",
                delegate { ApplyMotion(true); },
                // 红色只留给“删除”这类不可逆操作；改一个动效开关不该像按格式化。
                false,
                rollback,
                BuildMotionCostHint());
        }

        /// <summary>确认框正文：结论 + 实测数据（小标题 + 三行数据）+ 问句。</summary>
        private static FrameworkElement BuildMotionCostBody()
        {
            StackPanel body = new StackPanel();

            // 结论是陈述句；“？”属于标题行右侧的说明入口，不是句末标点。
            body.Children.Add(ConfirmDialog.BodyText("开启后将导致仪表盘持续渲染"));
            body.Children.Add(new TextBlock { Height = 12 });

            // 每行 = 标签 + 数值 run：数值加粗（表格数字），省电的那个数值再带一点绿，
            // 扫一眼就能分出“贵 / 便宜”。项目符号用自绘小圆点，不靠字体里手打的点。
            // 数字按确认过的版本；CPU 成本很小（同进程 A/B 落在噪声里），代价主要在 GPU。
            body.Children.Add(CostRow(0, "GPU 占用约 ", "2–7%", false));
            body.Children.Add(CostRow(1, "CPU 单核占用约 ", "1–2%", false));
            body.Children.Add(CostRow(2, "默认「悬停播放」模式，资源占用 ", "0.7%", true, " 左右"));

            TextBlock question = ConfirmDialog.BodyText("是否确认开启？");
            question.Foreground = Theme.TextPrimaryBrush;
            question.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(question);

            return body;
        }

        /// <summary>
        /// 标题行右侧的「?」入口：悬停显示实测机型与来源。它不是按钮，不触发任何动作，
        /// 只把“这些数字从哪来”从正文里拿出来，避免结论句被误读成问句。
        /// </summary>
        private static FrameworkElement BuildMotionCostHint()
        {
            Border badge = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = Theme.SurfaceRaisedBrush,
                BorderBrush = Theme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Help,
                ToolTip = "基于 4080FE/13900K 实测",
                Child = new TextBlock
                {
                    Text = "?",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.TextSecondaryBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            ToolTipService.SetInitialShowDelay(badge, 200);
            ToolTipService.SetShowDuration(badge, 60000);
            AutomationProperties.SetAutomationId(badge, "MotionCostHint");
            AutomationProperties.SetName(badge, "实测数据说明");
            return badge;
        }

        /// <summary>一行“项目符号 + 标签 + 加粗数值”。符号列固定宽，文字悬挂对齐。</summary>
        private static Grid CostRow(int index, string label, string value, bool cheap, string suffix = null)
        {
            // 行间 4（间距阶）；最后一行也只留 4，问句再补 8 → 数据块与问句之间 12。
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Ellipse dot = new Ellipse
            {
                Width = 3,
                Height = 3,
                Fill = Theme.TextTertiaryBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1, 9, 0, 0)
            };
            AutomationProperties.SetAutomationId(dot, "MotionCostDot");
            row.Children.Add(dot);

            TextBlock text = new TextBlock
            {
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)),
                TextWrapping = TextWrapping.NoWrap
            };
            text.Inlines.Add(new Run(label));
            Run number = new Run(value)
            {
                FontWeight = FontWeights.SemiBold,
                // 省电的那个数带绿：颜色的含义与全产品一致（绿 = 正常/便宜）。
                Foreground = cheap ? Theme.AccentGreenSoftBrush : Theme.TextPrimaryBrush
            };
            text.Inlines.Add(number);
            if (!string.IsNullOrEmpty(suffix)) text.Inlines.Add(new Run(suffix));
            System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            AutomationProperties.SetAutomationId(text, "MotionCostRow" + index);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            return row;
        }

        private void ApplyMotion(bool always)
        {
            AppSettings latest = _window.CurrentSettings.Clone();
            latest.AnimationMode = always ? AppSettings.AnimationAlways : AppSettings.AnimationHover;
            latest.Normalize();
            _window.SaveSettingsQuietly(latest);
            _window.ApplyAnimationMode();
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
