using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;


namespace QuadVitals.Settings
{
    /// <summary>
    /// 通用设置：logon autostart preference. The adapter picker and the refresh rate live in the
    /// "当前网速" card's gear dialog (NetworkSettingsDialog), so this page must not write those
    /// fields back from its own stale copy.
    ///
    /// Every control here saves on change, so
    /// there is no 保存 button and no page rebuild after saving.
    /// </summary>
    internal sealed class NetworkPage : UserControl, IRefreshable
    {
        private readonly SettingsWindow _window;
        private readonly StackPanel _root = new StackPanel();

        public NetworkPage(SettingsWindow window)
        {
            _window = window;
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(Theme.PagePadding, 0, Theme.PagePadding, Theme.PagePadding),
                Content = _root
            };
            Build();
        }

        public void Refresh()
        {
            Build();
        }

        private void Build()
        {
            _root.Children.Clear();
            _root.Children.Add(new PageHeading("通用设置", "启动方式与任务栏的全局偏好"));
            AppSettings settings = _window.CurrentSettings.Clone();

            Card behaviour = new Card("启动");
            behaviour.Margin = new Thickness(0, 0, 0, Theme.CardGap);

            CheckBox autostart = new DarkCheckBox
            {
                Content = "登录时自动启动传感器（计划任务，最高权限）",
                IsChecked = settings.StartAgentWithWindows,
                Margin = new Thickness(0, 0, 0, 0)
            };
            AutomationProperties.SetAutomationId(autostart, "GeneralAutostart");
            // Subscribe after the initial value: assigning IsChecked above must not write a file.
            autostart.Checked += (sender, e) => SaveStartWithWindows(true);
            autostart.Unchecked += (sender, e) => SaveStartWithWindows(false);
            behaviour.Content.Children.Add(autostart);

            behaviour.Content.Children.Add(new TextBlock
            {
                Text = "仅保存偏好；计划任务需执行 scripts\\install-agent-task.ps1 创建（需一次管理员授权）。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                // Aligned under the checkbox caption (18 px square + 9 px gap + 1 px): the hint
                // belongs to the switch, so it starts where the caption starts.
                Margin = new Thickness(28, 6, 0, 0)
            });
            _root.Children.Add(behaviour);

            // 元素间距：条宽由元素宽度 + 间距累加得出，所以这里改一个数会改变整条宽度；
            // 保存后如果宽度变了就重建一次 band（闪一下），与显示项页的处理保持一致。
            Card layout = new Card("任务栏");
            layout.Margin = new Thickness(0, 0, 0, 0);
            layout.Content.Children.Add(BuildSpacingRow(settings));
            layout.Content.Children.Add(new TextBlock
            {
                Text = "数值范围0-40px，修改后会自动重启资源管理器。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });
            _root.Children.Add(layout);
        }

        private FrameworkElement BuildSpacingRow(AppSettings settings)
        {
            Grid grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock label = new TextBlock
            {
                Text = "元素间距",
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            TextBox box = new DarkTextBox
            {
                Text = settings.Band.HorizontalSpacing.ToString(),
                Width = 80,
                TextAlignment = TextAlignment.Left
            };
            AutomationProperties.SetAutomationId(box, "GeneralSpacingBox");
            AutomationProperties.SetName(box, "元素间距");
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            TextBlock unit = new TextBlock
            {
                Text = "px",
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(unit, 2);
            grid.Children.Add(unit);

            Action commit = () =>
            {
                int parsed;
                if (!int.TryParse(box.Text, out parsed)) parsed = settings.Band.HorizontalSpacing;
                if (parsed < 0) parsed = 0;
                if (parsed > 40) parsed = 40;
                box.Text = parsed.ToString();
                if (parsed == settings.Band.HorizontalSpacing) return;

                AppSettings latest = _window.CurrentSettings.Clone();
                latest.Band.HorizontalSpacing = parsed;
                latest.Normalize();
                settings.Band.HorizontalSpacing = parsed;
                _window.SaveSettingsAndRebuild(latest);
            };
            box.LostFocus += (sender, e) => commit();
            box.KeyDown += (sender, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) commit();
            };
            return grid;
        }

        /// <summary>
        /// Writes just this page's field onto the latest settings (the adapter / refresh rate may
        /// have been changed in the gear dialog since this page was built) and saves silently.
        /// </summary>
        private void SaveStartWithWindows(bool value)
        {
            AppSettings latest = _window.CurrentSettings.Clone();
            if (latest.StartAgentWithWindows == value) return;
            latest.StartAgentWithWindows = value;
            latest.Normalize();
            _window.SaveSettingsQuietly(latest);
        }
    }
}
