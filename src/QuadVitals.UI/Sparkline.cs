using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using QuadVitals.Core;

namespace QuadVitals.UI
{
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
}
