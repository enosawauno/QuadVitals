using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuadVitals.UI;

namespace QuadVitals.Setup
{
    /// <summary>
    /// The page a cancelled run ends on.
    ///
    /// Cancelling is a real outcome, not a question to ask again: the page says nothing was changed and the
    /// window closes itself, so the user never has to click twice and can never get stuck on a dialog that
    /// keeps asking.
    /// </summary>
    internal sealed partial class SetupWindow
    {
        // ---- cancelled -----------------------------------------------------------------

        private void CancelWork()
        {
            if (_running) return;

            ExitCode = 0;
            ShowPage(BuildCancelledPage());

            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            timer.Tick += (sender, e) =>
            {
                timer.Stop();
                CloseOnce();   // the 关闭 button may have got there first
            };
            timer.Start();
        }

        /// <summary>
        /// Closes once. Both the self-close timer and the 关闭 button call this, and WPF throws if a closed
        /// window is closed again.
        /// </summary>
        private void CloseOnce()
        {
            if (_closed) return;
            _closed = true;
            Close();
        }

        /// <summary>Neutral end state: no check mark, no red cross, nothing was changed.</summary>
        private UIElement BuildCancelledPage()
        {
            StackPanel content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 26, 0, 0)
            };

            content.Children.Add(new TextBlock
            {
                Text = _uninstall ? "已取消卸载" : "已取消安装",
                Foreground = Theme.TextPrimaryBrush,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            content.Children.Add(new TextBlock
            {
                Text = _uninstall
                    ? "您已取消卸载，没有对系统做任何更改"
                    : "您已取消安装，没有对系统做任何更改",
                Foreground = Theme.TextSecondaryBrush,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            });

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            FlatButton close = new FlatButton("关闭", true);
            close.MinWidth = 96;
            System.Windows.Automation.AutomationProperties.SetAutomationId(close, "SetupCancelledClose");
            close.Click += (sender, e) => CloseOnce();
            buttons.Children.Add(close);

            return PageLayout(content, buttons);
        }
    }
}
