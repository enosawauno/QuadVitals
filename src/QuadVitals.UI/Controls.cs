using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using QuadVitals.Core;

namespace QuadVitals.UI
{
    /// <summary>Dark card container used across the dashboard.</summary>
    public class Card : Border
    {
        private readonly StackPanel _content;
        private readonly TextBlock _title;
        private readonly ContentControl _headerRight;
        private readonly StackPanel _titleGroup;

        public Card(string title) : this(title, (FrameworkElement)null)
        {
        }

        /// <summary>标题左侧的系统图标字形（Segoe MDL2 Assets）。</summary>
        public Card(string title, string glyph) : this(title, GlyphText(glyph))
        {
        }

        /// <summary>标题左侧的自绘图标；供“系统字形在小尺寸下只剩骨架”的场合使用。</summary>
        public Card(string title, FrameworkElement icon)
        {
            // The reference cards are flat #303030 blocks on #191919 with no visible border.
            Background = Theme.SurfaceCardBrush;
            BorderThickness = new Thickness(0);
            CornerRadius = new CornerRadius(Theme.CardRadius);
            Padding = new Thickness(Theme.CardPadding);
            SnapsToDevicePixels = true;

            StackPanel root = new StackPanel();
            _title = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            StackPanel titleGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            _titleGroup = titleGroup;
            if (icon != null) titleGroup.Children.Add(icon);
            titleGroup.Children.Add(_title);
            _headerRight = new ContentControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.Children.Add(titleGroup);
            Grid.SetColumn(_headerRight, 1);
            header.Children.Add(_headerRight);
            root.Children.Add(header);

            _content = new StackPanel();
            root.Children.Add(_content);

            Child = root;
        }

        /// <summary>与标题行（15 px 字高、右侧 7 px 间距）对齐的系统字形块。</summary>
        private static FrameworkElement GlyphText(string glyph)
        {
            if (string.IsNullOrEmpty(glyph)) return null;
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
        }

        /// <summary>
        /// 换掉标题左侧的图标。系统字形在小尺寸下只剩几条细线时，卡片改成手绘图标的入口。
        /// </summary>
        public void ReplaceIcon(FrameworkElement icon)
        {
            if (icon == null || _titleGroup == null) return;
            if (_titleGroup.Children.Count > 1) _titleGroup.Children.RemoveAt(0);
            _titleGroup.Children.Insert(0, icon);
        }

        /// <summary>
        /// 标题文字后面再补一个元素（提示徽标一类），与标题同一行居中。
        /// 给“说明文字改成 tooltip”的入口用：徽标占位小，不挤标题行的右侧内容。
        /// </summary>
        public void AddTitleSuffix(FrameworkElement element)
        {
            if (element == null || _titleGroup == null) return;
            element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(6, 0, 0, 0);
            _titleGroup.Children.Add(element);
        }

        public string Title
        {
            get { return _title.Text; }
            set { _title.Text = value; }
        }

        public StackPanel Content { get { return _content; } }

        /// <summary>Optional content on the title row (right-aligned), e.g. compact IP info.</summary>
        public ContentControl HeaderRight { get { return _headerRight; } }
    }

    /// <summary>Vertical gauge: value bar plus label + big number (device status card).</summary>
    public class MetricGauge : StackPanel
    {
        private readonly Border _track;
        private readonly Border _fill;
        private readonly TextBlock _value;
        private readonly TextBlock _label;
        private readonly TextBlock _sub;
        private readonly Color _accent;

        public MetricGauge(string label, Color accent)
        {
            _accent = accent;
            Orientation = Orientation.Vertical;
            HorizontalAlignment = HorizontalAlignment.Center;
            Margin = new Thickness(10, 0, 10, 0);

            _track = new Border
            {
                Width = 34,
                Height = 96,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Theme.TrackGrey),
                VerticalAlignment = VerticalAlignment.Bottom,
                ClipToBounds = true
            };

            _fill = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(accent),
                Height = 0,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _track.Child = _fill;

            _value = new TextBlock
            {
                Text = "--",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };

            _label = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _sub = new TextBlock
            {
                Text = string.Empty,
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            Children.Add(_track);
            Children.Add(_value);
            Children.Add(_label);
            Children.Add(_sub);
        }

        public double MaxValue { get; set; }

        /// <summary>value in 0..1, NaN for "no data" (renders "--" and an empty bar).</summary>
        public void SetValue(double normalized, string text, string sub)
        {
            if (double.IsNaN(normalized))
            {
                _fill.Height = 0;
                _value.Text = "--";
                _value.Foreground = Theme.TextTertiaryBrush;
            }
            else
            {
                double clamped = Math.Max(0, Math.Min(1, normalized));
                _fill.Height = Math.Max(2, (_track.Height - 4) * clamped);
                _value.Text = text;
                _value.Foreground = Theme.TextPrimaryBrush;
            }
            _sub.Text = sub ?? string.Empty;
        }
    }

    /// <summary>
    /// Pill progress bar used by the overview tiles: a rounded grey
    /// track with an accent fill. Replaces the earlier 20-dash bar, which read as too fiddly.
    /// </summary>
    /// <summary>How a <see cref="MetricBar"/> paints its progress.</summary>
    public enum MetricBarStyle
    {
        Capsule = 0,
        Segments = 1
    }

    public class SidebarItem : Border
    {
        private readonly TextBlock _text;
        private bool _selected;

        public event Action<SidebarItem> Clicked;

