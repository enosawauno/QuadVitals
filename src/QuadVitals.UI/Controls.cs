using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using QuadVitals.Core;

namespace QuadVitals.UI
{
    /// <summary>Dark card container used across the dashboard.</summary>
    public class Card : Border
    {
        private readonly StackPanel _content;
        private readonly TextBlock _title;
        private readonly ContentControl _headerRight;
        private readonly StackPanel _titleGroup;

        public Card(string title) : this(title, (FrameworkElement)null)
        {
        }

        /// <summary>标题左侧的系统图标字形（Segoe MDL2 Assets）。</summary>
        public Card(string title, string glyph) : this(title, GlyphText(glyph))
        {
        }

        /// <summary>标题左侧的自绘图标；供“系统字形在小尺寸下只剩骨架”的场合使用。</summary>
        public Card(string title, FrameworkElement icon)
        {
            // The reference cards are flat #303030 blocks on #191919 with no visible border.
            Background = Theme.SurfaceCardBrush;
            BorderThickness = new Thickness(0);
            CornerRadius = new CornerRadius(Theme.CardRadius);
            Padding = new Thickness(Theme.CardPadding);
            SnapsToDevicePixels = true;

            StackPanel root = new StackPanel();
            _title = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            StackPanel titleGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            _titleGroup = titleGroup;
            if (icon != null) titleGroup.Children.Add(icon);
            titleGroup.Children.Add(_title);
            _headerRight = new ContentControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.Children.Add(titleGroup);
            Grid.SetColumn(_headerRight, 1);
            header.Children.Add(_headerRight);
            root.Children.Add(header);

            _content = new StackPanel();
            root.Children.Add(_content);

            Child = root;
        }

        /// <summary>与标题行（15 px 字高、右侧 7 px 间距）对齐的系统字形块。</summary>
        private static FrameworkElement GlyphText(string glyph)
        {
            if (string.IsNullOrEmpty(glyph)) return null;
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
        }

        /// <summary>
        /// 换掉标题左侧的图标。系统字形在小尺寸下只剩几条细线时，卡片改成手绘图标的入口。
        /// </summary>
        public void ReplaceIcon(FrameworkElement icon)
        {
            if (icon == null || _titleGroup == null) return;
            if (_titleGroup.Children.Count > 1) _titleGroup.Children.RemoveAt(0);
            _titleGroup.Children.Insert(0, icon);
        }

        public string Title
        {
            get { return _title.Text; }
            set { _title.Text = value; }
        }

        public StackPanel Content { get { return _content; } }

        /// <summary>Optional content on the title row (right-aligned), e.g. compact IP info.</summary>
        public ContentControl HeaderRight { get { return _headerRight; } }
    }

    /// <summary>Vertical gauge: value bar plus label + big number (device status card).</summary>
    public class MetricGauge : StackPanel
    {
        private readonly Border _track;
        private readonly Border _fill;
        private readonly TextBlock _value;
        private readonly TextBlock _label;
        private readonly TextBlock _sub;
        private readonly Color _accent;

        public MetricGauge(string label, Color accent)
        {
            _accent = accent;
            Orientation = Orientation.Vertical;
            HorizontalAlignment = HorizontalAlignment.Center;
            Margin = new Thickness(10, 0, 10, 0);

            _track = new Border
            {
                Width = 34,
                Height = 96,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Theme.TrackGrey),
                VerticalAlignment = VerticalAlignment.Bottom,
                ClipToBounds = true
            };

            _fill = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(accent),
                Height = 0,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _track.Child = _fill;

            _value = new TextBlock
            {
                Text = "--",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };

            _label = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _sub = new TextBlock
            {
                Text = string.Empty,
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            Children.Add(_track);
            Children.Add(_value);
            Children.Add(_label);
            Children.Add(_sub);
        }

        public double MaxValue { get; set; }

        /// <summary>value in 0..1, NaN for "no data" (renders "--" and an empty bar).</summary>
        public void SetValue(double normalized, string text, string sub)
        {
            if (double.IsNaN(normalized))
            {
                _fill.Height = 0;
                _value.Text = "--";
                _value.Foreground = Theme.TextTertiaryBrush;
            }
            else
            {
                double clamped = Math.Max(0, Math.Min(1, normalized));
                _fill.Height = Math.Max(2, (_track.Height - 4) * clamped);
                _value.Text = text;
                _value.Foreground = Theme.TextPrimaryBrush;
            }
            _sub.Text = sub ?? string.Empty;
        }
    }

    /// <summary>
    /// Pill progress bar used by the overview tiles: a rounded grey
    /// track with an accent fill. Replaces the earlier 20-dash bar, which read as too fiddly.
    /// </summary>
    public class MetricBar : FrameworkElement
    {
        /// <summary>填充值上涨/回落速度（比重/秒），等价于 60 fps 下每帧 +1.2 / -2.4 个百分点。</summary>
        /// <summary>小于这个幅度（占满量程的比例）直接落位：常见的一两格抖动不值得开帧循环。</summary>
        private const double SnapBelow = 0.03;

        private const double FillRisePerSecond = 1.2 * 60 / 100.0;
        private const double FillFallPerSecond = 2.4 * 60 / 100.0;

        /// <summary>分段指示器的小段数。</summary>
        public const int SegmentCount = 16;

        private Brush _off;
        private Brush _on;
        private Color _onColor;
        private Color _offColor;
        private double _value;
        private MetricBarStyle _style = MetricBarStyle.Segments;

