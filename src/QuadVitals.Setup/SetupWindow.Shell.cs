using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using QuadVitals.UI;

namespace QuadVitals.Setup
{
    /// <summary>
    /// Window chrome: the shell and title bar, plus the pieces every page is built from (page layout,
    /// the option card, the haze behind a page, the busy state of the close button).
    /// </summary>
    internal sealed partial class SetupWindow
    {
        // ---- shell ---------------------------------------------------------------------

        private UIElement BuildShell()
        {
            Grid rows = new Grid();
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            rows.Children.Add(BuildTitleBar());

            _host = new Grid { Margin = new Thickness(26, 4, 26, 22) };
            Grid.SetRow(_host, 1);
            rows.Children.Add(_host);

            // The haze lives here, on the shell, so it is not clipped by a page's own bounds - clipped at
            // the title bar or at the 26 px content margins is what made its edge look hard. It fades to
            // fully transparent before the shell border, so there is no seam anywhere inside the window.
            _glow = SoftGlow(Theme.AccentGreen);

            Grid shellContent = new Grid();
            shellContent.Children.Add(_glow);
            shellContent.Children.Add(rows);

            Border shell = new Border
            {
                Background = Theme.BgBaseBrush,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 24,
                    ShadowDepth = 0,
                    Opacity = 0.55
                },
                Child = shellContent
            };

            // The shell is transparent so the drop shadow shows; the padding keeps it visible.
            Grid outer = new Grid { Margin = new Thickness(18) };
            outer.Children.Add(shell);
            return outer;
        }

        private UIElement BuildTitleBar()
        {
            Grid bar = new Grid { Height = 46, Background = Brushes.Transparent };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(22, 0, 0, 0)
            };
            left.Children.Add(AppIcon());
            left.Children.Add(new TextBlock
            {
                Text = _uninstall ? "QuadVitals 卸载" : "QuadVitals 安装",
                Foreground = Theme.TextSecondaryBrush,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 1, 0, 0)
            });

            bar.Children.Add(left);

            _closeButton = CaptionClose();
            _closeButton.Click += (sender, e) => Close();
            Grid.SetColumn(_closeButton, 2);
            bar.Children.Add(_closeButton);

            bar.MouseLeftButtonDown += (sender, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed && !_running) DragMove();
            };
            return bar;
        }

        /// <summary>
        /// Close button shaped like the product's own title bar button: transparent, drawn as two
        /// round-capped lines. The stock Aero2 button template paints a light rectangle on hover,
        /// which on a dark shell reads as a stray colour block; here only the glyph colour changes.
        /// Glyph box and stroke are the product's caption values scaled up by a third.
        /// </summary>
        private static Button CaptionClose()
        {
            Button button = new Button
            {
                Width = 42,
                Height = 36,
                Margin = new Thickness(0, 0, 2, 0),
                Foreground = Theme.TextSecondaryBrush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Template = BuildCaptionTemplate()
            };

            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "SetupClose");
            System.Windows.Automation.AutomationProperties.SetName(button, "关闭");

            Canvas glyph = new Canvas { Width = 21, Height = 21 };
            glyph.Children.Add(CaptionLine(button, 4, 4, 17, 17));
            glyph.Children.Add(CaptionLine(button, 17, 4, 4, 17));
            button.Content = glyph;

            button.MouseEnter += (sender, e) => button.Foreground = Theme.TextPrimaryBrush;
            button.MouseLeave += (sender, e) => button.Foreground = Theme.TextSecondaryBrush;
            return button;
        }

        private static System.Windows.Shapes.Line CaptionLine(Button owner,
                                                              double x1, double y1,
                                                              double x2, double y2)
        {
            System.Windows.Shapes.Line line = new System.Windows.Shapes.Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeThickness = 3.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            line.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
                new System.Windows.Data.Binding("Foreground") { Source = owner });
            return line;
        }

        /// <summary>Transparent background, only centers the glyph: no chrome, no hover fill.</summary>
        private static ControlTemplate BuildCaptionTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            template.VisualTree = border;
            return template;
        }

        private UIElement AppIcon()
        {
            try
            {
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(InstallerCore.SelfPath()))
                {
                    if (icon != null)
                    {
                        return new Image
                        {
                            Source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                                                                        BitmapSizeOptions.FromEmptyOptions()),
                            Width = 16,
                            Height = 16,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                }
            }
            catch (Exception)
            {
                // No icon at hand: the title text alone is enough.
            }
            return new Border { Width = 16, Height = 16 };
        }

        /// <summary>
        /// Content stays at the top and the buttons sit on the bottom edge, so a page with only a
        /// few rows does not look half-empty (the progress page fills the same box).
        /// </summary>
        private static UIElement PageLayout(UIElement content, UIElement buttons)
        {
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            grid.Children.Add(content);

            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);
            return grid;
        }

        /// <summary>Green on the happy paths, red when something failed; the haze itself is window-wide.</summary>
        private void TintGlow(Color tint)
        {
            if (_glow == null) return;
            _glow.Fill = SoftGlow(tint).Fill;
        }

        /// <summary>
        /// Puts the actionable rows in one card. The title block and the controls then read as two
        /// layers instead of one flat column, and the card owns the outer spacing so no row has to
        /// invent its own margin.
        /// </summary>
        private static Border Group(UIElement content)
        {
            return new Border
            {
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 16, 18, 16),
                Margin = new Thickness(0, 22, 0, 0),
                Child = content
            };
        }

        /// <summary>
        /// Greys out the title-bar X while the install runs. OnClosing already refuses to close, but a
        /// button that looks enabled and does nothing reads as a hang; disabled is the honest state.
        /// </summary>
        private void SetBusy(bool busy)
        {
            if (_closeButton == null) return;
            _closeButton.IsEnabled = !busy;
            _closeButton.Opacity = busy ? 0.3 : 1.0;
        }

        /// <summary>
        /// Very faint radial haze behind the page, warm green in the middle and gone by the edges. The
        /// window is otherwise flat #1A1A1A with a single icon, which read as plain; the gradient is
        /// kept under ~12% alpha so it never competes with the text.
        /// </summary>
        private static System.Windows.Shapes.Rectangle SoftGlow(Color tint)
        {
            // Radius 1.0 with the last stop at exactly 0 alpha: the falloff reaches zero at the shell
            // edge instead of being cut off while still tinted, which is what produced the hard rim.
            RadialGradientBrush brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.38),
                GradientOrigin = new Point(0.5, 0.38),
                RadiusX = 1.0,
                RadiusY = 1.0
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, tint.R, tint.G, tint.B), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x0E, tint.R, tint.G, tint.B), 0.35));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x04, tint.R, tint.G, tint.B), 0.70));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, tint.R, tint.G, tint.B), 1.0));
            brush.Freeze();

            return new System.Windows.Shapes.Rectangle
            {
                Fill = brush,
                IsHitTestVisible = false
            };
        }
    }
}
