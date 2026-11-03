using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuadVitals.Core;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// The submitted state of the feedback form: the tick, the headline, the two short lines and the
    /// countdown that closes the panel. Kept apart from the form itself - it shares nothing with the
    /// draft/attachment/upload plumbing except the fields declared here.
    /// </summary>
    internal sealed partial class FeedbackPage
    {
        private readonly FlatButton _retry = new FlatButton("重新上传");
        private readonly TextBlock _successUpload = new TextBlock();
        private readonly ContentControl _resultMark = new ContentControl();
        private readonly TextBlock _resultHeadline = new TextBlock();
        private readonly TextBlock _failureReason = new TextBlock();
        private readonly TextBlock _failurePath = new TextBlock();


        private StackPanel BuildResultBody()
        {
            // 成功页照安装器的完成页：绿色圆盘打勾 → 一句结论 → 下面两三行小字。
            // 不放按钮：关闭走面板右下角那个，再来一条就是关掉重开（草稿在提交时已清）。
            StackPanel content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _resultMark.Content = SuccessMark();
            _resultMark.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(_resultMark);

            _resultHeadline.Text = "反馈已发送成功";
            _resultHeadline.FontSize = 18;
            _resultHeadline.FontWeight = FontWeights.SemiBold;
            _resultHeadline.Foreground = Theme.TextPrimaryBrush;
            _resultHeadline.TextAlignment = TextAlignment.Center;
            _resultHeadline.HorizontalAlignment = HorizontalAlignment.Center;
            _resultHeadline.Margin = new Thickness(0, 16, 0, 0);
            content.Children.Add(_resultHeadline);

            _successId.FontSize = 12.5;
            _successId.Foreground = Theme.TextSecondaryBrush;
            _successId.HorizontalAlignment = HorizontalAlignment.Center;
            _successId.Margin = new Thickness(0, 14, 0, 0);
            content.Children.Add(_successId);

            _successUpload.FontSize = 11.5;
            _successUpload.TextWrapping = TextWrapping.Wrap;
            _successUpload.TextAlignment = TextAlignment.Center;
            _successUpload.HorizontalAlignment = HorizontalAlignment.Center;
            _successUpload.Margin = new Thickness(0, 6, 0, 0);
            content.Children.Add(_successUpload);

            _failureReason.FontSize = 11.5;
            _failureReason.Foreground = Theme.TextSecondaryBrush;
            _failureReason.TextWrapping = TextWrapping.Wrap;
            _failureReason.TextAlignment = TextAlignment.Center;
            _failureReason.HorizontalAlignment = HorizontalAlignment.Center;
            _failureReason.Margin = new Thickness(0, 6, 0, 0);
            _failureReason.Visibility = Visibility.Collapsed;
            content.Children.Add(_failureReason);

            _failurePath.FontSize = 11;
            _failurePath.Foreground = Theme.TextTertiaryBrush;
            _failurePath.TextWrapping = TextWrapping.Wrap;
            _failurePath.TextAlignment = TextAlignment.Center;
            _failurePath.HorizontalAlignment = HorizontalAlignment.Center;
            _failurePath.Margin = new Thickness(0, 6, 0, 0);
            _failurePath.Visibility = Visibility.Collapsed;
            content.Children.Add(_failurePath);

            return content;
        }

        /// <summary>
        /// Counts the result panel down and closes it. Five seconds is long enough to read the id and the
        /// upload line, short enough that a panel nobody needs to act on does not sit there.
        /// </summary>
        private void StartAutoClose()
        {
            StopAutoClose();
            _autoCloseSeconds = 5;
            if (_closeResult != null) _closeResult.Content = _autoCloseSeconds + "秒后自动关闭";

            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoCloseTimer.Tick += (sender, e) =>
            {
                if (_autoCloseSeconds > 0) _autoCloseSeconds--;
                if (_closeResult != null) _closeResult.Content = _autoCloseSeconds + "秒后自动关闭";
                if (_autoCloseSeconds > 0) return;

                // 让 0 先显示一会儿再收，否则最后一秒看起来像直接消失。
                StopAutoClose();
                Dispatcher.BeginInvoke(new Action(() => _window.CloseOverlay()),
                    DispatcherPriority.ApplicationIdle);
            };
            _autoCloseTimer.Start();
        }

        private void StopAutoClose()
        {
            if (_autoCloseTimer == null) return;
            _autoCloseTimer.Stop();
            _autoCloseTimer = null;
        }

        /// <summary>Green disc with a white tick, same shape as the installer's finish page.</summary>
        private static UIElement SuccessMark()
        {
            Canvas canvas = new Canvas
            {
                Width = 52,
                Height = 52,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            System.Windows.Shapes.Ellipse disc = new System.Windows.Shapes.Ellipse
            {
                Width = 52,
                Height = 52,
                Fill = Theme.AccentGreenBrush
            };
            canvas.Children.Add(disc);

            System.Windows.Shapes.Path tick = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 14,27 L 22,35 L 38,18"),
                Stroke = Brushes.White,
                StrokeThickness = 4.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            canvas.Children.Add(tick);
            return canvas;
        }


        private void ShowSuccess(FeedbackResult result)
        {
            _successId.Text = "编号：" + result.Id + "　·　" + result.AttachmentCount + " 个附件";
            _successUpload.Text = "准备上传…";
            _successUpload.Foreground = Theme.TextSecondaryBrush;
            _retry.Visibility = Visibility.Collapsed;

            // 同一块区域原地变成结果：表单收起，结果顶上来；底部只剩一个关闭。
            ShowingResult = true;
            // 标题行保持「反馈」：结论已经由成功页的大标题说了，两处都写就成了同义反复。
            SetHeading("反馈");
            _formBody.Visibility = Visibility.Collapsed;
            if (_logBlock != null) _logBlock.Visibility = Visibility.Collapsed;
            if (_clear != null) _clear.Visibility = Visibility.Collapsed;
            _addFiles.Visibility = Visibility.Collapsed;
            _submit.Visibility = Visibility.Collapsed;
            if (_closeResult != null) _closeResult.Visibility = Visibility.Visible;
            _resultBody.Visibility = Visibility.Visible;
            StartAutoClose();
            FadeIn(_resultBody);

            if (IsPreviewOpen)
            {
                _previewPanel.Visibility = Visibility.Collapsed;
                _previewButton.Content = "查看将包含的信息";
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_resultBody != null) _resultBody.BringIntoView();
            }), DispatcherPriority.Background);
        }

        /// <summary>
        /// Best-effort upload. The local zip already exists, so a failure only changes
        /// the wording on the success card and reveals the retry button.
        /// </summary>
        private async Task RunUploadAsync()
        {
            if (_lastRequest == null) return;

            _retry.IsEnabled = false;
            _retry.Visibility = Visibility.Collapsed;
            _successUpload.Text = "正在上传到 quadvitals.enosawa.uno …";
            _successUpload.Foreground = Theme.TextSecondaryBrush;

            FeedbackUploadResult upload;
            try
            {
                FeedbackUploadRequest request = _lastRequest;
                upload = await Task.Run(() => FeedbackUploader.Upload(request, _transport));
            }
            catch (Exception ex)
            {
                upload = new FeedbackUploadResult { Error = "上传失败：" + ex.GetType().Name };
            }

            _retry.IsEnabled = true;
            if (upload.Ok)
            {
                // 台账先记成功，再删本地包：删失败时下次启动按 "uploaded" 只删不重传。
                if (_lastResult != null) FeedbackStore.AppendUploaded(DataDirectory, _lastResult.Id, upload.FeedbackId);
                bool removed = false;
                try
                {
                    string packagePath = _lastResult == null ? null : _lastResult.PackagePath;
                    if (!string.IsNullOrEmpty(packagePath) && File.Exists(packagePath))
                    {
                        File.Delete(packagePath);
                        removed = true;
                    }
                }
                catch (Exception)
                {
                    // 删不掉就留着，启动时再删
                }

                CleanupUploadTemp();
                _successUpload.Text = "已上传，服务端编号 " + upload.FeedbackId;
                _successUpload.Foreground = Theme.AccentGreenBrush;
                _resultMark.Content = SuccessMark();
                _resultHeadline.Text = "反馈已发送成功";
                _failureReason.Visibility = Visibility.Collapsed;
                _failurePath.Visibility = Visibility.Collapsed;
                return;
            }

            if (_lastResult != null) FeedbackStore.AppendUploadFailed(DataDirectory, _lastResult.Id, upload.Error);
            ShowUploadFailure(upload.Error);
        }

        /// <summary>
        /// Upload failed: the local package stays, so the panel has to stay too. The countdown is
        /// stopped here - it exists to dismiss a finished result, not to hide the only retry button.
        /// </summary>
        private void ShowUploadFailure(string reason)
        {
            // A failed upload is not a success with a red footnote: the mark and the headline change
            // too, and the reason/path move to their own lines so neither gets squeezed.
            StopAutoClose();
            if (_closeResult != null) _closeResult.Content = "关闭";
            _resultMark.Content = FailureMark();
            _resultHeadline.Text = "上传未完成，反馈已保存到本机";

            _successUpload.Text = "本地包已生成，可重新上传";
            _successUpload.Foreground = Theme.TextSecondaryBrush;

            _failureReason.Text = "原因：" + reason;
            _failureReason.Visibility = Visibility.Visible;

            if (_lastResult != null)
            {
                _failurePath.Text = "文件：" + _lastResult.PackagePath;
                _failurePath.Visibility = Visibility.Visible;
            }
            _retry.Visibility = Visibility.Visible;
        }

        /// <summary>Red disc with a white exclamation mark: the package is fine, only the upload is not.</summary>
        private static UIElement FailureMark()
        {
            Canvas canvas = new Canvas
            {
                Width = 52,
                Height = 52,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            System.Windows.Shapes.Ellipse disc = new System.Windows.Shapes.Ellipse
            {
                Width = 52,
                Height = 52,
                Fill = Theme.AccentRedBrush
            };
            canvas.Children.Add(disc);

            TextBlock bang = new TextBlock
            {
                Text = "!",
                FontSize = 30,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Width = 52,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            canvas.Children.Add(bang);
            return canvas;
        }
    }
}