        public MetricBar(Color on, Color off)
        {
            _onColor = on;
            _offColor = off;
            // The fill brush stays mutable so a band change can cross-fade its colour instead of
            // swapping a frozen brush and blinking.
            _on = new SolidColorBrush(on);
            _off = Freeze(new SolidColorBrush(off));
            Height = 22;
            SnapsToDevicePixels = true;
            MouseEnter += OnMouseEnter;
            MouseLeave += OnMouseLeave;
        }

        /// <summary>胶囊条（连成一体）或 16 个小分段。</summary>
        public MetricBarStyle BarStyle
        {
            get { return _style; }
            set
            {
                if (_style == value) return;
                _style = value;
                InvalidateVisual();
            }
        }

        // ---- I/O 走马循环（滚动相位 + 刹车接回静态色阶） ----
        private double _phase;
        private double _speed;
        private double _release = 1;
        private double _waveTarget;
        private double _wavedOpacity;
        private double _speedTarget;
        private double _displayValue;
        private bool _displayInitialised;
        private FrameLoop _loop;

        /// <summary>
        /// 打开/更新走马循环。waveOpacity 是阈值附近的淡入度（0 = 纯静态色阶），
        /// cyclesPerSecond 是目标转速；两者都是**目标值**，内部按帧缓动逼近，所以加速、减速、
        /// 跨过阈值都不会跳变。
        /// </summary>
        public void SetIoWave(double waveOpacity, double cyclesPerSecond)
        {
            // 系统关了“动画效果”（WPF 侧的 prefers-reduced-motion）：只上淡入度，不转。
            if (!System.Windows.SystemParameters.ClientAreaAnimation) cyclesPerSecond = 0;
            if (RenderEnvironment.AnimationsDisabled)
            {
                // RDP / Tier 0：保持静态色阶，既不滚动也不缓动。
                _waveTarget = 0;
                _speedTarget = 0;
                _release = 1;
                _wavedOpacity = 0;
                Unhook();
                InvalidateVisual();
                return;
            }
            _waveTarget = waveOpacity <= 0 ? 0 : (waveOpacity > 1 ? 1 : waveOpacity);
            _speedTarget = cyclesPerSecond <= 0 ? 0 : cyclesPerSecond;
            if (_waveTarget > 0.001 || _speedTarget > 0.001) Hook();
        }

        /// <summary>
        /// 走马（磁盘 I/O 的滚动色阶）是否只在鼠标停在这一格时才跑。
        /// 分层窗口里任何一帧动画都会整窗重推，常驻的走马等于常驻几成 GPU；
        /// 数值与格子在静止时照样是正确的，丢掉的只是“在滚”这个装饰。
        /// </summary>
        public bool WaveOnHover { get; set; }

        /// <summary>True while the frame loop is actually running; read by the UI suite.</summary>
        public bool IsRunningForTests
        {
            get { return _loop != null && _loop.IsRunning; }
        }

        private bool _pointerInside;

        private void Hook()
        {
            // 用自己记的指针状态而不是 IsMouseOver：后者要真实命中测试，
            // 合成事件（测试、键盘路径）下永远为 false。
            if (WaveOnHover && !_pointerInside) return;
            if (_loop == null) _loop = new FrameLoop(this, Tick);
            _loop.Start();
            Unloaded -= OnUnloaded;
            Unloaded += OnUnloaded;
        }

        private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _pointerInside = true;
            if (WaveOnHover) Hook();
        }

