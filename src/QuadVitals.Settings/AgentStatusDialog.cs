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

            content.Children.Add(new TextBlock
            {
                Text = "传感器状态",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                Margin = new Thickness(0, 0, 0, 14)
            });

            content.Children.Add(BuildStateRow(state));
            content.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 16, 0, 14),
                Background = Theme.BorderSubtleBrush
            });

            content.Children.Add(DetailRow("通道", "Local\\QuadVitals.Metrics.v1"));
            content.Children.Add(DetailRow("采样数据", DescribeData(state)));
            content.Children.Add(DetailRow("启动方式", "登录计划任务"));
            content.Children.Add(DetailRow("自动恢复", AutoRecoveryLine()));

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            FlatButton action = new FlatButton(state.Alive ? "重启传感器" : "启动传感器", true);
            action.MinWidth = 96;
            if (state.Alive) action.ToolTip = "重新启动传感器进程，重新读取温度与显卡数据";
            AutomationProperties.SetAutomationId(action, "AgentAction");
            action.Click += (sender, e) =>
            {
                if (state.Alive)
                {
                    // 重启要等旧进程退出、新进程写出第一帧，按钮必须立刻显示“在做事”，
                    // 否则点下去到面板重建之间看起来像没反应。
                    action.IsEnabled = false;
                    action.Content = "重启中…";
                    _window.RestartAgentFromDialog();
                }
                else
                {
                    _window.StartAgentFromDialog();
                }
            };
            buttons.Children.Add(action);

            FlatButton close = new FlatButton("关闭");
            close.MinWidth = 88;
            close.Margin = new Thickness(10, 0, 0, 0);
            AutomationProperties.SetAutomationId(close, "AgentClose");
            close.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(close);

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
                Width = 30,
                Height = 30,
                Fill = haloBrush,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            Ellipse dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid light = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 1, 0) };
            AutomationProperties.SetAutomationId(light, "AgentStateIcon");
            light.Children.Add(halo);
            light.Children.Add(dot);

            if (alive)
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
            Grid row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
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
