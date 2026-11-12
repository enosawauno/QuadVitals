using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// The diagnostics preview ("查看将包含的信息") as a board of its own.
    ///
    /// It used to expand inside the form: the mono block took 190 px out of the same column the
    /// writing area lives in, so opening it shrank the box the user was typing in. Same geometry as
    /// the feedback panel (640×427) means opening it covers the panel exactly instead of resizing
    /// anything, and closing it puts the form back untouched.
    /// </summary>
    internal sealed class FeedbackDiagnosticsDialog
    {
        public const double DialogWidth = SettingsWindow.FeedbackPanelWidth;
        public const double DialogHeight = SettingsWindow.FeedbackPanelHeight;

        private readonly SettingsWindow _window;
        private readonly DarkTextBox _box = new DarkTextBox();

        public FeedbackDiagnosticsDialog(SettingsWindow window)
        {
            _window = window;
        }

        /// <summary>
        /// The preview text. Set again while the panel is open (the log switch lives behind it), so
        /// the board never shows a stale decision about whether logs are attached.
        /// </summary>
        public string Text
        {
            set { _box.Text = value ?? string.Empty; }
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
            AutomationProperties.SetAutomationId(panel, "FeedbackDiagnosticsPanel");

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "诊断信息",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(title);

            // 关掉它只关这一层：外层还是那张反馈面板，草稿、附件、输入到一半的文字都还在原位。
            Button close = SettingsWindow.PanelCloseButton("FeedbackDiagnosticsClose");
            close.Click += (sender, e) => _window.CloseSubOverlay();
            Grid.SetColumn(close, 1);
            head.Children.Add(close);

            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // 这句话原来贴在板底，与正文分成两截；它解释的是“这一板是什么”，
            // 所以当副标题放在标题下面，读者先知道在看什么，再看内容。
            TextBlock hint = new TextBlock
            {
                Text = "仅勾选后读取传感器与程序日志；提交前可查看，目录已脱敏。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            AutomationProperties.SetAutomationId(hint, "FeedbackDiagnosticsHint");
            Grid.SetRow(hint, 1);
            grid.Children.Add(hint);

            // 等宽字体 + 只读：这一块是给“核对哪几行会发出去”看的，不是编辑区。
            // 长行必须折行而不是被右边缘切掉，且每一行左右两条边都要齐齐地贴住卡片内边距。
            _box.IsReadOnly = true;
            _box.AcceptsReturn = true;
            _box.TextWrapping = TextWrapping.Wrap;
            _box.TextAlignment = TextAlignment.Justify;
            _box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _box.Height = double.NaN;                 // 模板自带固定高度，这里交给行高
            _box.VerticalAlignment = VerticalAlignment.Stretch;
            _box.FontFamily = Theme.MonoFont;
            _box.FontSize = 11;
            _box.Padding = new Thickness(10);
            AutomationProperties.SetAutomationId(_box, "FeedbackDiagnosticsText");
            Grid.SetRow(_box, 2);
            grid.Children.Add(_box);

            panel.Child = grid;
            return panel;
        }
    }
}
