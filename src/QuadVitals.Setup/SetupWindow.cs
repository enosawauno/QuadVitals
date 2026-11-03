using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuadVitals.UI;

namespace QuadVitals.Setup
{
    /// <summary>
    /// The installer window: options → (pre-flight) → progress → result, in the product's own dark
    /// palette, so installing does not look like a console tool bolted onto a taskbar app.
    ///
    /// The window owns presentation only. Every step that touches the machine goes through
    /// <see cref="InstallerCore"/>, which runs the same scripts the repo tests. Pages are split one
    /// per file (Shell / Welcome / Preflight / Progress / Result); this file holds the state they
    /// share, the page switch and the closing guard.
    /// </summary>
    internal sealed partial class SetupWindow : Window
    {
        private readonly string _version;
        private readonly bool _uninstall;
        private readonly InstallerCore _core;

        private Grid _host;
        private System.Windows.Shapes.Rectangle _glow;

        private TextBox _installDirBox;

        private DarkCheckBox _startAgent;
        private DarkCheckBox _desktopShortcut;
        private DarkCheckBox _keepSettings;


        private TextBlock _stageText;
        private ProgressBar _bar;
        private LogView _logBox;

        private LogView _resultLog;

        private bool _running;
        private bool _closed;
        private Button _closeButton;
        /// <summary>Set before the finish page is built: whether the band came back after the shell restart.</summary>
        private bool _bandRestored;

        public int ExitCode { get; private set; }