        private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _pointerInside = false;
            if (WaveOnHover) Unhook();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Unhook();
        }

        private void Unhook()
        {
            if (_loop != null) _loop.Stop();
            Unloaded -= OnUnloaded;
            InvalidateVisual();
        }

        private void Tick(double dt)
        {
            if (dt <= 0) return;

            // 显示用填充值缓动（与走马同一帧循环推进）。
            double displayTarget = _waveTarget > 0.001 ? 1.0 : _value;
            double step = dt * (displayTarget > _displayValue ? FillRisePerSecond : FillFallPerSecond);
            if (Math.Abs(displayTarget - _displayValue) <= step) _displayValue = displayTarget;
            else _displayValue += displayTarget > _displayValue ? step : -step;

            _speed += (_speedTarget - _speed) * Math.Min(1, dt * 3.5);
            if (Math.Abs(_speedTarget - _speed) < 0.002) _speed = _speedTarget;
            _phase = (_phase + dt * _speed) % 1.0;

            double releaseTarget = _speedTarget > 0 ? 0 : 1;
            _release += (releaseTarget - _release) * Math.Min(1, dt * 2.2);
            if (Math.Abs(releaseTarget - _release) < 0.002) _release = releaseTarget;

            _wavedOpacity += (_waveTarget - _wavedOpacity) * Math.Min(1, dt * 3.5);
            if (Math.Abs(_waveTarget - _wavedOpacity) < 0.002) _wavedOpacity = _waveTarget;

            InvalidateVisual();

            bool settled = _speedTarget <= 0.001 && _waveTarget <= 0.001 &&
                           Math.Abs(_displayValue - _value) <= 0.001 &&
                           _speed <= 0.002 && _release >= 0.998 && _wavedOpacity <= 0.002;
            if (settled) { _speed = 0; _release = 1; _wavedOpacity = 0; Unhook(); }
        }

        /// <summary>Recolours the fill (normal / warning / danger); no-op when unchanged.</summary>
        public void SetTone(Color color)
        {
            if (_onColor == color) return;
            _onColor = color;
            SolidColorBrush brush = _on as SolidColorBrush;
            if (brush != null && !brush.IsFrozen) brush.Color = color;
            else _on = Freeze(new SolidColorBrush(color));
            InvalidateVisual();
        }

        /// <summary>Band change: ease the fill colour over <paramref name="milliseconds"/>.</summary>
        public void SetToneSmooth(Color color, int milliseconds)
        {
            if (RenderEnvironment.AnimationsDisabled)
            {
                SetTone(color);
                return;
            }
            if (_onColor == color) return;
            _onColor = color;
            SolidColorBrush brush = _on as SolidColorBrush;
            if (brush == null)
            {
                _on = Freeze(new SolidColorBrush(color));
            }
            else
            {
                if (brush.IsFrozen)
                {
                    brush = new SolidColorBrush(brush.Color);
                    _on = brush;
                }
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color,
                    TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                });
            }
            InvalidateVisual();
        }

        /// <summary>0..1; NaN paints an empty bar.</summary>
        public void SetValue(double value)
        {
            if (double.IsNaN(value)) value = 0;
            value = Math.Max(0, Math.Min(1, value));
            if (!_displayInitialised) { _displayValue = value; _displayInitialised = true; }
            if (Math.Abs(_value - value) < 0.001) return;
            _value = value;
            if (RenderEnvironment.AnimationsDisabled)
            {
                // 环境禁用：直接落位，不启动缓动循环。
                _displayValue = _value;
                Unhook();
                InvalidateVisual();
                return;
            }
            // 大幅变化才演：数值每秒都在动，若每次都从当前值缓动过去，这个逐帧循环几乎
            // 永不静止——而分层窗口每帧都要整窗重推（实测 GPU 8–10%、CPU 20–30%）。
            // 1–2 个百分点的日常抖动直接落位，肉眼看不出，代价却差一个数量级。
            if (Math.Abs(_displayValue - _value) < SnapBelow) _displayValue = _value;
            // 格子一格一格长，而不是瞬间全亮（每帧 +1.2 / -2.4 个百分点，
            // 60 fps 下约 72 %/s 上涨、144 %/s 回落；这里按秒换算，不依赖帧率）。
            if (Math.Abs(_displayValue - _value) > 0.001 || _waveTarget > 0.001) Hook();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0) return;

            if (_style == MetricBarStyle.Segments)
            {
                DrawSegments(dc, width, height);
                return;
            }

            double radius = height / 2.0;
            dc.DrawRoundedRectangle(_off, null, new Rect(0, 0, width, height), radius, radius);
            if (_value <= 0) return;

            // 保持胶囊形：极小值也别退化成一根细线。
            double filled = Math.Max(height, width * _value);
            Rect area = new Rect(0, 0, filled, height);
            dc.DrawRoundedRectangle(_on, null, area, radius, radius);
        }

        /// <summary>
        /// 16 个小段：填充数 = ceil(占用 × 16)；已点亮的段按索引在一条“浅 → 深”的色阶上取色，
        /// 而中高负载时把这段等比铺满整条色阶（否则 50% 看着像“刚起步”）。低负载（≤3 段）保持浅色，
        /// 安静。空槽用中性灰，不跟随指标色相。
        /// </summary>
        private void DrawSegments(DrawingContext dc, double width, double height)
        {
            double shown = _displayInitialised ? _displayValue : _value;
            int filled = shown <= 0 ? 0 : (int)Math.Ceiling(Math.Min(1.0, shown) * SegmentCount);
            // 圆柱形：四角只做轻微圆角（半径跟段宽走，封顶 3 px），而不是把半径为半个高的椭圆。
            // 圆角 4 px（18 px 高的条，不是胶囊）；段太窄时按宽度收窄。
            double radius = Math.Max(1.0, Math.Min(4.0, SegmentRect(0, width, height).Width / 3.0));
            // 门控：必须彻底填满（99.9%）才允许波浪启动，避免“边填边滚”。
            bool wave = _waveTarget > 0.001 && shown >= 0.999;
            for (int i = 0; i < SegmentCount; i++)
            {
                Brush brush;
                if (i < filled)
                {
                    if (wave)
                    {
                        // 走马：这一段的取色位置 = 静态相位与滚动相位按波浪可见度与刹车进度插值；
                        // 直接用连续位置取色（不是先取整到 16 档），颜色才不会一段一段地跳。
                        double position = QuadVitals.Core.IoWave.ColorPosition(
                            i, SegmentCount, _phase, Math.Max(_waveTarget, _wavedOpacity), _release);
                        brush = Freeze(new SolidColorBrush(RampColorAt(position)));
                    }
                    else
                    {
                        int rampIndex = (filled <= 3 || filled >= SegmentCount)
                            ? i
                            : (int)Math.Round((double)i / (filled - 1) * (SegmentCount - 1));
                        brush = Freeze(new SolidColorBrush(RampColor(rampIndex)));
                    }
                }
                else
                {
                    // 空槽保持中性灰：没占用的段不该带指标色相，否则整条看起来像“暗绿色进度”。
                    brush = Freeze(new SolidColorBrush(EmptySlotColor()));
                }
                dc.DrawRoundedRectangle(brush, null, SegmentRect(i, width, height), radius, radius);
            }
        }

        /// <summary>
        /// 第 i 个分段的矩形。高度与胶囊条完全一致（都占满控件高度），所以两种样式切换时
        /// 卡片高度不会跳；宽度按段数与间隙均分。
        /// </summary>
        internal static Rect SegmentRect(int index, double width, double height)
        {
            double gap = SegmentCount > 1 ? (width < 120 ? 2.0 : 4.0) : 0;
            double segWidth = (width - gap * (SegmentCount - 1)) / SegmentCount;
            if (segWidth <= 0.5)
            {
                gap = 0;
                segWidth = width / SegmentCount;
            }
            return new Rect(index * (segWidth + gap), 0, segWidth, height);
        }

        /// <summary>
        /// 第 i 段/比例的取色，在 OKLCh 里插值：色阶两端是浅端 oklch(L=0.9, C 不超过 0.06, H) 到
        /// 基色 oklch(L=0.55, C 不超过 0.16, H)，中间按位置混（等价 color-mix(in oklch)）。
        /// seed 是当前色调，所以警告/危险档自动跟着换相。
        /// </summary>
        private Color RampColor(int index)
        {
            double ratio = SegmentCount <= 1 ? 1.0 : Math.Min(1.0, Math.Max(0.0, (double)index / (SegmentCount - 1)));
            return RampColorAt(ratio);
        }

        private Color RampColorAt(double ratio)
        {
            OklchColor seed = OklchColor.FromRgb(_onColor.R, _onColor.G, _onColor.B);
            OklchColor light = seed.With(0.9, 0.06);
            OklchColor deep = seed.With(0.55, 0.16);
            byte[] rgb = OklchColor.Lerp(light, deep, ratio).ToRgb();
            return Color.FromRgb(rgb[0], rgb[1], rgb[2]);
        }

        /// <summary>空槽的颜色：中性灰，不参与指标色相，也不随警告/危险档变色。</summary>
        private Color EmptySlotColor()
        {
            return Color.FromRgb(0x3A, 0x3A, 0x3A);
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>How a <see cref="MetricBar"/> paints its progress.</summary>
    public enum MetricBarStyle
    {
        Capsule = 0,
        Segments = 1
    }

    /// <summary>
    /// One overview tile: small label, big value + unit, thick pill bar, caption.
    /// </summary>
    public class MetricTile : StackPanel
    {
        private readonly TextBlock _label = new TextBlock();
        private readonly TextBlock _value = new TextBlock();
        private readonly TextBlock _unit = new TextBlock();
        private readonly TextBlock _caption = new TextBlock();
        private readonly MetricBar _bar;
        private readonly Color _accent;

        public MetricTile(string label, Color accent)
        {
            Orientation = Orientation.Vertical;
            _accent = accent;
            _bar = new MetricBar(accent, Theme.TrackGrey);

            _label.Text = label;
            _label.FontSize = 12;
            _label.Foreground = Theme.TextTertiaryBrush;
            Children.Add(_label);

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _value.Text = "--";
            _value.FontSize = 34;
            _value.FontWeight = FontWeights.SemiBold;
            _value.Foreground = Theme.TextPrimaryBrush;
            row.Children.Add(_value);

            _unit.FontSize = 14;
            _unit.Foreground = Theme.TextSecondaryBrush;
            _unit.VerticalAlignment = VerticalAlignment.Bottom;
            _unit.Margin = new Thickness(5, 0, 0, 6);
            row.Children.Add(_unit);
            Children.Add(row);

            _bar.Margin = new Thickness(0, 12, 0, 0);
            Children.Add(_bar);

            _caption.FontSize = 11.5;
            _caption.Foreground = Theme.TextSecondaryBrush;
            // 一行：说明文字折成两行时整排格子的对齐就被拉歪了（CPU 格出现过），
            // 所以宁精简文案也不允许换行；真的放不下时用省略号，不要硬切半个字。
            _caption.TextWrapping = TextWrapping.NoWrap;
            _caption.TextTrimming = TextTrimming.CharacterEllipsis;
            _caption.Margin = new Thickness(0, 10, 0, 0);
            Children.Add(_caption);
        }

        /// <summary>格子左上角的标签（`CPU · 占用` / `CPU · 温度` …），跟着所选指标变。</summary>
        public string Label
        {
            get { return _label.Text; }
            set { _label.Text = value; }
        }

        /// <summary>走马循环：淡入度与目标转速，内部缓动逼近（见 MetricBar.SetIoWave）。</summary>
        public void SetIoWave(double waveOpacity, double cyclesPerSecond)
        {
            _bar.SetIoWave(waveOpacity, cyclesPerSecond);
        }

        /// <summary>走马只在鼠标停在这一格时才跑（见 MetricBar.WaveOnHover）。</summary>
        public bool IoWaveOnHover
        {
            get { return _bar.WaveOnHover; }
            set { _bar.WaveOnHover = value; }
        }

        /// <summary>胶囊条还是 16 段小分段；从 settings 的 indicatorStyle 来，切换后立即重画。</summary>
        public MetricBarStyle BarStyle
        {
            get { return _bar.BarStyle; }
            set { _bar.BarStyle = value; }
        }

        public void SetValue(string value, string unit, double fraction, string caption,
                             MetricTone tone = MetricTone.Normal, Brush valueBrush = null)
        {
            _value.Text = value ?? "--";
            _unit.Text = unit ?? string.Empty;
            _bar.SetValue(fraction);
            _caption.Text = caption ?? string.Empty;
            // “没有数据”和“数据是 0”必须一眼分开：说明行用警告色，有数据时才是次级灰。
            bool noData = string.IsNullOrEmpty(value) || value == MetricFormat.Invalid;
            _caption.Foreground = noData ? Theme.AccentOrangeBrush : Theme.TextSecondaryBrush;

            Color barColor = _accent;
            Brush textBrush = Theme.TextPrimaryBrush;
            if (tone == MetricTone.Warning)
            {
                barColor = Theme.AccentOrange;
                textBrush = Theme.AccentOrangeBrush;
            }
            else if (tone == MetricTone.Danger)
            {
                barColor = Theme.AccentRed;
                textBrush = Theme.AccentRedBrush;
            }

            _bar.SetTone(barColor);
            _value.Foreground = valueBrush ?? textBrush;
        }
    }

    /// <summary>Tiny inline sparkline for the network card (no charting dependency).</summary>
    public class Sparkline : FrameworkElement
    {
        private readonly Brush _stroke;
        private readonly Brush _area;
        private readonly Brush _areaFade;
        private readonly Pen _pen;
        private readonly Pen _penSharp;
        // 顶部刻度的下限：低于它的一律按它算，空闲时的低幅抖动不会被放大成大波浪。
        private const double MinScale = 8 * 1024.0;
        private double[] _values = new double[0];
        private bool _showArea = true;
        private bool _smooth = true;
        private int _slotCount;
        private string _peakText = string.Empty;
        private string[] _labels = new string[0];
        private int _hoverIndex = -1;
        private readonly Brush _axisBrush;
        private readonly Brush _dotGlow;
        private readonly Pen _guidePen;
        private readonly Brush _tipBack;
        private readonly Brush _tipText;
        private readonly Pen _tipBorder;

        public Sparkline(Color accent, double width, double height)
        {
            Width = width;
            Height = height;
            ClipToBounds = true;
            MinHeight = 40;

            SolidColorBrush stroke = new SolidColorBrush(accent);
            stroke.Freeze();
            _stroke = stroke;
            _pen = new Pen(_stroke, 2.0)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            _pen.Freeze();

            // 折线模式要参差的锐角：Round/Bevel 都会把拐角磨掉，只有 Miter 是尖的。
            Pen sharp = new Pen(_stroke, 2.0)
            {
                LineJoin = PenLineJoin.Miter,
                MiterLimit = 10,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            sharp.Freeze();
            _penSharp = sharp;

            // 方向：**底部浓、往上渐隐**（基色在 0 那一侧最实，靠近曲线处最淡）。
            // 反向（顶部浓）会让贴底脉冲的底部空掉，看着像悬在半空。
            LinearGradientBrush area = new LinearGradientBrush(
                Color.FromArgb(0x3D, accent.R, accent.G, accent.B),
                Color.FromArgb(0x00, accent.R, accent.G, accent.B),
                new Point(0.5, 1), new Point(0.5, 0));
            area.Freeze();
            _area = area;

            // 只在**左端**淡入（约 8%），右端保持实心：右端是最新数据，羽化会把末端锚点
            // （带光晕的圆点）淡掉、看起来像被裁；左端是旧数据，硬边会显脏。
            GradientStopCollection stops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0),
                new GradientStop(Colors.White, 0.08),
                new GradientStop(Colors.White, 1.0)
            };
            LinearGradientBrush fade = new LinearGradientBrush(
                stops, new Point(0, 0.5), new Point(1, 0.5))
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            fade.Freeze();
            _areaFade = fade;

            SolidColorBrush axis = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E));
            axis.Freeze();
            _axisBrush = axis;

            SolidColorBrush glow = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
            glow.Freeze();
            _dotGlow = glow;

            Pen guide = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1);
            guide.Freeze();
            _guidePen = guide;

            SolidColorBrush tipBack = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
            tipBack.Freeze();
            _tipBack = tipBack;

            Pen tipBorder = new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), 1);
            tipBorder.Freeze();
            _tipBorder = tipBorder;

            // 胶囊里的文字用主文字色（白）：灰色小字压在深色框上读起来费劲。
            SolidColorBrush tipText = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            tipText.Freeze();
            _tipText = tipText;
        }

        /// <summary>Area fill is on by default; line-only charts read better in a compact card.</summary>
        public bool ShowArea
        {
            get { return _showArea; }
            set
            {
                _showArea = value;
                InvalidateVisual();
            }
        }

        /// <summary>
        /// 圆滑（Catmull-Rom 贝塞尔 + 二项平滑）或原始折线。折线只改画法，
        /// 采样密度与 EMA 完全一致，两种样式看到的是同一组数据。
        /// </summary>
        public bool Smooth
        {
            get { return _smooth; }
            set
            {
                if (_smooth == value) return;
                _smooth = value;
                InvalidateVisual();
            }
        }

        /// <summary>
        /// 时间轴固定槽位数（例如 60 = 60 个采样点）。点数不足时**从右侧对齐、不拉伸**：
        /// 否则刚启动那十几秒会被拉满整条曲线，看着像“一大段没有起伏”。
        /// 0 表示按当前点数铺满（旧行为）。
        /// </summary>
        public int SlotCount
        {
            get { return _slotCount; }
            set
            {
                if (_slotCount == value) return;
                _slotCount = value;
                InvalidateVisual();
            }
        }

        /// <summary>右上角参考值（例如“峰值 2 MB/s”），空串则不画。</summary>
        public string PeakText
        {
            get { return _peakText; }
            set
            {
                if (_peakText == value) return;
                _peakText = value ?? string.Empty;
                InvalidateVisual();
            }
        }

        public void SetValues(double[] values)
        {
            _values = values ?? new double[0];
            InvalidateVisual();
        }

        /// <summary>与 <see cref="SetValues"/> 等长的悬停文本（时间 + 数值）。</summary>
        public void SetLabels(string[] labels)
        {
            _labels = labels ?? new string[0];
        }

        /// <summary>
        /// 悬停时把鼠标 X 映射回采样点，在图内画“时间 · 数值”。
        /// 不用 ToolTip：它是独立 HWND，会被鼠标自己遮挡，样式也和深色壳子对不上。
        /// </summary>
        /// <summary>
        /// 自绘控件没有背景刷子，默认只有“画到的像素”参与命中测试，
        /// 悬停需要整个矩形都响应，所以整块都算命中。
        /// </summary>
        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_values.Length < 2 || _labels.Length != _values.Length) return;

            Point position = e.GetPosition(this);
            int slots = _slotCount > _values.Length ? _slotCount : _values.Length;
            double plotWidth = Math.Max(8, ActualWidth - 6);
            double stepX = plotWidth / Math.Max(1, slots - 1);
            if (stepX <= 0) return;

            double offsetX = (slots - _values.Length) * stepX;
            int index = (int)Math.Round((position.X - offsetX) / stepX);
            if (index < 0) index = 0;
            if (index >= _values.Length) index = _values.Length - 1;

            if (index == _hoverIndex) return;
            _hoverIndex = index;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex < 0) return;
            _hoverIndex = -1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0 || _values.Length < 2) return;

            // 先对采样值做 3 遍二项平滑：单个尖峰会被削圆，曲线才“丝滑”。
            // 折线样式不做平滑，否则“折线”会被削成看不出拐角。
            List<Point> points = BuildPoints(_smooth ? SmoothSeries(_values) : _values, width, height);

            // 面积与线条放在同一个遮罩里：两者一起左右羽化，否则面积淡了线还是硬切在边缘。
            dc.PushOpacityMask(_areaFade);
            if (_showArea)
            {
                dc.DrawGeometry(_area, null, BuildGeometry(points, height, _smooth));
            }
            dc.DrawGeometry(null, _smooth ? _pen : _penSharp, BuildGeometry(points, double.NaN, _smooth));
            dc.Pop();

            // Y 轴参考值：没有刻度时无法判断曲线高度代表多少速率。
            // 悬停时让位：右上角峰值标签和鼠标旁的数值框会重叠。
            if (!string.IsNullOrEmpty(_peakText) && width > 80 && _hoverIndex < 0)
            {
                FormattedText axis = new FormattedText(_peakText,
                    System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 10, _axisBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(axis, new Point(width - axis.Width - 2, 1));
            }

            // 末端锚点：最新采样点带一圈微光，刷新时跟着移动。
            Point last = points[points.Count - 1];
            dc.DrawEllipse(_dotGlow, null, last, 5, 5);
            dc.DrawEllipse(_stroke, null, last, 2.4, 2.4);

            // 悬停：竖线 + 该点标签，全部画在同一张图上（不弹独立窗口）。
            if (_hoverIndex >= 0 && _hoverIndex < points.Count && _labels.Length == points.Count)
            {
                Point hit = points[_hoverIndex];
                dc.DrawLine(_guidePen, new Point(hit.X, 2), new Point(hit.X, height - 2));
                dc.DrawEllipse(_stroke, null, hit, 2.8, 2.8);

                FormattedText label = new FormattedText(_labels[_hoverIndex],
                    System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 10.5, _tipText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double boxW = label.Width + 12;
                double boxH = label.Height + 6;
                double boxX = Math.Min(Math.Max(hit.X - boxW / 2, 0), Math.Max(0, width - boxW));
                double boxY = Math.Max(hit.Y - boxH - 8, 0);
                dc.DrawRoundedRectangle(_tipBack, _tipBorder, new Rect(boxX, boxY, boxW, boxH), 6, 6);
                dc.DrawText(label, new Point(boxX + 6, boxY + 3));
            }
        }

        private List<Point> BuildPoints(double[] values, double width, double height)
        {
            // 线性刻度 + 稳健峰值。速率跨度大时，对数刻度虽然能容下尖峰，但十几 K、几十 K、
            // 几百 K 在同一个对数轴上只差几个像素，看起来永远是一条平线；改用线性刻度后，
            // 正常流量段的起伏能按比例张开。单个/少量尖峰由 ScalePeak 裁到顶部，不会把
            // 其余采样压扁。底部仍保留 8 KB/s 的下限，空闲窗口不会被 1 KB/s 级抖动撑满。
            double max = ScalePeak(values) * 1.1;
            // 顶部至少按 8 KB/s 取刻度。
            if (max < MinScale) max = MinScale;
            double denominator = max;

            List<Point> points = new List<Point>(values.Length);
            int slots = _slotCount > values.Length ? _slotCount : values.Length;
            // 右侧留出一个圆点的半径：末端锚点（光晕 5 px）贴着右边缘会被 ClipToBounds 裁掉。
            double plotWidth = Math.Max(8, width - 6);
            double stepX = plotWidth / Math.Max(1, slots - 1);
            // 右侧对齐：第 i 个点落在时间轴末尾往前数 (len-1-i) 个槽位。
            double offsetX = (slots - values.Length) * stepX;
            for (int i = 0; i < values.Length; i++)
            {
                double value = values[i] > 0 ? values[i] : 0;
                double normalized = value / denominator;
                if (normalized < 0) normalized = 0;
                if (normalized > 1) normalized = 1;
                // 基线贴底：0 就是图的下边缘（只留 1 px 给线宽，免得半条线被裁掉）；
                // 上下各留 4 px 会让静默期悬在半空，看起来像有“波谷”。
                double top = 6;
                double bottom = height - 1;
                if (bottom < top) bottom = top;
                double y = bottom - normalized * (bottom - top);
                points.Add(new Point(offsetX + i * stepX, y));
            }
            return points;
        }

        /// <summary>
        /// Scale anchor for the y axis. A few samples far above the rest (a multi-second burst)
        /// would flatten the whole window, so when max > 4 × P85 the axis follows P85 and those
        /// spikes are clipped at the top. Flatter windows keep the true maximum, so the curve does
        /// not get stretched on purpose.
        /// </summary>
        private static double ScalePeak(double[] values)
        {
            double max = MaxValue(values);
            if (values.Length < 20) return max;

            double[] sorted = (double[])values.Clone();
            Array.Sort(sorted);
            int index = (int)Math.Floor((sorted.Length - 1) * 0.85);
            double p85 = sorted[index];
            if (p85 > 0 && max > p85 * 4) return p85;
            return max;
        }

        private static double MaxValue(double[] values)
        {
            double max = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > max) max = values[i];
            }
            return max;
        }

        /// <summary>
        /// 两遍 [1 4 6 4 1]/16 高斯平滑（≈ σ 2 个采样点），端点夹紧。1 Hz 采样的网速本身
        /// 就会 ±20% 抖动，一遍 3 抽头只能削掉一点毛边，看上去仍然是镬齿；五抽头两遍才把
        /// 单点毛刺抹成波浪，同时保留宽峰（峰值标签仍用原始值，不会跟着变小）。
        /// </summary>
        private static double[] SmoothSeries(double[] source)
        {
            double[] current = source;
            for (int pass = 0; pass < 2; pass++)
            {
                double[] next = new double[current.Length];
                for (int i = 0; i < current.Length; i++)
                {
                    double left2 = current[i > 1 ? i - 2 : 0];
                    double left1 = current[i > 0 ? i - 1 : 0];
                    double right1 = current[i + 1 < current.Length ? i + 1 : current.Length - 1];
                    double right2 = current[i + 2 < current.Length ? i + 2 : current.Length - 1];
                    next[i] = (left2 + 4 * left1 + 6 * current[i] + 4 * right1 + right2) / 16.0;
                }
                current = next;
            }
            return current;
        }

        /// <summary>
        /// 单调三次 Hermite（Fritsch–Carlson）：曲线穿过每个采样点，且在极值处切线水平。
        /// 切线水平 = 拐角是圆的（Catmull-Rom 在极值处仍有斜度，看上去就是一个个直角）；
        /// 同时相邻同号的斜率取调和平均，所以段内一律不冲出两端（不会出现假尖峰）。
        /// baseY 有值时把曲线向下封口（面积填充用）。
        /// </summary>
        private static StreamGeometry BuildGeometry(List<Point> points, double baseY, bool smooth)
        {
            StreamGeometry geometry = new StreamGeometry();
            double[] tangents = smooth ? MonotoneTangents(points) : null;
            using (StreamGeometryContext ctx = geometry.Open())
            {
                if (!double.IsNaN(baseY))
                {
                    ctx.BeginFigure(new Point(points[0].X, baseY), true, true);
                    ctx.LineTo(points[0], false, false);
                }
                else
                {
                    ctx.BeginFigure(points[0], false, false);
                }

                for (int i = 0; i < points.Count - 1; i++)
                {
                    if (!smooth)
                    {
                        ctx.LineTo(points[i + 1], true, false);
                        continue;
                    }

                    Point p1 = points[i];
                    Point p2 = points[i + 1];
                    double h = (p2.X - p1.X) / 3.0;
                    Point c1 = new Point(p1.X + h, p1.Y + h * tangents[i]);
                    Point c2 = new Point(p2.X - h, p2.Y - h * tangents[i + 1]);
                    ctx.BezierTo(c1, c2, p2, true, false);
                }

                if (!double.IsNaN(baseY))
                {
                    ctx.LineTo(new Point(points[points.Count - 1].X, baseY), false, false);
                }
            }
            geometry.Freeze();
            return geometry;
        }

        /// <summary>
        /// Fritsch–Carlson 切线：极值处给 0（拐角变圆），同号斜率用加权调和平均（不超调）。
        /// </summary>
        private static double[] MonotoneTangents(List<Point> points)
        {
            int count = points.Count;
            double[] tangents = new double[count];
            if (count < 2) return tangents;

            double[] secants = new double[count - 1];
            double[] widths = new double[count - 1];
            for (int i = 0; i < count - 1; i++)
            {
                widths[i] = points[i + 1].X - points[i].X;
                secants[i] = widths[i] == 0 ? 0 : (points[i + 1].Y - points[i].Y) / widths[i];
            }

            tangents[0] = secants[0];
            tangents[count - 1] = secants[count - 2];
            for (int i = 1; i < count - 1; i++)
            {
                double before = secants[i - 1];
                double after = secants[i];
                if (before * after <= 0)
                {
                    // 坡向翻转（或一边是平的）：切线取水平，曲线在这里变圆顶/圆谷。
                    tangents[i] = 0;
                    continue;
                }
                double w1 = 2 * widths[i] + widths[i - 1];
                double w2 = widths[i] + 2 * widths[i - 1];
                tangents[i] = (w1 + w2) / (w1 / before + w2 / after);
            }
            return tangents;
        }
    }

    /// <summary>Sidebar navigation entry with selected/hover states.</summary>
    public class SidebarItem : Border
    {
        private readonly TextBlock _text;
        private bool _selected;

        public event Action<SidebarItem> Clicked;

        public SidebarItem(string title)
        {
            CornerRadius = new CornerRadius(12);
            // 固定行高 + 4 px 间距：SidebarItem 自身的尺寸就是侧栏滑块（NavGlider）的步长，
            // 改这里必须同步 SettingsWindow.NavStep。
            Height = 44;
            Padding = new Thickness(12, 10, 12, 10);
            Margin = new Thickness(0, 0, 0, 4);
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;

            _text = new TextBlock
            {
                Text = title,
                FontSize = 14.5,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Child = _text;

            MouseLeftButtonUp += (sender, e) =>
            {
                Action<SidebarItem> handler = Clicked;
                if (handler != null) handler(this);
            };
            MouseEnter += (sender, e) => { if (!_selected) Background = Theme.SurfaceHoverBrush; };
            MouseLeave += (sender, e) => { if (!_selected) Background = Brushes.Transparent; };
        }

        public bool Selected
        {
            get { return _selected; }
            set
            {
                _selected = value;
                // 选中面是列表背后那块会滑动的滑块；行自己必须保持透明，否则会盖住滑块。
                Background = Brushes.Transparent;
                // 滑块是品牌绿：白字对比度只有 ~2.4:1，用近黑（和主按钮一致）。
                _text.Foreground = value ? Theme.OnAccentBrush : Theme.TextSecondaryBrush;
                _text.FontWeight = value ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    /// <summary>Coloured key/value row used in the agent status card.</summary>
    public class KeyValueRow : Grid
    {
        private readonly TextBlock _value;

        public KeyValueRow(string key, string initialValue = "—")
        {
            Margin = new Thickness(0, 3, 0, 3);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock label = new TextBlock
            {
                Text = key,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush
            };
            SetColumn(label, 0);

            _value = new TextBlock
            {
                Text = initialValue,
                FontSize = 12,
                Foreground = Theme.TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            SetColumn(_value, 1);

            Children.Add(label);
            Children.Add(_value);
        }

        public string Value
        {
            get { return _value.Text; }
            set { _value.Text = value; }
        }

        public void SetValue(string text, Color? color = null)
        {
            _value.Text = text;
            _value.Foreground = color.HasValue ? new SolidColorBrush(color.Value) : Theme.TextPrimaryBrush;
        }
    }

    /// <summary>Flat button that follows the theme, including a "primary" variant.</summary>
    public class FlatButton : Button
    {
        /// <param name="spring">
        /// False for buttons that fill their container: the hover/press scale grows a full-width
        /// control past the card padding, so those get a background lift instead of a transform.
        /// </param>
        public FlatButton(string text, bool primary = false, bool spring = true)
        {
            Content = text;
            FontSize = 12.5;
            Padding = new Thickness(14, 7, 14, 7);
            BorderThickness = new Thickness(1);
            Cursor = Cursors.Hand;
            SnapsToDevicePixels = true;

            Background = primary ? Theme.AccentGreenBrush : Theme.ButtonGreyBrush;
            BorderBrush = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Foreground = primary ? Theme.OnAccentBrush : Theme.TextPrimaryBrush;

            // See BuildTemplate: focus is drawn as a green edge, not the default dotted rectangle.
            FocusVisualStyle = null;

            Template = BuildTemplate(primary);

            if (spring)
            {
                Interactions.AttachSpring(this);
            }
            else
            {
                SolidColorBrush idle = Background as SolidColorBrush;
                if (idle != null)
                {
                    SolidColorBrush hover = new SolidColorBrush(Lift(idle.Color));
                    hover.Freeze();
                    MouseEnter += (sender, e) => Background = hover;
                    MouseLeave += (sender, e) => Background = idle;
                }
            }
        }

        /// <summary>One step towards white: enough to read as feedback, not enough to look like another colour.</summary>
        private static Color Lift(Color color)
        {
            return Color.FromRgb(
                (byte)(color.R + (255 - color.R) * 0.14),
                (byte)(color.G + (255 - color.G) * 0.14),
                (byte)(color.B + (255 - color.B) * 0.14));
        }

        private static ControlTemplate BuildTemplate(bool primary)
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Theme.ControlRadius));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            // The default focus rectangle is a black dotted outline; on these dark surfaces it reads as
            // a stray black line. Focus is shown as a green edge instead.
            Trigger focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            Setter focusEdge = new Setter(Border.BorderBrushProperty, Theme.AccentGreenBrush);
            focusEdge.TargetName = "border";
            focus.Setters.Add(focusEdge);
            template.Triggers.Add(focus);

            return template;
        }
    }

    /// <summary>
    /// 页头：每个页面第一件东西，主标题 + 一行副标题，卡片整体排在它下面。
    /// 导航已经标了“你在哪”，这里给出的是这一页在做什么（一句话，手册体）。
    /// </summary>
    public class PageHeading : StackPanel
    {
        public PageHeading(string title, string subtitle)
        {
            Orientation = Orientation.Vertical;
            // 上边距 2：标题字形的墨迹上边缘要和侧栏第一个导航项的顶边落在同一条线上
            // （字形在行框里天然低约 4 px，这里补 2 让它看起来齐平）。
            Margin = new Thickness(0, 2, 0, 16);

            TextBlock heading = new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(heading, "PageTitle");
            Children.Add(heading);

            if (!string.IsNullOrEmpty(subtitle))
            {
                TextBlock caption = new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12,
                    Foreground = Theme.TextSecondaryBrush,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                AutomationProperties.SetAutomationId(caption, "PageSubtitle");
                Children.Add(caption);
            }
        }
    }
}
