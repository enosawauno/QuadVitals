using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuadVitals.UI
{
    /// <summary>
    /// Dark scrollbar for every ScrollViewer/ListBox in the dashboard.
    ///
    /// Built in code (no XAML compiler in this project). Track is not IAddChild and exposes no
    /// Thumb dependency property, so the thumb is attached on Loaded through Track.Thumb.
    /// The stock template is replaced only for the track/thumb; wheel and drag still work.
    /// </summary>
    public static class DarkScrollBars
    {
        public static Style Create()
        {
            Style style = new Style(typeof(ScrollBar));
            ControlTemplate template = new ControlTemplate(typeof(ScrollBar));

            FrameworkElementFactory track = new FrameworkElementFactory(typeof(Track));
            track.Name = "PART_Track";
            track.SetValue(FrameworkElement.WidthProperty, 10.0);
            track.SetValue(Track.OrientationProperty,
                new TemplateBindingExtension(ScrollBar.OrientationProperty));
            track.SetValue(Track.ViewportSizeProperty,
                new TemplateBindingExtension(ScrollBar.ViewportSizeProperty));
            track.SetValue(Track.MaximumProperty,
                new TemplateBindingExtension(ScrollBar.MaximumProperty));
            track.SetValue(Track.MinimumProperty,
                new TemplateBindingExtension(ScrollBar.MinimumProperty));
            track.SetValue(Track.ValueProperty,
                new TemplateBindingExtension(ScrollBar.ValueProperty));
            track.SetValue(Track.IsDirectionReversedProperty, true);
            template.VisualTree = track;

            Trigger horizontal = new Trigger
            {
                Property = ScrollBar.OrientationProperty,
                Value = Orientation.Horizontal
            };
            horizontal.Setters.Add(Target(FrameworkElement.WidthProperty, double.NaN, "PART_Track"));
            horizontal.Setters.Add(Target(FrameworkElement.HeightProperty, 10.0, "PART_Track"));
            horizontal.Setters.Add(Target(Track.IsDirectionReversedProperty, false, "PART_Track"));
            template.Triggers.Add(horizontal);

            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            // ScrollBar 自带 MinWidth = SystemParameters.VerticalScrollBarWidth（17），
            // 不压掉的话设了 Width=10 也只有 17 px 宽。
            style.Setters.Add(new Setter(Control.MinWidthProperty, 0.0));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 0.0));
            style.Setters.Add(new Setter(Control.WidthProperty, 10.0));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new EventSetter(FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnScrollBarLoaded)));

            Trigger horizontalStyle = new Trigger
            {
                Property = ScrollBar.OrientationProperty,
                Value = Orientation.Horizontal
            };
            horizontalStyle.Setters.Add(new Setter(Control.WidthProperty, double.NaN));
            horizontalStyle.Setters.Add(new Setter(Control.HeightProperty, 10.0));
            style.Triggers.Add(horizontalStyle);
            return style;
        }

        private static void OnScrollBarLoaded(object sender, RoutedEventArgs e)
        {
            ScrollBar bar = sender as ScrollBar;
            if (bar == null) return;
            EnsureThumb(bar);

            // Loaded 会在模板应用之前就到达，那时 PART_Track 还是 null，早期版本直接返回，
            // 结果是一个没有 Thumb 的 Track：「能滚但看不见滚动条」。模板应用完再补一次。
            bar.Dispatcher.BeginInvoke(new Action(() => EnsureThumb(bar)), DispatcherPriority.Loaded);
        }

        private static void EnsureThumb(ScrollBar bar)
        {
            if (bar == null) return;
            // 无条件 ApplyTemplate：Template 可能已经设上但还没实例化，此时 FindName 返回 null。
            bar.ApplyTemplate();

            Track track = bar.Template == null ? null : bar.Template.FindName("PART_Track", bar) as Track;
            if (track == null)
            {
                // 模板还没实例化（例如元素尚未进入布局）：等第一帧布局完成后再补一次。
                EventHandler once = null;
                once = (sender, args) =>
                {
                    bar.LayoutUpdated -= once;
                    EnsureThumb(bar);
                };
                bar.LayoutUpdated += once;
                return;
            }
            if (track.Thumb != null) return;

            Thumb thumb = new Thumb { Template = BuildThumbTemplate() };
            if (bar.Orientation == Orientation.Horizontal)
            {
                thumb.Height = 6;
                thumb.MinWidth = 24;
                thumb.HorizontalAlignment = HorizontalAlignment.Stretch;
                thumb.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                thumb.Width = 6;
                thumb.MinHeight = 24;
                thumb.HorizontalAlignment = HorizontalAlignment.Center;
                thumb.VerticalAlignment = VerticalAlignment.Stretch;
            }
            thumb.Margin = new Thickness(2);
            track.Thumb = thumb;
        }

        private static Setter Target(DependencyProperty property, object value, string targetName)
        {
            Setter setter = new Setter(property, value);
            setter.TargetName = targetName;
            return setter;
        }

        private static ControlTemplate BuildThumbTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Thumb));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
            template.VisualTree = border;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x74, 0x74, 0x74))));
            template.Triggers.Add(hover);

            Trigger dragging = new Trigger { Property = Thumb.IsDraggingProperty, Value = true };
            dragging.Setters.Add(new Setter(Border.BackgroundProperty, Theme.AccentGreenBrush));
            template.Triggers.Add(dragging);
            return template;
        }
    }
}
