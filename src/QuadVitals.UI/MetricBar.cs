using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using QuadVitals.Core;

namespace QuadVitals.UI
{
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
}
