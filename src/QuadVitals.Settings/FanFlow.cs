using System;
using System.Windows;
using System.Windows.Media;
using QuadVitals.Core;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 风扇右侧的气流：风场流线。
    ///
    /// 每一条线都是一个粒子最近走过的轨迹——位置沿一个缓变的流场推进，画出来的就是一条带弧度的细线，
    /// 头尾收窄、两端淡出。线彼此同向、弯曲程度接近，叠在一起读作“空气在流”，而不是一堆静止的线段。
    /// 转速决定推进速度与线的数量；颜色由调用方给出，风扇危险档整体向上弯、颜色由红转橙，
    /// 水泵保持单一青色且不向上弯。
    /// </summary>
    internal sealed class FanFlow : FrameworkElement
    {
        private const int TrailCount = 14;
        private const int Segments = 14;
        private const double MinWidth = 0.7;

        private readonly double[] _x = new double[TrailCount];      // 粒子头部的 x（最右端）
        private readonly double[] _y = new double[TrailCount];
        private readonly double[] _speed = new double[TrailCount];
        private readonly double[] _length = new double[TrailCount];
        private readonly double[] _phase = new double[TrailCount];
        private readonly double[] _thickness = new double[TrailCount];
        // 每条流线的几何与点数组只分配一次：20 fps × 14 条线的对象分配在这里全部消掉，
        // 高频重绘时不会给 GC 添压力（几何对象不 Freeze，下一帧直接重写内容）。
        private readonly StreamGeometry[] _geometries = new StreamGeometry[TrailCount];
        private readonly Point[][] _paths = new Point[TrailCount][];
        private readonly double[][] _widths = new double[TrailCount][];
        private readonly Random _random = new Random(20261103);

        private double _time;
        private double _normalized;
        private bool _heat;
        private Color _colour = Theme.AccentGreenSoft;
        private Brush _brush = BuildBrush(Theme.AccentGreenSoft, false);
        private double _width = 80;

        public FanFlow()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = false;
            for (int i = 0; i < TrailCount; i++)
            {
                _geometries[i] = new StreamGeometry();
                _paths[i] = new Point[Segments + 1];
                _widths[i] = new double[Segments + 1];
                Respawn(i, true);
            }
        }

        /// <summary>Continuous accent colour and speed factor (0..1); only the heat flag lifts the stream.</summary>
        public void SetState(Color colour, double normalized, bool heat)
        {
            if (normalized < 0) normalized = 0;
            if (normalized > 1) normalized = 1;
            _normalized = normalized;
            bool heatChanged = heat != _heat;
            _heat = heat;
            // The colour changes at most once per snapshot; rebuilding the gradient then is
            // cheaper than keeping a mutable brush in sync with every alpha stop.
            if (colour != _colour || heatChanged)
            {
                _colour = colour;
                _brush = BuildBrush(colour, heat);
            }
            InvalidateVisual();
        }

        /// <summary>Advances every stream (called from the card's frame loop).</summary>
        public void Tick(double seconds)
        {
            _time += seconds;
            for (int i = 0; i < TrailCount; i++)
            {
                _x[i] += _speed[i] * (0.45 + 0.95 * _normalized) * seconds;
                if (_x[i] - _length[i] > _width - 2) Respawn(i, false);
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            _width = ActualWidth;
            if (_width <= 6 || ActualHeight <= 6) return;

            int visible = 8 + (int)Math.Round(16 * _normalized);
            for (int i = 0; i < TrailCount; i++)
            {
                if (i >= visible) continue;
                DrawTrail(dc, i);
            }
        }

        private void DrawTrail(DrawingContext dc, int index)
        {
            double headX = _x[index];
            double length = _length[index];
            double speed = Math.Max(1, _speed[index]);
            double phase = _phase[index];
            double thickness = _thickness[index];

            Point[] points = _paths[index];
            double[] widths = _widths[index];
            for (int s = 0; s <= Segments; s++)
            {
                double u = (double)s / Segments;          // 0 = tail, 1 = head
                double distance = u * length;
                double x = headX - length + distance;
                // The tail is older than the head: sample the field at the time the particle
                // actually passed there, which is what turns a sine into a real path.
                double age = (length - distance) / speed;
                double y = _y[index] + FlowY(x, _time - age, phase);
                if (_heat) y -= 14 * Math.Pow(x / Math.Max(1, _width), 2);
                points[s] = new Point(x, y);
                double taper = Math.Sin(Math.PI * u);
                if (taper < 0) taper = 0;
                widths[s] = Math.Max(MinWidth * 0.5, thickness * Math.Pow(taper, 0.65));
            }

            StreamGeometry geometry = _geometries[index];
            StreamGeometryContext ctx;
            try
            {
                ctx = geometry.Open();
            }
            catch (InvalidOperationException)
            {
                // 上一次绘制还没被合成线程释放：换一块新的，下一帧继续复用。
                geometry = new StreamGeometry();
                _geometries[index] = geometry;
                ctx = geometry.Open();
            }
            using (ctx)
            {
                ctx.BeginFigure(new Point(points[0].X, points[0].Y - widths[0]), true, true);
                for (int s = 1; s <= Segments; s++) ctx.LineTo(new Point(points[s].X, points[s].Y - widths[s]), true, false);
                for (int s = Segments; s >= 0; s--) ctx.LineTo(new Point(points[s].X, points[s].Y + widths[s]), true, false);
            }

            // Each trail fades in at its tail and out at its head, so a line never begins or ends
            // on a hard pixel; the brush is mapped to the geometry's own bounding box.
            // 头部接近元素右缘时羽化：不靠 ClipToBounds（那会切出一条硬边），
            // 也不越过元素边界压到右边的读数。
            double nearEdge = Math.Min(1.0, Math.Min(headX, _width - headX) / 22.0);
            if (nearEdge < 0.12) nearEdge = 0.12;
            dc.PushOpacity(nearEdge);
            dc.DrawGeometry(_brush, null, geometry);
            dc.Pop();
        }

        /// <summary>Smooth, low-frequency field: two sine layers with different wavelengths.</summary>
        private static double FlowY(double x, double time, double phase)
        {
            return Math.Sin(x * 0.052 + time * 1.15 + phase) * 3.4
                   + Math.Sin(x * 0.021 - time * 0.62 + phase * 0.7) * 4.6;
        }

        private void Respawn(int index, bool initial)
        {
            _length[index] = 26 + _random.NextDouble() * 30;
            _speed[index] = 22 + _random.NextDouble() * 34;
            _y[index] = 18 + _random.NextDouble() * 86;
            _phase[index] = _random.NextDouble() * Math.PI * 2;
            _thickness[index] = 0.8 + _random.NextDouble() * 0.9;
            // Initial spread keeps the lane populated on the very first frame instead of showing
            // all trails bunched at the emitter.
            _x[index] = initial
                ? _width - 2 - _random.NextDouble() * (_width + 40)
                : _length[index] + 4;
        }

        private static Brush BuildBrush(Color colour, bool heat)
        {
            LinearGradientBrush brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            Color tail = heat ? Theme.AccentOrange : colour;
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(160, colour.R, colour.G, colour.B), 0.25));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(150, tail.R, tail.G, tail.B), 0.80));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, tail.R, tail.G, tail.B), 1.0));
            brush.Freeze();
            return brush;
        }
    }
}
