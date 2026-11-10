using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 功耗卡的第二种指示样式：两圈同心环（外环 CPU、内环 GPU）。
    /// 语义与条形图一致——弧长 = 占各自功耗上限的比例，颜色沿用分档色——
    /// 只是把「横条」换成「从 12 点顺时针走的圆弧」。样式由设置里的
    /// `powerCardStyle` 决定；切换时只折叠元素与列宽，不重建卡片。
    /// </summary>
    internal sealed partial class PowerCard
    {
        private const double RingSize = 128;
        private const double RingThickness = 10;
        private const double OuterRadius = 56;
        private const double InnerRadius = 40;

        private readonly Path _ringCpuPath = new Path();
        private readonly Path _ringGpuPath = new Path();
        private readonly TextBlock _ringTotal = new TextBlock();
        private readonly TextBlock _ringUnit = new TextBlock();
        private readonly SolidColorBrush _ringCpuBrush = new SolidColorBrush(Theme.AccentGreen);
        private readonly SolidColorBrush _ringGpuBrush = new SolidColorBrush(Theme.AccentGreenSoft);
        private ColumnDefinition _ringColumn;
        private ColumnDefinition _ringGap;
        private ColumnDefinition _cpuBarColumn;
        private ColumnDefinition _gpuBarColumn;
        private FrameworkElement _ringHost;
        private FrameworkElement _totalLine;
        private string _appliedStyle;

        /// <summary>功耗卡的指示样式：`bars`（默认）或 `rings`；由概览页每帧从设置里带进来。</summary>
        public string CardStyle { get; set; }

        private FrameworkElement BuildRingCluster()
        {
            Grid host = new Grid
            {
                Width = RingSize,
                Height = RingSize,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                ToolTip = "外环 CPU、内环 GPU；一圈画满表示达到各自的功耗上限"
            };
            AutomationProperties.SetAutomationId(host, "PowerRings");

            Canvas canvas = new Canvas { Width = RingSize, Height = RingSize };
            canvas.Children.Add(BuildRingTrack(OuterRadius));
            canvas.Children.Add(BuildRingTrack(InnerRadius));

            _ringCpuPath.Stroke = _ringCpuBrush;
            _ringGpuPath.Stroke = _ringGpuBrush;
            Path[] arcs = { _ringCpuPath, _ringGpuPath };
            foreach (Path arc in arcs)
            {
                arc.StrokeThickness = RingThickness;
                arc.StrokeStartLineCap = PenLineCap.Round;
                arc.StrokeEndLineCap = PenLineCap.Round;
                canvas.Children.Add(arc);
            }
            host.Children.Add(canvas);

            // 环心放合计：条形样式里这一行在左边，环样式里它进圆心，不重复出现。
            StackPanel center = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _ringTotal.Text = MetricFormat.Invalid;
            _ringTotal.FontSize = 20;
            _ringTotal.FontWeight = FontWeights.SemiBold;
            _ringTotal.Foreground = Theme.TextPrimaryBrush;
            _ringTotal.HorizontalAlignment = HorizontalAlignment.Center;
            Typography.SetNumeralAlignment(_ringTotal, FontNumeralAlignment.Tabular);
            AutomationProperties.SetAutomationId(_ringTotal, "PowerRingTotal");
            center.Children.Add(_ringTotal);

            _ringUnit.Text = "W";
            _ringUnit.FontSize = 11;
            _ringUnit.Foreground = Theme.TextSecondaryBrush;
            _ringUnit.HorizontalAlignment = HorizontalAlignment.Center;
            _ringUnit.Margin = new Thickness(0, 1, 0, 0);
            center.Children.Add(_ringUnit);
            host.Children.Add(center);

            return host;
        }

        private static Ellipse BuildRingTrack(double radius)
        {
            Ellipse track = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = new SolidColorBrush(Theme.TrackGrey),
                StrokeThickness = RingThickness,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(track, RingSize / 2 - radius);
            Canvas.SetTop(track, RingSize / 2 - radius);
            return track;
        }

        /// <summary>
        /// 从 12 点顺时针画一段弧；一整圈拆成两段半圆，避免起终点重合时 ArcTo 退化。
        /// 0° 在 12 点，顺时针为正。
        /// </summary>
        private static Geometry RingGeometry(double radius, double fraction)
        {
            double sweep = 360.0 * (fraction < 0 ? 0 : (fraction > 1 ? 1 : fraction));
            StreamGeometry geometry = new StreamGeometry();
            if (sweep <= 0.01) return geometry;

            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(PointOnRing(radius, 0), false, false);
                context.ArcTo(PointOnRing(radius, sweep / 2), new Size(radius, radius), 0,
                    false, SweepDirection.Clockwise, true, false);
                context.ArcTo(PointOnRing(radius, sweep), new Size(radius, radius), 0,
                    false, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private static Point PointOnRing(double radius, double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            double center = RingSize / 2;
            return new Point(center + radius * Math.Sin(radians), center - radius * Math.Cos(radians));
        }

        private void UpdateRings(double cpuFraction, double gpuFraction, bool cpuValid, bool gpuValid,
                                 string totalText)
        {
            if (_ringHost == null) return;

            _ringCpuPath.Data = RingGeometry(OuterRadius, cpuValid ? cpuFraction : 0);
            _ringGpuPath.Data = RingGeometry(InnerRadius, gpuValid ? gpuFraction : 0);
            _ringCpuBrush.Color = cpuValid ? ColourFor(_cpuTone) : Theme.TextSecondary;
            _ringGpuBrush.Color = gpuValid ? ColourFor(_gpuTone) : Theme.TextSecondary;
            _ringTotal.Text = totalText;
            _ringUnit.Text = totalText == MetricFormat.Invalid ? string.Empty : "W";
        }

        /// <summary>
        /// 样式切换只改可见性与列宽：条形 → 环 时把两条横条的列宽压到 0、收起左边合计行，
        /// 环 → 条形 时反过来。不重建卡片，切换不会丢动画相位或焦点。
        /// </summary>
        private void ApplyCardStyle()
        {
            bool rings = string.Equals(CardStyle, AppSettings.PowerStyleRings, StringComparison.OrdinalIgnoreCase);
            string stamp = rings ? AppSettings.PowerStyleRings : AppSettings.PowerStyleBars;
            if (stamp == _appliedStyle) return;
            _appliedStyle = stamp;

            if (_ringHost != null) _ringHost.Visibility = rings ? Visibility.Visible : Visibility.Collapsed;
            if (_ringColumn != null) _ringColumn.Width = new GridLength(rings ? RingSize : 0);
            if (_ringGap != null) _ringGap.Width = new GridLength(rings ? 16 : 0);

            if (_totalLine != null) _totalLine.Visibility = rings ? Visibility.Collapsed : Visibility.Visible;
            _cpuBar.Visibility = rings ? Visibility.Collapsed : Visibility.Visible;
            _gpuBar.Visibility = rings ? Visibility.Collapsed : Visibility.Visible;
            if (_cpuBarColumn != null) _cpuBarColumn.Width = new GridLength(rings ? 0 : BarWidth);
            if (_gpuBarColumn != null) _gpuBarColumn.Width = new GridLength(rings ? 0 : BarWidth);
        }
    }
}
