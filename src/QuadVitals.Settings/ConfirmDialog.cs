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
        /// <summary>纯文字正文：Body 级、行高 20、可折行。</summary>
        internal static TextBlock BodyText(string body)
        {
            return new TextBlock
            {
                Text = body,
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)),
                TextWrapping = TextWrapping.Wrap
            };
        }

        private readonly SettingsWindow _window;

        public ConfirmDialog(SettingsWindow window)
        {
            _window = window;
        }

        public FrameworkElement Build(string title, string body, string confirmText, Action onConfirm,
                                      bool danger = false, Action onCancel = null)
        {
            return BuildWithContent(title, BodyText(body), confirmText, onConfirm, danger, onCancel);
        }

        /// <summary>
        /// 同一个确认框，正文换成调用方自己排好的结构（例如“结论 + 实测数据 + 问句”）。
        /// 一串同字号同颜色的句子堆在一起没有层级，读起来是一段散文，所以正文允许结构化。
        /// </summary>
        public FrameworkElement BuildWithContent(string title, FrameworkElement body, string confirmText,
                                                 Action onConfirm, bool danger = false, Action onCancel = null,
                                                 FrameworkElement titleExtra = null)
        {
            StackPanel content = new StackPanel();

            TextBlock titleText = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (titleExtra == null)
            {
                titleText.Margin = new Thickness(0, 0, 0, 16);
                content.Children.Add(titleText);
            }
            else
            {
                // 标题行：左边标题、右边一枚小提示图标（悬停出 tooltip），标题与图标同一基线。
                Grid titleRow = new Grid { Margin = new Thickness(0, 0, 0, 16) };
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                titleRow.Children.Add(titleText);
                titleExtra.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(titleExtra, 1);
                titleRow.Children.Add(titleExtra);
                content.Children.Add(titleRow);
            }

            content.Children.Add(body);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                // 内容 → 按钮 24（间距阶）：20 不在阶上，视觉上按钮会贴着正文。
                Margin = new Thickness(0, 24, 0, 0)
            };

            FlatButton cancel = new FlatButton("取消");
            AutomationProperties.SetAutomationId(cancel, "ConfirmCancel");
            cancel.MinWidth = 96;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) =>
            {
                // 取消回调先跑：调用方要把“已经拨过去的下拉”拨回来，再关面板。
                if (onCancel != null) onCancel();
                _window.CloseOverlay();
            };
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
