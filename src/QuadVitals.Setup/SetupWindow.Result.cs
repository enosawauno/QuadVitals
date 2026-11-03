using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuadVitals.UI;

namespace QuadVitals.Setup
{
    /// <summary>
    /// Finish and failure pages: one mark, one heading, one block of small print, and the log of what
    /// the scripts did.
    /// </summary>
    internal sealed partial class SetupWindow
    {
        // ---- result --------------------------------------------------------------------

        private void ShowResult(string installDir)
        {
            ShowPage(BuildResultPage(installDir));
        }

        private void ShowFailure(string error, bool showLog = true)
        {
            ShowPage(BuildFailurePage(error, showLog));
        }

        /// <summary>
        /// Same shape as the finish page (icon → heading → small print, all on one centre axis), but
        /// the mark is a red disc with a cross. The script log only appears when the failure is not
        /// something the user already knows they did.
        /// </summary>
        private UIElement BuildFailurePage(string error, bool showLog = true)
        {
            StackPanel head = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0)
            };

            head.Children.Add(FailureMark());

            head.Children.Add(new TextBlock
            {
                Text = _uninstall ? "卸载失败" : "安装失败",
                Foreground = Theme.TextPrimaryBrush,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            });

            head.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(error)
                    ? (_uninstall ? "卸载没有完成，原因未知" : "安装没有完成，原因未知")
                    : error,
                Foreground = Theme.TextSecondaryBrush,
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 460,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            });

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            // The copy link sits in the button row, at the far left: it belongs to "what can I do now"
            // and it must not take height away from the log above it.
            if (showLog)
            {
                string headline = string.IsNullOrEmpty(error)
                    ? (_uninstall ? "卸载没有完成" : "安装没有完成")
                    : error;
                FlatButton copy = CopyButton(_resultLog, "复制错误信息",
                    () => "QuadVitals " + _version + (_uninstall ? " 卸载失败" : " 安装失败") +
                          Environment.NewLine + headline);
                copy.VerticalAlignment = VerticalAlignment.Center;
                copy.Margin = new Thickness(0, 0, 12, 0);
                buttons.Children.Add(copy);
            }

            // Something survived the elevated stop step: the one useful action is an elevated Task Manager,
            // because a plain one cannot end a process that outranks it.
            if (_core.StopFailed)
            {
                FlatButton taskManager = new FlatButton("打开任务管理器");
                taskManager.MinWidth = 120;
                taskManager.Margin = new Thickness(0, 0, 12, 0);
                System.Windows.Automation.AutomationProperties.SetAutomationId(taskManager, "SetupFailureTaskManager");
                taskManager.Click += (sender, e) => OpenTaskManager();
                buttons.Children.Add(taskManager);
            }

            FlatButton close = new FlatButton("关闭");
            close.MinWidth = 96;
            close.Margin = new Thickness(0, 0, 8, 0);
            System.Windows.Automation.AutomationProperties.SetAutomationId(close, "SetupFailureClose");
            close.Click += (sender, e) => { ExitCode = 1; Close(); };
            buttons.Children.Add(close);

            // Restarts the wizard from the first page. Install failures are rolled back, so a retry
            // starts from a clean slate.
            FlatButton retry = new FlatButton(_uninstall ? "重新卸载" : "重新安装", true);
            retry.MinWidth = 96;
            System.Windows.Automation.AutomationProperties.SetAutomationId(retry, "SetupRetry");
            retry.Click += (sender, e) => { ExitCode = 0; ShowPage(BuildWelcome()); };
            buttons.Children.Add(retry);

            // Three rows so the log can never run into the buttons: header (auto), log (the rest of
            // the space), buttons (auto). A stack panel let the log grow past the button row.
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Red haze while failing: same treatment, opposite meaning.
            TintGlow(Theme.AccentRed);

            grid.Children.Add(head);

            if (showLog)
            {
                _resultLog = new LogView(80);
                _resultLog.Reset(_core.LogText);
                _resultLog.Host.Margin = new Thickness(0, 16, 0, 0);
                _resultLog.Host.MaxWidth = 460;
                _resultLog.Host.HorizontalAlignment = HorizontalAlignment.Center;
                _resultLog.Host.VerticalAlignment = VerticalAlignment.Stretch;
                Grid.SetRow(_resultLog.Host, 1);
                grid.Children.Add(_resultLog.Host);
            }

            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);
            return grid;
        }

        /// <summary>
        /// Opens Task Manager with the UAC shield. Always elevated: the only reason this button exists is a
        /// process that the elevated install script could not end, and a plain Task Manager answers the same
        /// "access denied" the user just came here to escape.
        /// </summary>
        private void OpenTaskManager()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true, Verb = "runas" };
                Process.Start(info);
                _core.Note("已以管理员身份打开任务管理器");
                return;
            }
            catch (Exception)
            {
                // Declined UAC or blocked by policy: say so on the page instead of doing nothing visible.
                _core.Note("未获得管理员权限，任务管理器没有启动");
            }

            ShowFailure("任务管理器没拿到管理员权限，请结束 QuadVitals 进程后点「重新" +
                        (_uninstall ? "卸载" : "安装") + "」", true);
        }

        /// <summary>Red disc with a white round-capped cross: the failure counterpart of the check.</summary>
        private static UIElement FailureMark()
        {
            Canvas canvas = new Canvas
            {
                Width = 58,
                Height = 58,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };

            canvas.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 58,
                Height = 58,
                Fill = Theme.AccentRedBrush
            });

            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 20,20 L 38,38 M 38,20 L 20,38"),
                Stroke = Brushes.White,
                StrokeThickness = 4.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            return canvas;
        }

        private UIElement BuildResultPage(string installDir)
        {
            // Finish page reads top-down and centred: a big check mark says what happened before any
            // words do, the heading names it, the small print explains the last manual step. The small
            // print is justified inside a fixed measure so both edges line up under the centred block.
            StackPanel content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                // Top-aligned rather than centred in the free space: the check mark should sit near
                // the top of the page and leave the air below the small print, not above the icon.
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 0, 0)
            };

            content.Children.Add(SuccessMark());

            content.Children.Add(new TextBlock
            {
                Text = _uninstall ? "卸载完成" : "安装完成",
                Foreground = Theme.TextPrimaryBrush,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 18, 0, 0)
            });

            content.Children.Add(new TextBlock
            {
                Text = _uninstall
                    ? "QuadVitals 已从这台电脑移除"
                    : "已安装到 " + installDir,
                Foreground = Theme.TextSecondaryBrush,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            });

            content.Children.Add(new TextBlock
            {
                Text = _uninstall
                    ? "任务栏上的条目会随 Explorer 刷新而消失"
                    : _bandRestored
                        ? "任务栏条已随资源管理器重新加载\r\n" +
                          "如未显示，右键任务栏>工具栏>勾选QuadVitals"
                        : "请查看任务栏上是否出现仪表盘\r\n" +
                          "如未显示，右键任务栏>工具栏>勾选QuadVitals，若提示「Windows不兼容」，选「是」即可",
                Foreground = Theme.TextTertiaryBrush,
                FontSize = 11.5,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                // Wider than the old 460 so the second hint line stays on one line; a third line
                // would break the "two lines" shape of this block.
                TextAlignment = TextAlignment.Center,
                MaxWidth = 512,
                HorizontalAlignment = HorizontalAlignment.Center,
                // Same gap as the row above: heading -> subtitle -> hint reads as one rhythm.
                Margin = new Thickness(0, 10, 0, 0)
            });


            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // Keep the primary action at the right edge. When there is no dashboard to open (uninstall),
            // "完成" is the only action and stays green.
            FlatButton done = new FlatButton("完成", _uninstall);
            done.MinWidth = 96;
            done.Margin = new Thickness(0, 0, 8, 0);
            done.Click += (sender, e) => Close();
            buttons.Children.Add(done);

            if (!_uninstall)
            {
                FlatButton dashboard = new FlatButton("打开仪表盘", true);
                dashboard.MinWidth = 96;
                // Launch first, then get out of the way: once the work is done this window has nothing left
                // to show, and leaving it on top of the dashboard it just opened reads as "still installing".
                dashboard.Click += (sender, e) =>
                {
                    OpenDashboard(installDir);
                    Close();
                };
                buttons.Children.Add(dashboard);
            }

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(content);
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);
            return grid;
        }

        /// <summary>Filled green disc with a white round-capped check: the page's single focal point.</summary>
        private static UIElement SuccessMark()
        {
            Canvas canvas = new Canvas
            {
                Width = 58,
                Height = 58,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };

            System.Windows.Shapes.Ellipse disc = new System.Windows.Shapes.Ellipse
            {
                Width = 58,
                Height = 58,
                Fill = Theme.AccentGreenBrush
            };
            canvas.Children.Add(disc);

            System.Windows.Shapes.Path tick = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 16,30 L 25,39 L 42,20"),
                Stroke = Brushes.White,
                StrokeThickness = 4.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            canvas.Children.Add(tick);
            return canvas;
        }

        private static void OpenDashboard(string installDir)
        {
            try
            {
                string exe = Path.Combine(installDir, "QuadVitals.Settings.exe");
                if (File.Exists(exe)) Process.Start(exe);
            }
            catch (Exception)
            {
                // The window is already closed by whatever happens here; the file is on disk.
            }
        }
    }
}
