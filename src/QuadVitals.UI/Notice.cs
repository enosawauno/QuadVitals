using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace QuadVitals.UI
{
    /// <summary>
    /// 应用内提示栈（右下角），用来替掉 MessageBox。
    ///
    /// MessageBox 是系统 Win32 对话框：浅色、带系统图标、模态阻塞，放在这个深色无边框壳子里
    /// 跟另一个软件一样。这里用同一套卡片的圆角/配色，入场走 Theme 的缓动，普通提示几秒后
    /// 自己淡出，重要提示（例如“要重新勾选工具栏”）不自动消失，必须手动关。
    /// </summary>
    public sealed class NoticeHost : Grid
    {
        // Bottom-CENTRE, not bottom-right: the page actions (「放弃改动 / 保存」) live in the
        // bottom-right corner and a toast sitting on top of them blocks the click.
        private readonly StackPanel _stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(24)
        };

        public NoticeHost()
        {
            Children.Add(_stack);
        }

        /// <param name="persistent">true = 不自动消失，带关闭按钮（用于需要手动处理的提示）。</param>
        public void Show(string text, bool persistent = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            Border card = BuildCard(text, persistent);
            _stack.Children.Add(card);
            AnimateIn(card);

            if (persistent) return;

            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2800) };
            timer.Tick += (sender, e) =>
            {
                timer.Stop();
                DismissStatic(card);
            };
            timer.Start();
        }

        public void Clear()
        {
            _stack.Children.Clear();
        }

        private static Border BuildCard(string text, bool persistent)
        {
            Border card = new Border
            {
                Background = Theme.SurfaceCardBrush,
                BorderBrush = Theme.BorderSubtleBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 11, 12, 11),
                Margin = new Thickness(0, 8, 0, 0),
                MaxWidth = 430,
                Opacity = 0,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 8,
                    Direction = 270,
                    Opacity = 0.35,
                    Color = Colors.Black
                }
            };

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock icon = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = Theme.AccentGreenBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 9, 0)
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            TextBlock message = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                Foreground = Theme.TextPrimaryBrush,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(message, 1);
            row.Children.Add(message);

            if (persistent)
            {
                TextBlock close = new TextBlock
                {
                    Text = "\uE711",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 11,
                    Foreground = Theme.TextTertiaryBrush,
                    Margin = new Thickness(10, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                close.MouseEnter += (sender, e) => close.Foreground = Theme.TextPrimaryBrush;
                close.MouseLeave += (sender, e) => close.Foreground = Theme.TextTertiaryBrush;
                Grid.SetColumn(close, 2);
                row.Children.Add(close);

                close.MouseLeftButtonUp += (sender, e) =>
                {
                    e.Handled = true;
                    DismissStatic(card);
                };
            }

            card.Child = row;
            return card;
        }

        private static void AnimateIn(Border card)
        {
            TranslateTransform slide = new TranslateTransform(0, 10);
            card.RenderTransform = slide;

            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(Theme.MotionFadeMs))
            {
                EasingFunction = Theme.EnterFade()
            });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0,
                TimeSpan.FromMilliseconds(Theme.MotionPopMs))
            {
                EasingFunction = Theme.EnterFade()
            });
        }

        private static void DismissStatic(Border card)
        {
            if (card == null) return;

            DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = Theme.EnterFade()
            };
            fade.Completed += (sender, e) =>
            {
                Panel parent = card.Parent as Panel;
                if (parent != null) parent.Children.Remove(card);
            };
            card.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
