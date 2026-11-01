using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Centred dialog for notices that need the user to do something ("宽度变了，要重新勾选工具栏")
    /// or that report a failure.
    ///
    /// They used to be cards in the bottom-right corner of the window, where they covered the
    /// 「放弃改动 / 保存」 buttons. Short-lived success toasts stay in the corner but now sit
    /// bottom-centre, clear of the buttons (`NoticeHost` placement).
    /// </summary>
    internal sealed class NoticeDialog
    {
        public const double PanelWidth = 560;

        private readonly SettingsWindow _window;

        public NoticeDialog(SettingsWindow window)
        {
            _window = window;
        }

        public FrameworkElement Build(string title, string body)
        {
            StackPanel content = new StackPanel();

            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                Margin = new Thickness(0, 0, 0, 12)
            });

            content.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)),
                TextWrapping = TextWrapping.Wrap
            });

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            FlatButton ok = new FlatButton("知道了", true);
            AutomationProperties.SetAutomationId(ok, "NoticeOk");
            ok.MinWidth = 96;
            ok.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(ok);
            content.Children.Add(buttons);

            return new Border
            {
                Width = PanelWidth,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true,
                Child = content
            };
        }
    }
}