        public SidebarItem(string title)
        {
            CornerRadius = new CornerRadius(12);
            // 固定行高 + 4 px 间距：SidebarItem 自身的尺寸就是侧栏滑块（NavGlider）的步长，
            // 改这里必须同步 SettingsWindow.NavStep。
            Height = 44;
            Padding = new Thickness(12, 10, 12, 10);
            Margin = new Thickness(0, 0, 0, 4);
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;

            _text = new TextBlock
            {
                Text = title,
                FontSize = 14.5,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Child = _text;

            MouseLeftButtonUp += (sender, e) =>
            {
                Action<SidebarItem> handler = Clicked;
                if (handler != null) handler(this);
            };
            MouseEnter += (sender, e) => { if (!_selected) Background = Theme.SurfaceHoverBrush; };
            MouseLeave += (sender, e) => { if (!_selected) Background = Brushes.Transparent; };
        }

        public bool Selected
        {
            get { return _selected; }
            set
            {
                _selected = value;
                // 选中面是列表背后那块会滑动的滑块；行自己必须保持透明，否则会盖住滑块。
                Background = Brushes.Transparent;
                // 滑块是品牌绿：白字对比度只有 ~2.4:1，用近黑（和主按钮一致）。
                _text.Foreground = value ? Theme.OnAccentBrush : Theme.TextSecondaryBrush;
                _text.FontWeight = value ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    /// <summary>Coloured key/value row used in the agent status card.</summary>
    public class KeyValueRow : Grid
    {
        private readonly TextBlock _value;

        public KeyValueRow(string key, string initialValue = "—")
        {
            Margin = new Thickness(0, 3, 0, 3);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock label = new TextBlock
            {
                Text = key,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush
            };
            SetColumn(label, 0);

            _value = new TextBlock
            {
                Text = initialValue,
                FontSize = 12,
                Foreground = Theme.TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            SetColumn(_value, 1);

            Children.Add(label);
            Children.Add(_value);
        }

        public string Value
        {
            get { return _value.Text; }
            set { _value.Text = value; }
        }

        public void SetValue(string text, Color? color = null)
        {
            _value.Text = text;
            _value.Foreground = color.HasValue ? new SolidColorBrush(color.Value) : Theme.TextPrimaryBrush;
        }
    }

    /// <summary>Flat button that follows the theme, including a "primary" variant.</summary>
    public class FlatButton : Button
    {
        /// <param name="spring">
        /// False for buttons that fill their container: the hover/press scale grows a full-width
        /// control past the card padding, so those get a background lift instead of a transform.
        /// </param>
        public FlatButton(string text, bool primary = false, bool spring = true)
        {
            Content = text;
            FontSize = 12.5;
            Padding = new Thickness(14, 7, 14, 7);
            BorderThickness = new Thickness(1);
            Cursor = Cursors.Hand;
            SnapsToDevicePixels = true;

            Background = primary ? Theme.AccentGreenBrush : Theme.ButtonGreyBrush;
            BorderBrush = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Foreground = primary ? Theme.OnAccentBrush : Theme.TextPrimaryBrush;

            // See BuildTemplate: focus is drawn as a green edge, not the default dotted rectangle.
            FocusVisualStyle = null;

            Template = BuildTemplate(primary);

            if (spring)
            {
                Interactions.AttachSpring(this);
            }
            else
            {
                SolidColorBrush idle = Background as SolidColorBrush;
                if (idle != null)
                {
                    SolidColorBrush hover = new SolidColorBrush(Lift(idle.Color));
                    hover.Freeze();
                    MouseEnter += (sender, e) => Background = hover;
                    MouseLeave += (sender, e) => Background = idle;
                }
            }
        }

        /// <summary>One step towards white: enough to read as feedback, not enough to look like another colour.</summary>
        private static Color Lift(Color color)
        {
            return Color.FromRgb(
                (byte)(color.R + (255 - color.R) * 0.14),
                (byte)(color.G + (255 - color.G) * 0.14),
                (byte)(color.B + (255 - color.B) * 0.14));
        }

        private static ControlTemplate BuildTemplate(bool primary)
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Theme.ControlRadius));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            // The default focus rectangle is a black dotted outline; on these dark surfaces it reads as
            // a stray black line. Focus is shown as a green edge instead.
            Trigger focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            Setter focusEdge = new Setter(Border.BorderBrushProperty, Theme.AccentGreenBrush);
            focusEdge.TargetName = "border";
            focus.Setters.Add(focusEdge);
            template.Triggers.Add(focus);

            return template;
        }
    }

    /// <summary>
    /// 页头：每个页面第一件东西，主标题 + 一行副标题，卡片整体排在它下面。
    /// 导航已经标了“你在哪”，这里给出的是这一页在做什么（一句话，手册体）。
    /// </summary>
    public class PageHeading : StackPanel
    {
        public PageHeading(string title, string subtitle)
        {
            Orientation = Orientation.Vertical;
            // 上边距 2：标题字形的墨迹上边缘要和侧栏第一个导航项的顶边落在同一条线上
            // （字形在行框里天然低约 4 px，这里补 2 让它看起来齐平）。
            Margin = new Thickness(0, 2, 0, 16);

            TextBlock heading = new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(heading, "PageTitle");
            Children.Add(heading);

            if (!string.IsNullOrEmpty(subtitle))
            {
                TextBlock caption = new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12,
                    Foreground = Theme.TextSecondaryBrush,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                AutomationProperties.SetAutomationId(caption, "PageSubtitle");
                Children.Add(caption);
            }
        }
    }
}
