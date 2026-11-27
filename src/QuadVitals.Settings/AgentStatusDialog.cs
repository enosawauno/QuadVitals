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
    /// <summary>One reading of the agent channel, taken when the status dialog is built.</summary>
    internal struct AgentStateInfo
    {
        public bool Alive;
        public bool ChannelOpen;
        public bool HasSnapshot;
        public double AgeSeconds;
        public DateTime LastUpdateLocal;
        /// <summary>Validity flags after <see cref="MetricSanity"/>; the data watchdog reads these.</summary>
        public MetricFlags Flags;
    }

    /// <summary>
    /// The title-bar agent chip opens this panel: what the agent is doing right now, and one
    /// button that acts on it (restart when it is alive, start it when it is not).
    ///
    /// The badge used to be a read-only label, so a dead agent left the user with a red-ish chip
    /// and no way back: the dashboard would never recover until the next logon.
    /// </summary>
    internal sealed class AgentStatusDialog
    {
        /// <summary>Red flag for "not running"; a filled dot for "alive".</summary>
        public const string FlagGlyph = "\u2691";
        public const string DotGlyph = "\u25CF";

        private readonly SettingsWindow _window;

        public AgentStatusDialog(SettingsWindow window)
        {
            _window = window;
        }

        public FrameworkElement Build(AgentStateInfo state)
        {
            StackPanel content = new StackPanel();

            // 标题行只放标题与右上角的叉：关闭是一个“离开”动作，和标题同层比和主操作同排更好找，
            // 也与反馈、诊断、设置那几块板保持一致。
            Grid head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Text = "传感器状态",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            Button close = SettingsWindow.PanelCloseButton("AgentClose");
            close.Click += (sender, e) => _window.CloseOverlay();
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            content.Children.Add(head);

            // 重启期间面板只留一个加载环：正文用 Hidden 而不是 Collapsed，占位还在，
            // 卡片尺寸因此不跳；环叠在同一个格子里。
            StackPanel body = new StackPanel();
            body.Children.Add(BuildStateRow(state));
            body.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 10, 0, 8),
                Background = Theme.BorderSubtleBrush
            });

            body.Children.Add(DetailRow("通道", "Local\\QuadVitals.Metrics.v1"));
            body.Children.Add(DetailRow("采样数据", DescribeData(state)));
            body.Children.Add(DetailRow("权限", AgentDiagnostics.ElevationLine(state.Flags)));
            if ((state.Flags & MetricFlags.PawnIoMissing) != 0)
                body.Children.Add(DetailRow("驱动", AgentDiagnostics.PawnIoRequired));
            body.Children.Add(DetailRow("自动恢复", AutoRecoveryLine()));

            RotateTransform spin = new RotateTransform(0);
            FrameworkElement ring = BuildLoadingRing(spin);
            ring.Visibility = Visibility.Collapsed;

            Grid stage = new Grid();
            stage.Children.Add(body);
            stage.Children.Add(ring);
            content.Children.Add(stage);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            // 一个动作：重启传感器。原先“修复登录计划任务”是第二个按钮，但它与重启是同一件事的两半
            // （重建任务 → 换进程），拆开后点哪个都只完成一半；现在重建任务在重启里自动完成。
            // 按钮只留文案：图标在 12 px 上只能画成一块色斑，读不出“授权”的含义。
            FlatButton action = new FlatButton(state.Alive ? "重启传感器" : "启动传感器", true);
            // 按钮贴内容宽度：左右内边距收窄后不再比文字宽一圈，也不再靠 MinWidth 撑着。
            action.Padding = new Thickness(12, 7, 12, 7);
            action.ToolTip = state.Alive
                ? "以管理员权限重启传感器进程，然后重新读取全部数据"
                : "以管理员权限启动传感器进程";
            AutomationProperties.SetAutomationId(action, "AgentAction");

            TextBlock actionLabel = new TextBlock
            {
                Text = state.Alive ? "重启传感器" : "启动传感器",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            action.Content = actionLabel;

            // 重启/启动进行中的文案不省略号：“正在重启传感器”是完整的一句，
            // 省略号会让按钮宽度随文案跳动。
            string busyLabel = state.Alive ? "正在重启传感器" : "正在启动传感器";
            string idleLabel = state.Alive ? "重启传感器" : "启动传感器";

            action.Click += (sender, e) =>
            {
                if (state.Alive)
                {
                    // 重启要等旧进程退出、新进程写出第一帧，按钮必须立刻显示“在做事”，
                    // 否则点下去到面板重建之间看起来像没反应。
                    action.IsEnabled = false;
                    actionLabel.Text = busyLabel;
                    body.Visibility = Visibility.Hidden;
                    ring.Visibility = Visibility.Visible;
                    // 只有“正在重启”这个图形态把按钮摆到正中；文字态始终留在右下角。
                    buttons.HorizontalAlignment = HorizontalAlignment.Center;
                    StartSpin(spin);
                    _window.RestartAgentFromDialog(ok =>
                    {
                        // 成功时面板直接关掉，不用复位；失败要把正文和按钮还原，否则面板停在环上。
                        if (ok) return;
                        body.Visibility = Visibility.Visible;
                        ring.Visibility = Visibility.Collapsed;
                        spin.BeginAnimation(RotateTransform.AngleProperty, null);
                        buttons.HorizontalAlignment = HorizontalAlignment.Right;
                        action.IsEnabled = true;
                        actionLabel.Text = idleLabel;
                    });
                }
                else
                {
                    action.IsEnabled = false;
                    actionLabel.Text = busyLabel;
                    _window.StartAgentFromDialog();
                }
            };
            buttons.Children.Add(action);

            content.Children.Add(buttons);

            return new Border
            {
                Width = 520,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true,
                Child = content
            };
        }

        /// <summary>
        /// 一行读完：状态点 + 状态词 + 最近一次采集时间。原来点、词、时间分两行，眼睛要在
        /// 上下两行之间跳；合成一行后“有没有在采、上一帧什么时候”一眼扫完。
        /// </summary>
        private static FrameworkElement BuildStateRow(AgentStateInfo state)
        {
            StackPanel row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            row.Children.Add(BuildStatusLight(state.Alive));

            TextBlock headline = new TextBlock
            {
                Text = state.Alive ? "正常" : "离线",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = state.Alive ? Theme.AccentGreenBrush : Theme.AccentRedBrush
            };
            AutomationProperties.SetAutomationId(headline, "AgentHeadline");
            row.Children.Add(headline);

            row.Children.Add(new Border
            {
                Width = 1,
                Height = 12,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Theme.BorderStrongBrush,
                SnapsToDevicePixels = true
            });

            TextBlock detail = new TextBlock
            {
                Text = state.Alive
                    ? StateLine(state)
                    : (state.ChannelOpen ? "通道在，但没有收到快照" : "共享内存通道不存在"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.TextSecondaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            AutomationProperties.SetAutomationId(detail, "AgentSubline");
            row.Children.Add(detail);

            return row;
        }

        /// <summary>
        /// 状态点：实心小圆 + 一圈呼吸光晕。光晕用径向渐变而不是模糊，缩到 100% DPI 也不糊；
        /// 动画只改透明度和缩放，2 秒一个来回，和“心跳”一个节奏。
        /// </summary>
        private static FrameworkElement BuildStatusLight(bool alive)
        {
            Color color = alive ? Theme.AccentGreen : Theme.AccentRed;

            RadialGradientBrush haloBrush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, color.R, color.G, color.B), 0.0));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x3A, color.R, color.G, color.B), 0.5));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1.0));
            haloBrush.Freeze();

            Ellipse halo = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = haloBrush,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            Ellipse dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid light = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetAutomationId(light, "AgentStateIcon");
            light.Children.Add(halo);
            light.Children.Add(dot);

            if (alive && !RenderEnvironment.AnimationsDisabled)
            {
                Duration cycle = TimeSpan.FromMilliseconds(2000);
                SineEase ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                ScaleTransform pulse = new ScaleTransform(0.9, 0.9);
                halo.RenderTransform = pulse;

                DoubleAnimation grow = new DoubleAnimation(0.9, 1.12, cycle)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = ease
                };
                pulse.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                pulse.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

                halo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.5, 1.0, cycle)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = ease
                });
            }
            else
            {
                halo.Opacity = 0.5;
            }

            return light;
        }

        /// <summary>
        /// 重启期间的环状加载：一圈淡轨道加一段 270° 的弧。均匀虚线整圈旋转看上去是静止的，
        /// 所以弧必须不对称（一个缺口当“头”），转速恒定（加了缓动会一顿一顿）。RDP、WPF 软件
        /// 渲染与电池供电下逐帧动画本来就被禁，那时只留静态的一圈。
        /// </summary>
        private static FrameworkElement BuildLoadingRing(RotateTransform spin)
        {
            const double diameter = 80;
            const double thickness = 6;

            // 虚线段长度按“描边宽度为 1 单位”计，所以 270° + 90° 缺口要自己换算周长。
            double circumference = Math.PI * (diameter - thickness);
            double units = circumference / thickness;
            double visible = units * 0.75;

            Grid host = new Grid
            {
                Width = diameter + 28,
                Height = diameter + 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(host, "AgentLoadingRing");

            // 一层很淡的绿晕，让环在空面板上不是一块孤立的线；静态，不跟着转。
            RadialGradientBrush haloBrush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0x1D, 0xB9, 0x54), 0.0));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x1D, 0xB9, 0x54), 1.0));
            haloBrush.Freeze();
            host.Children.Add(new Ellipse
            {
                Width = diameter + 16,
                Height = diameter + 16,
                Fill = haloBrush,
                IsHitTestVisible = false
            });

            host.Children.Add(new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Stroke = Theme.AccentGreenBrush,
                StrokeThickness = thickness,
                Opacity = 0.14
            });

            Ellipse arc = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Stroke = Theme.AccentGreenBrush,
                StrokeThickness = thickness,
                StrokeDashArray = new DoubleCollection(new double[] { visible, units - visible }),
                StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = spin
            };
            host.Children.Add(arc);
            return host;
        }

        private static void StartSpin(RotateTransform spin)
        {
            if (RenderEnvironment.AnimationsDisabled)
            {
                // 没动画时让缺口停在右上，看起来是一个“进度环”而不是断掉的圈。
                spin.Angle = 135;
                return;
            }
            spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1050))
                {
                    // 匀速：进度指示一旦加速或回弹，就会读成“卡了一下”。
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }

        private static string StateLine(AgentStateInfo state)
        {
            if (!state.HasSnapshot) return "通道已打开，等待第一帧";
            return string.Format("最近一次采集时间 {0}（{1}）",
                state.LastUpdateLocal.ToString("HH:mm:ss"), FormatAge(state.AgeSeconds));
        }

        private static string DescribeData(AgentStateInfo state)
        {
            if (!state.HasSnapshot) return "无";
            return state.LastUpdateLocal.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static string AutoRecoveryLine()
        {
            // 从退避表本身取间隔，不再手写一份：改表时面板不会继续显示旧数字。
            List<string> intervals = new List<string>();
            for (int i = 1; i < AgentAutoRecovery.DelaysSeconds.Length; i++)
            {
                intervals.Add(AgentAutoRecovery.DelaysSeconds[i].ToString());
            }
            return string.Format("离线后自动重试 {0} 次（间隔 {1} 秒）",
                AgentAutoRecovery.MaxAttempts, string.Join(" / ", intervals.ToArray()));
        }

        /// <summary>秒数只在最近一分钟内直说；再旧就换成分钟/小时，否则一串 7000 秒没人读得出来。</summary>
        private static string FormatAge(double seconds)
        {
            if (seconds < 0) return "刚刚";
            if (seconds < 60) return string.Format("{0:0.0} 秒前", seconds);
            if (seconds < 3600) return string.Format("{0:0.0} 分钟前", seconds / 60.0);
            return string.Format("{0:0.0} 小时前", seconds / 3600.0);
        }

        private static FrameworkElement DetailRow(string label, string value)
        {
            Grid row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock caption = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush
            };
            row.Children.Add(caption);

            TextBlock content = new TextBlock
            {
                Text = value,
                FontSize = 12,
                Foreground = Theme.TextPrimaryBrush,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            return row;
        }
    }
}
