using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace QuadVitals.UI
{
    /// <summary>
    /// Dark replacements for the two controls the dashboard needs. The WPF defaults (Aero2) are
    /// light and look broken inside a dark card, and there is no XAML compiler on this box, so the
    /// templates are built in code.
    /// </summary>
    public class DarkTextBox : TextBox
    {
        /// <param name="focusEdge">
        /// False drops the green edge this box paints while focused. The feedback form uses it: the box
        /// there is a large writing area, and a full-frame accent outline reads as a selection, not as
        /// "the caret is here" - the caret already says that.
        /// </param>
        public DarkTextBox(bool focusEdge = true)
        {
            Background = Theme.SurfaceHoverBrush;
            Foreground = Theme.TextPrimaryBrush;
            BorderBrush = Theme.BorderSubtleBrush;
            BorderThickness = new Thickness(1);
            // 多行输入框出现竖向滚动条时，ScrollViewer 模板右下角的小方块取
            // SystemColors.ControlBrushKey；窗口把它重涂成 #1A1A1A，在 #3A3A3A 的字段底上就是一块黑。
            // 只在这个控件内部覆盖成字段自己的底色，不影响别的控件。
            Resources[SystemColors.ControlBrushKey] = Background;
            CaretBrush = Theme.TextPrimaryBrush;
            SelectionBrush = new SolidColorBrush(Color.FromArgb(0x88, 0x2B, 0xD6, 0x7B));
            SelectionOpacity = 0.6;
            FontSize = 12.5;
            // 垂直内边距 2（不是 4）：28 px 高的框扣掉 2 px 边框后只剩 24，给 12.5 px 的
            // 中文字形留够 20 px，否则圆角模板里上下会被裁掉一截。
            Padding = new Thickness(8, 2, 8, 2);
            VerticalContentAlignment = VerticalAlignment.Center;
            // 与 DarkComboBox / FlatButton 同高（30），否则同一列里输入框比下拉矮一截。
            Height = 30;

            // No black dotted focus rectangle: the template can turn the edge green on focus instead.
            FocusVisualStyle = null;
            _focusEdge = focusEdge;

            // 圆角要靠自建模板：TextBox 没有 CornerRadius，用系统模板时永远是直角，
            // 和圆角的下拉/按钮并排就显得不齐。
            // 内边距交给外层 Border、让 PART_ContentHost 自己撑满：放到 ScrollViewer 的 Margin
            // 上会把文字挤到只剩上半截。
            Template = BuildTemplate(focusEdge);
        }

        private readonly bool _focusEdge;

        /// <summary>True when the template paints the accent edge while focused.</summary>
        public bool HasFocusEdge { get { return _focusEdge; } }

        private static ControlTemplate BuildTemplate(bool focusEdge)
        {
            ControlTemplate template = new ControlTemplate(typeof(TextBox));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Theme.ControlRadius));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);
            // 内边距必须由 Border 承担：放在 ScrollViewer 的 Margin 上会把文字挤到只剩上半截
            // （round 1 就是栽在这里）。注意 TextBox 自己也会把 Padding 应用到内容宿主，
            // 两者叠加后 1 px 内边距等于 2 px 缩进（数字行的取值按这个叠加关系算）。
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            FrameworkElementFactory host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            // Keep the host stretched: binding it to VerticalContentAlignment shrinks it to the
            // measured text height and the glyph tops get clipped in this 28 px box.
            host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);

            border.AppendChild(host);
            template.VisualTree = border;

            if (focusEdge)
            {
                Trigger focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
                Setter focusSetter = new Setter(Border.BorderBrushProperty, Theme.AccentGreenBrush);
                focusSetter.TargetName = "border";
                focus.Setters.Add(focusSetter);
                template.Triggers.Add(focus);
            }

            Trigger mouse = new Trigger { Property = IsMouseOverProperty, Value = true };
            Setter mouseEdge = new Setter(Border.BorderBrushProperty, Theme.BorderStrongBrush);
            mouseEdge.TargetName = "border";
            mouse.Setters.Add(mouseEdge);
            template.Triggers.Add(mouse);

            return template;
        }
    }

    public class DarkComboBox : ComboBox
    {
        // 材质分层的几何：远层阴影摊开的宽度、近层收紧的宽度、以及弹层与字段的间距。
        // 三个数值同时决定容器内边距和 Popup 的水平偏移，改一个必须同步另一个。
        private const double ShadowSpread = 8;
        private const double NearSpread = 6;
        private const double FieldGap = 4;

        public DarkComboBox()
        {
            Background = Theme.SurfaceHoverBrush;
            Foreground = Theme.TextPrimaryBrush;
            BorderBrush = Theme.BorderSubtleBrush;            FontSize = 12.5;
            Height = 30;
            Padding = new Thickness(10, 0, 8, 0);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;

            // Same reason as DarkCheckBox: the default dotted focus rectangle shows up as a black
            // dashed line on a dark field.
            FocusVisualStyle = null;

            ItemContainerStyle = BuildItemStyle();
            Template = BuildTemplate();
        }

        /// <summary>
        /// 下拉展开的入场动效：列表内容从顶部往下展开（ScaleY 0.94 → 1，BackEase 过冲）
        /// 同时淡入。用 RenderTransform 做，不改布局尺寸，Popup 窗口大小不变，不会因为重排而抖。
        /// </summary>
        protected override void OnDropDownOpened(EventArgs e)
        {
            base.OnDropDownOpened(e);

            Popup popup = GetTemplateChild("PART_Popup") as Popup;
            FrameworkElement list = popup == null ? null : popup.Child as FrameworkElement;
            if (list == null) return;

            ScaleTransform scale = new ScaleTransform(1, 0.94);
            list.RenderTransformOrigin = new Point(0.5, 0);   // 从列表顶部展开
            list.RenderTransform = scale;
            list.Opacity = 0;

            // 曲线在 Theme.EnterPop：慢起 → 55% 处略过 1 → 落回（“微微晃动”）。
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Theme.EnterPop(1.012));
            list.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(Theme.MotionFadeMs))
            {
                EasingFunction = Theme.EnterFade()
            });
        }

        private static Style BuildItemStyle()
        {
            Style style = new Style(typeof(ComboBoxItem));
            ControlTemplate template = new ControlTemplate(typeof(ComboBoxItem));

            // 行背景铺满弹层内宽（不留左右外边），行内边距 10 与字段文字同一条竖线。
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "itemBorder";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.PaddingProperty, new Thickness(0));
            border.SetValue(Border.MarginProperty, new Thickness(0, 1, 0, 1));

            FrameworkElementFactory row = new FrameworkElementFactory(typeof(Grid));

            // 选中指示：2 px 竖条紧贴左边缘，上下各留 8 px（避开行圆角弧线）。
            FrameworkElementFactory accent = new FrameworkElementFactory(typeof(Border));
            accent.Name = "accent";
            accent.SetValue(FrameworkElement.WidthProperty, 2.0);
            accent.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 8));
            accent.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            accent.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
            accent.SetValue(Border.BackgroundProperty, Theme.AccentGreenBrush);
            accent.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 8, 10, 8));

            row.AppendChild(accent);
            row.AppendChild(presenter);
            border.AppendChild(row);
            template.VisualTree = border;

            // hover 比选中弱：白 6% vs 绿 10%。
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            Setter hoverBackground = new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
            hoverBackground.TargetName = "itemBorder";
            hover.Setters.Add(hoverBackground);
            template.Triggers.Add(hover);

            Trigger selected = new Trigger { Property = IsSelectedProperty, Value = true };
            Setter selectedBackground = new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x26, 0x2B, 0xD6, 0x7B)));
            selectedBackground.TargetName = "itemBorder";
            selected.Setters.Add(selectedBackground);
            Setter selectedAccent = new Setter(UIElement.VisibilityProperty, Visibility.Visible);
            selectedAccent.TargetName = "accent";
            selected.Setters.Add(selectedAccent);
            template.Triggers.Add(selected);

            style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextPrimaryBrush));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            // 系统默认的焦点虚线框会叠在选中行上（看着像一块半成品），下拉项一律不用它。
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private ControlTemplate BuildTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ComboBox));

            FrameworkElementFactory root = new FrameworkElementFactory(typeof(Grid));

            // Closed state: border + current selection + arrow
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));

            FrameworkElementFactory inner = new FrameworkElementFactory(typeof(Grid));
            inner.SetValue(FrameworkElement.MarginProperty, new Thickness(0));

            FrameworkElementFactory selection = new FrameworkElementFactory(typeof(ContentPresenter));
            selection.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(PaddingProperty));
            selection.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            selection.SetValue(ContentPresenter.ContentProperty,
                new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            selection.SetValue(ContentPresenter.ContentTemplateProperty,
                new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
            selection.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(ForegroundProperty));

            // 两个箭头互斥显示（不靠旋转：旋转会让 12 px 的盒子在收/展之间错位）：
            //   收起 ›（里面还有东西） / 展开 ⌄（已经摊开）
            inner.AppendChild(selection);

            FrameworkElementFactory arrowRight = ChevronFactory("arrowRight",
                "M 4.5 3 L 8.5 6 L 4.5 9");
            FrameworkElementFactory arrowDown = ChevronFactory("arrowDown",
                "M 3 4.5 L 6 8.5 L 9 4.5");
            arrowDown.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

            inner.AppendChild(arrowRight);
            inner.AppendChild(arrowDown);
            border.AppendChild(inner);
            root.AppendChild(border);

            // WPF 的 ComboBox 靠模板里的 ToggleButton 把 IsChecked 双向绑到 IsDropDownOpen，
            // 没有它鼠标点下拉根本打不开（只能靠键盘）。放一个透明铺满的 ToggleButton 盖在
            // border 上面，不改变外观，只负责点击开合。
            FrameworkElementFactory toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.Name = "toggleButton";
            toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
            toggle.SetValue(UIElement.FocusableProperty, false);
            toggle.SetValue(Control.IsTabStopProperty, false);
            toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            toggle.SetValue(Control.TemplateProperty, BuildToggleTemplate());
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                Mode = BindingMode.TwoWay
            });
            root.AppendChild(toggle);

            // Popup with the item list
            FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            // 外面两层阴影容器的内边距会把卡片往右下推，用偏移抵回去，
            // 否则弹层不跟字段左对齐（就是“弹窗错位”）。
            popup.SetValue(Popup.HorizontalOffsetProperty, -(ShadowSpread + NearSpread));
            popup.SetValue(Popup.VerticalOffsetProperty, 0.0);
            // 内建的 Slide 是整窗生硬位移，不能扣缓动；展开动画在 DarkComboBox.OnDropDownOpened
            // 里自己对列表内容做（缩放 + 淡入，带轻微过冲）。
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.None);
            popup.SetValue(Popup.IsOpenProperty,
                new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));

            FrameworkElementFactory popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(Border.BackgroundProperty, Theme.SurfaceRaisedBrush);
            popupBorder.SetValue(Border.BorderBrushProperty, Theme.BorderStrongBrush);
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            // 卡片本身仍与字段同宽；外面两层只负责给投影留出绘制空间（弹层窗口按控件尺寸裁切）。
            popupBorder.SetValue(FrameworkElement.MinWidthProperty,
                new TemplateBindingExtension(FrameworkElement.ActualWidthProperty));

            FrameworkElementFactory scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.MaxHeightProperty, 260.0);
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroll.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 6));

            FrameworkElementFactory items = new FrameworkElementFactory(typeof(StackPanel));
            items.SetValue(StackPanel.IsItemsHostProperty, true);
            scroll.AppendChild(items);

            // 材质第一层：顶部 1 px 受光边（深色浮层靠这道高光读出“有厚度”，而不是靠投影）。
            FrameworkElementFactory highlight = new FrameworkElementFactory(typeof(Border));
            highlight.SetValue(FrameworkElement.HeightProperty, 1.0);
            highlight.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            highlight.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 9, 0));
            highlight.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
            highlight.SetValue(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));

            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Grid));
            surface.AppendChild(scroll);
            surface.AppendChild(highlight);
            popupBorder.AppendChild(surface);

            // 材质第二层：两道阴影——近层收紧边缘、远层摊开体量。单层投影只会显得“脏边”。
            FrameworkElementFactory nearHost = new FrameworkElementFactory(typeof(Border));
            nearHost.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            nearHost.SetValue(Border.PaddingProperty,
                new Thickness(NearSpread, FieldGap, NearSpread, NearSpread));
            nearHost.SetValue(UIElement.EffectProperty, new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 6,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.35
            });
            nearHost.AppendChild(popupBorder);

            FrameworkElementFactory farHost = new FrameworkElementFactory(typeof(Border));
            farHost.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            farHost.SetValue(Border.PaddingProperty,
                new Thickness(ShadowSpread, 0, ShadowSpread, ShadowSpread + NearSpread));
            farHost.SetValue(UIElement.EffectProperty, new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 26,
                ShadowDepth = 8,
                Direction = 270,
                Opacity = 0.28
            });
            farHost.AppendChild(nearHost);

            popup.AppendChild(farHost);
            root.AppendChild(popup);

            template.VisualTree = root;

            // hover / open 两个描边状态：open 必须写在 hover 后，否则展开时把鼠标停在字段上
            // 会把绿描边盖回灰色。
            Trigger hoverField = new Trigger { Property = IsMouseOverProperty, Value = true };
            Setter hoverStroke = new Setter(Border.BorderBrushProperty, Theme.BorderStrongBrush);
            hoverStroke.TargetName = "border";
            hoverField.Setters.Add(hoverStroke);
            template.Triggers.Add(hoverField);

            Trigger open = new Trigger { Property = ComboBox.IsDropDownOpenProperty, Value = true };
            // 展开态：右箭头换成下箭头并提亮 —— 箭头的形状说明列表当前朝哪边摊开。
            Setter hideRight = new Setter(UIElement.VisibilityProperty, Visibility.Collapsed);
            hideRight.TargetName = "arrowRight";
            open.Setters.Add(hideRight);
            Setter showDown = new Setter(UIElement.VisibilityProperty, Visibility.Visible);
            showDown.TargetName = "arrowDown";
            open.Setters.Add(showDown);
            Setter rightLit = new Setter(Shape.StrokeProperty, Theme.TextPrimaryBrush);
            rightLit.TargetName = "arrowRight";
            open.Setters.Add(rightLit);
            Setter downLit = new Setter(Shape.StrokeProperty, Theme.TextPrimaryBrush);
            downLit.TargetName = "arrowDown";
            open.Setters.Add(downLit);
            template.Triggers.Add(open);

            return template;
        }

        /// <summary>
        /// One chevron inside a fixed 12 x 12 box so the two states occupy exactly the same space
        /// (a rotated path drifts a couple of pixels between collapsed and expanded).
        /// </summary>
        private static FrameworkElementFactory ChevronFactory(string name, string data)
        {
            FrameworkElementFactory chevron = new FrameworkElementFactory(typeof(Path));
            chevron.Name = name;
            chevron.SetValue(Path.DataProperty, Geometry.Parse(data));
            chevron.SetValue(Shape.StrokeProperty, Theme.TextSecondaryBrush);
            chevron.SetValue(Shape.StrokeThicknessProperty, 1.5);
            chevron.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            chevron.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            chevron.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
            chevron.SetValue(Shape.StretchProperty, Stretch.None);
            chevron.SetValue(FrameworkElement.WidthProperty, 12.0);
            chevron.SetValue(FrameworkElement.HeightProperty, 12.0);
            chevron.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            chevron.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            chevron.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            return chevron;
        }

        /// <summary>Click surface for the ComboBox toggle: transparent, no chrome.</summary>
        private static ControlTemplate BuildToggleTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ToggleButton));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            surface.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            template.VisualTree = surface;
            return template;
        }
    }

    /// <summary>Dark ListBoxItem style: transparent, accent tint when selected, subtle on hover.</summary>
    public static class DarkListStyles
    {
        public static Style ListBoxItem()
        {
            return ListBoxItem(new Thickness(8, 6, 8, 6), new Thickness(0, 1, 0, 1), 8);
        }

        /// <summary>
        /// 同一个 styles，可调内边距/外边距/圆角 —— 显示项页的元素方块需要在 202 px 高的卡里塞下
        /// “标题 + 一行方块 + 按钮行”，默认的 8/6 内边距会把方块顶出去。
        /// </summary>
        public static Style ListBoxItem(Thickness padding, Thickness margin, double cornerRadius)
        {
            Style style = new Style(typeof(ListBoxItem));
            ControlTemplate template = new ControlTemplate(typeof(ListBoxItem));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "row";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.PaddingProperty, padding);
            border.SetValue(Border.MarginProperty, margin);

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(presenter);
            template.VisualTree = border;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            Setter hoverBackground = new Setter(Border.BackgroundProperty, Theme.SurfaceHoverBrush);
            hoverBackground.TargetName = "row";
            hover.Setters.Add(hoverBackground);
            template.Triggers.Add(hover);

            Trigger selected = new Trigger { Property = System.Windows.Controls.Primitives.Selector.IsSelectedProperty, Value = true };
            Setter selectedBackground = new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x30, 0x2B, 0xD6, 0x7B)));
            selectedBackground.TargetName = "row";
            selected.Setters.Add(selectedBackground);
            template.Triggers.Add(selected);

            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextPrimaryBrush));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }
    }

    /// <summary>
    /// Capsule on/off switch. Same size regardless of the label, so
    /// rows stay aligned; the knob slides with the shared motion curve.
    ///
    /// Implemented on CheckBox so keyboard (Space) and accessibility come for free.
    /// </summary>
    public class ToggleSwitch : CheckBox
    {
        private const double TrackWidth = 40;
        private const double TrackHeight = 22;
        private const double KnobSize = 16;

        public ToggleSwitch()
        {
            Foreground = Theme.TextPrimaryBrush;
            FontSize = 12.5;
            VerticalContentAlignment = VerticalAlignment.Center;

            // No black dotted focus rectangle on dark surfaces (see DarkCheckBox).
            FocusVisualStyle = null;

            ControlTemplate template = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            FrameworkElementFactory track = new FrameworkElementFactory(typeof(Border));
            track.Name = "track";
            track.SetValue(Border.WidthProperty, TrackWidth);
            track.SetValue(Border.HeightProperty, TrackHeight);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(TrackHeight / 2));
            track.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)));
            track.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 9, 0));
            track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            FrameworkElementFactory knob = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            knob.Name = "knob";
            knob.SetValue(FrameworkElement.WidthProperty, KnobSize);
            knob.SetValue(FrameworkElement.HeightProperty, KnobSize);
            knob.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.White);
            knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            knob.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            knob.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
            FrameworkElementFactory knobHost = new FrameworkElementFactory(typeof(Grid));
            knobHost.AppendChild(knob);
            track.AppendChild(knobHost);

            FrameworkElementFactory label = new FrameworkElementFactory(typeof(ContentPresenter));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(ForegroundProperty));

            root.AppendChild(track);
            root.AppendChild(label);
            template.VisualTree = root;

            // off -> hover brightens the track a little (no colour change on the knob)
            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            Setter hoverTrack = new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)));
            hoverTrack.TargetName = "track";
            hover.Setters.Add(hoverTrack);
            template.Triggers.Add(hover);

            Trigger on = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            Setter onTrack = new Setter(Border.BackgroundProperty, Theme.AccentGreenFillBrush);
            onTrack.TargetName = "track";
            on.Setters.Add(onTrack);
            template.Triggers.Add(on);

            // 位置不用 Setter（Setter 是瞬间跳）：改成 OnApplyTemplate 里挂 TranslateTransform，
            // 由 Checked/Unchecked 用带弹性的缓动去推。
            Checked += (sender, e) => AnimateKnob(true);
            Unchecked += (sender, e) => AnimateKnob(false);

            Template = template;
        }

        private TranslateTransform _knobShift;

        private static double KnobOffset(bool on)
        {
            // 轨道 40 - 圆点 16 - 两头各 3 px 内缩
            return on ? TrackWidth - KnobSize - 6 : 0;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            System.Windows.Shapes.Ellipse knob = GetTemplateChild("knob") as System.Windows.Shapes.Ellipse;
            if (knob == null) return;

            _knobShift = new TranslateTransform(KnobOffset(IsChecked == true), 0);
            knob.RenderTransform = _knobShift;
        }

        private void AnimateKnob(bool on)
        {
            if (_knobShift == null) return;   // 模板还没应用：OnApplyTemplate 会直接摆好位置

            DoubleAnimation slide = new DoubleAnimation(KnobOffset(on),
                TimeSpan.FromMilliseconds(Theme.MotionPopMs))
            {
                EasingFunction = Theme.ToggleSpring()
            };
            _knobShift.BeginAnimation(TranslateTransform.XProperty, slide);
        }
    }

    /// <summary>
    /// Dark check box. 塑料感的来源是「绿描边 + 绿勾 + 深灰底」这种空心发光框：
    /// 勾选状态改成实心填充 + 白色对勾（印上去的），未选状态用中性描边，不用品牌绿。
    /// </summary>
    public class DarkCheckBox : CheckBox
    {
        public DarkCheckBox()
        {
            Foreground = Theme.TextPrimaryBrush;
            FontSize = 12.5;
            VerticalContentAlignment = VerticalAlignment.Center;

            // WPF's default focus rectangle is a black dotted outline drawn on top of the control -
            // on this dark surface it reads as a stray black line (seen on the installer's first
            // checkbox and anywhere a check box takes keyboard focus). The template below shows focus
            // by turning the box edge green instead, so the default visual is turned off.
            FocusVisualStyle = null;

            ControlTemplate template = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            FrameworkElementFactory box = new FrameworkElementFactory(typeof(Border));
            box.Name = "box";
            box.SetValue(Border.WidthProperty, 18.0);
            box.SetValue(Border.HeightProperty, 18.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetValue(Border.BorderBrushProperty, Theme.BorderStrongBrush);
            box.SetValue(Border.BackgroundProperty, Theme.SurfaceHoverBrush);
            box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 9, 0));
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            FrameworkElementFactory tick = new FrameworkElementFactory(typeof(Path));
            tick.Name = "tick";
            // 16 px 盒子里居中、略微收紧的对勾，圆头笔画；填充状态用白色。
            // 勾收小并略偏左下：16 px 盒子里撑满会显得挤、发塑料。
            tick.SetValue(Path.DataProperty, Geometry.Parse("M 5.3 9.4 L 7.9 12 L 12.9 6.6"));
            tick.SetValue(Shape.StrokeProperty, Brushes.White);
            tick.SetValue(Shape.StrokeThicknessProperty, 1.9);
            tick.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            tick.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            tick.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
            tick.SetValue(Shape.StretchProperty, Stretch.None);
            tick.SetValue(FrameworkElement.WidthProperty, 18.0);
            tick.SetValue(FrameworkElement.HeightProperty, 18.0);
            tick.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            tick.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            tick.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.AppendChild(tick);

            FrameworkElementFactory label = new FrameworkElementFactory(typeof(ContentPresenter));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(ForegroundProperty));

            root.AppendChild(box);
            root.AppendChild(label);
            template.VisualTree = root;

            // 悬停：未选中只提亮描边，不铺底。
            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            Setter hoverBorder = new Setter(Border.BorderBrushProperty, Theme.TextSecondaryBrush);
            hoverBorder.TargetName = "box";
            hover.Setters.Add(hoverBorder);
            template.Triggers.Add(hover);

            Trigger checkedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            Setter tickVisible = new Setter(UIElement.VisibilityProperty, Visibility.Visible);
            tickVisible.TargetName = "tick";
            checkedTrigger.Setters.Add(tickVisible);
            Setter boxFill = new Setter(Border.BackgroundProperty, Theme.AccentGreenFillBrush);
            boxFill.TargetName = "box";
            checkedTrigger.Setters.Add(boxFill);
            // 1 px 深绿边把边缘“收”住，满饱和填充配无描边会显得发光。
            Setter boxEdge = new Setter(Border.BorderBrushProperty, Theme.AccentGreenEdgeBrush);
            boxEdge.TargetName = "box";
            checkedTrigger.Setters.Add(boxEdge);
            template.Triggers.Add(checkedTrigger);

            // Keyboard focus: green edge on the box itself, no dotted rectangle.
            Trigger focusTrigger = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            Setter focusEdge = new Setter(Border.BorderBrushProperty, Theme.AccentGreenBrush);
            focusEdge.TargetName = "box";
            focusTrigger.Setters.Add(focusEdge);
            template.Triggers.Add(focusTrigger);

            Template = template;
        }
    }

/// <summary>
/// 深色 ToolTip：默认模板是浅色的，在深色卡片上很扎眼（图表悬停提示尤其明显）。
/// </summary>
public static class DarkToolTips
{
    public static Style Create()
    {
        Style style = new Style(typeof(ToolTip));
        ControlTemplate template = new ControlTemplate(typeof(ToolTip));

        FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Theme.SurfaceCardBrush);
        border.SetValue(Border.BorderBrushProperty, Theme.BorderSubtleBrush);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(9, 5, 9, 5));

        FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);

        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextPrimaryBrush));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.5));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
}
