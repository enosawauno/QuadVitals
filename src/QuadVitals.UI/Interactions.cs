using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QuadVitals.UI
{
    /// <summary>
    /// 卡片右上角的设置齿轮平时不显示，鼠标进入卡片（圆角矩形区域）才淡入，移出即淡出。
    /// 用不透明度而不是 Visibility：齿轮始终占着标题行右侧的位置，显隐不会把标题或内容顶动。
    /// 隐藏时一并关掉命中测试与焦点，避免点到看不见的按钮、或用 Tab 停在一个看不见的控件上。
    /// </summary>
    public static class HoverReveal
    {
        public static void Attach(FrameworkElement surface, UIElement target, bool startHidden = true)
        {
            if (surface == null || target == null) return;
            Apply(target, startHidden ? 0.0 : 1.0, !startHidden);
            surface.MouseEnter += (sender, e) => Apply(target, 1.0, true);
            surface.MouseLeave += (sender, e) => Apply(target, 0.0, IsKeyboardInside(target));

            // 只认鼠标等于把键盘与触屏挡在设置外面：Tab 进不来（Focusable=false），
            // 触屏第一下又只负责淡入。三条都要接上，但“平时不显示”保持不变。
            target.IsKeyboardFocusWithinChanged += (sender, e) =>
            {
                if (target.IsKeyboardFocusWithin) Apply(target, 1.0, true);
                else if (!surface.IsMouseOver) Apply(target, 0.0, false);
            };
            target.GotFocus += (sender, e) => Apply(target, 1.0, true);
            target.PreviewStylusDown += (sender, e) => Apply(target, 1.0, true);
        }

        private static bool IsKeyboardInside(UIElement target)
        {
            FrameworkElement element = target as FrameworkElement;
            return element != null && element.IsKeyboardFocusWithin;
        }

        private static void Apply(UIElement target, double opacity, bool interactive)
        {
            // Hold the base value while hidden as well: a card that is built but not rendered yet
            // would otherwise show its gear until the first animation tick.
            if (opacity <= 0.001) target.Opacity = 0;
            DoubleAnimation fade = new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(interactive ? 120 : 160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            target.BeginAnimation(UIElement.OpacityProperty, fade);
            // 命中测试跟着显隐走（不点看不见的东西），但**焦点始终可达**：
            // 隐藏时 `Focusable=false` 会让 Tab 永远进不了这个按钮，直接把键盘挡在设置外面。
            // 键盘焦点进来时由 GotFocus 把它淡入，所以“看不见却停在上面”也不会发生。
            target.IsHitTestVisible = interactive;
            target.Focusable = true;
        }
    }

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
