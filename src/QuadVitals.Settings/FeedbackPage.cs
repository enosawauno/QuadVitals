using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Feedback page: type + text + attachments + optional diagnostics,
    /// previewed before submit, packaged locally as a zip, then uploaded. Upload is best effort:
    /// a failure keeps the local package and offers a manual retry (no background queue).
    /// </summary>
    internal sealed class FeedbackPage : UserControl, IRefreshable
    {
        private readonly SettingsWindow _window;
        private readonly FeedbackOptions _options = new FeedbackOptions();
        private readonly FeedbackKind[] _kinds = { FeedbackKind.Bug, FeedbackKind.Feature, FeedbackKind.Other };
        private readonly List<FeedbackDraftAttachment> _attachments = new List<FeedbackDraftAttachment>();

        // WPF 的 TextBoxView 在左侧给光标留 2 px：同一个内边距下，输入文字第一格的起点
        // 总比「边框 + 2×内边距」多 2。提示层要跟上这个偏移，否则会和输入文字错开。
        private const double HintCaretInset = 2;

        private readonly ComboBox _kind = new DarkComboBox();
        private readonly DarkTextBox _text = new DarkTextBox();
        private readonly DarkCheckBox _includeLogs = new DarkCheckBox();
        private readonly StackPanel _attachmentRows = new StackPanel();
        private readonly TextBlock _attachmentHint = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly FlatButton _submit = new FlatButton("提交反馈", true);
        private readonly FlatButton _addFiles = new FlatButton("添加文件");
        private readonly FlatButton _previewButton = new FlatButton("查看将包含的信息");
        private readonly StackPanel _previewPanel = new StackPanel();
        private readonly DarkTextBox _previewBox = new DarkTextBox();
        // 反馈页只有一张卡片：_formBody 装表单，_resultBody 装提交结果，两者互斥显示。
        private Card _card;
        private StackPanel _formBody;
        private readonly TextBlock _placeholder = new TextBlock();
        private StackPanel _resultBody;
        private readonly TextBlock _successId = new TextBlock();
        private readonly TextBlock _successPath = new TextBlock();
        private readonly DispatcherTimer _draftTimer;
        private readonly DispatcherTimer _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(StatusHoldMs)
        };
        private const int StatusHoldMs = 4500;
        private const int StatusFadeMs = 500;
        private ScrollViewer _scroll;

        private bool _filling;
        private bool _submitting;
        private FeedbackResult _lastResult;
        private FeedbackUploadRequest _lastRequest;
        private string _uploadTempDirectory;
        private readonly IFeedbackTransport _transport = new HttpFeedbackTransport();
        private readonly FlatButton _retry = new FlatButton("重新上传");
        private readonly TextBlock _successUpload = new TextBlock();
        private readonly bool _compact;

        public FeedbackPage(SettingsWindow window) : this(window, false) { }

        /// <param name="compact">
        /// True when the form is hosted in the title-bar panel instead of a page: the panel supplies the
        /// frame and the title, so the page heading and the page margins would only waste room.
        /// </param>
        public FeedbackPage(SettingsWindow window, bool compact)
        {
            _window = window;
            _compact = compact;

            Content = BuildLayout();
            LoadDraft();

            // reference 对齐：正文与附件同一个区域，截图 Ctrl+V 直接贴进来，文件拖进来也一样。
            // TextBox 自带一套“把文本拖进来”的拖放处理：它在类处理器里先把 DragOver 标成 Handled
            // 并把效果改成 None，Drop 也走它自己的编辑路径。所以文件拖放只能挂在隧道的 Preview
            // 事件上，并在处理完后标记 Handled，避免再落到编辑器的 drop 逻辑里。
            _text.AllowDrop = true;
            _text.PreviewDragOver += OnTextDragOver;
            _text.PreviewDragLeave += (sender, e) => _text.BorderBrush = Theme.BorderSubtleBrush;
            _text.PreviewDrop += OnTextDrop;
            DataObject.AddPastingHandler(_text, OnTextPaste);
            // TextBox 的 Paste 命令在剪贴板只有图片/文件时是禁用的（它只认文本），
            // 所以 Ctrl+V 必须自己先拦一道，否则截图根本贴不进来。
            _text.PreviewKeyDown += OnTextPreviewKeyDown;

            // Stable hooks for UI tests / accessibility (ASCII ids, names come from content).
            AutomationProperties.SetAutomationId(_text, "FeedbackText");
            AutomationProperties.SetAutomationId(_submit, "FeedbackSubmit");
            AutomationProperties.SetAutomationId(_addFiles, "FeedbackAddFiles");
            AutomationProperties.SetAutomationId(_includeLogs, "FeedbackIncludeLogs");
            AutomationProperties.SetAutomationId(_previewButton, "FeedbackPreview");

            _statusTimer.Tick += (sender, e) => OnStatusTimeout();

            _draftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _draftTimer.Tick += (sender, e) =>
            {
                _draftTimer.Stop();
                SaveDraftNow();
            };

            _text.TextChanged += (sender, e) =>
            {
                UpdatePlaceholder();
                ScheduleDraft();
            };
            _kind.SelectionChanged += (sender, e) => ScheduleDraft();
            _includeLogs.Checked += (sender, e) => { ScheduleDraft(); if (IsPreviewOpen) UpdatePreview(); };
            _includeLogs.Unchecked += (sender, e) => { ScheduleDraft(); if (IsPreviewOpen) UpdatePreview(); };

            Unloaded += (sender, e) =>
            {
                SaveDraftNow();
                ResetResultOnLeave();
            };
            Loaded += (sender, e) => Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_scroll != null) _scroll.ScrollToTop();
                    UpdateAgentPill();
                }),
                DispatcherPriority.Loaded);
        }

        private static string DataDirectory
        {
            get { return Path.GetDirectoryName(AppSettings.DefaultPath); }
        }

        private static string AppVersion
        {
            get { return ProductVersion.SemVer; }
        }

        private bool IsPreviewOpen
        {
            get { return _previewPanel.Visibility == Visibility.Visible; }
        }

        // ---- layout -------------------------------------------------------------------

        private FrameworkElement BuildLayout()
        {
            double side = _compact ? 0 : Theme.PagePadding;
            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(side, 0, side, side)
            };
            _scroll = scroll;

            StackPanel root = new StackPanel();

            if (!_compact)
            {
                root.Children.Add(new PageHeading("反馈", "描述问题；可附带截图与诊断日志"));
            }
            root.Children.Add(BuildContentCard());

            // 提交/清空固定在页面右下角（不随内容滚动），对齐标注的位置。
            scroll.Padding = new Thickness(side, 0, side, 0);
            scroll.Content = root;

            Grid page = new Grid();
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroll, 0);
            page.Children.Add(scroll);

            FrameworkElement footer = BuildFooter();
            footer.Margin = _compact
                ? new Thickness(0, Theme.CardGap, 0, 0)
                : new Thickness(Theme.PagePadding, Theme.CardGap, Theme.PagePadding, Theme.PagePadding);
            Grid.SetRow(footer, 1);
            page.Children.Add(footer);
            return page;
        }

        private FrameworkElement BuildContentCard()
        {
            _card = new Card("反馈内容");
            _formBody = new StackPanel();

            // 类型行已按反馈删除；保留隐藏的 ComboBox 让草稿/提交仍沿用默认“问题反馈”。
            foreach (FeedbackKind item in _kinds)
            {
                _kind.Items.Add(new ComboBoxItem
                {
                    Content = FeedbackKinds.DisplayName(item),
                    Tag = item,
                    Background = Theme.SurfaceHoverBrush,
                    Foreground = Theme.TextPrimaryBrush
                });
            }
            _kind.SelectedIndex = 0;

            _text.AcceptsReturn = true;
            _text.TextWrapping = TextWrapping.Wrap;
            _text.VerticalContentAlignment = VerticalAlignment.Top;
            _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _text.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _text.Height = 150;
            _text.Padding = new Thickness(10, 8, 10, 8);
            _text.FontSize = 13;

            // 提示搬进输入框当占位文字：浅浅一层，一旦有内容就消失，不再白占一行。
            // 位置必须和输入文字同源：DarkTextBox 的模板把内边距同时给到 Border 与内容宿主，
            // 文字起点是「边框 + 2×内边距」再左让 2 px 光标位；右边界不加那 2 px，
            // 这样提示与输入文字的换行宽度一致，同一段话不会两处折行不同。
            _placeholder.Text = "截图直接 Ctrl+V 粘贴，也可以把文件拖进这里。最多 8 个，单个 ≤ 50 MB。";
            _placeholder.FontSize = 13;
            _placeholder.Foreground = Theme.TextTertiaryBrush;
            _placeholder.TextWrapping = TextWrapping.Wrap;
            _placeholder.Margin = new Thickness(
                _text.BorderThickness.Left + _text.Padding.Left * 2 + HintCaretInset,
                _text.BorderThickness.Top + _text.Padding.Top * 2,
                _text.BorderThickness.Right + _text.Padding.Right * 2,
                0);
            _placeholder.VerticalAlignment = VerticalAlignment.Top;
            _placeholder.IsHitTestVisible = false;   // 不拦输入、拖放和右键

            Grid textArea = new Grid();
            textArea.Children.Add(_text);
            textArea.Children.Add(_placeholder);
            _formBody.Children.Add(textArea);

            _attachmentHint.FontSize = 11.5;
            _attachmentHint.Foreground = Theme.TextTertiaryBrush;
            _attachmentHint.Margin = new Thickness(0, 8, 0, 0);
            _formBody.Children.Add(_attachmentHint);

            _attachmentRows.Margin = new Thickness(0, 2, 0, 0);
            _formBody.Children.Add(_attachmentRows);

            // 左下角：诊断日志开关 + 它的说明。
            _formBody.Children.Add(BuildCardFooter());

            _previewBox.IsReadOnly = true;
            _previewBox.AcceptsReturn = true;
            _previewBox.TextWrapping = TextWrapping.NoWrap;
            _previewBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _previewBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _previewBox.Height = 190;
            _previewBox.FontFamily = Theme.MonoFont;
            _previewBox.FontSize = 11;
            _previewBox.Padding = new Thickness(10);
            _previewPanel.Children.Add(_previewBox);

            _previewPanel.Visibility = Visibility.Collapsed;
            _previewPanel.Margin = new Thickness(0, 12, 0, 0);
            _formBody.Children.Add(_previewPanel);

            _resultBody = BuildResultBody();
            _resultBody.Visibility = Visibility.Collapsed;

            _card.Content.Children.Add(_formBody);
            _card.Content.Children.Add(_resultBody);
            UpdateAttachmentList();
            UpdatePlaceholder();
            return _card;
        }

        /// <summary>
        /// Card footer: the diagnostics switch and its hint on the left, the two actions on the
        /// right, with "查看将包含的信息" sitting left of "添加文件".
        /// </summary>
        private FrameworkElement BuildCardFooter()
        {
            Grid row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new StackPanel();
            _includeLogs.Content = "附带诊断日志（传感器 / 程序日志与当前状态）";
            _includeLogs.IsChecked = true;
            left.Children.Add(_includeLogs);

            left.Children.Add(new TextBlock
            {
                Text = "只有勾选后才会读取日志；提交前可以先查看内容，用户目录会被替换成 %USERPROFILE%。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                // Aligned under the checkbox caption, not under its square (user picked "B"):
                // 18 px square + 9 px gap + 1 px glyph bearing.
                Margin = new Thickness(28, 6, 0, 0)
            });
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(12, 0, 0, 0)
            };
            _previewButton.HorizontalAlignment = HorizontalAlignment.Left;
            _previewButton.Margin = new Thickness(0, 0, 8, 0);
            _previewButton.Click += (sender, e) => TogglePreview();
            buttons.Children.Add(_previewButton);

            _addFiles.Click += (sender, e) => PickFiles();
            buttons.Children.Add(_addFiles);

            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            return row;
        }

        private FrameworkElement BuildFooter()
        {
            Grid footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _status.FontSize = 12;
            _status.Foreground = Theme.TextTertiaryBrush;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_status, 0);
            footer.Children.Add(_status);

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
            FlatButton clear = new FlatButton("清空");
            clear.Margin = new Thickness(0, 0, 8, 0);
            clear.Click += (sender, e) => ClearForm();
            buttons.Children.Add(clear);

            _submit.Click += OnSubmit;
            buttons.Children.Add(_submit);
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);
            return footer;
        }

        private StackPanel BuildResultBody()
        {
            StackPanel content = new StackPanel();

            _successId.FontSize = 12;
            _successId.Foreground = Theme.TextSecondaryBrush;
            content.Children.Add(_successId);

            _successPath.FontSize = 11;
            _successPath.Foreground = Theme.TextTertiaryBrush;
            _successPath.TextWrapping = TextWrapping.Wrap;
            _successPath.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(_successPath);

            _successUpload.FontSize = 11.5;
            _successUpload.TextWrapping = TextWrapping.Wrap;
            _successUpload.Margin = new Thickness(0, 8, 0, 0);
            content.Children.Add(_successUpload);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };
            FlatButton copy = new FlatButton("复制编号");
            copy.Margin = new Thickness(0, 0, 8, 0);
            copy.Click += (sender, e) => CopyId();
            buttons.Children.Add(copy);

            _retry.Margin = new Thickness(0, 0, 8, 0);
            _retry.Visibility = Visibility.Collapsed;
            _retry.Click += async (sender, e) => await RunUploadAsync();
            buttons.Children.Add(_retry);

            FlatButton again = new FlatButton("再写一条");
            again.Click += (sender, e) =>
            {
                ClearForm();
                _text.Focus();
            };
            buttons.Children.Add(again);

            content.Children.Add(buttons);
            return content;
        }

        private static void FadeIn(UIElement element)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 0;
            DoubleAnimation animation = new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        /// <summary>
        /// 成功卡是一次性状态：离开反馈页时收回表单，切回来就是可写的空表单，不必再点「再写一条」。
        /// </summary>
        private void ResetResultOnLeave()
        {
            if (_resultBody == null || _resultBody.Visibility != Visibility.Visible) return;
            RestoreForm();
        }

        private void RestoreForm()
        {
            if (_card == null || _resultBody == null) return;
            _card.Title = "反馈内容";
            _resultBody.Visibility = Visibility.Collapsed;
            _formBody.Visibility = Visibility.Visible;
        }

        /// <summary>The placeholder is only visible while the box is empty.</summary>
        private void UpdatePlaceholder()
        {
            if (_placeholder == null) return;
            _placeholder.Visibility = string.IsNullOrEmpty(_text.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // ---- draft --------------------------------------------------------------------

        private void LoadDraft()
        {
            List<string> warnings;
            FeedbackDraft draft = FeedbackStore.LoadDraft(DataDirectory, out warnings);
            if (draft == null) return;

            _filling = true;
            try
            {
                _text.Text = draft.Text ?? string.Empty;
                _includeLogs.IsChecked = draft.IncludeLogs;
                SelectKind(draft.Kind);
                _attachments.Clear();
                if (draft.Attachments != null) _attachments.AddRange(draft.Attachments);
                UpdateAttachmentList();
            }
            finally
            {
                _filling = false;
            }

            if (warnings.Count > 0) ShowStatus(string.Join(" ", warnings.ToArray()), false);
        }

        private void ScheduleDraft()
        {
            if (_filling || _draftTimer == null) return;
            _draftTimer.Stop();
            _draftTimer.Start();
        }

        private void SaveDraftNow()
        {
            if (_filling) return;
            try
            {
                string text = _text.Text ?? string.Empty;
                if (text.Trim().Length == 0 && _attachments.Count == 0)
                {
                    FeedbackStore.ClearDraft(DataDirectory);
                    return;
                }

                FeedbackDraft draft = new FeedbackDraft
                {
                    Kind = SelectedKind,
                    Text = text,
                    IncludeLogs = _includeLogs.IsChecked == true,
                    Attachments = new List<FeedbackDraftAttachment>(_attachments)
                };
                FeedbackStore.SaveDraft(DataDirectory, draft);
            }
            catch (Exception)
            {
                // Draft saving must never interrupt typing; the explicit submit shows errors.
            }
        }

        // ---- attachments --------------------------------------------------------------

        private void OnTextDragOver(object sender, DragEventArgs e)
        {
            // 拖的只是文本时交给 TextBox 自己处理，别把正常的拖文本编辑挡掉。
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            e.Effects = DragDropEffects.Copy;
            _text.BorderBrush = Theme.AccentGreenBrush;
            e.Handled = true;
        }

        private void OnTextDrop(object sender, DragEventArgs e)
        {
            _text.BorderBrush = Theme.BorderSubtleBrush;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) AddFiles(files);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        /// <summary>
        /// Ctrl+V: an image or a file in the clipboard becomes an attachment (reference behaviour);
        /// plain text still pastes normally, so this never eats the default command.
        /// </summary>
        private void OnTextPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            if (TryPasteAttachment()) e.Handled = true;
        }

        /// <summary>
        /// Returns true when the clipboard held an image or files (and they were added).
        /// Clipboard text wins over everything: selecting text in Word also copies a bitmap.
        /// </summary>
        private bool TryPasteAttachment()
        {
            try
            {
                if (Clipboard.ContainsText()) return false;

                if (Clipboard.ContainsImage())
                {
                    BitmapSource image = Clipboard.GetImage();
                    if (image != null)
                    {
                        AddPastedImage(image);
                        return true;
                    }
                }

                if (Clipboard.ContainsFileDropList())
                {
                    System.Collections.Specialized.StringCollection list = Clipboard.GetFileDropList();
                    if (list != null && list.Count > 0)
                    {
                        List<string> files = new List<string>();
                        foreach (string item in list) files.Add(item);
                        AddFiles(files.ToArray());
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // 剪贴板被别的进程占用时，当作没有可粘贴的附件
            }
            return false;
        }

        private void OnTextPaste(object sender, DataObjectPastingEventArgs e)
        {
            IDataObject data = e.DataObject;

            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    e.CancelCommand();
                    AddFiles(files);
                    return;
                }
            }

            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                BitmapSource image = data.GetData(DataFormats.Bitmap) as BitmapSource;
                if (image != null)
                {
                    e.CancelCommand();
                    AddPastedImage(image);
                }
            }
        }

        /// <summary>
        /// 有些截图工具给的 BGRA 像素 alpha 全是 0（RGB 完好），直接存 PNG 会得到一张全透明的图——
        /// 粘贴进来“什么都看不到”。只有确认全图 alpha 全为 0 时才补成不透明；真透明图不动。
        /// </summary>
        private static BitmapSource EnsureVisible(BitmapSource source)
        {
            BitmapSource bgra = source;
            if (source.Format != PixelFormats.Bgra32)
            {
                bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            }

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);

            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) { anyAlpha = true; break; }
            }
            if (anyAlpha) return bgra;

            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return BitmapSource.Create(width, height, bgra.DpiX, bgra.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
        }

        private void AddPastedImage(BitmapSource image)
        {
            if (_attachments.Count >= _options.MaxAttachments)
            {
                ShowStatus("最多只能添加 " + _options.MaxAttachments + " 个附件。", true);
                return;
            }

            image = EnsureVisible(image);

            string temp = Path.Combine(Path.GetTempPath(),
                "截图-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 4) + ".png");
            try
            {
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                {
                    encoder.Save(stream);
                }

                string error;
                FeedbackDraftAttachment attachment =
                    FeedbackStore.ImportDraftAttachment(DataDirectory, temp, _options, out error);
                if (attachment == null)
                {
                    ShowStatus(error, true);
                    return;
                }
                _attachments.Add(attachment);
                UpdateAttachmentList();
                ScheduleDraft();
                ShowStatus("已粘贴截图：" + attachment.OriginalName, false);
            }
            catch (Exception ex)
            {
                ShowStatus("粘贴截图失败：" + ex.GetType().Name, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            }
        }

        private void PickFiles()
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Multiselect = true,
                CheckFileExists = true,
                Title = "选择反馈附件",
                Filter = "支持的附件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.txt;*.log;*.md;*.json;*.csv;*.zip;*.7z;*.rar;*.pdf|所有文件|*.*"
            };
            if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
        }

        private void AddFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            int errors = 0;
            string lastError = null;
            long totalBytes = 0;
            foreach (FeedbackDraftAttachment existing in _attachments) totalBytes += existing.Bytes;

            foreach (string path in paths)
            {
                if (_attachments.Count >= _options.MaxAttachments)
                {
                    ShowStatus("最多只能添加 " + _options.MaxAttachments + " 个附件。", true);
                    break;
                }
                if (!File.Exists(path))
                {
                    errors++;
                    lastError = "只支持文件，不支持文件夹：" + Path.GetFileName(path);
                    continue;
                }

                long size;
                try { size = new FileInfo(path).Length; }
                catch (Exception) { size = 0; }
                if (totalBytes + size > _options.MaxTotalAttachmentBytes)
                {
                    ShowStatus("附件总大小不能超过 " + MetricFormat.Bytes(_options.MaxTotalAttachmentBytes) + "。", true);
                    break;
                }

                string error;
                FeedbackDraftAttachment attachment =
                    FeedbackStore.ImportDraftAttachment(DataDirectory, path, _options, out error);
                if (attachment == null)
                {
                    errors++;
                    lastError = error;
                    continue;
                }
                _attachments.Add(attachment);
                totalBytes += attachment.Bytes;
            }

            UpdateAttachmentList();
            ScheduleDraft();
            if (errors > 0) ShowStatus(lastError ?? (errors + " 个附件添加失败"), true);
            else ShowStatus(string.Empty, false);
        }

        private void RemoveAttachment(FeedbackDraftAttachment attachment)
        {
            FeedbackStore.RemoveDraftAttachment(DataDirectory, attachment.FileName);
            _attachments.Remove(attachment);
            UpdateAttachmentList();
            ScheduleDraft();
        }

        private void UpdateAttachmentList()
        {
            _attachmentRows.Children.Clear();
            _attachmentHint.Text = _attachments.Count == 0
                ? string.Empty
                : "已添加 " + _attachments.Count + " 个附件：";
            _attachmentHint.Visibility = _attachments.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;

            foreach (FeedbackDraftAttachment attachment in _attachments)
            {
                Grid row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                TextBlock name = new TextBlock
                {
                    Text = attachment.OriginalName,
                    FontSize = 12.5,
                    Foreground = Theme.TextPrimaryBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                TextBlock size = new TextBlock
                {
                    Text = MetricFormat.Bytes(attachment.Bytes),
                    FontSize = 11,
                    Foreground = Theme.TextTertiaryBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 10, 0)
                };
                Grid.SetColumn(size, 1);
                row.Children.Add(size);

                FlatButton remove = new FlatButton("移除");
                remove.Padding = new Thickness(9, 3, 9, 3);
                remove.FontSize = 11;
                remove.VerticalAlignment = VerticalAlignment.Center;
                FlatButton captured = remove;
                FeedbackDraftAttachment item = attachment;
                captured.Click += (sender, e) => RemoveAttachment(item);
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);

                _attachmentRows.Children.Add(row);
            }
        }

        // ---- diagnostics --------------------------------------------------------------

        private FeedbackContext CaptureContext()
        {
            return FeedbackContext.Capture(AppVersion, _window.CurrentSettings, _includeLogs.IsChecked == true);
        }

        private void UpdateAgentPill()
        {
            try
            {
                using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen())
                {
                    MetricsSnapshot snapshot = reader == null ? null : reader.TryRead();
                    bool alive = snapshot != null &&
                                 snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout);
                    _window.SetAgentStatus(alive,
                        snapshot == null ? 0 : snapshot.Age(DateTime.UtcNow).TotalSeconds);
                }
            }
            catch (Exception)
            {
                _window.SetAgentStatus(false, 0);
            }
        }

        private void TogglePreview()
        {
            if (IsPreviewOpen)
            {
                _previewPanel.Visibility = Visibility.Collapsed;
                _previewButton.Content = "查看将包含的信息";
                return;
            }

            UpdatePreview();
            _previewPanel.Visibility = Visibility.Visible;
            _previewButton.Content = "收起诊断信息";
        }

        private void UpdatePreview()
        {
            try
            {
                FeedbackPreview preview = FeedbackStore.BuildPreview(
                    CaptureContext(), _includeLogs.IsChecked == true, _options);
                string logSource = string.IsNullOrEmpty(preview.LogFileName)
                    ? "没有找到日志文件"
                    : "日志来源：" + preview.LogFileName;
                _previewBox.Text =
                    "【将随反馈提交的诊断信息】\n" + preview.MetadataText +
                    "\n\n【" + logSource + "，最近 40 行】\n" + preview.LogsText;
            }
            catch (Exception ex)
            {
                _previewBox.Text = "读取诊断信息失败：" + ex.Message;
            }
        }

        // ---- submit -------------------------------------------------------------------

        private FeedbackKind SelectedKind
        {
            get
            {
                ComboBoxItem item = _kind.SelectedItem as ComboBoxItem;
                return item == null ? FeedbackKind.Bug : (FeedbackKind)item.Tag;
            }
        }

        private void SelectKind(FeedbackKind kind)
        {
            for (int i = 0; i < _kind.Items.Count; i++)
            {
                ComboBoxItem item = _kind.Items[i] as ComboBoxItem;
                if (item != null && (FeedbackKind)item.Tag == kind)
                {
                    _kind.SelectedIndex = i;
                    return;
                }
            }
        }

        private async void OnSubmit(object sender, RoutedEventArgs e)
        {
            if (_submitting) return;

            List<string> paths = new List<string>();
            string attachmentDirectory = FeedbackStore.DraftAttachmentsDirectory(DataDirectory);
            foreach (FeedbackDraftAttachment attachment in _attachments)
            {
                paths.Add(Path.Combine(attachmentDirectory, attachment.FileName));
            }

            FeedbackSubmission submission = new FeedbackSubmission
            {
                Kind = SelectedKind,
                Text = _text.Text ?? string.Empty,
                IncludeLogs = _includeLogs.IsChecked == true,
                AttachmentPaths = paths
            };

            string error;
            if (!FeedbackStore.Validate(submission, _options, out error))
            {
                ShowStatus(error, true);
                return;
            }

            FeedbackContext context = CaptureContext();
            _submitting = true;
            _submit.IsEnabled = false;
            _submit.Content = "正在生成…";
            ShowStatus("正在生成反馈包…", false);

            try
            {
                FeedbackResult result = await Task.Run(
                    () => FeedbackStore.Submit(submission, context, _options));
                if (!result.Ok)
                {
                    ShowStatus(result.Error, true);
                    return;
                }

                _lastResult = result;
                FeedbackStore.ClearDraft(DataDirectory);
                ShowSuccess(result);
                ClearFormAfterSubmit();
                if (!string.IsNullOrEmpty(result.Warning)) ShowStatus(result.Warning, true);

                // 上传内容以本地 zip 为准：草稿附件在提交后已清理，不能再拿草稿路径去发。
                FeedbackUploadRequest request;
                string tempDirectory;
                string packageId;
                if (FeedbackRecovery.TryBuildRequest(result.PackagePath, out request, out tempDirectory, out packageId))
                {
                    _lastRequest = request;
                    _uploadTempDirectory = tempDirectory;
                    await RunUploadAsync();
                }
                else
                {
                    _successUpload.Text = "仅本机保存：本地包无法读回（上传未开始）";
                    _successUpload.Foreground = Theme.AccentRedBrush;
                    _retry.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                ShowStatus("提交失败：" + ex.Message, true);
            }
            finally
            {
                _submitting = false;
                _submit.IsEnabled = true;
                _submit.Content = "提交反馈";
            }
        }

        private void ShowSuccess(FeedbackResult result)
        {
            _successId.Text = "编号：" + result.Id + "　·　" + result.AttachmentCount + " 个附件";
            _successPath.Text = "文件：" + result.PackagePath;
            _successUpload.Text = "准备上传…";
            _successUpload.Foreground = Theme.TextSecondaryBrush;
            _retry.Visibility = Visibility.Collapsed;

            // 同一张卡片原地变成结果：表单收起，结果顶上来。
            _card.Title = "反馈已保存";
            _formBody.Visibility = Visibility.Collapsed;
            _resultBody.Visibility = Visibility.Visible;
            FadeIn(_resultBody);

            if (IsPreviewOpen)
            {
                _previewPanel.Visibility = Visibility.Collapsed;
                _previewButton.Content = "查看将包含的信息";
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_card != null) _card.BringIntoView();
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

                _successPath.Text = removed ? "本地包已删除，台账保留编号" : "本地包未能删除，下次启动会重试";
                CleanupUploadTemp();
                _successUpload.Text = "已上传，服务端编号 " + upload.FeedbackId;
                _successUpload.Foreground = Theme.AccentGreenBrush;
                return;
            }

            if (_lastResult != null) FeedbackStore.AppendUploadFailed(DataDirectory, _lastResult.Id, upload.Error);
            _successUpload.Text = "仅本机保存：上传失败（" + upload.Error + "）";
            _successUpload.Foreground = Theme.AccentRedBrush;
            _retry.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 上传用的附件是从本地 zip 解到临时目录的（草稿附件提交后已删），成功或清空时回收；
        /// 失败时留着给“重新上传”用，进程退出后的残留由下次启动的清理扫描负责。
        /// </summary>
        private void CleanupUploadTemp()
        {
            string directory = _uploadTempDirectory;
            _uploadTempDirectory = null;
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception)
            {
            }
        }

        private void ClearFormAfterSubmit()
        {
            _filling = true;
            try
            {
                _text.Text = string.Empty;
                _kind.SelectedIndex = 0;
                _includeLogs.IsChecked = true;
                _attachments.Clear();
                UpdateAttachmentList();
                ShowStatus(string.Empty, false);
            }
            finally
            {
                _filling = false;
            }
        }

        private void ClearForm()
        {
            _filling = true;
            try
            {
                _text.Text = string.Empty;
                _kind.SelectedIndex = 0;
                _includeLogs.IsChecked = true;
                foreach (FeedbackDraftAttachment attachment in _attachments)
                {
                    FeedbackStore.RemoveDraftAttachment(DataDirectory, attachment.FileName);
                }
                _attachments.Clear();
                UpdateAttachmentList();
                RestoreForm();
                if (IsPreviewOpen)
                {
                    _previewPanel.Visibility = Visibility.Collapsed;
                    _previewButton.Content = "查看将包含的信息";
                }
                ShowStatus(string.Empty, false);
            }
            finally
            {
                _filling = false;
            }
            FeedbackStore.ClearDraft(DataDirectory);
            _lastRequest = null;
            CleanupUploadTemp();
        }

        private void CopyId()
        {
            if (_lastResult == null) return;
            try
            {
                Clipboard.SetText(_lastResult.Id);
                ShowStatus("编号已复制。", false);
            }
            catch (Exception ex)
            {
                ShowStatus("复制失败：" + ex.Message, true);
            }
        }

        private void ShowStatus(string text, bool isError)
        {
            // 新的提示要能接替还没淡完的旧提示：先撤掉淡出动画，否则透明度会停在半路。
            _status.BeginAnimation(UIElement.OpacityProperty, null);
            _status.Opacity = 1;

            _status.Text = text ?? string.Empty;
            _status.Foreground = string.IsNullOrEmpty(text)
                ? Theme.TextTertiaryBrush
                : (isError ? Theme.AccentRedBrush : Theme.TextSecondaryBrush);

            _statusTimer.Stop();
            if (isError && !string.IsNullOrEmpty(text)) _statusTimer.Start();
        }

        /// <summary>
        /// 错误提示不常驻：4.5 秒开始淡出，5 秒时清空，否则一条旧错误会一直挂在页脚。
        /// 成功/进行中的提示保持原样，等下一次状态覆盖。
        /// </summary>
        private void OnStatusTimeout()
        {
            _statusTimer.Stop();
            DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(StatusFadeMs))
            {
                EasingFunction = Theme.EnterFade()
            };
            fade.Completed += (sender, e) =>
            {
                _status.Text = string.Empty;
                _status.Opacity = 1;
            };
            _status.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public void Refresh()
        {
            // The page owns its draft; nothing to reload when the user navigates back.
        }
    }
}