        public SetupWindow(string version, bool uninstall, string preview = null)
        {
            _version = version;
            _uninstall = uninstall;
            _core = new InstallerCore(version);

            Title = "QuadVitals";
            Width = 640;
            Height = 436;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Segoe UI");

            Content = BuildShell();

            // --preview lets the UI checks render a page that only exists after a real run
            // (the finish and failure pages). It never touches the machine.
            if (string.Equals(preview, "done", StringComparison.OrdinalIgnoreCase))
            {
                ShowPage(BuildResultPage(InstallerCore.DefaultInstallDir));
            }
            else if (string.Equals(preview, "failed", StringComparison.OrdinalIgnoreCase))
            {
                ShowPage(BuildFailurePage(
                    (uninstall ? "卸载" : "安装") + "没有完成（步骤返回 1）\r\n下方是" +
                    (uninstall ? "卸载" : "安装") + "过程的详细记录，可以据此反馈问题"));

                // Enough lines to prove the log cannot grow into the button row.
                System.Text.StringBuilder sample = new System.Text.StringBuilder();
                for (int i = 1; i <= 24; i++)
                {
                    sample.AppendLine("  [" + i + "] copy QuadVitals.Agent.exe / QuadVitals.Core.dll / tray.ico");
                    if (i == 20)
                    {
                        // A realistic long path, to prove it wraps instead of running off the edge.
                        sample.AppendLine("  [20] cannot write C:\\Users\\<user>\\AppData\\Local\\QuadVitals\\QuadVitals.UI.dll" +
                                          " (the file is being used by another process)");
                    }
                }
                if (_resultLog != null) _resultLog.Reset(sample.ToString());
            }
            else if (string.Equals(preview, "stopfail", StringComparison.OrdinalIgnoreCase))
            {
                // The failure page after the elevated stop step reported a survivor. Fed through the same
                // marker the script prints, so the page shows the real text.
                _core.FeedPreviewLine("QV-STOP-FAILED: QuadVitals.Agent (pid 4212)");
                ShowFailure(_core.DescribeFailure(1, uninstall), true);
            }
            else if (string.Equals(preview, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                // Real cancel path, including the self-close: --preview cancelled in a normal run is how
                // the "window closes on its own" behaviour gets measured.
                CancelWork();
            }
            else if (string.Equals(preview, "progress", StringComparison.OrdinalIgnoreCase))
            {
                // Seeded with more lines than fit, so the check proves the log pane still scrolls by
                // wheel while showing no scrollbar at all. The "== stage" lines go through the same
                // localisation path a real install uses.
                ShowPage(BuildProgress());
                SetBusy(true);
                foreach (string stage in new[] { "check build outputs", "stop running QuadVitals processes",
                                                 "copy files", "register DeskBand (per-user)",
                                                 "refresh the taskbar", "install logon task (one UAC prompt)",
                                                 "start agent" })
                {
                    _core.FeedPreviewLine("== " + stage);
                }
                for (int i = 1; i <= 24; i++)
                {
                    AppendLog(_logBox, "  QuadVitals.Agent.exe / QuadVitals.Core.dll / tray.ico  («- 第 " + i + " 行示例日志)");
                }
            }
            else if (string.Equals(preview, "uac", StringComparison.OrdinalIgnoreCase))
            {
                // The page the user gets when they decline the elevation prompt: a failed install, not
                // a "mostly worked" one.
                ShowPage(BuildFailurePage(
                    "系统授权(UAC)失败\r\n请点击「重新" + (uninstall ? "卸载" : "安装") +
                    "」并完成UAC提权后即可继续", false));
            }
            else
            {
                ShowPage(BuildWelcome());
            }
        }

        private void ShowPage(UIElement page)
        {
            _host.Children.Clear();
            _host.Children.Add(page);
        }

        // ---- screenshot (development aid) ------------------------------------------------

        /// <summary>
        /// Renders the whole window into a PNG at 96 dpi. Used by the UI checks: capturing this
        /// window off the screen depends on nothing else being in front of it, which is not
        /// something a build check can guarantee.
        /// </summary>
        public void SaveScreenshot(string path)
        {
            UpdateLayout();

            FrameworkElement root = Content as FrameworkElement;
            if (root == null) return;

            const double scale = 1.0;
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * scale));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * scale));

            RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale,
                                                               PixelFormats.Pbgra32);

            // The window is transparent so its drop shadow shows; paint the page colour behind the
            // content so the PNG is readable in any viewer.
            DrawingVisual backdrop = new DrawingVisual();
            using (DrawingContext context = backdrop.RenderOpen())
            {
                context.DrawRectangle(Theme.BgBaseBrush, null,
                                      new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            bitmap.Render(backdrop);
            bitmap.Render(root);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            using (FileStream stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }

        // ---- small pieces --------------------------------------------------------------

        private static TextBlock Heading(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Theme.TextPrimaryBrush,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold
            };
        }

        private static TextBlock Subtitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Theme.TextSecondaryBrush,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0)
            };
        }

        private static TextBlock Note(string text, Brush brush)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
                Margin = new Thickness(0, 18, 0, 0)
            };
        }

        private static void KeyValue(string label, string value, Panel host)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Theme.TextSecondaryBrush,
                FontSize = 13
            });

            TextBlock text = new TextBlock
            {
                Text = value,
                Foreground = Theme.TextPrimaryBrush,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            host.Children.Add(row);
        }

        /// <summary>
        /// Scroll-less log view. A TextBox draws the system scrollbar (light grey, square, completely
        /// foreign to this window); a ScrollViewer with the bars hidden still lets the wheel scroll,
        /// so nothing is lost and nothing foreign appears.
        /// </summary>
        private sealed class LogView
        {
            private const int MaxLines = 500;

            private readonly Queue<string> _lines = new Queue<string>();
            private readonly TextBlock _text;
            private readonly ScrollViewer _scroller;

            public LogView(double minHeight)
            {
                _text = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)),
                    TextWrapping = TextWrapping.Wrap
                };

                _scroller = new ScrollViewer
                {
                    // Hidden, not Disabled: the wheel keeps working, only the bar disappears.
                    VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                    Content = _text
                };

                Host = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(Theme.ControlRadius),
                    Padding = new Thickness(10, 8, 10, 8),
                    MinHeight = minHeight,
                    Child = _scroller
                };
            }

            public Border Host { get; private set; }

            /// <summary>Raw text without the soft-wrap hints: this is what "copy" must put on the clipboard.</summary>
            public string RawText
            {
                get { lock (_lines) { return string.Join(Environment.NewLine, _lines); } }
            }

            public void Append(string line)
            {
                _lines.Enqueue(line ?? string.Empty);
                while (_lines.Count > MaxLines) _lines.Dequeue();
                Flush();
            }

            public void Reset(string text)
            {
                _lines.Clear();
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (string line in text.Replace("\r\n", "\n").Split('\n')) _lines.Enqueue(line);
                }
                Flush();
            }

            private void Flush()
            {
                System.Text.StringBuilder display = new System.Text.StringBuilder();
                foreach (string line in _lines)
                {
                    if (display.Length > 0) display.AppendLine();
                    SoftWrap(line, display);
                }

                _text.Text = display.ToString();
                _scroller.ScrollToEnd();
            }

            /// <summary>
            /// Inserts zero-width spaces so a long path can break. WPF only wraps at whitespace and
            /// between CJK characters, so a machine-generated path like
            /// C:\Users\…\QuadVitals\QuadVitals.UI.dll runs off the right edge and the rest of the line
            /// (the actual reason) gets pushed out of sight. The hint characters never reach the
            /// clipboard - RawText keeps the original.
            /// </summary>
            private static void SoftWrap(string line, System.Text.StringBuilder target)
            {
                const char Hint = '\u200B';
                const int MaxRun = 18;

                int run = 0;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    target.Append(c);

                    if (c == '\\' || c == '/' || c == ':' || c == '_' || c == '-')
                    {
                        target.Append(Hint);
                        run = 0;
                        continue;
                    }

                    if (c == ' ' || c == '\t')
                    {
                        run = 0;
                        continue;
                    }

                    run++;
                    if (run >= MaxRun)
                    {
                        target.Append(Hint);
                        run = 0;
                    }
                }
            }
        }

        private static void AppendLog(LogView box, string line)
        {
            if (box == null) return;
            box.Append(line);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Closing mid-install would leave a half-copied directory; keep the window until the
            // worker returns and the result page (or failure page) is up.
            if (_running)
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }
    }
}
