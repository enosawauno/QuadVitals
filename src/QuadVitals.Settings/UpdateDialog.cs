using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QuadVitals.Core;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 发现新版本后的唯一界面：版本号、更新时间、更新内容，然后是下载、校验、交给安装向导。
    ///
    /// 为什么守在“打开仪表盘”这一刻：常驻进程没有第二次启动，弹窗只能出现在有人看的时候。
    /// 弹窗不负责检查（检查由 UpdateAutoCheck 触发），也不负责安装（安装向导自己提权、停进程），
    /// 它只把流程的每一步画出来，并且在关闭时取消正在进行的下载。
    /// </summary>
    internal sealed class UpdateDialog
    {
        private readonly SettingsWindow _window;
        private readonly UpdateManifest _manifest;
        private readonly Action _onSkipped;

        private UpdateFlow _flow;
        private bool _closed;

        private TextBlock _title;
        private TextBlock _date;
        private TextBlock _status;
        private ScrollViewer _notesScroll;
        private StackPanel _notesList;
        private Grid _progressTrack;
        private Border _progressFill;
        private StackPanel _actions;
        private FlatButton _install;
        private FlatButton _later;
        private FlatButton _retry;
        private FlatButton _dismiss;
        private Border _close;

        public UpdateDialog(SettingsWindow window, UpdateManifest manifest, Action onSkipped)
        {
            _window = window;
            _manifest = manifest;
            _onSkipped = onSkipped;
        }

        /// <summary>Opens the dialog for a manifest that was just fetched.</summary>
        public static void Present(SettingsWindow window, UpdateManifest manifest, Action onSkipped)
        {
            if (window == null || manifest == null) return;
            UpdateDialog dialog = new UpdateDialog(window, manifest, onSkipped);
            window.ShowOverlay(dialog.Build());
            window.OverlayClosed += dialog.OnOverlayClosed;
        }

        private FrameworkElement Build()
        {
            StackPanel content = new StackPanel();

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel heading = new StackPanel();
            _title = new TextBlock
            {
                Text = "发现新版本 v" + _manifest.Version,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(_title, "UpdateDialogTitle");
            heading.Children.Add(_title);

            _date = new TextBlock
            {
                Text = _manifest.PublishedAtUtc == DateTime.MinValue
                    ? string.Empty
                    : "更新时间：" + _manifest.PublishedAtUtc.ToLocalTime()
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = _manifest.PublishedAtUtc == DateTime.MinValue ? Visibility.Collapsed : Visibility.Visible
            };
            AutomationProperties.SetAutomationId(_date, "UpdateDialogDate");
            heading.Children.Add(_date);
            Grid.SetColumn(heading, 0);
            header.Children.Add(heading);

            _close = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                // 图标字体在 12 px 下单线只有 1 px，深底上看着发虚；改用两笔圆头线段。
                Child = CloseGlyph()
            };
            AutomationProperties.SetAutomationId(_close, "UpdateDialogClose");
            _close.MouseLeftButtonUp += (sender, e) => DismissByUser();
            Grid.SetColumn(_close, 1);
            header.Children.Add(_close);

            content.Children.Add(header);

            _notesList = new StackPanel();
            foreach (string note in NotesForDisplay(_manifest))
            {
                _notesList.Children.Add(NoteRow(note));
            }
            _notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _notesList
            };
            AutomationProperties.SetAutomationId(_notesScroll, "UpdateDialogNotes");

            Border notesBox = new Border
            {
                Background = Theme.BgBaseBrush,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 18, 0, 0),
                // 更新内容是这张卡的主体：固定高度把中间整块留给它，条数少也不向下收，
                // 多则框内滚动；发现态与下载态因此保持同一高度，底部行不跳。
                Height = 240,
                Child = _notesScroll
            };
            content.Children.Add(notesBox);

            _status = new TextBlock
            {
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 20, 0, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(_status, "UpdateDialogStatus");
            content.Children.Add(_status);

            _progressTrack = new Grid
            {
                Height = 4,
                Margin = new Thickness(0, 12, 0, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(_progressTrack, "UpdateDialogProgress");
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
            AutomationProperties.SetAutomationId(_progressFill, "UpdateDialogProgressFill");
            _progressTrack.Children.Add(_progressFill);
            content.Children.Add(_progressTrack);

            _actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // 主操作落在右下角：和系统对话框、安装向导的按钮位置一致。
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            _later = ActionButton("再等等", false, "UpdateDialogLater");
            _later.Click += (sender, e) => DismissByUser(skipVersion: true);
            _actions.Children.Add(_later);

            _install = ActionButton("我要升级", true, "UpdateDialogInstall");
            _install.Margin = new Thickness(10, 0, 0, 0);
            _install.Click += (sender, e) => BeginInstall();
            _actions.Children.Add(_install);

            _dismiss = ActionButton("关闭", false, "UpdateDialogDismiss");
            _dismiss.Visibility = Visibility.Collapsed;
            _dismiss.Click += (sender, e) => DismissByUser();
            _actions.Children.Add(_dismiss);

            _retry = ActionButton("重试", true, "UpdateDialogRetry");
            _retry.Margin = new Thickness(10, 0, 0, 0);
            _retry.Visibility = Visibility.Collapsed;
            _retry.Click += (sender, e) => BeginInstall();
            _actions.Children.Add(_retry);

            content.Children.Add(_actions);

            return new Border
            {
                Width = NoticeDialog.PanelWidth,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                // 上边距比其余三边少 4：标题贴得紧一点，底部留给按钮行的呼吸不变。
                Padding = new Thickness(24, 20, 24, 24),
                SnapsToDevicePixels = true,
                Child = content
            };
        }

        private static FrameworkElement CloseGlyph()
        {
            Grid glyph = new Grid { Width = 12, Height = 12 };
            glyph.Children.Add(new Line
            {
                X1 = 1.5, Y1 = 1.5, X2 = 10.5, Y2 = 10.5,
                Stroke = Theme.TextSecondaryBrush,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            glyph.Children.Add(new Line
            {
                X1 = 10.5, Y1 = 1.5, X2 = 1.5, Y2 = 10.5,
                Stroke = Theme.TextSecondaryBrush,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            return glyph;
        }

        private static IEnumerable<string> NotesForDisplay(UpdateManifest manifest)
        {
            if (manifest.Notes == null || manifest.Notes.Length == 0)
            {
                yield return "本次更新没有提供说明。";
                yield break;
            }
            foreach (string note in manifest.Notes)
            {
                if (!string.IsNullOrEmpty(note)) yield return note;
            }
        }

        private static FrameworkElement NoteRow(string note)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Ellipse dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = Theme.AccentGreenBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 10, 0)
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            TextBlock text = new TextBlock
            {
                Text = note,
                FontSize = 12.5,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.TextSecondaryBrush
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            return row;
        }

        private static FlatButton ActionButton(string text, bool primary, string automationId)
        {
            FlatButton button = new FlatButton(text, primary, spring: false);
            button.MinWidth = 120;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            AutomationProperties.SetAutomationId(button, automationId);
            return button;
        }

        private void BeginInstall()
        {
            if (_closed) return;
            if (_flow != null && _flow.IsRunning) return;

            _install.Visibility = Visibility.Collapsed;
            _later.Visibility = Visibility.Collapsed;
            _retry.Visibility = Visibility.Collapsed;
            _dismiss.Visibility = Visibility.Collapsed;
            // 下载态没有按钮：整行收起，状态与进度条直接贴到卡片底部（否则那一行的上边距
            // 会在进度条下方留出一截空白，看起来像没贴底）。
            if (_actions != null) _actions.Visibility = Visibility.Collapsed;
            _close.Visibility = Visibility.Visible;
            ShowStatus("正在准备下载…", false);
            ShowProgress(true);
            SetProgress(0);

#if QV_DEV_TOOLS
            if (_preview)
            {
                RunPreviewDownload();
                return;
            }
#endif

            _flow = new UpdateFlow(OnFlowState, null);
            _flow.StartInstall(_manifest);
        }

        private void OnFlowState(UpdateFlowState state)
        {
            Dispatcher dispatcher = _window.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(() => Render(state)));
        }

        private void Render(UpdateFlowState state)
        {
            if (_closed) return;

            switch (state.Phase)
            {
                case UpdatePhase.Downloading:
                    if (state.Progress != null)
                    {
                        SetProgress(state.Progress.Fraction);
                        ShowStatus(DescribeProgress(state.Progress), false);
                    }
                    else
                    {
                        ShowStatus("正在下载…", false);
                    }
                    break;

                case UpdatePhase.Verifying:
                    SetProgress(1.0);
                    ShowStatus("正在校验安装包…", false);
                    break;

                case UpdatePhase.Launching:
                    SetProgress(1.0);
                    ShowStatus(state.Message, false);
                    Close();
                    break;

                case UpdatePhase.Cancelled:
                    Close();
                    break;

                case UpdatePhase.Failed:
                    if (_actions != null) _actions.Visibility = Visibility.Visible;
                    ShowStatus(state.Message, true);
                    _retry.Visibility = Visibility.Visible;
                    _dismiss.Visibility = Visibility.Visible;
                    _close.Visibility = Visibility.Visible;
                    break;
            }
        }

        private static string DescribeProgress(UpdateProgress progress)
        {
            long received = progress.Received;
            long total = progress.Total;
            if (total > 0)
            {
                int percent = (int)Math.Round(progress.Fraction * 100);
                return "正在下载 " + MetricFormat.Bytes(received) + " / " + MetricFormat.Bytes(total) +
                       "（" + percent + "%）· " + MetricFormat.Rate(progress.BytesPerSecond);
            }
            return "正在下载 " + MetricFormat.Bytes(received) + " · " + MetricFormat.Rate(progress.BytesPerSecond);
        }

        private void ShowStatus(string text, bool error)
        {
            if (_status == null) return;
            _status.Text = text;
            _status.Foreground = error ? Theme.AccentRedBrush : Theme.TextTertiaryBrush;
            _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowProgress(bool visible)
        {
            if (_progressTrack == null) return;
            _progressTrack.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) SetProgress(0);
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

        /// <summary>
        /// 右上角关闭与「再等等」的共同出口。下载中点关闭等于取消：半成品由下载器删除，
        /// 不会留下一个只能手动运行的包。
        /// </summary>
        private void DismissByUser(bool skipVersion = false)
        {
            if (_closed) return;

            if (skipVersion && _onSkipped != null)
            {
                try { _onSkipped(); }
                catch (Exception) { /* remembering the skip is best effort */ }
            }

            if (_flow != null && _flow.IsRunning) _flow.Cancel();
            Close();
        }

        /// <summary>Overlay closed by Esc or by another panel taking the layer: cancel the transfer.</summary>
        private void OnOverlayClosed()
        {
            if (_closed) return;
            _closed = true;
            _window.OverlayClosed -= OnOverlayClosed;
            if (_flow != null && _flow.IsRunning) _flow.Cancel();
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            _window.OverlayClosed -= OnOverlayClosed;
            _window.CloseOverlay();
        }

#if QV_DEV_TOOLS
        /// <summary>
        /// Screenshot-only entry: a synthetic manifest and a simulated transfer, so the dialog can
        /// be checked without publishing a release. Compiled out of release builds.
        /// </summary>
        public static void ShowPreview(SettingsWindow window)
        {
            UpdateManifest manifest = new UpdateManifest
            {
                Version = "1.0.1",
                PublishedAtUtc = DateTime.UtcNow,
                Notes = new[]
                {
                    "新增打开仪表盘时检查更新",
                    "新增更新弹窗，下载与校验在同一步完成",
                    "优化任务栏条在高负载下的刷新稳定性",
                    "修复风扇通道在驱动重置后不再重连"
                },
                SetupName = "QuadVitals-Setup-1.0.1.exe",
                SetupSize = 1700000,
                SetupSha256 = new string('a', 64)
            };
            UpdateDialog dialog = new UpdateDialog(window, manifest, null);
            window.ShowOverlay(dialog.Build());
            window.OverlayClosed += dialog.OnOverlayClosed;
            dialog._preview = true;
        }

        private bool _preview;
        private DispatcherTimerAdapter _previewTimer;

        /// <summary>Simulated transfer for the preview build; the real flow replaces it on click.</summary>
        private void RunPreviewDownload()
        {
            double fraction = 0;
            _previewTimer = new DispatcherTimerAdapter(TimeSpan.FromMilliseconds(60), () =>
            {
                fraction += 0.035;
                if (fraction >= 1.0)
                {
                    if (_previewTimer != null) _previewTimer.Stop();
                    _previewTimer = null;
                    SetProgress(1.0);
                    ShowStatus("正在校验安装包…", false);
                    Dispatcher dispatcher = _window.Dispatcher;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        ShowStatus("安装包校验通过（预览），正在启动安装程序…", false);
                    }));
                    return;
                }
                SetProgress(fraction);
                ShowStatus("正在下载 " + MetricFormat.Bytes((long)(1700000 * fraction)) + " / " +
                           MetricFormat.Bytes(1700000) + "（" + (int)(fraction * 100) + "%）· " +
                           MetricFormat.Rate(860 * 1024), false);
            });
            _previewTimer.Start();
        }

        private sealed class DispatcherTimerAdapter
        {
            private readonly System.Windows.Threading.DispatcherTimer _timer;

            public DispatcherTimerAdapter(TimeSpan interval, Action tick)
            {
                _timer = new System.Windows.Threading.DispatcherTimer { Interval = interval };
                _timer.Tick += (sender, e) => tick();
            }

            public void Start() { _timer.Start(); }
            public void Stop() { _timer.Stop(); }
        }
#endif
    }
}
