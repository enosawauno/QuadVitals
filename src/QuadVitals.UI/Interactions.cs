using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QuadVitals.UI
{
    /// <summary>
    /// 按钮的“弹”手感：只取参考按钮的弹性动效，颜色/阴影/渐变都不要。
    ///
    /// 用事件而不是模板 Trigger：按下后拖出按钮再松手、鼠标离开时归位这些情况事件更容易照顾到，
    /// 也不用把 ScaleTransform 写进模板（模板里的实例会被所有按钮共享，缩放会串台）。
    /// 变换只影响渲染，不参与布局，所以按下时邻居不会跟着动。
    /// </summary>
    public static class Interactions
    {
        public static void AttachSpring(FrameworkElement element, double hoverScale = 1.05, double pressScale = 0.96)
        {
            if (element == null) return;

            ScaleTransform scale = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;

            bool pressed = false;

            element.MouseEnter += (sender, e) => Animate(scale, pressed ? pressScale : hoverScale, 110, false);
            element.MouseLeave += (sender, e) => Animate(scale, 1.0, 110, false);

            element.PreviewMouseLeftButtonDown += (sender, e) =>
            {
                pressed = true;
                Animate(scale, pressScale, 90, false);
            };
            element.PreviewMouseLeftButtonUp += (sender, e) =>
            {
                if (!pressed) return;
                pressed = false;
                Animate(scale, element.IsMouseOver ? hoverScale : 1.0, 150, true);
            };

            // 按下后右键/失去捕获时也要回弹，否则按钮会一直停在缩小的状态。
            element.LostMouseCapture += (sender, e) =>
            {
                if (!pressed) return;
                pressed = false;
                Animate(scale, element.IsMouseOver ? hoverScale : 1.0, 150, true);
            };
            element.IsEnabledChanged += (sender, e) =>
            {
                if (element.IsEnabled) return;
                pressed = false;
                Animate(scale, 1.0, 90, false);
            };
        }

        /// <param name="settle">true = 回弹时轻微过冲（BackEase），用于松手那一下。</param>
        private static void Animate(ScaleTransform scale, double target, int milliseconds, bool settle)
        {
            IEasingFunction easing;
            if (settle) easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            else easing = new SineEase { EasingMode = EasingMode.EaseOut };

            DoubleAnimation x = new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = easing
            };
            DoubleAnimation y = new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = easing
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, y);
        }
    }
}
