using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuadVitals.UI;

namespace QuadVitals.Setup
{
    /// <summary>
    /// Progress page: stage line, bar and log pane, and the worker that runs the install scripts off
    /// the UI thread.
    /// </summary>
    internal sealed partial class SetupWindow
    {
        // ---- progress ------------------------------------------------------------------

        private void StartWork()
        {
            if (_running) return;
            _running = true;
            SetBusy(true);

            ShowPage(BuildProgress());

            string installDir = _uninstall ? InstallerCore.DefaultInstallDir : _installDirBox.Text.Trim();
            // The logon task (and with it the single UAC prompt) is part of the product, not an option:
            // without admin rights the agent cannot load the sensor driver, so temperature would stay
            // "--" and the stop step could not end an older agent. Silent installs can still pass -NoTask
            // to install.ps1 directly.
            bool createTask = !_uninstall;
            bool startAgent = !_uninstall && _startAgent.IsChecked == true;
            bool desktopShortcut = !_uninstall && _desktopShortcut.IsChecked == true;
            bool keepSettings = _uninstall && _keepSettings.IsChecked == true;

            _stageText.Text = _uninstall ? "准备卸载…" : "准备安装…";

            Task.Run(() =>
            {
                string payloadRoot = null;
                InstallResult result;
                try
                {
                    payloadRoot = _core.ExtractPayload();
                    result = _uninstall
                        ? _core.Uninstall(payloadRoot, installDir, keepSettings)
                        : _core.Install(payloadRoot, installDir, createTask, startAgent, desktopShortcut);
                }
                catch (Exception ex)
                {
                    result = InstallResult.Failure(ex.GetType().Name + ": " + ex.Message);
                }

                if (payloadRoot != null) _core.TryDeletePayload(payloadRoot);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _running = false;
                    SetBusy(false);
                    if (result.Succeeded)
                    {
                        _bandRestored = result.BandRestored;
                        ShowResult(installDir);
                    }
                    else ShowFailure(result.Error, result.ShowLog);
                }));
            });
        }

        private UIElement BuildProgress()
        {
            StackPanel stack = new StackPanel();

            stack.Children.Add(Heading(_uninstall ? "正在卸载" : "正在安装"));

            _stageText = new TextBlock
            {
                Text = "准备中…",
                Foreground = Theme.TextSecondaryBrush,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };

            // The copy link shares the stage row: it must not steal height from the log pane.
            Grid stageRow = new Grid { Margin = new Thickness(0, 4, 0, 14) };
            stageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stageRow.Children.Add(_stageText);

            _logBox = new LogView(150);
            _logBox.Host.Margin = new Thickness(0, 10, 0, 0);

            FlatButton copy = CopyButton(_logBox, "复制日志", null);
            copy.VerticalAlignment = VerticalAlignment.Center;
            copy.Margin = new Thickness(0);
            Grid.SetColumn(copy, 1);
            stageRow.Children.Add(copy);

            stack.Children.Add(stageRow);

            _bar = new ProgressBar
            {
                Height = 6,
                IsIndeterminate = true,
                Foreground = Theme.AccentGreenBrush,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(0)
            };
            stack.Children.Add(_bar);

            _logBox = new LogView(150);
            _logBox.Host.Margin = new Thickness(0, 0, 0, 0);

            _core.OnStatus = stage => Dispatcher.BeginInvoke(new Action(() => { _stageText.Text = stage; }));
            _core.OnLog = line => Dispatcher.BeginInvoke(new Action(() => AppendLog(_logBox, line)));

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(stack);

            Grid.SetRow(_logBox.Host, 1);
            grid.Children.Add(_logBox.Host);
            return grid;
        }

        /// <summary>
        /// The log pane plus its copy button. Copying is the difference between "the log shows a reason"
        /// and "the user can send that reason to the developer"; the raw text goes to the clipboard, the
        /// soft-wrap hints do not.
        /// </summary>
        /// <summary>
        /// Link-styled "copy" button. It lives in the button row (failure page) or next to the stage text
        /// (progress page), never above the log: the log pane is the thing that needs the height.
        /// </summary>
        private FlatButton CopyButton(LogView log, string label, Func<string> header)
        {
            FlatButton copy = new FlatButton(label);
            copy.Height = 26;
            copy.Padding = new Thickness(10, 0, 10, 0);
            copy.FontSize = 11.5;
            copy.Background = Brushes.Transparent;
            copy.Foreground = Theme.AccentGreenBrush;
            copy.Focusable = false;
            copy.Margin = new Thickness(0, 0, 12, 0);
            System.Windows.Automation.AutomationProperties.SetAutomationId(copy, "SetupCopyLog");
            copy.Click += (sender, e) =>
            {
                try
                {
                    string text = header == null ? string.Empty : header() + Environment.NewLine + Environment.NewLine;
                    System.Windows.Clipboard.SetText(text + log.RawText);

                    copy.Content = "已复制";
                    DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                    timer.Tick += (sender2, e2) => { timer.Stop(); copy.Content = label; };
                    timer.Start();
                }
                catch (Exception)
                {
                    copy.Content = "复制失败";
                }
            };
            return copy;
        }
    }
}
