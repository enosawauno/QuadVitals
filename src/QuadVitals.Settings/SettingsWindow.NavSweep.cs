using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 一级导航选中面里的那层扫光：fops 的 edge sweep 在这里是「一段虚线沿圆角矩形轮廓位移」。
    ///
    /// 单独一个文件，因为它是自成一体的动画件：几何、周长换算与时间线都在这里，
    /// 主窗口那边只在切换时叫一次 StartNavSweep。
    /// </summary>
    internal sealed partial class SettingsWindow
    {
        /// <summary>扫光描边宽度：2.6 px 比那块 1 px 的边框亮一圈。</summary>
        private const double NavSweepThickness = 2.6;

        /// <summary>扫光长度：整圈的一个比例。太长会像「整圈都亮」，太短看不清在跑。</summary>
        private const double NavSweepFraction = 0.16;

        /// <summary>扫光一圈 1.8 s，与 fops 的 edge-sweep 同一个节奏。</summary>
        private const int NavSweepMs = 1800;

        private System.Windows.Shapes.Path _navSweep;

        /// <summary>扫光路径的长度，按描边单位计（虚线数组与位移都用这套单位）。</summary>
        private double _navGliderPerimeter;

        /// <summary>
        /// 选中面内部：一条顶部亮线，加一圈会在切换时绕边框跑一趟的扫光。
        /// 扫光不用 conic 渐变（WPF 没有），而是让一段虚线沿圆角矩形的轮廓走一圈：
        /// 虚线段长按周长换算成描边单位，位移从 0 走到整圈。
        /// </summary>
        private FrameworkElement BuildNavGliderContent()
        {
            Grid content = new Grid();
            content.Children.Add(new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1, 1, 1, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
            });

            SolidColorBrush sweepBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            sweepBrush.Freeze();

            _navSweep = new System.Windows.Shapes.Path
            {
                Stroke = sweepBrush,
                StrokeThickness = NavSweepThickness,
                StrokeDashCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
                Opacity = 0
            };
            AutomationProperties.SetAutomationId(_navSweep, "NavSweep");
            content.Children.Add(_navSweep);

            content.SizeChanged += (sender, e) =>
            {
                _navGliderPerimeter = ApplyNavSweepGeometry(e.NewSize.Width, e.NewSize.Height);
            };
            return content;
        }

        /// <summary>按选中面的实际尺寸重建轮廓与虚线节拍；返回路径长度（描边单位）。</summary>
        private double ApplyNavSweepGeometry(double width, double height)
        {
            if (_navSweep == null || width <= 2 || height <= 2) return 0;

            const double radius = 9;
            _navSweep.Data = new RectangleGeometry(new Rect(1, 1, width - 2, height - 2), radius, radius);

            // 圆角矩形周长：两倍(宽+高) − 8r + 2πr。
            double perimeter = 2 * ((width - 2) + (height - 2)) - 8 * radius + 2 * Math.PI * radius;
            double units = perimeter / NavSweepThickness;
            double visible = units * NavSweepFraction;
            _navSweep.StrokeDashArray = new DoubleCollection(new double[] { visible, units - visible });
            _navSweep.StrokeDashOffset = 0;
            return units;
        }

        /// <summary>
        /// 切换时跑一圈：位移匀速走完整圈，不透明度在中段之后淡出，跑完就彻底熄灭。
        /// 动画被环境禁用（RDP / WPF Tier 0 / 电池）时直接不跑，边框上不留残影。
        /// </summary>
        private void StartNavSweep()
        {
            if (_navSweep == null || _navGliderPerimeter <= 0) return;
            if (RenderEnvironment.AnimationsDisabled)
            {
                _navSweep.BeginAnimation(UIElement.OpacityProperty, null);
                _navSweep.Opacity = 0;
                return;
            }

            _navSweep.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
            _navSweep.BeginAnimation(UIElement.OpacityProperty, null);
            _navSweep.Opacity = 1;

            _navSweep.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(0, -_navGliderPerimeter, TimeSpan.FromMilliseconds(NavSweepMs)));

            DoubleAnimationUsingKeyFrames fade = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(NavSweepMs)
            };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.45)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            _navSweep.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
