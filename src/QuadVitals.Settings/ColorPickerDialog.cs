using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 取色器：色相条 + 饱和度/明度方块 + 十六进制输入 + 预设色板 + 对比度提示。
    ///
    /// 为什么不用系统 `ChooseColor`：那是 Win32 浅色对话框，和这套深色壳子放在一起像是从别的
    /// 程序里弹出来的；而且它只有 16 个固定色 + 自定义区，配不出任务栏条需要的高饱和颜色。
    ///
    /// 颜色一律以 `#AARRGGBB` 存取（settings.json 的写法），面板上不提供透明度编辑：任务栏
    /// 元素不透明才可读，alpha 只在读旧配置时保留原值。
    /// </summary>
    internal sealed class ColorPickerDialog
    {
        public const double DialogWidth = 548;
        public const double DialogHeight = 356;
        private const double SquareSize = 208;
        private const double StripWidth = 14;
        private const double ThumbSize = 12;

        /// <summary>对比度低于这个值就提示「可能看不清」（图形/大字阈值 3:1）。</summary>
        private const double ContrastWarning = 3.0;

        private readonly SettingsWindow _window;
        private readonly string _label;
        private readonly string _role;
        private readonly string _defaultHex;
        private readonly Action<string> _apply;

        private double _hue;         // 0..360
        private double _saturation;  // 0..1
        private double _value;       // 0..1
        private byte _alpha = 255;

        private Rectangle _squareBase;
        private Canvas _squareLayer;
        private Border _squareThumb;
        private Canvas _hueLayer;
        private Border _hueThumb;
        private Border _preview;
        private Color _initialColor;
        private PaletteChips _palette;
        private TextBox _hexBox;
        private TextBlock _contrast;
        private bool _syncingHex;

        /// <summary>
        /// True while the hex box holds文字 the user typed. Only then may the box keep its own
        /// text; any状态变化 from the square/hue strip/presets rewrites it with the real colour.
        /// </summary>
        private bool _hexDirty;

        public ColorPickerDialog(SettingsWindow window, string label, string role, string initialHex,
                                 string defaultHex, Action<string> apply)
        {
            _window = window;
            _label = label;
            _role = ColorRoles.Canonical(role);
            _defaultHex = string.IsNullOrEmpty(defaultHex) ? AppSettings.DefaultGreen : defaultHex;
            _apply = apply;

            Color parsed = Theme.ToColor(initialHex, Theme.AccentGreen);
            _initialColor = parsed;
            _alpha = parsed.A;
            double hue, saturation, value;
            ToHsv(parsed, out hue, out saturation, out value);
            _hue = hue;
            _saturation = saturation;
            _value = value;
        }

        public FrameworkElement Build()
        {
            Border panel = new Border
            {
                Width = DialogWidth,
                Height = DialogHeight,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true
            };

            // 标题贴顶 / 中间内容 / 按钮贴底，与网络设置弹窗同一套版式。
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "取色 · " + _label,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(title, "ColorPickerTitle");
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            Grid body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SquareSize) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StripWidth) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FrameworkElement square = BuildSquare();
            // 三块都以「左边缘色块的顶边」为基线对齐：固定高度的控件（方块 208、色相条 208）
            // 在 Star 行里默认会垂直居中，顶部就会比右列低一截。
            square.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(square, 0);
            body.Children.Add(square);

            FrameworkElement strip = BuildHueStrip();
            strip.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(strip, 2);
            body.Children.Add(strip);

            FrameworkElement side = BuildSide();
            side.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(side, 4);
            body.Children.Add(side);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            FrameworkElement buttons = BuildButtons();
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            panel.Child = root;
            UpdateAll();
            return panel;
        }

        // ---- 饱和度 / 明度方块 -------------------------------------------------------

        private FrameworkElement BuildSquare()
        {
            Grid host = new Grid
            {
                Width = SquareSize,
                Height = SquareSize,
                ClipToBounds = true
            };

            _squareBase = new Rectangle { Width = SquareSize, Height = SquareSize };

            Rectangle white = new Rectangle
            {
                Width = SquareSize,
                Height = SquareSize,
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(255, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255), 0)
            };

            Rectangle black = new Rectangle
            {
                Width = SquareSize,
                Height = SquareSize,
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb(255, 0, 0, 0), 90)
            };

            host.Children.Add(_squareBase);
            host.Children.Add(white);
            host.Children.Add(black);

            _squareLayer = new Canvas { Width = SquareSize, Height = SquareSize, Background = Brushes.Transparent };
            _squareThumb = MakeThumb();
            _squareLayer.Children.Add(_squareThumb);
            host.Children.Add(_squareLayer);

            // 左边渐变区用直角：Border 会把子元素裁到圆角，
            // 所以描边框和可点击容器两边都必须是 0，只改其中一个角上仍会是圆的。
            Border frame = new Border
            {
                CornerRadius = new CornerRadius(0),
                BorderBrush = Theme.BorderSubtleBrush,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            host.Children.Add(frame);

            Border clickable = new Border
            {
                Width = SquareSize,
                Height = SquareSize,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(0),
                Child = host
            };
            AutomationProperties.SetAutomationId(clickable, "ColorPickerSquare");
            AutomationProperties.SetName(clickable, "饱和度与明度");

            clickable.MouseLeftButtonDown += (sender, e) =>
            {
                clickable.CaptureMouse();
                SetFromSquare(e.GetPosition(host));
                e.Handled = true;
            };
            clickable.MouseMove += (sender, e) =>
            {
                if (clickable.IsMouseCaptured) SetFromSquare(e.GetPosition(host));
            };
            clickable.MouseLeftButtonUp += (sender, e) =>
            {
                if (clickable.IsMouseCaptured) clickable.ReleaseMouseCapture();
                e.Handled = true;
            };
            return clickable;
        }

        private void SetFromSquare(Point point)
        {
            double x = point.X / SquareSize;
            double y = point.Y / SquareSize;
            _saturation = Clamp(x, 0, 1);
            _value = Clamp(1 - y, 0, 1);
            // The square is now the source of truth: the hex box must show this colour again.
            _hexDirty = false;
            UpdateAll();
        }

        // ---- 色相条 ------------------------------------------------------------------

        private FrameworkElement BuildHueStrip()
        {
            _hueLayer = new Canvas { Width = StripWidth, Height = SquareSize, Background = Brushes.Transparent };

            LinearGradientBrush rainbow = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            rainbow.GradientStops.Add(new GradientStop(HsvColor(0, 1, 1), 0.0));
            rainbow.GradientStops.Add(new GradientStop(HsvColor(60, 1, 1), 1.0 / 6));
            rainbow.GradientStops.Add(new GradientStop(HsvColor(120, 1, 1), 2.0 / 6));
            rainbow.GradientStops.Add(new GradientStop(HsvColor(180, 1, 1), 3.0 / 6));
            rainbow.GradientStops.Add(new GradientStop(HsvColor(240, 1, 1), 4.0 / 6));
            rainbow.GradientStops.Add(new GradientStop(HsvColor(300, 1, 1), 5.0 / 6));
            rainbow.GradientStops.Add(new GradientStop(HsvColor(360, 1, 1), 1.0));

            Rectangle bar = new Rectangle
            {
                Width = StripWidth,
                Height = SquareSize,
                RadiusX = 0,
                RadiusY = 0,
                Fill = rainbow,
                IsHitTestVisible = false
            };
            _hueLayer.Children.Add(bar);

            _hueThumb = MakeThumb();
            _hueLayer.Children.Add(_hueThumb);

            Border clickable = new Border
            {
                Width = StripWidth,
                Height = SquareSize,
                Background = Brushes.Transparent,
                Child = _hueLayer
            };
            AutomationProperties.SetAutomationId(clickable, "ColorPickerHue");
            AutomationProperties.SetName(clickable, "色相");

            clickable.MouseLeftButtonDown += (sender, e) =>
            {
                clickable.CaptureMouse();
                SetFromHue(e.GetPosition(_hueLayer));
                e.Handled = true;
            };
            clickable.MouseMove += (sender, e) =>
            {
                if (clickable.IsMouseCaptured) SetFromHue(e.GetPosition(_hueLayer));
            };
            clickable.MouseLeftButtonUp += (sender, e) =>
            {
                if (clickable.IsMouseCaptured) clickable.ReleaseMouseCapture();
                e.Handled = true;
            };
            return clickable;
        }

        private void SetFromHue(Point point)
        {
            _hue = Clamp(point.Y / SquareSize, 0, 0.9999) * 360.0;
            _hexDirty = false;
            UpdateAll();
        }

        private static Border MakeThumb()
        {
            return new Border
            {
                Width = ThumbSize,
                Height = ThumbSize,
                CornerRadius = new CornerRadius(ThumbSize / 2),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 3,
                    ShadowDepth = 0,
                    Opacity = 0.55
                }
            };
        }

        // ---- 右侧：预览 / 十六进制 / 预设 / 对比度 ------------------------------------

        private FrameworkElement BuildSide()
        {
            StackPanel side = new StackPanel();

            Grid previewRow = new Grid();
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 预览是**一个色块**，但左右区分：左＝进面板时的原颜色、右＝当前颜色。
            // 两半紧贴、共用外轮廓（只有外侧倒角），不要两小块拼起来的感觉，
            // 中间仅一条 1 px 缝隙（用卡片底色切一刀）。（`Border` 不会把子元素裁到圆角，
            // 所以圆角必须画在两个半边上，不能靠外层容器裁剪。）
            Grid previews = new Grid { Width = 52, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            Border original = new Border
            {
                CornerRadius = new CornerRadius(8, 0, 0, 8),
                Background = new SolidColorBrush(_initialColor),
                ToolTip = "左：原颜色　右：当前颜色"
            };
            AutomationProperties.SetAutomationId(original, "ColorPickerOriginal");
            AutomationProperties.SetName(original, "原颜色（左半）");
            Grid.SetColumn(original, 0);
            previews.Children.Add(original);

            _preview = new Border
            {
                CornerRadius = new CornerRadius(0, 8, 8, 0),
                Background = new SolidColorBrush(CurrentColor()),
                ToolTip = "左：原颜色　右：当前颜色"
            };
            AutomationProperties.SetAutomationId(_preview, "ColorPickerCurrent");
            AutomationProperties.SetName(_preview, "当前颜色（右半）");
            Grid.SetColumn(_preview, 1);
            previews.Children.Add(_preview);

            // 不加任何分隔线与空隙，两半直接相邻，
            // 左右只靠颜色本身区分（tooltip 里标明左=原色、右=当前色）。

            Grid.SetColumn(previews, 0);
            previewRow.Children.Add(previews);

            _hexBox = new DarkTextBox
            {
                Text = Theme.ToHex(CurrentColor()),
                FontFamily = Theme.MonoFont,
                FontSize = 12,
                Height = 30,
                Padding = new Thickness(8, 3, 8, 3)
            };
            AutomationProperties.SetAutomationId(_hexBox, "ColorPickerHex");
            AutomationProperties.SetName(_hexBox, "十六进制颜色");
            // 取色器里是「边输入边预览」：输完一个合法的十六进制就立即换色，不等失焦。
            //
            // `_hexDirty` 是关键：输入时不能把框里的文字覆盖掉，但拖方块/点预设时
            // 又必须把文字改回真实颜色。旧实现用的是「有焦点就不回写」，于是拖完颜色后框里还是
            // 旧值，点确定时失焦又把旧值写回去——看起来就是“选完颜色确定不生效”。
            _hexBox.TextChanged += (sender, e) =>
            {
                if (_syncingHex) return;
                string typed = (_hexBox.Text ?? string.Empty).Trim();
                string canonical;
                if (!ColorText.TryParse(typed, out canonical))
                {
                    _hexDirty = true;
                    return;
                }
                ApplyHex(canonical, true);
            };
            _hexBox.LostFocus += (sender, e) => NormalizeHexText();
            _hexBox.KeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter) { NormalizeHexText(); e.Handled = true; }
            };
            Grid.SetColumn(_hexBox, 1);
            previewRow.Children.Add(_hexBox);
            side.Children.Add(previewRow);

            // 「预设」标题行：左边标题、竖线分隔、右边对比度小字（小字直接接在标题后面，同字号，用竖线隔开）。
            Grid presetHeader = new Grid { Margin = new Thickness(0, 16, 0, 8) };
            presetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            presetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            presetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock presetLabel = new TextBlock
            {
                Text = "预设",
                FontSize = 13,   // 标题行与开关行统一字号
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(presetLabel, "ColorPickerPresetLabel");
            Grid.SetColumn(presetLabel, 0);
            presetHeader.Children.Add(presetLabel);

            Border divider = new Border
            {
                Width = 1,
                Height = 12,
                Background = Theme.BorderStrongBrush,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(divider, "ColorPickerPresetDivider");
            Grid.SetColumn(divider, 1);
            presetHeader.Children.Add(divider);

            _contrast = new TextBlock
            {
                FontSize = 13,   // 与「预设」同字号
                TextWrapping = TextWrapping.Wrap,
                // TextTertiary（#6E6E6E）在 #303030 卡片上只有 2.6:1，基本看不清：
                // 一句提醒“颜色可能看不清”的文案自己看不清很讽刺，所以用 TextSecondary。
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(_contrast, "ColorPickerContrast");
            Grid.SetColumn(_contrast, 2);
            presetHeader.Children.Add(_contrast);

            side.Children.Add(presetHeader);

            WrapPanel palette = new WrapPanel();
            // 与显示项页那一排共用同一份色板（可加可删）。
            _palette = new PaletteChips(_window, palette, _role, () => Theme.ToHex(CurrentColor()),
                colour => ApplyHex(colour), 20, new Thickness(0, 0, 6, 6), true);
            _palette.Fill();
            side.Children.Add(palette);

            // 「添加到颜色预设」：标题在左、胶囊开关在右（在色块行下面）。
            Grid autoRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock autoLabel = new TextBlock
            {
                Text = "添加到颜色预设",
                FontSize = 13,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(autoLabel, "ColorPickerAutoAddLabel");
            Grid.SetColumn(autoLabel, 0);
            autoRow.Children.Add(autoLabel);

            ToggleSwitch autoAdd = new ToggleSwitch
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _window != null && _window.CurrentSettings != null &&
                            _window.CurrentSettings.AutoAddPreset
            };
            AutomationProperties.SetAutomationId(autoAdd, "ColorPickerAutoAdd");
            AutomationProperties.SetName(autoAdd, "添加到颜色预设");
            autoAdd.Checked += (sender, e) => SetAutoAdd(true);
            autoAdd.Unchecked += (sender, e) => SetAutoAdd(false);
            Grid.SetColumn(autoAdd, 1);
            autoRow.Children.Add(autoAdd);
            side.Children.Add(autoRow);

            // 「恢复默认」不在这里：它放到面板底部按钮行，夹在「取消」与「确定」中间。

            return side;
        }

        private Grid BuildButtons()
        {
            Grid row = new Grid();
            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            FlatButton cancel = new FlatButton("取消");
            AutomationProperties.SetAutomationId(cancel, "ColorPickerCancel");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(cancel);

            FlatButton reset = new FlatButton("恢复默认");
            AutomationProperties.SetAutomationId(reset, "ColorPickerReset");
            reset.Margin = new Thickness(0, 0, 8, 0);
            reset.Click += (sender, e) => ApplyHex(_defaultHex);
            buttons.Children.Add(reset);

            FlatButton ok = new FlatButton("确定", true);
            AutomationProperties.SetAutomationId(ok, "ColorPickerOk");
            ok.Click += (sender, e) =>
            {
                string hex = Theme.ToHex(CurrentColor());
                bool remember = _window != null && _window.CurrentSettings != null &&
                                _window.CurrentSettings.AutoAddPreset;
                if (_window != null) _window.CloseOverlay();
                // 开关打开时把这次选的色记进预设（末尾淘汰），再交给调用方写进元素。
                if (remember) RememberPreset(hex);
                if (_apply != null) _apply(hex);
            };
            buttons.Children.Add(ok);

            row.Children.Add(buttons);
            return row;
        }

        // ---- 状态同步 ----------------------------------------------------------------

        /// <summary>
        /// 「确定时加入预设」开关：立即写回 settings.json（这是全局偏好，不属于某个元素的草稿，
        /// 所以不跟随页面上的「保存/放弃改动」）。
        /// </summary>
        private void SetAutoAdd(bool value)
        {
            if (_window == null) return;
            AppSettings current = _window.CurrentSettings;
            if (current == null || current.AutoAddPreset == value) return;

            AppSettings edited = current.Clone();
            edited.AutoAddPreset = value;
            edited.Normalize();
            _window.SaveSettingsQuietly(edited);
        }

        /// <summary>把这次选定的颜色加进**这个角色**的预设；满 16 个时挤掉最早的（PaletteRules）。</summary>
        private void RememberPreset(string hex)
        {
            PaletteChips.RememberPreset(_window, _role, hex);
        }

        private void UpdateAll()
        {
            Color current = CurrentColor();

            if (_squareBase != null) _squareBase.Fill = new SolidColorBrush(HsvColor(_hue, 1, 1));
            if (_squareThumb != null)
            {
                Canvas.SetLeft(_squareThumb, _saturation * SquareSize - ThumbSize / 2);
                Canvas.SetTop(_squareThumb, (1 - _value) * SquareSize - ThumbSize / 2);
            }
            if (_hueThumb != null)
            {
                Canvas.SetLeft(_hueThumb, (StripWidth - ThumbSize) / 2);
                Canvas.SetTop(_hueThumb, _hue / 360.0 * SquareSize - ThumbSize / 2);
            }
            if (_preview != null) _preview.Background = new SolidColorBrush(current);
            if (_palette != null) _palette.UpdateSelection();
            if (_hexBox != null && !_hexDirty) SetHexText(Theme.ToHex(current));
            if (_contrast != null) UpdateContrast(current);
        }

        private void SetHexText(string text)
        {
            if (_hexBox == null) return;
            _syncingHex = true;
            _hexBox.Text = text;
            _syncingHex = false;
        }

        private void UpdateContrast(Color colour)
        {
            double ratio = ContrastRatio(colour, Theme.BgBase);
            if (ratio < ContrastWarning)
            {
                _contrast.Text = string.Format(CultureInfo.InvariantCulture,
                    "与深色背景对比度 {0:0.0}:1，低于 3:1，任务栏上可能看不清", ratio);
                _contrast.Foreground = Theme.AccentOrangeBrush;
            }
            else
            {
                _contrast.Text = string.Format(CultureInfo.InvariantCulture,
                    "与深色背景对比度 {0:0.0}:1，可读", ratio);
                _contrast.Foreground = Theme.TextTertiaryBrush;
            }
        }

        private static bool IsValidHexText(string text)
        {
            string canonical;
            return ColorText.TryParse(text, out canonical);
        }

        /// <summary>
        /// Blur/Enter: drop the dirty flag and put the canonical `#AARRGGBB` back into the box.
        /// Never re-applies the text on top of the picker state - that is what made “确定”写回旧色。
        /// </summary>
        private void NormalizeHexText()
        {
            string text = (_hexBox.Text ?? string.Empty).Trim();
            if (IsValidHexText(text)) ApplyHex(text, false);
            _hexDirty = false;
            SetHexText(Theme.ToHex(CurrentColor()));
        }

        /// <param name="fromTyping">
        /// True when the hex box is the source of this colour, so its text must not be rewritten
        /// mid-keystroke.
        /// </param>
        private void ApplyHex(string hex, bool fromTyping = false)
        {
            string canonical;
            if (!ColorText.TryParse(hex, out canonical)) return;

            Color colour = Theme.ToColor(canonical, CurrentColor());
            _alpha = colour.A;
            double hue, saturation, value;
            ToHsv(colour, out hue, out saturation, out value);
            _hue = hue;
            _saturation = saturation;
            _value = value;
            _hexDirty = fromTyping;
            UpdateAll();
        }

        private Color CurrentColor()
        {
            return WithAlpha(HsvColor(_hue, _saturation, _value), _alpha);
        }

        private Color WithAlpha(Color colour, byte alpha)
        {
            // 面板不编辑 alpha；但读旧配置时不把它抹成 255，保存后仍是原值。
            return Color.FromArgb(alpha == 0 ? (byte)255 : alpha, colour.R, colour.G, colour.B);
        }

        // ---- 颜色数学 ----------------------------------------------------------------

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static Color HsvColor(double hue, double saturation, double value)
        {
            double h = ((hue % 360.0) + 360.0) % 360.0;
            double s = Clamp(saturation, 0, 1);
            double v = Clamp(value, 0, 1);

            int sector = (int)Math.Floor(h / 60.0) % 6;
            double f = h / 60.0 - Math.Floor(h / 60.0);
            byte vByte = (byte)Math.Round(v * 255);
            byte p = (byte)Math.Round(v * (1 - s) * 255);
            byte q = (byte)Math.Round(v * (1 - s * f) * 255);
            byte t = (byte)Math.Round(v * (1 - s * (1 - f)) * 255);

            switch (sector)
            {
                case 0: return Color.FromRgb(vByte, t, p);
                case 1: return Color.FromRgb(q, vByte, p);
                case 2: return Color.FromRgb(p, vByte, t);
                case 3: return Color.FromRgb(p, q, vByte);
                case 4: return Color.FromRgb(t, p, vByte);
                default: return Color.FromRgb(vByte, p, q);
            }
        }

        private static void ToHsv(Color colour, out double hue, out double saturation, out double value)
        {
            double r = colour.R / 255.0;
            double g = colour.G / 255.0;
            double b = colour.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            value = max;
            saturation = max <= 0 ? 0 : delta / max;

            if (delta <= 0.00001)
            {
                hue = 0;   // grey: hue is meaningless, keep red so the square does not jump
                return;
            }

            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);

            if (hue < 0) hue += 360;
        }

        private static double Luminance(Color colour)
        {
            return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
        }

        private static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        private static double ContrastRatio(Color a, Color b)
        {
            double la = Luminance(a);
            double lb = Luminance(b);
            double lighter = Math.Max(la, lb);
            double darker = Math.Min(la, lb);
            return (lighter + 0.05) / (darker + 0.05);
        }
    }
}
