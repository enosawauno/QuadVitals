using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using QuadVitals.Core;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 概览页的风扇卡：左边一台会转的风扇与它的气流，右边是当前通道的转速。
    ///
    /// 气流画成几条两端收窄的飘带（见 <see cref="FanFlow"/>），叠在一起像被吹过去的烟，而不是线条或圆点。
    /// 飘带的波动幅度、相位速度与不透明度跟真实 RPM 走（真实角速度会被帧率混叠，叶片只给“在转”的直觉）；
    /// 颜色由卡片按连续色阶给出（水泵固定青色），风扇危险档飘带向上弯、由红转橙，读作热风。
    /// 页面不可见、窗口卸载或系统关闭动画时停止渲染循环，不留后台空转。
    /// </summary>
    internal sealed class FanCard : Grid, IDisposable
    {
        private const double FanSize = 96;
        private const double FanLeft = 4;
        private const double FanTop = 14;
        private const double SceneWidth = 176;
        private const double SceneHeight = 124;
        /// <summary>The flow rebuilds at 20 Hz; the rotor turns on the shared frame loop.</summary>
        private const double FlowIntervalSeconds = 0.05;

        private readonly Card _card;
        // 不裁剪场景：裁出来的是一条硬边（右侧全被截断），
        // 羽化交给流线自己的渐变（见 FanFlow：头部在元素右缘前淡出）。
        private readonly Canvas _scene = new Canvas
        {
            Width = SceneWidth,
            Height = SceneHeight
        };
        private readonly RotateTransform _rotor = new RotateTransform(0);
        private readonly List<Path> _blades = new List<Path>();
        private readonly FanFlow _flow = new FanFlow();
        private readonly Ellipse _ring;
        private readonly Ellipse _glow;
        private readonly Ellipse _pumpGlow;
        private readonly TextBlock _value = new TextBlock();
        private readonly TextBlock _unit = new TextBlock();
        private readonly TextBlock _caption = new TextBlock();
        private readonly TextBlock _others = new TextBlock();
        private readonly TextBlock _empty = new TextBlock();
        private readonly MetricBar _bar;
        private readonly FrameLoop _loop;

        private double _angle;
        private double _revolutionsPerSecond;
        private double _targetRevolutionsPerSecond;
        private double _normalized;
        private double _flowPendingSeconds;
        private double _pumpTime;
        private bool _disposed;
        private bool _hover;
        private bool _pumpBreathing;
        private bool _heat;
        private int _lastKind = -1;
        private Color _accent = Theme.AccentGreen;

        /// <summary>true = 常驻动画（设置里的「常驻」档）；false = 指针停在卡上才动。</summary>
        public bool AlwaysAnimate { get; private set; }

        /// <summary>切换动效模式；立刻生效，不重建卡片。</summary>
        public void SetAlwaysAnimate(bool value)
        {
            AlwaysAnimate = value;
            if (value) StartIfAllowed(); else if (!_hover) Stop();
        }
        private MetricTone _tone = MetricTone.Normal;
        private readonly FanToneLatch _toneLatch = new FanToneLatch();
        private readonly SolidColorBrush _ringBrush = new SolidColorBrush(Theme.AccentGreen);
        private readonly SolidColorBrush _bladeBrush = new SolidColorBrush(Theme.AccentGreen);
        private readonly SolidColorBrush _valueBrush = new SolidColorBrush(Theme.TextPrimary);
        private Color _ringShown = Theme.AccentGreen;
        private Color _bladeShown = Theme.AccentGreen;
        private Color _valueShown = Theme.TextPrimary;
        private MetricsSnapshot.FanReading[] _currentFans = new MetricsSnapshot.FanReading[0];

        public FanCard()
        {
            _loop = new FrameLoop(this, OnFrame);
            // 与设备卡、网速卡同一个字形位（风扇），三卡并排时标题不会左移。
            _card = new Card("风扇", "");
            AutomationProperties.SetAutomationId(_card, "FanCard");

            FlatButton gear = new FlatButton(string.Empty)
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Theme.TextSecondaryBrush,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "风扇设置"
            };
            TextBlock gearIcon = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12
            };
            gearIcon.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding("Foreground") { Source = gear });
            gear.Content = gearIcon;
            AutomationProperties.SetAutomationId(gear, "FanSettings");
            AutomationProperties.SetName(gear, "风扇设置");
            gear.MouseEnter += (sender, e) => gear.Foreground = Theme.TextPrimaryBrush;
            gear.MouseLeave += (sender, e) => gear.Foreground = Theme.TextSecondaryBrush;
            gear.Click += (sender, e) => { Action handler = OpenSettings; if (handler != null) handler(); };
            _card.HeaderRight.Content = gear;
            HoverReveal.Attach(_card, gear);

            _ring = new Ellipse
            {
                Width = FanSize,
                Height = FanSize,
                Stroke = _ringBrush,
                StrokeThickness = 2,
                Opacity = 0.8
            };
            Canvas.SetLeft(_ring, FanLeft);
            Canvas.SetTop(_ring, FanTop);

            // 暖光晕只出现在危险档：它表达“热”，不是常态装饰。
            RadialGradientBrush glow = new RadialGradientBrush();
            glow.GradientOrigin = new Point(0.5, 0.5);
            glow.Center = new Point(0.5, 0.5);
            glow.RadiusX = 0.5;
            glow.RadiusY = 0.5;
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(90, Theme.AccentRed.R, Theme.AccentRed.G, Theme.AccentRed.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, Theme.AccentRed.R, Theme.AccentRed.G, Theme.AccentRed.B), 1));
            glow.Freeze();
            _glow = new Ellipse
            {
                Width = FanSize + 34,
                Height = FanSize + 34,
                Fill = glow,
                Opacity = 0,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_glow, FanLeft - 17);
            Canvas.SetTop(_glow, FanTop - 17);
            _scene.Children.Add(_glow);

            // 水泵没有“危险档”可言，它的光晕只表达“在转”，所以单独一层青色，
            // 呼吸由帧循环驱动；静止时是一枚固定的青色光晕，不常驻重绘。
            RadialGradientBrush pumpGlow = new RadialGradientBrush();
            pumpGlow.GradientOrigin = new Point(0.5, 0.5);
            pumpGlow.Center = new Point(0.5, 0.5);
            pumpGlow.RadiusX = 0.5;
            pumpGlow.RadiusY = 0.5;
            pumpGlow.GradientStops.Add(new GradientStop(Color.FromArgb(90, Theme.AccentCyan.R, Theme.AccentCyan.G, Theme.AccentCyan.B), 0));
            pumpGlow.GradientStops.Add(new GradientStop(Color.FromArgb(0, Theme.AccentCyan.R, Theme.AccentCyan.G, Theme.AccentCyan.B), 1));
            pumpGlow.Freeze();
            _pumpGlow = new Ellipse
            {
                Width = FanSize + 34,
                Height = FanSize + 34,
                Fill = pumpGlow,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            Canvas.SetLeft(_pumpGlow, FanLeft - 17);
            Canvas.SetTop(_pumpGlow, FanTop - 17);
            _scene.Children.Add(_pumpGlow);

            // 气流从风扇右缘之后开始，压在圆环与叶片下面，看起来是“从风扇后面吹出来”。
            _flow.Width = SceneWidth - 90;
            _flow.Height = SceneHeight;
            Canvas.SetLeft(_flow, 90);
            Canvas.SetTop(_flow, 0);
            _scene.Children.Add(_flow);
            _scene.Children.Add(_ring);
            AutomationProperties.SetAutomationId(_scene, "FanScene");

            Canvas blades = new Canvas
            {
                Width = FanSize,
                Height = FanSize,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _rotor
            };
            AutomationProperties.SetAutomationId(blades, "FanRotor");
            Canvas.SetLeft(blades, FanLeft);
            Canvas.SetTop(blades, FanTop);
            for (int i = 0; i < 5; i++)
            {
                // One swept blade, repeated by rotation: reads as an impeller at 96 px with
                // visible gaps between the blades (a teardrop x7 reads as a flower, not a fan).
                Path blade = new Path
                {
                    Data = Geometry.Parse("M 48,14 C 62,18 70,30 68,44 L 57,50 C 53,38 50,26 48,18 Z"),
                    Fill = _bladeBrush,
                    Opacity = 0.92,
                    RenderTransform = new RotateTransform(i * 360.0 / 5.0, 48, 48)
                };
                _blades.Add(blade);
                blades.Children.Add(blade);
            }
            Ellipse hub = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = Theme.SurfaceRaisedBrush,
                Stroke = Theme.BorderStrongBrush,
                StrokeThickness = 1
            };
            blades.Children.Add(hub);
            Canvas.SetLeft(hub, 39);
            Canvas.SetTop(hub, 39);
            _scene.Children.Add(blades);

            _value.Text = "--";
            _value.FontSize = 34;
            _value.FontWeight = FontWeights.SemiBold;
            _value.Foreground = _valueBrush;
            AutomationProperties.SetAutomationId(_value, "FanValue");

            _unit.Text = "RPM";
            _unit.FontSize = 14;
            _unit.Foreground = Theme.TextSecondaryBrush;
            _unit.VerticalAlignment = VerticalAlignment.Bottom;
            _unit.Margin = new Thickness(5, 0, 0, 6);

            _caption.FontSize = 11.5;
            _caption.Foreground = Theme.TextSecondaryBrush;
            AutomationProperties.SetAutomationId(_caption, "FanCaption");

            _bar = new MetricBar(Theme.AccentGreen, Theme.TrackGrey);
            _bar.Height = 10;
            _bar.Margin = new Thickness(0, 12, 0, 0);
            _bar.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(_bar, "FanBar");

            _others.FontSize = 11;
            _others.Foreground = Theme.TextSecondaryBrush;
            _others.TextWrapping = TextWrapping.Wrap;
            _others.Margin = new Thickness(0, 10, 0, 0);
            AutomationProperties.SetAutomationId(_others, "FanOthers");

            _empty.Text = string.Empty;
            // 与设备四格的说明行同一个字号、同一种颜色、同样只占一行：
            // 同一屏上的“读不到”不应该有两种大小和两种语气。
            _empty.FontSize = 11.5;
            _empty.Foreground = Theme.AccentOrangeBrush;
            _empty.TextWrapping = TextWrapping.NoWrap;
            _empty.Margin = new Thickness(0, 6, 0, 0);
            AutomationProperties.SetAutomationId(_empty, "FanEmpty");

            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SceneWidth) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(_scene);

            StackPanel readout = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_value);
            row.Children.Add(_unit);
            readout.Children.Add(_caption);
            readout.Children.Add(row);
            readout.Children.Add(_bar);
            readout.Children.Add(_empty);
            readout.Children.Add(_others);
            Grid.SetColumn(readout, 2);
            body.Children.Add(readout);

            _card.Content.Children.Add(body);
            Children.Add(_card);

            Loaded += (sender, e) => StartIfAllowed();
            Unloaded += (sender, e) => Stop();
            IsVisibleChanged += (sender, e) => { if ((bool)e.NewValue) StartIfAllowed(); else Stop(); };
            // 动效只在指针停在卡上时跑：这是整个仪表盘最贵的动画（分层窗口每帧都要重推），
            // 让它常态空转等于给一个监控工具常驻几成 GPU，为的只是“看起来在转”。
            MouseEnter += (sender, e) => { _hover = true; StartIfAllowed(); };
            MouseLeave += (sender, e) => { _hover = false; Stop(); };
        }

        /// <summary>Live read-out for the card. Called from the overview tick (about once per second).</summary>
        public void Update(MetricsSnapshot snapshot, bool alive)
        {
            if (!alive)
            {
                ShowEmpty("传感器未运行");
                return;
            }
            if (snapshot == null || !snapshot.Has(MetricFlags.FanValid) ||
                snapshot.Fans == null || snapshot.Fans.Length == 0)
            {
                ShowEmpty(AgentDiagnostics.FanUnavailableLine(snapshot));
                return;
            }

            _currentFans = snapshot.Fans;

            // 三种状态必须分开，否则“选的那一路没读数”会被悄悄换成另一路：
            //   live    —— 选中的（或自动挑的）那一路在转；
            //   stopped —— 那一路是 0 RPM（风扇真停了）；
            //   missing —— 设置里指定的通道这次不在快照里（不悄悄换到另一路）。
            MetricsSnapshot.FanReading hero = null;
            bool selected = PreferredChannel >= 0;
            if (selected)
            {
                for (int i = 0; i < snapshot.Fans.Length; i++)
                {
                    MetricsSnapshot.FanReading candidate = snapshot.Fans[i];
                    if (candidate != null && candidate.Valid && candidate.Channel == PreferredChannel) { hero = candidate; break; }
                }
            }
            else
            {
                // 自动：先挑在转的那一路，都没有才退回第一路（于是显示停转，而不是空白）。
                for (int i = 0; i < snapshot.Fans.Length; i++)
                {
                    MetricsSnapshot.FanReading candidate = snapshot.Fans[i];
                    if (candidate != null && candidate.Valid && candidate.Rpm > 0) { hero = candidate; break; }
                }
                if (hero == null)
                {
                    for (int i = 0; i < snapshot.Fans.Length; i++)
                    {
                        if (snapshot.Fans[i] != null && snapshot.Fans[i].Valid) { hero = snapshot.Fans[i]; break; }
                    }
                }
            }

            if (hero == null)
            {
                // 指定了通道但这次没有它：说清楚，不换一路，也不退回“未读取到主板风扇”。
                ApplyEmptyVisuals();
                _value.Text = MetricFormat.Invalid;
                _unit.Text = string.Empty;
                _caption.Text = "通道 " + PreferredChannel;
                _empty.Text = "所选通道未读数";
                _empty.Foreground = Theme.AccentOrangeBrush;
                _bar.SetValue(0);
                _tone = MetricTone.Warning;
                return;
            }

            int rpm = hero.Rpm;
            bool stopped = rpm <= 0;
            bool pump = FanKinds.IsPump(hero.Kind);
            if (hero.Kind != _lastKind)
            {
                // 换了一路之后不能沿用上一路的档位/呼吸相位，否则切到水泵的第一帧还是风扇的红。
                _lastKind = hero.Kind;
                _toneLatch.Reset();
                _pumpTime = 0;
            }

            _caption.Text = FanLabels.For(hero.Kind, hero.Channel);
            _value.Text = rpm.ToString("0");
            _unit.Text = "RPM";
            // 停转是告警，不是“正常但数字小”；空出的这一行正好用来写这两个字。
            _empty.Text = stopped ? "风扇停转" : string.Empty;
            _empty.Foreground = stopped ? Theme.AccentRedBrush : Theme.AccentOrangeBrush;
            _bar.SetValue(FanZones.Fraction(rpm));

            MetricTone tone;
            Color colour;
            bool heat;
            if (stopped)
            {
                tone = MetricTone.Danger;
                colour = Theme.AccentRed;
                heat = false;
            }
            else if (pump)
            {
                // 水泵只有“在转 / 停转”两态：3342 转在这张卡上不是火警，也不参与 1400/1600 分档。
                tone = MetricTone.Normal;
                colour = Theme.AccentCyan;
                heat = false;
            }
            else
            {
                tone = _toneLatch.Update(rpm);
                colour = FanColour(rpm, tone);
                heat = tone == MetricTone.Danger;
            }
            List<string> extra = new List<string>();
            int remaining = 0;
            for (int i = 0; i < snapshot.Fans.Length; i++)
            {
                MetricsSnapshot.FanReading fan = snapshot.Fans[i];
                if (fan == null || !fan.Valid || fan.Rpm <= 0 || ReferenceEquals(fan, hero)) continue;
                if (extra.Count >= 3) { remaining++; continue; }
                extra.Add(FanLabels.For(fan.Kind, fan.Channel) + "  " + fan.Rpm.ToString("0") + " RPM");
            }
            if (remaining > 0) extra.Add("等 " + (remaining + 1) + " 路");
            _others.Text = extra.Count == 0 ? string.Empty : "其余 · " + string.Join("    ", extra.ToArray());

            // 三档给三种明显不同的转速：绿 0.30–0.45、黄 0.65–0.80、红 1.00–1.25 转/秒。
            // 不追求与真实 RPM 等比例（真实 25 转/秒在屏幕上只会频闪），但档位之间必须一眼能看出来。
            double rev;
            if (tone == MetricTone.Danger)
            {
                rev = 1.00 + 0.25 * Clamp01((rpm - FanZones.DangerRpm) / 400.0);
            }
            else if (tone == MetricTone.Warning)
            {
                rev = 0.65 + 0.15 * Clamp01((rpm - FanZones.WarningRpm) / 200.0);
            }
            else
            {
                rev = 0.30 + 0.15 * Clamp01(rpm / (double)FanZones.WarningRpm);
            }
            if (stopped) rev = 0;
            _targetRevolutionsPerSecond = rev;
            _normalized = Clamp01(rev / 1.25);
            _flow.Visibility = stopped ? Visibility.Collapsed : Visibility.Visible;
            if (stopped) _toneLatch.Reset();

            _tone = tone;
            _accent = colour;
            _heat = heat;
            _pumpBreathing = pump && !stopped;
            ApplyAppearance(tone, colour, heat, _pumpBreathing);

            // A card that had no reading (or was hidden) may have stopped its loop; the next
            // snapshot with data is the point where it is worth animating again.
            StartIfAllowed();
            for (int i = 0; i < _blades.Count; i++) _blades[i].Opacity = 0.92 - 0.25 * _normalized;
        }

        /// <summary>Grey everything out: no reading means no green ring and no spinning blades.</summary>
        private void ApplyEmptyVisuals()
        {
            SetImmediate(_ringBrush, Theme.TextSecondary);
            SetImmediate(_bladeBrush, Theme.TextSecondary);
            SetImmediate(_valueBrush, Theme.TextSecondary);
            _ringShown = Theme.TextSecondary;
            _bladeShown = Theme.TextSecondary;
            _valueShown = Theme.TextSecondary;
            _ring.Opacity = 1.0;
            for (int i = 0; i < _blades.Count; i++) _blades[i].Opacity = 0.75;
            _glow.Opacity = 0;
            _pumpGlow.Opacity = 0;
            _pumpBreathing = false;
            _flow.Visibility = Visibility.Collapsed;
            _targetRevolutionsPerSecond = 0;
            _normalized = 0;
            _heat = false;
            _accent = Theme.TextSecondary;
            _lastKind = -1;
            _toneLatch.Reset();
        }

        private void ShowEmpty(string reason)
        {
            _caption.Text = string.Empty;
            _value.Text = MetricFormat.Invalid;
            _unit.Text = string.Empty;
            _empty.Text = reason;
            _others.Text = string.Empty;
            _bar.SetValue(0);
            _tone = MetricTone.Normal;
            _empty.Foreground = Theme.AccentOrangeBrush;
            // 整套图形走灰：环、叶片、`--` 都换成能读的灰。
            // 绿着显示 `--`，空态看起来就像“正常但没数值”，只有换色才说得清区别。
            ApplyEmptyVisuals();
            _bar.SetTone(Theme.TrackGrey);
            // Nothing moves on an empty card: stop the loop instead of spinning a still fan and
            // rebuilding 14 trail geometries 20 times a second for no pixels.
            _loop.Stop();
        }

        private void ApplyAppearance(MetricTone tone, Color colour, bool heat, bool pump)
        {
            // 正常档的数值保持白色（绿色数值在深色卡上偏弱），只有告警/危险和水泵才染成强调色。
            Color textColour = pump || tone != MetricTone.Normal ? colour : Theme.TextPrimary;

            // 颜色随转速连续变化，跨档时 320 ms 过渡；悬停模式下静止时也照常更新，
            // 只是气流不推进——看到的颜色不再只在三档边界跳。
            Fade(_ringBrush, ref _ringShown, colour);
            Fade(_bladeBrush, ref _bladeShown, colour);
            Fade(_valueBrush, ref _valueShown, textColour);
            // 空态把环调成不透明好让灰色读得出来；回到有数据的状态要还原。
            _ring.Opacity = 0.8;
            _bar.SetToneSmooth(colour, 320);

            _glow.Opacity = heat ? 0.55 : 0;
            if (!pump) _pumpGlow.Opacity = 0;
            _flow.SetState(colour, _normalized, heat);
        }

        /// <summary>
        /// 风扇的连续色阶。1400 / 1600 仍是原来的黄点与红点，只是档内不再是单一色：
        /// 绿色由浅到深、红色由亮到暗，静止时转速变了颜色也跟着变。
        /// </summary>
        private static Color FanColour(int rpm, MetricTone tone)
        {
            if (rpm <= 0) return Theme.AccentRed;
            // 段内连续、段间交给 320 ms 过渡：颜色跟着已经过回差的档位走，
            // 否则 1400 附近会先变橙再等档位，文字和环形会各说各话。
            if (tone == MetricTone.Danger)
            {
                double t = Clamp01((rpm - FanZones.DangerRpm) /
                    (double)(FanZones.NominalMaxRpm - FanZones.DangerRpm));
                return Lerp(Theme.AccentRed, Theme.AccentRedDeep, t);
            }
            if (tone == MetricTone.Warning)
            {
                double t = Clamp01((rpm - FanZones.WarningRpm) /
                    (double)(FanZones.DangerRpm - FanZones.WarningRpm));
                return Lerp(Theme.AccentOrange, Theme.AccentRed, t);
            }
            return Lerp(Theme.AccentGreenSoft, Theme.AccentGreen,
                Clamp01(rpm / (double)FanZones.WarningRpm));
        }

        private static Color Lerp(Color from, Color to, double t)
        {
            t = Clamp01(t);
            return Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * t),
                (byte)Math.Round(from.G + (to.G - from.G) * t),
                (byte)Math.Round(from.B + (to.B - from.B) * t));
        }

        /// <summary>Direct colour set; clears any running fade so the value actually lands.</summary>
        private static void SetImmediate(SolidColorBrush brush, Color colour)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = colour;
        }

        private static void Fade(SolidColorBrush brush, ref Color shown, Color colour)
        {
            // 只比上一次的目标色：HasAnimatedProperties 一旦为真就再也不会为假，
            // 拿它当 guard 等于每秒重开一次同样的 320 ms 动画；而比较 base color 会让
            // 动画从最初的颜色重新开始（连续色阶下就是每秒闪一下）。
            if (shown == colour) return;
            Color from = shown;
            shown = colour;
            if (!SystemParameters.ClientAreaAnimation || RenderEnvironment.AnimationsDisabled)
            {
                // 系统关闭了动画，或当前是 RDP/Tier 0：直接落色，颜色仍然随转速变，
                // 只是不做 320 ms 过渡。
                SetImmediate(brush, colour);
                return;
            }
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, colour,
                TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });
        }

        private void StartIfAllowed()
        {
            if (_disposed) return;
            if (!SystemParameters.ClientAreaAnimation || (!_hover && !AlwaysAnimate))
            {
                // 静止态：气流按当前状态画一帧就不动了，扇叶停在上一次的角度；
                // 水泵的青色光晕在这里是固定的，不靠常驻动画呼吸。
                _flow.Tick(0);
                ApplyStaticPumpGlow();
                return;
            }
            // Start() 仍要调用：RDP/Tier 0 下它只登记意愿、不挂钩；环境恢复时
            // FrameLoop 会通过 RenderEnvironment.Changed 自己续上。
            _loop.Start();
            if (RenderEnvironment.AnimationsDisabled)
            {
                _flow.Tick(0);
                ApplyStaticPumpGlow();
            }
        }

        private void Stop()
        {
            _loop.Stop();
            ApplyStaticPumpGlow();
        }

        private void ApplyStaticPumpGlow()
        {
            if (_pumpBreathing)
            {
                _pumpGlow.Opacity = 0.36;
                ScaleTransform scale = _pumpGlow.RenderTransform as ScaleTransform;
                if (scale != null) scale.ScaleX = scale.ScaleY = 1.0;
            }
            else
            {
                _pumpGlow.Opacity = 0;
            }
        }

        private void OnFrame(double dt)
        {
            if (dt <= 0) return;

            // 水泵的呼吸光晕：2 s 一个来回，透明度 0.28↔0.50、缩放 1.00↔1.10。
            // 只在帧循环允许时跑；静止态由 ApplyStaticPumpGlow 停在固定值。
            if (_pumpBreathing)
            {
                _pumpTime += dt;
                double phase = 0.5 + 0.5 * Math.Sin(_pumpTime * Math.PI);
                _pumpGlow.Opacity = 0.28 + 0.22 * phase;
                ScaleTransform scale = _pumpGlow.RenderTransform as ScaleTransform;
                if (scale != null) scale.ScaleX = scale.ScaleY = 1.0 + 0.10 * phase;
            }

            // 扇叶每一帧都动，转速用 250 ms 的时间常数逼近目标值，
            // 所以档位切换是加速/减速，不是跳变。
            // 加速快、减速慢：像有惯性的扇叶，掉档时是滑行下来而不是立刻刹住。
            double tau = _targetRevolutionsPerSecond > _revolutionsPerSecond ? 0.35 : 0.90;
            double ease = 1.0 - Math.Exp(-dt / tau);
            _revolutionsPerSecond += (_targetRevolutionsPerSecond - _revolutionsPerSecond) * ease;
            _angle = (_angle + _revolutionsPerSecond * 360.0 * dt) % 360.0;
            _rotor.Angle = _angle;

            // 气流不必跟扇叶同频：20 Hz 已足够顺，几何重建的次数降到三分之一。
            // 用累加器而不是清零，帧率上限一变（30 Hz 时单帧 33 ms）气流密度才不会跟着掉。
            _flowPendingSeconds += dt;
            while (_flowPendingSeconds >= FlowIntervalSeconds)
            {
                // 气流的密度与颜色跟着缓动后的转速走，不在档位切换那一刻硬跳。
                _flow.SetState(_accent, Clamp01(_revolutionsPerSecond / 1.25), _heat);
                _flow.Tick(FlowIntervalSeconds);
                _flowPendingSeconds -= FlowIntervalSeconds;
            }
        }

        /// <summary>Which channel the card shows; -1 = the first one that reports.（设置弹窗写入）</summary>
        /// <summary>-1 = 自动（先在转的那一路）；>= 0 = 指定通道号（0 表示名字里没有 # 的通道）。</summary>
        public int PreferredChannel { get; set; } = -1;

        /// <summary>Fans from the last snapshot, for the settings dialog's dropdown.</summary>
        public MetricsSnapshot.FanReading[] CurrentFans { get { return _currentFans; } }

        /// <summary>Opens the fan settings dialog (wired by the overview page).</summary>
        public Action OpenSettings { get; set; }

        /// <summary>Exposed for the UI suite: a card that is off-screen must not keep a frame loop.</summary>
        public bool IsAnimating { get { return _loop.IsRunning; } }

        /// <summary>Exposed for the UI suite: the band the current reading maps to.</summary>
        public MetricTone Tone { get { return _tone; } }

        /// <summary>Exposed for the UI suite: the continuous accent colour for the current RPM.</summary>
        public Color AccentColour { get { return _accent; } }

        /// <summary>Exposed for the UI suite: true while the pump's cyan halo is visible.</summary>
        public bool PumpGlowVisible { get { return _pumpGlow.Opacity > 0.01; } }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _loop.Dispose();
        }
    }
}
