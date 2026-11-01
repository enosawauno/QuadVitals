using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Read-only document viewer used by the About page: licence files used to be handed to
    /// Notepad, which drops a light system window into the dark shell — the same problem that
    /// replaced MessageBox with NoticeHost.
    ///
    /// Every document opens in the SAME panel size so the two licence dialogs and the settings
    /// preview feel like one component instead of three different windows. Text always wraps: a
    /// horizontal scrollbar for prose was the other problem.
    /// </summary>
    internal sealed class DocumentViewerDialog
    {
        public const double PanelWidth = 900;
        public const double PanelHeight = 620;

        private const double SidePadding = 24;
        private const double BodyFontSize = 12.5;
        private const double BodyLineHeight = 20;

        private readonly SettingsWindow _window;

        public DocumentViewerDialog(SettingsWindow window)
        {
            _window = window;
        }

        /// <param name="monospace">JSON / logs read better monospaced; licence prose does not.</param>
        /// <param name="secondaryLabel">Optional extra button on the left of 关闭 (e.g. 在记事本中编辑).</param>
        public FrameworkElement Build(string title, string body, bool monospace = false,
                                      string secondaryLabel = null, Action secondaryAction = null)
        {
            Border panel = new Border
            {
                Width = PanelWidth,
                Height = PanelHeight,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(SidePadding),
                SnapsToDevicePixels = true
            };

            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock heading = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(heading, 0);
            layout.Children.Add(heading);

            // TextBlock (not a read-only TextBox): only TextBlock exposes LineHeight, and the
            // caret of a focused TextBox used to land on the last page when the overlay took
            // focus.
            // Licence files arrive hard-wrapped at ~70 columns; unwrapping them is what lets the
            // text fill the panel and justify. JSON is left exactly as written.
            string shown = monospace ? (body ?? string.Empty) : PlainTextReflow.Unwrap(body);

            TextBlock bodyText = new TextBlock
            {
                Text = shown,
                TextWrapping = TextWrapping.Wrap,
                // Justified prose: licence texts are read as paragraphs, and the ragged right
                // edge was the "rough" look the user pointed at. JSON stays left-aligned because
                // justification would destroy its indentation.
                TextAlignment = monospace ? TextAlignment.Left : TextAlignment.Justify,
                FontFamily = monospace ? Theme.MonoFont : Theme.UiFont,
                FontSize = monospace ? 11.5 : BodyFontSize,
                LineHeight = monospace ? 18 : BodyLineHeight,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xD8, 0xD8, 0xD8)),
                Margin = new Thickness(16, 14, 12, 14)
            };
            AutomationProperties.SetAutomationId(bodyText, "DocumentBody");

            ScrollViewer scroll = new ScrollViewer
            {
                Content = bodyText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Theme.SurfaceHoverBrush,      // #3A3A3A
                BorderBrush = Theme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                Focusable = true
            };
            scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), DarkScrollBars.Create());
            AutomationProperties.SetAutomationId(scroll, "DocumentScroll");
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            if (!string.IsNullOrEmpty(secondaryLabel) && secondaryAction != null)
            {
                FlatButton secondary = new FlatButton(secondaryLabel);
                AutomationProperties.SetAutomationId(secondary, "DocumentSecondary");
                secondary.Margin = new Thickness(0, 0, 8, 0);
                secondary.Click += (sender, e) => secondaryAction();
                buttons.Children.Add(secondary);
            }

            FlatButton close = new FlatButton("关闭", true);
            AutomationProperties.SetAutomationId(close, "DocumentClose");
            close.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 2);
            layout.Children.Add(buttons);

            panel.Child = layout;
            return panel;
        }
    }
}
