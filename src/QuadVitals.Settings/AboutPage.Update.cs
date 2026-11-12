using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuadVitals.Core;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 关于页的「更新和校验」卡：检查 → 下载 → 校验 → 拉起安装程序，一条链路走完。
    ///
    /// 边界：这里不下载运行时、不改设置、不碰 Agent；安装程序自己会停进程和提权。下载与校验
    /// 全在后台线程，界面只接收进度和一句状态，避免 WPF 在等待网络时假死。
    /// </summary>
    internal sealed partial class AboutPage
    {
        /// <summary>进度刷新节流：每 80 ms 一次，够平滑也不会把 UI 线程喂满。</summary>
        private const int ProgressIntervalMs = 80;

        private DispatcherTimer _statusReset;
        private Border _progressFill;
        private Grid _progressTrack;
        private FlatButton _checkButton;
        private FlatButton _cancelButton;

        private bool _updateRunning;
        private UpdateFlow _flow;
        private long _lastProgressTicks;

        /// <summary>
        /// 「更新和校验」卡。静止时只留：当前版本、检查更新按钮、一句说明；两行占位（最新版本、
        /// 安装包校验）与空状态行都不画 —— 它们在没有内容时只是噪声。进度条与状态行在真正
        /// 开始检查后才出现，检查到新版本后不再要第二次点击，因为“检查更新”的语义就是把它装上。
        /// </summary>
        private FrameworkElement BuildUpdateCard()
        {
            Card card = new Card("更新和校验");

            // 版本号与按钮同一行：按钮挂在它要操作的那一行上，而不是孤零零地漂在卡片中间。
            KeyValueRow current = new KeyValueRow("当前版本", ProductVersion.SemVer);
            AutomationProperties.SetAutomationId(current, "AboutUpdateCurrent");

            // 动作占满整行：检查更新是这张卡唯一的主操作，做成通栏比在角落里缩成一小块清楚。
            Grid actions = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 通栏按钮不缩放：hover 时整体放大 5% 会顶到卡片边框。
            _checkButton = new FlatButton("检查更新", true, spring: false);
            _checkButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            _checkButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            AutomationProperties.SetAutomationId(_checkButton, "AboutUpdateCheck");
            _checkButton.Click += (sender, e) => BeginUpdateFlow();
            Grid.SetColumn(_checkButton, 0);
            actions.Children.Add(_checkButton);

            _cancelButton = new FlatButton("取消");
            AutomationProperties.SetAutomationId(_cancelButton, "AboutUpdateCancel");
            _cancelButton.Margin = new Thickness(8, 0, 0, 0);
            _cancelButton.VerticalAlignment = VerticalAlignment.Center;
            _cancelButton.Visibility = Visibility.Collapsed;
            _cancelButton.Click += (sender, e) =>
            {
                if (_flow != null) _flow.Cancel();
                SetStatus("正在取消…", Theme.TextSecondary);
            };
            Grid.SetColumn(_cancelButton, 1);
            actions.Children.Add(_cancelButton);

            actions.Margin = new Thickness(0, 14, 0, 0);

            card.Content.Children.Add(current);
            FrameworkElement changelogEarly = ValueLinkRow("日志明细", "GitHub", OpenChangelog);
            AutomationProperties.SetAutomationId(changelogEarly, "AboutUpdateChangelog");
            card.Content.Children.Add(changelogEarly);
            card.Content.Children.Add(actions);

            _progressTrack = new Grid
            {
                Height = 4,
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(_progressTrack, "AboutUpdateProgress");
            _progressTrack.Children.Add(new Border
            {
                Background = Theme.SurfaceHoverBrush,
                CornerRadius = new CornerRadius(2)
            });
            _progressFill = new Border
            {
                Background = Theme.AccentGreenBrush,
                CornerRadius = new CornerRadius(2),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(0, 1)
            };
            _progressTrack.Children.Add(_progressFill);
            card.Content.Children.Add(_progressTrack);

            return card;
        }

        /// <summary>
        /// Same geometry as <see cref="KeyValueRow"/> - label in the 96 px column, value right after it -
        /// but the value is a green link.
        /// </summary>
        private static FrameworkElement ValueLinkRow(string label, string value, Action onClick)
        {
            Grid row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock caption = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush
            };
            Grid.SetColumn(caption, 0);
            row.Children.Add(caption);

            StackPanel link = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            link.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 12,
                Foreground = Theme.AccentGreenBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            link.Children.Add(new TextBlock
            {
                Text = "\uE8A7",   // Segoe MDL2: open in new window
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = Theme.AccentGreenBrush,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            // A null-background container is not hit-testable, so the click lives on a transparent
            // border that also covers the gap between text and arrow.
            Border hit = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Child = link
            };
            hit.MouseLeftButtonUp += (sender, e) => onClick();
            Grid.SetColumn(hit, 1);
            row.Children.Add(hit);
            return row;
        }

        private void BeginUpdateFlow()
        {
            if (_updateRunning) return;
            _updateRunning = true;
            _lastProgressTicks = 0;

            _checkButton.IsEnabled = false;
            _checkButton.Content = "正在检查…";
            _cancelButton.Visibility = Visibility.Visible;
            SetProgressVisible(false);
            SetStatus("正在检查更新…", Theme.TextSecondary);

            _flow = new UpdateFlow(OnFlowState, null);
            _flow.StartCheck();
        }

        /// <summary>
        /// Renders one flow step on the card. The card is one-click on purpose: finding a newer
        /// version continues straight into the download, so the button never asks for a second
        /// confirmation the owner just gave.
        /// </summary>
        private void OnFlowState(UpdateFlowState state)
        {
            Post(() =>
            {
                switch (state.Phase)
                {
                    case UpdatePhase.Checking:
                        SetStatus("正在检查更新…", Theme.TextSecondary);
                        break;

                    case UpdatePhase.Current:
                        Finish();
                        SetStatus(state.Message, Theme.AccentGreenSoft);
                        break;

                    case UpdatePhase.Available:
                        SetButtonLabel("正在下载…");
                        SetStatus(state.Message, Theme.TextSecondary);
                        SetProgressVisible(true);
                        if (_flow != null) _flow.StartInstall(state.Manifest);
                        break;

                    case UpdatePhase.Downloading:
                        if (state.Progress != null) OnDownloadProgress(state.Progress);
                        break;

                    case UpdatePhase.Verifying:
                        SetButtonLabel("正在校验…");
                        SetStatus(state.Message, Theme.TextSecondary);
                        break;

                    case UpdatePhase.Cancelled:
                        Finish();
                        SetProgressVisible(false);
                        SetStatus(state.Message, Theme.TextSecondary);
                        break;

                    case UpdatePhase.Launching:
                        SetProgress(1.0);
                        SetStatus(state.Message, Theme.AccentGreenSoft);
                        // The installer stops this process itself (it has to run elevated for that);
                        // closing first keeps the two from fighting and makes the hand-over one action.
                        _window.Close();
                        break;

                    case UpdatePhase.Failed:
                        Finish();
                        SetProgressVisible(false);
                        SetStatus(state.Message, Theme.AccentRed);
                        break;
                }
            });
        }

        private void OnDownloadProgress(UpdateProgress progress)
        {
            long now = Stopwatch.GetTimestamp();
            long last = System.Threading.Interlocked.Read(ref _lastProgressTicks);
            long interval = Stopwatch.Frequency * ProgressIntervalMs / 1000;
            if (last != 0 && now - last < interval) return;
            System.Threading.Interlocked.Exchange(ref _lastProgressTicks, now);

            double fraction = progress.Fraction;
            long received = progress.Received;
            long total = progress.Total;
            double speed = progress.BytesPerSecond;
            Post(() =>
            {
                SetProgress(fraction);
                string text;
                if (total > 0)
                {
                    text = "正在下载 " + MetricFormat.Bytes(received) + " / " + MetricFormat.Bytes(total) +
                           "（" + (int)Math.Round(fraction * 100) + "%）· " + MetricFormat.Rate(speed);
                }
                else
                {
                    text = "正在下载 " + MetricFormat.Bytes(received) + " · " + MetricFormat.Rate(speed);
                }
                SetStatus(text, Theme.TextSecondary);
            });
        }

        /// <summary>Restores the buttons once the background work is over (the window may be closing).</summary>
        private void Finish()
        {
            Post(() =>
            {
                _updateRunning = false;
                _checkButton.IsEnabled = true;
                SetButtonLabel("检查更新");
                _cancelButton.Visibility = Visibility.Collapsed;
                SetProgressVisible(false);
            });
        }

        private void Post(Action action)
        {
            try
            {
                Dispatcher dispatcher = _window.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(DispatcherPriority.Background, action);
            }
            catch (Exception)
            {
                // The window can go away mid-flow; a dropped UI update must not crash the process.
            }
        }

        /// <summary>
        /// Status lives on the button, not on a line under it: the card only has room for the data rows
        /// and one action, and a second line of text underneath broke that shape. Whatever the flow says
        /// shows on the button and drops back to the idle label after a few seconds.
        /// </summary>
        private void SetStatus(string text, Color colour)
        {
            if (_checkButton == null) return;
            if (string.IsNullOrEmpty(text))
            {
                StopStatusReset();
                SetButtonLabel(IdleActionLabel);
                return;
            }

            _checkButton.Content = text;
            StartStatusReset();
        }

        private const string IdleActionLabel = "检查更新";

        /// <summary>The button doubles as the phase indicator: its width is fixed by the longest label.</summary>
        private void SetButtonLabel(string text)
        {
            if (_checkButton == null) return;
            _checkButton.Content = text;
        }

        /// <summary>After the outcome has been readable for a while the button goes back to what it does.</summary>
        private void StartStatusReset()
        {
            StopStatusReset();
            _statusReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusReset.Tick += (sender, e) =>
            {
                StopStatusReset();
                if (_updateRunning) return;
                SetButtonLabel(IdleActionLabel);
            };
            _statusReset.Start();
        }

        private void StopStatusReset()
        {
            if (_statusReset == null) return;
            _statusReset.Stop();
            _statusReset = null;
        }

        private void SetProgress(double fraction)
        {
            if (_progressFill == null) return;
            ScaleTransform scale = _progressFill.RenderTransform as ScaleTransform;
            if (scale == null) return;
            double value = fraction;
            if (value < 0) value = 0;
            if (value > 1) value = 1;
            scale.ScaleX = value;
        }

        private void SetProgressVisible(bool visible)
        {
            if (_progressTrack == null) return;
            _progressTrack.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) SetProgress(0);
        }
    }
}
