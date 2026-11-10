using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 概览页的功耗卡：总功耗是主角，CPU 与 GPU 是两行明细。
    ///
    /// 左边是一枚没有外框的闪电：光晕呼吸、电弧来回跳动，颜色按“最接近自己上限的那一路”
    /// 分档（绿 / 黄 / 红）。动效和风扇卡同一套纪律：指针停在卡上或设置成常驻才逐帧，
    /// RDP / Tier 0 / 电池由 FrameLoop 与 RenderEnvironment 统一挡住。
    ///
    /// 右边第一行是两路之和（40 px 主数值），下面 CPU / GPU 各一行：标签与 18 px 数值在左，
    /// 分段条在右，条尾写 TDP 上限。条用设备四格同一套 16 段几何，不铺满整行，
    /// 否则 16 段会被拉成近方形的块。
    /// </summary>
    internal sealed partial class PowerCard : Grid, IDisposable
    {
        private const double BarHeight = 22.0;
        private const double BarWidth = 200.0;
        private const double IconWidth = 96.0;
        private const double IconHeight = 128.0;
        private const double ComponentValueSize = 18.0;

        private readonly Card _card;
        private readonly TextBlock _total = new TextBlock();
        private readonly TextBlock _totalUnit = new TextBlock();
        private readonly TextBlock _cpuValue = new TextBlock();
        private readonly TextBlock _cpuUnit = new TextBlock();
        private readonly TextBlock _gpuValue = new TextBlock();
        private readonly TextBlock _gpuUnit = new TextBlock();
        private readonly TextBlock _cpuTdpText = new TextBlock();
        private readonly TextBlock _gpuTdpText = new TextBlock();
        private readonly TextBlock _note = new TextBlock();
        private readonly MetricBar _cpuBar;
        private readonly MetricBar _gpuBar;

        // 左侧能量图标
        private readonly Canvas _scene;
        private Ellipse _glow;
        private readonly ScaleTransform _glowScale = new ScaleTransform(1, 1);
        private Path _bolt;
        private readonly List<Path> _sparks = new List<Path>();
        private readonly List<TranslateTransform> _sparkDrift = new List<TranslateTransform>();
        private readonly List<Vector> _sparkDirection = new List<Vector>();
        private readonly SolidColorBrush _ink = new SolidColorBrush(Theme.AccentGreen);
        private readonly SolidColorBrush _sparkInk = new SolidColorBrush(Theme.AccentGreen);
        private readonly RadialGradientBrush _glowFill = new RadialGradientBrush();
        private readonly FrameLoop _loop;

        private double _cpuTdp = PowerLimits.DefaultCpuTdpWatts;
        private double _gpuTdp = PowerLimits.DefaultGpuTdpWatts;
        private bool _cpuTdpKnown;
        private bool _cpuTdpFromMsr;

        private readonly Random _random = new Random();
        private readonly double[] _flickerLevels = new double[10];
        private double _phase;
        private double _load;
        private double _nextFlickerIn;
        private double _flickerLeft;
        private double _flickerLength;
        private int _flickerSteps;
        private bool _disposed;
        private bool _hover;
        private Color _sceneColour = Theme.AccentGreen;
        private MetricTone _tone = MetricTone.Normal;
        private MetricTone _cpuTone = MetricTone.Normal;
        private MetricTone _gpuTone = MetricTone.Normal;

        /// <summary>当前分档，供断言与测试读取。</summary>
        public MetricTone Tone { get { return _tone; } }

        /// <summary>True while the frame loop is actually running; read by the UI suite.</summary>
        public bool IsAnimating { get { return _loop.IsRunning; } }

        /// <summary>标题行齿轮的入口，由概览页接到设置窗口。</summary>
        public Action OpenSettings { get; set; }

        /// <summary>CPU 功耗上限显示 TDP（基础功耗）还是 MTP（最大睿频功耗）。</summary>
        public string CpuPowerMode { get; set; }

        public PowerCard()
        {
            _loop = new FrameLoop(this, OnFrame);
            _nextFlickerIn = NextFlickerDelay();
            CpuPowerMode = AppSettings.CpuPowerMtp;

            _card = new Card("功耗", BuildBoltGlyph());
            AutomationProperties.SetAutomationId(_card, "PowerCard");
            // 统计范围改成标题旁的提示徽标：徽标在标题行上，悬停展开那句话。
            _card.AddTitleSuffix(BuildScopeHint());
            _card.HeaderRight.Content = BuildHeaderRight();

            _cpuBar = BuildBar(Theme.AccentGreen, "PowerCpuBar");
            _gpuBar = BuildBar(Theme.AccentGreenSoft, "PowerGpuBar");

            _scene = BuildScene();
            _card.Content.Children.Add(BuildBody());

            Children.Add(_card);

            Loaded += (sender, e) => StartIfAllowed();
            Unloaded += (sender, e) => Stop();
            IsVisibleChanged += (sender, e) => { if ((bool)e.NewValue) StartIfAllowed(); else Stop(); };
            // 悬停门控只在「悬停时播放动画」档生效；其他档由 FrameLoop 按渲染策略/环境上限决定。
            MouseEnter += (sender, e) => { _hover = true; StartIfAllowed(); };
            MouseLeave += (sender, e) => { _hover = false; if (RenderEnvironment.HoverOnlyCards) Stop(); };
            RenderEnvironment.Changed += OnRenderEnvironmentChanged;
        }

        private void OnRenderEnvironmentChanged()
        {
            if (_disposed) return;
            if (RenderEnvironment.HoverOnlyCards && !_hover) Stop();
            else StartIfAllowed();
        }

        /// <summary>分段/胶囊样式由通用设置的「进度样式」统一控制，不只作用于设备四格。</summary>
        public void SetBarStyle(MetricBarStyle style)
        {
            _cpuBar.BarStyle = style;
            _gpuBar.BarStyle = style;
        }

        /// <summary>Called from the overview tick (about once per second).</summary>
        public void Update(MetricsSnapshot snapshot, bool alive)
        {
            if (!alive)
            {
                ShowInvalid("传感器未运行");
                return;
            }

            bool cpuValid = snapshot != null &&
                            snapshot.Has(MetricFlags.CpuPowerValid) &&
                            snapshot.CpuPowerWatts > 0;
            bool gpuValid = snapshot != null &&
                            snapshot.Has(MetricFlags.GpuPowerValid) &&
                            snapshot.GpuPowerWatts > 0;

            RefreshLimits(snapshot);
            double cpuWatts = cpuValid ? snapshot.CpuPowerWatts : 0;
            double gpuWatts = gpuValid ? snapshot.GpuPowerWatts : 0;
            double cpuFraction = cpuValid ? Math.Min(1.0, cpuWatts / _cpuTdp) : 0;
            double gpuFraction = gpuValid ? Math.Min(1.0, gpuWatts / _gpuTdp) : 0;

            _cpuValue.Text = cpuValid ? FormatWatts(cpuWatts) : MetricFormat.Invalid;
            _cpuUnit.Text = cpuValid ? "W" : string.Empty;
            _gpuValue.Text = gpuValid ? FormatWatts(gpuWatts) : MetricFormat.Invalid;
            _gpuUnit.Text = gpuValid ? "W" : string.Empty;

            _cpuBar.SetToneSmooth(cpuValid ? ColourFor(MetricTones.Generic(cpuFraction * 100)) : Theme.TextSecondary, 320);
            _gpuBar.SetToneSmooth(gpuValid ? ColourFor(MetricTones.Generic(gpuFraction * 100)) : Theme.TextSecondary, 320);
            _cpuBar.SetValue(cpuFraction);
            _gpuBar.SetValue(gpuFraction);

            // 分档看“最接近自己上限的那一路”：GPU 单路吃满时不该被空闲的 CPU 摊平。
            _cpuTone = MetricTones.Generic(cpuFraction * 100);
            _gpuTone = MetricTones.Generic(gpuFraction * 100);
            _load = Math.Max(cpuFraction, gpuFraction);
            _tone = MetricTones.Generic(Math.Max(cpuFraction, gpuFraction) * 100);
            ApplySceneTone(ColourFor(_tone));
            ApplyCardStyle();

            if (cpuValid && gpuValid)
            {
                _total.Text = (cpuWatts + gpuWatts).ToString("0.0", CultureInfo.InvariantCulture);
                _totalUnit.Text = "W";
                _total.Foreground = Theme.TextPrimaryBrush;
                ShowScopeNote();
            }
            else
            {
                _total.Text = MetricFormat.Invalid;
                _totalUnit.Text = string.Empty;
                _total.Foreground = Theme.TextSecondaryBrush;
                _note.Text = !cpuValid && !gpuValid
                    ? "CPU 与 GPU 功耗读取失败"
                    : "缺少 CPU 或 GPU 功耗";
                _note.Foreground = Theme.AccentOrangeBrush;
                _note.Visibility = Visibility.Visible;
            }

            // 合计文案上面才定下来，环心用它，所以放在最后。
            UpdateRings(cpuFraction, gpuFraction, cpuValid, gpuValid, _total.Text);

            StartIfAllowed();
        }

        private void RefreshLimits(MetricsSnapshot snapshot)
        {
            string cpuModel = HardwareInfo.CpuSignature;
            bool mtp = !string.Equals(CpuPowerMode, AppSettings.CpuPowerTdp, StringComparison.OrdinalIgnoreCase);
            // 两个固件槽：基础功耗在 170（0x614），主板配置的上限在 370（0x610 PL1/PL2）。
            ushort firmwareBase = snapshot == null ? (ushort)0 : snapshot.CpuTdpWatts;
            ushort firmwareLimit = snapshot == null ? (ushort)0 : snapshot.CpuPowerLimitWatts;

            // MTP（最大睿频功耗）是厂商给这个 SKU 定的固定值，只能来自规格表；
            // PL1/PL2 是主板抄给这台机器的配置，拿它冒充 MTP 会把“解锁的 4096 W”显示成规格。
            CpuSpecEntry spec = null;
            CpuSpecTable table = CpuSpecs.Current;
            bool hasSpec = table != null && table.TryLookup(cpuModel, out spec);

            _cpuTdpFromMsr = false;
            if (mtp)
            {
                if (hasSpec)
                {
                    _cpuTdp = spec.MaxWatts;
                    _cpuTdpKnown = true;
                }
                else if (PowerLimits.CpuMaxPowerKnown(cpuModel))
                {
                    _cpuTdp = PowerLimits.CpuMaxPower(cpuModel);
                    _cpuTdpKnown = true;
                }
                else
                {
                    _cpuTdp = PowerLimits.DefaultCpuMaxWatts;
                    _cpuTdpKnown = false;
                }
            }
            else if (firmwareBase > 0)
            {
                _cpuTdp = firmwareBase;
                _cpuTdpKnown = true;
                _cpuTdpFromMsr = true;
            }
            else if (hasSpec)
            {
                _cpuTdp = spec.BaseWatts;
                _cpuTdpKnown = true;
            }
            else
            {
                double cpuTdp = PowerLimits.CpuTdp(cpuModel);
                if (cpuTdp > 0) _cpuTdp = cpuTdp;
                _cpuTdpKnown = PowerLimits.CpuTdpKnown(cpuModel);
            }

            // GPU 上限先问驱动（enforced power limit 就是这块卡的 TDP），拿不到才退回型号表。
            double driverLimit = GpuPowerLimit.ReadWatts();
            double gpuTdp = driverLimit > 0 ? driverLimit : PowerLimits.GpuTdp(HardwareInfo.GpuName);
            if (gpuTdp > 0) _gpuTdp = gpuTdp;

            _cpuTdpText.Text = (mtp ? "MTP " : "TDP ") + _cpuTdp.ToString("0", CultureInfo.InvariantCulture) + " W" + (_cpuTdpKnown ? string.Empty : "*");
            string source = mtp
                ? (hasSpec
                    ? "规格表里的最大睿频功耗（MTP）"
                    : (_cpuTdpKnown
                        ? "型号表里的最大睿频功耗（MTP）"
                        : "未登记该型号的最大睿频功耗，当前数是估算值"))
                : (_cpuTdpFromMsr
                    ? "固件 MSR 0x614 报的处理器基础功耗（PBP）"
                    : (hasSpec ? "规格表里的处理器基础功耗（TDP/PBP）" : "型号表估算的处理器基础功耗（TDP/PBP）"));
            // PL1/PL2 不做标签，但能帮人判断这台机器有没有被解锁，放在 tooltip 里补一句。
            if (firmwareLimit > 0)
            {
                source += "；本机配置上限 " + firmwareLimit.ToString(CultureInfo.InvariantCulture) + " W（MSR 0x610，不是规格值）";
            }
            _cpuTdpText.ToolTip = source;
            _gpuTdpText.Text = "TDP " + _gpuTdp.ToString("0", CultureInfo.InvariantCulture) + " W";
            _gpuTdpText.ToolTip = driverLimit > 0
                ? "显卡驱动报告的功耗上限（" + GpuPowerLimit.Source + "）"
                : "显卡型号表；驱动没有报告功耗上限";

            _cpuBar.ToolTip = "CPU 功耗 / 上限 " + _cpuTdp.ToString("0", CultureInfo.InvariantCulture) + " W";
            _gpuBar.ToolTip = "GPU 功耗 / TDP " + _gpuTdp.ToString("0", CultureInfo.InvariantCulture) + " W";
        }

        private void ShowInvalid(string reason)
        {
            _total.Text = MetricFormat.Invalid;
            _totalUnit.Text = string.Empty;
            _total.Foreground = Theme.TextSecondaryBrush;
            _cpuValue.Text = MetricFormat.Invalid;
            _cpuUnit.Text = string.Empty;
            _gpuValue.Text = MetricFormat.Invalid;
            _gpuUnit.Text = string.Empty;
            _cpuBar.SetTone(Theme.TextSecondary);
            _gpuBar.SetTone(Theme.TextSecondary);
            _cpuBar.SetValue(0);
            _gpuBar.SetValue(0);
            _load = 0;
            _tone = MetricTone.Normal;
            ApplySceneTone(Theme.TextSecondary);
            _note.Text = reason;
            _note.Foreground = Theme.AccentOrangeBrush;
            _note.Visibility = Visibility.Visible;
            _ringCpuPath.Data = null;
            _ringGpuPath.Data = null;
            _ringTotal.Text = MetricFormat.Invalid;
            _ringUnit.Text = string.Empty;
            Stop();
        }

        private FrameworkElement BuildHeaderRight()
        {
            // 统计范围只留在标题旁边的「?」徽标里：这里再写一句「CPU + GPU」是重复信息。
            FlatButton gear = new FlatButton(string.Empty);
            gear.Width = 22;
            gear.Height = 22;
            gear.Padding = new Thickness(0);
            gear.Background = Brushes.Transparent;
            gear.Foreground = Theme.TextSecondaryBrush;
            gear.BorderThickness = new Thickness(0);
            gear.Cursor = System.Windows.Input.Cursors.Hand;
            gear.VerticalAlignment = VerticalAlignment.Center;
            gear.ToolTip = "功耗设置";
            TextBlock glyph = new TextBlock
            {
                Text = "\uE713",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            glyph.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding("Foreground") { Source = gear });
            gear.Content = glyph;
            gear.MouseEnter += (sender, e) => gear.Foreground = Theme.TextPrimaryBrush;
            gear.MouseLeave += (sender, e) => gear.Foreground = Theme.TextSecondaryBrush;
            gear.Click += (sender, e) => { Action handler = OpenSettings; if (handler != null) handler(); };
            AutomationProperties.SetAutomationId(gear, "PowerSettings");
            AutomationProperties.SetName(gear, "功耗设置");
            gear.HorizontalAlignment = HorizontalAlignment.Right;
            // 与其他卡片一致：平时不显示，指针进入卡片才淡入。
            HoverReveal.Attach(_card, gear);
            return gear;
        }

        /// <summary>底部一行：正常状态不占行（统计范围在标题徽标的 tooltip 里），异常时写原因。</summary>
        private void ShowScopeNote()
        {
            _note.Foreground = Theme.TextSecondaryBrush;
            _note.Text = _cpuTdpKnown
                ? string.Empty
                : "CPU 型号未登记，" + (string.Equals(CpuPowerMode, AppSettings.CpuPowerTdp, StringComparison.OrdinalIgnoreCase) ? "TDP" : "MTP") + " 按 " +
                  _cpuTdp.ToString("0", CultureInfo.InvariantCulture) + " W 估算";
            _note.Visibility = _note.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// 标题旁的提示徽标：统计范围从小字行改成 tooltip，入口放在标题后面，
        /// 不再在卡片底部挤一行灰字。
        /// </summary>
        private static FrameworkElement BuildScopeHint()
        {
            TextBlock mark = new TextBlock
            {
                Text = "?",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextSecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Border badge = new Border
            {
                Width = 15,
                Height = 15,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.BorderStrongBrush,
                Background = Brushes.Transparent,
                Child = mark,
                // tooltip 限宽换行：不限制的话一句话会拉成一整行，跑到主窗口外面去。
                ToolTip = new TextBlock
                {
                    Text = "只统计 CPU 封装与 GPU 模块；不含主板、硬盘、风扇以及其他硬件数据和电源损耗。",
                    MaxWidth = 280,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            AutomationProperties.SetAutomationId(badge, "PowerScopeHint");
            AutomationProperties.SetName(badge, "功耗统计范围");
            return badge;
        }

        private static string FormatWatts(double watts)
        {
            return watts.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static Color ColourFor(MetricTone tone)
        {
            if (tone == MetricTone.Danger) return Theme.AccentRed;
            if (tone == MetricTone.Warning) return Theme.AccentOrange;
            return Theme.AccentGreen;
        }

        // ---- 布局 ----

        private FrameworkElement BuildBody()
        {
            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconWidth) });
            // 图标列与读数之间留一格：4 px 在放大后的闪电旁看起来是粘住的。
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            // 环样式专用的两列：条形样式下宽度为 0，所以两种样式共用同一个 body。
            _ringColumn = new ColumnDefinition { Width = new GridLength(0) };
            _ringGap = new ColumnDefinition { Width = new GridLength(0) };
            body.ColumnDefinitions.Add(_ringColumn);
            body.ColumnDefinitions.Add(_ringGap);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(_scene);

            _ringHost = BuildRingCluster();
            Grid.SetColumn(_ringHost, 2);
            body.Children.Add(_ringHost);

            StackPanel readout = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _totalLine = BuildTotalLine();
            readout.Children.Add(_totalLine);
            readout.Children.Add(BuildReadout("CPU", _cpuValue, _cpuUnit, _cpuTdpText, _cpuBar, "PowerCpu", 8, out _cpuBarColumn));
            readout.Children.Add(BuildReadout("GPU", _gpuValue, _gpuUnit, _gpuTdpText, _gpuBar, "PowerGpu", 0, out _gpuBarColumn));
            // 说明行跟合计、CPU / GPU 同一条左边缘：卡片里所有文字都从这一列起。
            readout.Children.Add(BuildNote());
            Grid.SetColumn(readout, 4);
            body.Children.Add(readout);
            return body;
        }

        private FrameworkElement BuildTotalLine()
        {
            TextBlock label = new TextBlock
            {
                Text = "合计",
                FontSize = 11.5,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 12, 7)
            };
            AutomationProperties.SetAutomationId(label, "PowerTotalLabel");

            _total.Text = MetricFormat.Invalid;
            MetricTypography.ApplyValue(_total);
            _total.Foreground = Theme.TextSecondaryBrush;
            AutomationProperties.SetAutomationId(_total, "PowerTotal");

            _totalUnit.Text = string.Empty;
            MetricTypography.ApplyUnit(_totalUnit);
            _totalUnit.Foreground = Theme.TextSecondaryBrush;
            _totalUnit.VerticalAlignment = VerticalAlignment.Bottom;
            _totalUnit.Margin = new Thickness(4, 0, 0, 4);
            AutomationProperties.SetAutomationId(_totalUnit, "PowerUnit");

            StackPanel line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            line.Children.Add(label);
            line.Children.Add(_total);
            line.Children.Add(_totalUnit);
            return line;
        }

        /// <summary>
        /// 一行明细：标签与 18 px 数值在左，分段条在右，条尾是 TDP 上限。
        /// 条宽固定成设备四格的格子宽度，16 段才不会被拉成方块。
        /// </summary>
        private static FrameworkElement BuildReadout(string name, TextBlock value, TextBlock unit,
                                                     TextBlock tdp, MetricBar bar, string automationId,
                                                     double bottomMargin, out ColumnDefinition barColumn)
        {
            TextBlock label = new TextBlock
            {
                Text = name,
                FontSize = 11.5,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 12, 4)
            };
            AutomationProperties.SetAutomationId(label, automationId + "Label");

            value.Text = MetricFormat.Invalid;
            value.FontSize = ComponentValueSize;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = Theme.TextPrimaryBrush;
            value.VerticalAlignment = VerticalAlignment.Bottom;
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            AutomationProperties.SetAutomationId(value, automationId);

            unit.Text = string.Empty;
            unit.FontSize = 12;
            unit.Foreground = Theme.TextSecondaryBrush;
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            unit.Margin = new Thickness(3, 0, 0, 2);
            AutomationProperties.SetAutomationId(unit, automationId + "Unit");

            tdp.Text = string.Empty;
            tdp.FontSize = 11;
            tdp.Foreground = Theme.TextSecondaryBrush;
            tdp.VerticalAlignment = VerticalAlignment.Center;
            tdp.Margin = new Thickness(10, 0, 0, 0);
            AutomationProperties.SetAutomationId(tdp, automationId + "Tdp");

            StackPanel line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            line.Children.Add(label);
            line.Children.Add(value);
            line.Children.Add(unit);

            Grid row = new Grid { Margin = new Thickness(0, 0, 0, bottomMargin) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barColumn = new ColumnDefinition { Width = new GridLength(BarWidth) };
            row.ColumnDefinitions.Add(barColumn);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(line);
            Grid.SetColumn(bar, 2);
            bar.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(bar);
            Grid.SetColumn(tdp, 3);
            row.Children.Add(tdp);
            return row;
        }

        private FrameworkElement BuildNote()
        {
            _note.Text = string.Empty;
            _note.FontSize = 11;
            _note.Foreground = Theme.TextSecondaryBrush;
            _note.TextWrapping = TextWrapping.NoWrap;
            _note.TextTrimming = TextTrimming.CharacterEllipsis;
            _note.Margin = new Thickness(0, 10, 0, 0);
            AutomationProperties.SetAutomationId(_note, "PowerNote");
            return _note;
        }

        // ---- 左侧能量图标 ----

        private Canvas BuildScene()
        {
            Canvas scene = new Canvas
            {
                Width = IconWidth,
                Height = IconHeight,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(scene, "PowerIcon");

            _glowFill.GradientOrigin = new Point(0.5, 0.5);
            _glowFill.Center = new Point(0.5, 0.5);
            _glowFill.RadiusX = 0.5;
            _glowFill.RadiusY = 0.5;
            _glowFill.GradientStops.Add(new GradientStop(WithAlpha(Theme.AccentGreen, 130), 0));
            _glowFill.GradientStops.Add(new GradientStop(WithAlpha(Theme.AccentGreen, 0), 1));
            _glow = new Ellipse
            {
                Width = IconWidth,
                Height = IconWidth,
                Fill = _glowFill,
                Opacity = 0.25,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _glowScale
            };
            Canvas.SetTop(_glow, (IconHeight - IconWidth) / 2);
            scene.Children.Add(_glow);

            _bolt = new Path
            {
                Data = Geometry.Parse("M 11,1 L 3,11 L 8,11 L 7,17 L 15,6 L 10,6 Z"),
                Fill = _ink,
                Stretch = Stretch.Uniform,
                Width = 58,
                Height = 80,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_bolt, (IconWidth - 58) / 2);
            Canvas.SetTop(_bolt, (IconHeight - 80) / 2);
            scene.Children.Add(_bolt);

            // 三簇电弧错开相位跳动，比给整块图标做缩放更像“带电”；静止时它们收起，只留闪电与光晕。
            AddSpark(scene, "M 0,0 L 7,11 L 1,17 L 9,28", 3, 29, -18, -1.0, 0.2);
            AddSpark(scene, "M 0,0 L 5,8 L 1,15 L 8,23", 75, 16, 22, 1.0, -0.3);
            AddSpark(scene, "M 0,0 L 7,9 L 1,16 L 9,25", 72, 80, -14, 1.0, 0.4);
            return scene;
        }

        private void AddSpark(Canvas scene, string data, double left, double top, double angle,
                              double dx, double dy)
        {
            TranslateTransform drift = new TranslateTransform(0, 0);
            TransformGroup group = new TransformGroup();
            group.Children.Add(new RotateTransform(angle, 0, 0));
            group.Children.Add(drift);

            Path spark = new Path
            {
                Data = Geometry.Parse(data),
                Stroke = _sparkInk,
                StrokeThickness = 2.1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.25,
                IsHitTestVisible = false,
                RenderTransform = group
            };
            Canvas.SetLeft(spark, left);
            Canvas.SetTop(spark, top);
            scene.Children.Add(spark);
            _sparks.Add(spark);
            _sparkDrift.Add(drift);
            _sparkDirection.Add(new Vector(dx, dy));
        }

        private void OnFrame(double dt)
        {
            if (dt <= 0) return;
            _phase = (_phase + dt * (0.35 + 1.10 * _load)) % 1.0;

            // 接触不良式的闪烁：每 1–10 秒随机来一次短促的断/续，跟电弧的节奏分开。
            double bolt = 1.0;
            if (_flickerLeft > 0)
            {
                _flickerLeft -= dt;
                double elapsed = 1.0 - Math.Max(0.0, _flickerLeft) / _flickerLength;
                int index = Math.Min(_flickerSteps - 1, (int)(elapsed * _flickerSteps));
                bolt = _flickerLevels[index];
            }
            else
            {
                _nextFlickerIn -= dt;
                if (_nextFlickerIn <= 0)
                {
                    BeginFlicker();
                    _nextFlickerIn = NextFlickerDelay();
                }
            }

            _bolt.Opacity = bolt;
            SceneFrame(bolt);
        }

        private double NextFlickerDelay()
        {
            return 1.0 + _random.NextDouble() * 9.0;
        }

        /// <summary>一次短促的断续：6–10 个随机亮/灭台阶，0.30–0.75 秒走完。</summary>
        private void BeginFlicker()
        {
            _flickerLength = 0.30 + _random.NextDouble() * 0.45;
            _flickerLeft = _flickerLength;
            _flickerSteps = 6 + _random.Next(5);
            bool dropout = false;
            for (int i = 0; i < _flickerSteps; i++)
            {
                bool off = _random.NextDouble() < 0.45;
                _flickerLevels[i] = off ? 0.12 : 1.0;
                dropout |= off;
            }
            if (!dropout) _flickerLevels[1] = 0.12;
        }

        private void SceneFrame(double boltOpacity)
        {
            int count = _sparks.Count;
            for (int i = 0; i < count; i++)
            {
                double t = (_phase + (double)i / count) % 1.0;
                double pulse = Math.Sin(t * Math.PI);
                _sparks[i].Opacity = 0.12 + 0.82 * pulse;
                Vector direction = _sparkDirection[i];
                _sparkDrift[i].X = direction.X * (3 + 5 * pulse);
                _sparkDrift[i].Y = direction.Y * (3 + 5 * pulse);
            }

            double breathe = 0.5 - 0.5 * Math.Cos(_phase * 2.0 * Math.PI);
            _glow.Opacity = (0.16 + 0.12 * _load + 0.08 * breathe) * (0.35 + 0.65 * boltOpacity);
            double scale = 0.96 + 0.05 * breathe;
            _glowScale.ScaleX = scale;
            _glowScale.ScaleY = scale;
        }

        private void RenderStatic()
        {
            for (int i = 0; i < _sparks.Count; i++)
            {
                _sparks[i].Opacity = 0;
                _sparkDrift[i].X = 0;
                _sparkDrift[i].Y = 0;
            }
            _bolt.Opacity = 1.0;
            _glow.Opacity = 0.16 + 0.12 * _load + 0.05;
            _glowScale.ScaleX = 1.0;
            _glowScale.ScaleY = 1.0;
        }

        private void StartIfAllowed()
        {
            if (_disposed) return;
            if (RenderEnvironment.HoverOnlyCards && !_hover)
            {
                Stop();
                return;
            }
            if (!SystemParameters.ClientAreaAnimation)
            {
                RenderStatic();
                return;
            }
            _loop.Start();
            SceneFrame(_bolt.Opacity);
        }

        private void Stop()
        {
            _loop.Stop();
            _flickerLeft = 0;
            RenderStatic();
        }

        private void ApplySceneTone(Color colour)
        {
            if (_sceneColour == colour) return;
            _sceneColour = colour;
            Color glow = WithAlpha(colour, 130);

            if (RenderEnvironment.AnimationsDisabled)
            {
                _ink.Color = colour;
                _sparkInk.Color = colour;
                _glowFill.GradientStops[0].Color = glow;
                return;
            }

            Duration span = new Duration(TimeSpan.FromMilliseconds(320));
            QuadraticEase ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            _ink.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(colour, span) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
            _sparkInk.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(colour, span) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
            _glowFill.GradientStops[0].BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(glow, span) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        }

        private static Color WithAlpha(Color colour, byte alpha)
        {
            return Color.FromArgb(alpha, colour.R, colour.G, colour.B);
        }

        private static MetricBar BuildBar(Color colour, string automationId)
        {
            MetricBar bar = new MetricBar(colour, Theme.TrackGrey)
            {
                Height = BarHeight,
                Width = BarWidth,
                BarStyle = MetricBarStyle.Segments,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetAutomationId(bar, automationId);
            return bar;
        }

        private static FrameworkElement BuildBoltGlyph()
        {
            Path bolt = new Path
            {
                Data = Geometry.Parse("M 11,1 L 3,11 L 8,11 L 7,17 L 15,6 L 10,6 Z"),
                Fill = Theme.TextSecondaryBrush,
                Stretch = Stretch.Uniform
            };
            return new Viewbox
            {
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                Child = bolt
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RenderEnvironment.Changed -= OnRenderEnvironmentChanged;
            _loop.Dispose();
        }
    }
}
