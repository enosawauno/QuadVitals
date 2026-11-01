using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

        private TextBlock _updateStatus;
        private Border _progressFill;
        private Grid _progressTrack;
        private FlatButton _checkButton;
        private FlatButton _cancelButton;

        private bool _updateRunning;
        private volatile bool _updateCancelled;
        private long _lastProgressTicks;

        /// <summary>
        /// 「更新和校验」卡。静止时只留：当前版本、检查更新按钮、一句说明；两行占位（最新版本、
        /// 安装包校验）与空状态行都不画 —— 它们在没有内容时只是噪声。进度条与状态行在真正
        /// 开始检查后才出现，检查到新版本后不再要第二次点击，因为“检查更新”的语义就是把它装上。
        /// </summary>
        private FrameworkElement BuildUpdateCard()
        {
            Card card = new Card("更新和校验");

            KeyValueRow current = new KeyValueRow("当前版本", ProductVersion.SemVer);
            AutomationProperties.SetAutomationId(current, "AboutUpdateCurrent");
            card.Content.Children.Add(current);

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

            // 进度、结果与失败原因共用这一行：空的时候不占位，所以卡里不会留一行“尚未检查更新”。
            _updateStatus = new TextBlock
            {
                Text = string.Empty,
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 10, 0, 0)
            };
            AutomationProperties.SetAutomationId(_updateStatus, "AboutUpdateStatus");
            card.Content.Children.Add(_updateStatus);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };

            _checkButton = new FlatButton("检查更新", true);
            AutomationProperties.SetAutomationId(_checkButton, "AboutUpdateCheck");
            _checkButton.Click += (sender, e) => BeginUpdateFlow();
            buttons.Children.Add(_checkButton);

            _cancelButton = new FlatButton("取消");
            AutomationProperties.SetAutomationId(_cancelButton, "AboutUpdateCancel");
            _cancelButton.Margin = new Thickness(8, 0, 0, 0);
            _cancelButton.Visibility = Visibility.Collapsed;
            _cancelButton.Click += (sender, e) =>
            {
                _updateCancelled = true;
                SetStatus("正在取消…", Theme.TextSecondary);
            };
            buttons.Children.Add(_cancelButton);

            card.Content.Children.Add(buttons);
            card.Content.Children.Add(new TextBlock
            {
                Text = "更新包从 GitHub Releases 获取；下载完成后按发布清单校验 SHA-256，通过才会启动安装程序。",
                FontSize = 11.5,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });
            return card;
        }

        private void BeginUpdateFlow()
        {
            if (_updateRunning) return;
            _updateRunning = true;
            _updateCancelled = false;
            _lastProgressTicks = 0;

            _checkButton.IsEnabled = false;
            _checkButton.Content = "正在检查…";
            _cancelButton.Visibility = Visibility.Visible;
            SetProgressVisible(false);
            SetStatus("正在检查更新…", Theme.TextSecondary);

            Task.Run((Action)RunUpdateFlow);
        }

        /// <summary>Background half of the flow. Every UI touch goes through <see cref="Post"/>.</summary>
        private void RunUpdateFlow()
        {
            string baseUrl = UpdateFeed.BaseUrl();
            IUpdateTransport transport = new HttpUpdateTransport();

            UpdateCheckOutcome outcome = UpdateChecker.Check(transport, baseUrl, ProductVersion.SemVer);
            if (outcome.Status == UpdateCheckStatus.Failed)
            {
                Finish();
                Post(() => SetStatus("无法检查更新：" + DescribeFailure(outcome.Error), Theme.AccentRed));
                return;
            }

            UpdateManifest manifest = outcome.Manifest;

            if (outcome.Status == UpdateCheckStatus.Current)
            {
                Finish();
                Post(() => SetStatus("已是最新版本（" + ProductVersion.SemVer + "）", Theme.AccentGreenSoft));
                return;
            }

            Post(() =>
            {
                SetButtonLabel("正在下载…");
                SetStatus("发现新版本 " + outcome.LatestVersion + "，正在下载…", Theme.TextSecondary);
            });

            string destination = UpdateInstaller.InstallerPath(manifest);
            string url;
            try
            {
                url = UpdateFeed.InstallerUrl(baseUrl, manifest.SetupName);
            }
            catch (ArgumentException)
            {
                Finish();
                Post(() => SetStatus("更新清单里的安装包名不可用。", Theme.AccentRed));
                return;
            }

            Post(() => SetProgressVisible(true));
            UpdateDownloadResult download = UpdateDownloader.Download(
                transport, url, destination, OnDownloadProgress, () => _updateCancelled, null);

            if (download.Cancelled)
            {
                Finish();
                Post(() =>
                {
                    SetProgressVisible(false);
                    SetStatus("已取消更新", Theme.TextSecondary);
                });
                return;
            }
            if (!download.Ok)
            {
                Finish();
                Post(() =>
                {
                    SetProgressVisible(false);
                    SetStatus("下载失败：" + DescribeDownloadFailure(download.Error), Theme.AccentRed);
                });
                return;
            }

            Post(() =>
            {
                SetButtonLabel("正在校验…");
                SetStatus("正在校验安装包…", Theme.TextSecondary);
            });

            string actualHash;
            string verifyError;
            bool verified = UpdateInstaller.Verify(manifest, destination, out actualHash, out verifyError);
            if (!verified)
            {
                DeleteQuietly(destination);
                Finish();
                Post(() =>
                {
                    SetProgressVisible(false);
                    SetStatus("安装包校验未通过（" + DescribeVerifyFailure(verifyError) + "），文件已删除。", Theme.AccentRed);
                });
                return;
            }

            Post(() =>
            {
                SetProgress(1.0);
                SetStatus("安装包校验通过（" + ShortHash(actualHash) + "），正在启动安装程序…", Theme.AccentGreenSoft);
            });

            string launchError;
            if (!UpdateInstaller.Launch(destination, out launchError))
            {
                Finish();
                Post(() => SetStatus("无法启动安装程序：" + DescribeFailure(launchError), Theme.AccentRed));
                return;
            }

            // The installer stops this process itself (it has to run elevated for that); closing
            // first keeps the two from fighting and makes the hand-over look like one action.
            Post(() => _window.Close());
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

        private void SetStatus(string text, Color colour)
        {
            if (_updateStatus == null) return;
            _updateStatus.Text = text;
            _updateStatus.Foreground = new SolidColorBrush(colour);
            // The line has no place in the idle card: it only exists while there is something to say,
            // and it keeps the final outcome after the flow ends.
            _updateStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>The button doubles as the phase indicator: its width is fixed by the longest label.</summary>
        private void SetButtonLabel(string text)
        {
            if (_checkButton == null) return;
            _checkButton.Content = text;
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

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "—";
            string text = hash.ToLowerInvariant();
            return text.Length <= 12 ? text : text.Substring(0, 12) + "…";
        }

        /// <summary>Turns transport/parse codes into one readable sentence. The raw code is never shown.</summary>
        private static string DescribeFailure(string error)
        {
            if (string.IsNullOrEmpty(error)) return "暂时无法检查更新";
            switch (error)
            {
                case "Timeout": return "连接超时";
                case "NameResolutionFailure": return "无法解析更新源域名";
                case "ConnectFailure": return "无法连接更新源";
                case "TrustFailure":
                case "SecureChannelFailure": return "更新源的证书校验失败";
                case "ProtocolError": return "更新源返回错误（可能尚未发布正式版本）";
                case "manifest-invalid-json":
                case "manifest-empty":
                case "manifest-bad-version":
                case "manifest-no-setup":
                case "manifest-bad-name":
                case "manifest-bad-size":
                case "manifest-bad-hash": return "更新清单格式不正确";
                case "manifest-too-large": return "更新清单体积异常";
                case "installer-missing": return "安装程序不存在";
                default: return "暂时无法检查更新";
            }
        }

        private static string DescribeDownloadFailure(string error)
        {
            if (string.IsNullOrEmpty(error)) return "未知原因";
            if (error.StartsWith("download-failed", StringComparison.Ordinal)) return "连接中断";
            switch (error)
            {
                case "too-large": return "安装包体积异常";
                case "short-read": return "下载不完整";
                default: return DescribeFailure(error);
            }
        }

        private static string DescribeVerifyFailure(string error)
        {
            switch (error)
            {
                case "size-mismatch": return "大小与清单不符";
                case "hash-mismatch": return "SHA-256 与清单不符";
                case "installer-missing": return "安装包不存在";
                default: return "无法读取安装包";
            }
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (Exception)
            {
                // A rejected package that stays on disk is never launched: the name is only ever
                // reused after a fresh download overwrites it.
            }
        }
    }
}
