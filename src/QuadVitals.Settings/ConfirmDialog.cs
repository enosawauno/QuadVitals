using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 需要确认的破坏性操作（当前用于「删除元素」）。
    ///
    /// 与 <see cref="NoticeDialog"/> 同一套外观（560 宽居中面板 + 70% 遮罩 + 190 ms 入场），
    /// 区别是多一个真正的「确认」按钮并把点击结果回调出去 —— 提示框只能「知道了」，
    /// 用它做删除会变成“点了就删”。
    /// </summary>
    internal sealed class ConfirmDialog
    {
        private readonly SettingsWindow _window;

        public ConfirmDialog(SettingsWindow window)
        {
            _window = window;
        }

        public FrameworkElement Build(string title, string body, string confirmText, Action onConfirm,
                                      bool danger = false)
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

            FlatButton cancel = new FlatButton("取消");
            AutomationProperties.SetAutomationId(cancel, "ConfirmCancel");
            cancel.MinWidth = 96;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(cancel);

            FlatButton ok = new FlatButton(confirmText, true);
            AutomationProperties.SetAutomationId(ok, "ConfirmOk");
            ok.MinWidth = 96;
            if (danger)
            {
                // 破坏性操作：主按钮转红。绿色确认键会把“删除”做得像一次普通保存。
                ok.Background = Theme.AccentRedBrush;
                ok.Foreground = Theme.TextPrimaryBrush;
            }
            ok.Click += (sender, e) =>
            {
                _window.CloseOverlay();
                if (onConfirm != null) onConfirm();
            };
            buttons.Children.Add(ok);

            content.Children.Add(buttons);

            Border panel = new Border
            {
                Width = NoticeDialog.PanelWidth,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                Child = content
            };

            return panel;
        }
    }
}
