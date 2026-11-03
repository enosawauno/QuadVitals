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
