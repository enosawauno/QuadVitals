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
        private const double RingThickness = 13;
        private const double OuterRadius = 59;
        private const double InnerRadius = 40;

        private readonly RingArc _ringCpu = new RingArc(OuterRadius);
        private readonly RingArc _ringGpu = new RingArc(InnerRadius);
        private readonly TextBlock _ringTotal = new TextBlock();
        private readonly TextBlock _ringUnit = new TextBlock();
        private ColumnDefinition _ringColumn;
        private ColumnDefinition _ringGap;
        private ColumnDefinition _cpuBarColumn;
        private ColumnDefinition _gpuBarColumn;
        private ColumnDefinition _cpuStarColumn;
        private ColumnDefinition _gpuStarColumn;
        private Ellipse _cpuDot;
        private Ellipse _gpuDot;
        private FrameworkElement _ringHost;
        private StackPanel _readout;
        private FrameworkElement _totalLine;
        private Grid _body;
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
            // 轨道也用同一段圆弧画（Fraction = 1），不用 Ellipse：Shape 的布局盒会把描边算进去，
            // 实际半径是 (Width - StrokeThickness)/2，跟弧线的半径对不上，看着就是「弧比轨道偏外」。
            canvas.Children.Add(new RingArc(OuterRadius) { Fraction = 1, FlatColour = Theme.TrackGrey, IsHitTestVisible = false });
            canvas.Children.Add(new RingArc(InnerRadius) { Fraction = 1, FlatColour = Theme.TrackGrey, IsHitTestVisible = false });
            canvas.Children.Add(_ringCpu);
            canvas.Children.Add(_ringGpu);
            host.Children.Add(canvas);

            // 环心放合计：条形样式里这一行在左边，环样式里它进圆心，不重复出现。
            StackPanel center = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 环心只放数值：和单位当成一整块居中，不再占一行「合计」。
            _ringTotal.Text = MetricFormat.Invalid;
            _ringTotal.FontSize = 22;
            // 合计搬到右侧读数块顶部，环心不再放数字。
            _ringTotal.Visibility = Visibility.Collapsed;
            _ringUnit.Visibility = Visibility.Collapsed;
            // 不给固定宽度：「数目 + W」整体居中，才会真的落在环心；
            // 位数变化时数字向两侧对称展开，环本身不动。
            _ringTotal.TextAlignment = TextAlignment.Center;
            _ringTotal.FontWeight = FontWeights.SemiBold;
            _ringTotal.Foreground = Theme.TextPrimaryBrush;
            _ringTotal.HorizontalAlignment = HorizontalAlignment.Center;
            Typography.SetNumeralAlignment(_ringTotal, FontNumeralAlignment.Tabular);
            AutomationProperties.SetAutomationId(_ringTotal, "PowerRingTotal");

            _ringUnit.Text = "W";
            _ringUnit.FontSize = 12;
            _ringUnit.TextAlignment = TextAlignment.Left;
            _ringUnit.Foreground = Theme.TextSecondaryBrush;
            _ringUnit.VerticalAlignment = VerticalAlignment.Bottom;
            _ringUnit.Margin = new Thickness(3, 0, 0, 3);
            StackPanel totalRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            totalRow.Children.Add(_ringTotal);
            totalRow.Children.Add(_ringUnit);
            center.Children.Add(totalRow);
            host.Children.Add(center);

            return host;
        }

        private void UpdateRings(double cpuFraction, double gpuFraction, bool cpuValid, bool gpuValid,
                                 string totalText)
        {
            if (_ringHost == null) return;

            _ringCpu.Fraction = cpuValid ? cpuFraction : 0;
            _ringGpu.Fraction = gpuValid ? gpuFraction : 0;
            // 两个圆点用弧末端的同一个色阶颜色：数据与环靠颜色一一对应，不用记“外环是 CPU”。
            if (_cpuDot != null) _cpuDot.Fill = new SolidColorBrush(RingArc.RampColour(cpuValid ? cpuFraction * 100 : 0));
            if (_gpuDot != null) _gpuDot.Fill = new SolidColorBrush(RingArc.RampColour(gpuValid ? gpuFraction * 100 : 0));
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
            if (_ringGap != null) _ringGap.Width = new GridLength(rings ? 24 : 0);

            // 环样式下内容比卡片窄，整体居中才不会左边一堆、右边一片空。
            if (_body != null) _body.HorizontalAlignment = rings ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            // 读数块宽度固定：居中布局里只要内容宽度变了，整组就会左右挪一下（每秒一次像抖动）。
            if (_readout != null) _readout.Width = rings ? 236 : double.NaN;
            // 圆点只占位不变化：只改透明度，行宽不会跟着变。
            if (_cpuDot != null) _cpuDot.Opacity = rings ? 1 : 0;
            if (_gpuDot != null) _gpuDot.Opacity = rings ? 1 : 0;
            _cpuBar.Visibility = rings ? Visibility.Collapsed : Visibility.Visible;
            _gpuBar.Visibility = rings ? Visibility.Collapsed : Visibility.Visible;
            if (_cpuBarColumn != null) _cpuBarColumn.Width = new GridLength(rings ? 0 : BarWidth);
            if (_gpuBarColumn != null) _gpuBarColumn.Width = new GridLength(rings ? 0 : BarWidth);
            // 环样式下横条没了，星号列会把 TDP 推到卡片最右边（中间空出一大截）。
            // 把星号压成 0，两行就变成「CPU 138.7 W　MTP 253 W」的紧凑一行。
            if (_cpuStarColumn != null) _cpuStarColumn.Width = rings ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            if (_gpuStarColumn != null) _gpuStarColumn.Width = rings ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        }

        /// <summary>
        /// 一段圆弧：从 12 点顺时针走 fraction 圈，圆头端点，轨道由外层画。
        /// 自己实现 OnRender 而不是用 Path，是因为 Shape 会按几何包围盒推算尺寸，
        /// 弧的包围盒比整圆小，一不小心就被 Stretch 放大、看着比轨道粗一圈。
        /// </summary>
        internal sealed class RingArc : FrameworkElement
        {
            private readonly double _radius;
            private double _fraction;

            public RingArc(double radius)
            {
                _radius = radius;
                Width = RingSize;
                Height = RingSize;
                SnapsToDevicePixels = false;
            }

            /// <summary>固定颜色（轨道用）；null 表示按占用色阶扫渐变。</summary>
            public Color? FlatColour { get; set; }

            /// <summary>轨道与弧共用的半径；两者必须相等，否则会出现「弧在轨道外」。</summary>
            public double Radius { get { return _radius; } }

            public double Fraction
            {
                get { return _fraction; }
                set
                {
                    double clamped = value < 0 ? 0 : (value > 1 ? 1 : value);
                    if (Math.Abs(clamped - _fraction) < 0.0005) return;
                    _fraction = clamped;
                    InvalidateVisual();
                }
            }


            protected override void OnRender(DrawingContext drawing)
            {
                base.OnRender(drawing);
                if (_fraction <= 0.002) return;

                Point center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
                double sweep = 360.0 * _fraction;

                // 与任务栏那个「渐变环」同一套：弧按 4 度一段描，每段取自己所在位置的色阶颜色
                // （绿 → 80% 黄 → 90% 红），扫过去就是一整条连续渐变，而不是三档纯色。
                const double stepDegrees = 4.0;
                int segments = (int)(sweep / stepDegrees);
                if (segments < 1) segments = 1;

                for (int i = 0; i < segments; i++)
                {
                    double from = i * stepDegrees;
                    double to = (i + 1 == segments) ? sweep : (i + 1) * stepDegrees;
                    double middlePercent = (from + to) / 2.0 / 360.0 * 100.0;

                    StreamGeometry geometry = new StreamGeometry();
                    using (StreamGeometryContext context = geometry.Open())
                    {
                        context.BeginFigure(PointOn(center, from), false, false);
                        // 段与段之间重叠 0.5 度，圆头端点接起来才没有暗缝。
                        context.ArcTo(PointOn(center, to + 0.5), new Size(_radius, _radius), 0,
                            (to - from) > 180.0, SweepDirection.Clockwise, true, false);
                    }
                    geometry.Freeze();

                    Color stroke = FlatColour.HasValue ? FlatColour.Value : RampColour(middlePercent);
                    Pen pen = new Pen(new SolidColorBrush(stroke), RingThickness)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round
                    };
                    pen.Freeze();
                    drawing.DrawGeometry(null, pen, geometry);
                }
            }

            /// <summary>
            /// 占用色阶：0 到 80 从绿插到黄，80 到 90 从黄插到红，90 以上恒红。
            /// 与任务栏渐变环同一组锚点（80 / 90），所以两处同一读数看到的是同一个颜色。
            /// </summary>
            internal static Color RampColour(double percent)
            {
                if (percent != percent) percent = 0;
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;

                Color from;
                Color to;
                double t;
                if (percent < 80) { from = Theme.AccentGreen; to = Theme.AccentOrange; t = percent / 80.0; }
                else if (percent < 90) { from = Theme.AccentOrange; to = Theme.AccentRed; t = (percent - 80) / 10.0; }
                else return Theme.AccentRed;

                return Color.FromRgb(
                    (byte)(from.R + (to.R - from.R) * t + 0.5),
                    (byte)(from.G + (to.G - from.G) * t + 0.5),
                    (byte)(from.B + (to.B - from.B) * t + 0.5));
            }

            /// <summary>0° 在 12 点，顺时针为正。</summary>
            private Point PointOn(Point center, double degrees)
            {
                double radians = degrees * Math.PI / 180.0;
                return new Point(center.X + _radius * Math.Sin(radians), center.Y - _radius * Math.Cos(radians));
            }
        }
    }
}
