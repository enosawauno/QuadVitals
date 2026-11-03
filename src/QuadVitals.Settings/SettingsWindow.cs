using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Shell;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;


namespace QuadVitals.Settings
{
    /// <summary>
    /// Dashboard shell: borderless dark window, left navigation, per-page content.
    /// Pages are plain controls, no XAML involved.
    /// </summary>
    internal sealed class SettingsWindow : Window
    {
        private readonly ContentControl _host = new ContentControl();
        private readonly List<SidebarItem> _navItems = new List<SidebarItem>();
        // 侧栏选中滑块：SidebarItem 高 44 + 间距 4 = 48；改行高必须同步这里。
        private const double NavStep = 48;
        // 左上角标记的定位契约：左边 = 导航胶囊左边缘（36），上边 = 传感器胶囊上边缘（22）。
        // 这两个数来自侧栏内边距与标题栏里芯片的实测位置，改任一处都要回来重对。
        private const double MarkLeft = 36;
        private const double MarkTop = 22;
        private const double MarkWidth = 58;
        private Border _navGlider;
        private TranslateTransform _navGliderShift;
        private bool _navGliderPlaced;
        private readonly Dictionary<string, Func<FrameworkElement>> _pages = new Dictionary<string, Func<FrameworkElement>>();
        private readonly Dictionary<string, FrameworkElement> _cache = new Dictionary<string, FrameworkElement>();
        /// <summary>最后建出的概览页实例。保存设置会清空页缓存，但屏幕上那个实例还在，改样式得直接找它。</summary>
        private OverviewPage _overviewPage;
        private readonly TextBlock _agentStatus = new TextBlock();
        private Button _agentPill;
        // 反馈从侧边条搬到了标题栏气泡：表单实例活到窗口关，重开面板能接着草稿与结果卡。
        private FeedbackPage _feedbackPage;
        private MetricsChannel.Reader _statusReader;
        private DateTime _statusReaderAttemptUtc = DateTime.MinValue;
        // 自动恢复状态：连续两秒读到离线才开始动手，避免和刚启动的 Agent 抢。
        private int _deadTicks;
        private int _autoStartAttempts;
        private DateTime _nextAutoStartUtc = DateTime.MinValue;
        private Grid _overlay;
        private ContentControl _overlayContent;
        // 只有当前开着的确实是传感器面板时，自动恢复才会去刷新它；否则会把别的弹窗顶掉。
        private bool _agentPanelShown;
        // 面板按钮正在重启 Agent：这期间的“离线”是预期内的，自动恢复不能同时拉起第二个实例。
        private bool _agentRestartInProgress;
        // 指标兜底：Agent 活着但某项数据持续缺失时换进程；抖动、宽限与退避都在看门狗里。
        private readonly MetricWatchdog _metricWatch = new MetricWatchdog();
        private AppSettings _settings;
        private string _currentPage = "overview";

        public SettingsWindow(string startPage = "overview")
        {
            Title = "QuadVitals";
            // 提示框弹得快一点：WPF 默认 InitialShowDelay 约 400 ms，在密集的小控件
            // （色块、色值、按钮）上显得“悬停没反应”。
            ToolTipService.SetInitialShowDelay(this, 150);
            ToolTipService.SetBetweenShowDelay(this, 100);
            ToolTipService.SetShowDuration(this, 10000);
            Width = 1280;
            // 宽高比 3:2（1280 × 853）：窗口仍可拖动缩放，最小尺寸不变。
            Height = 853;
            MinWidth = 980;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = Brushes.Transparent;
            AllowsTransparency = true;   // smooth, antialiased rounded corners
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = Theme.UiFont;

            // The stock WPF scrollbar is light; replace it with the dark template. Keep the
            // SystemColors retint for the stock TextBox focus visuals.
            Resources.Add(typeof(ScrollBar), DarkScrollBars.Create());
            Resources.Add(typeof(ToolTip), DarkToolTips.Create());
            Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            Resources[SystemColors.ControlLightBrushKey] = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            Resources[SystemColors.ControlDarkBrushKey] = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            Resources[SystemColors.HotTrackBrushKey] = Theme.AccentGreenBrush;
            Resources[SystemColors.HighlightBrushKey] = Theme.AccentGreenBrush;
            Resources[SystemColors.WindowBrushKey] = Theme.BgBaseBrush;
            Resources[SystemColors.WindowTextBrushKey] = Theme.TextPrimaryBrush;

            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            RenderOptions.SetClearTypeHint(this, ClearTypeHint.Auto);

            WindowChrome chrome = new WindowChrome
            {
                CaptionHeight = 0,                               // we drag from the title bar ourselves
                ResizeBorderThickness = new Thickness(6),        // keep resize handles with transparency
                CornerRadius = new CornerRadius(0),              // WPF draws the rounded shell instead
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);

            LoadSettings();
            BuildPages();


            Content = BuildRoot();
            // "feedback" is no longer a page: the flag still works, it just opens the title-bar panel.
            bool startFeedback = string.Equals(startPage, "feedback", StringComparison.OrdinalIgnoreCase);
            ShowPage(_pages.ContainsKey(startPage) ? startPage : "overview");
            if (startFeedback) Loaded += (sender, e) => ShowFeedback();

            // 关闭前把网速曲线落盘：下次打开接着上一张图的右端继续跑。
            Closed += (sender, e) => OverviewPage.FlushHistory();

            PreviewKeyDown += (sender, e) =>
            {
                if (e.Key != Key.Escape) return;

                // Esc belongs to the dialog layer when one is open: closing the whole dashboard
                // from inside a modal panel is never what the user meant.
                if (IsOverlayOpen) { CloseOverlay(); e.Handled = true; }
                else Close();
            };

            // 芯片状态由窗口自己每秒刷新一次：概览页之外（显示项/设置/关于）以前没有任何东西
            // 在更新它，Agent 掉了再重启也不会变回来。
            DispatcherTimer chipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            chipTimer.Tick += (sender, e) =>
            {
                AgentStateInfo state = ReadAgentState();
                SetAgentStatus(state.Alive, state.AgeSeconds);
                TrackAgentHealth(state);
            };
            chipTimer.Start();
        }

        public void LoadSettings()
        {
            List<string> warnings;
            _settings = AppSettings.Load(AppSettings.DefaultPath, out warnings);
            foreach (string warning in warnings)
            {
                System.Diagnostics.Debug.WriteLine("[settings] " + warning);
            }

            // Bar column counts follow the CPU. The hardware read is slow (WMI), so
            // it runs on a worker thread and re-syncs the file afterwards.
            System.Threading.Tasks.Task.Run(() =>
            {
                HardwareInfo.Load();
                Dispatcher.BeginInvoke(new Action(SyncBarColumns));
            });
        }

        /// <summary>
        /// Keeps `historyColumns` equal to the CPU-derived count for every bar element, so the
        /// native band (which only reads that number) matches the capsule switch without the user
        /// having to open the elements page. Silent: the saved file is a cache of the hardware.
        /// </summary>
        private void SyncBarColumns()
        {
            if (_settings == null) return;
            int cores = HardwareInfo.PhysicalCores;
            int threads = HardwareInfo.LogicalProcessors;
            if (cores <= 0 && threads <= 0) return;

            AppSettings updated = _settings.Clone();
            bool changed = false;
            foreach (BandElement item in updated.Items)
            {
                if (item == null || !item.IsType(BandElementType.Bar)) continue;
                // 自定义列数是显式选择，启动时不要用 CPU 数字覆盖它
                if (ColumnModes.Canonical(item.ColumnMode) == ColumnModes.Custom) continue;
                int columns = BandLayout.ColumnsFor(item, cores, threads);
                if (item.HistoryColumns != columns)
                {
                    item.HistoryColumns = columns;
                    changed = true;
                }
            }
            if (changed) SaveSettingsQuietly(updated);
        }

        private void BuildPages()
        {
            _pages["overview"] = () => { _overviewPage = new OverviewPage(this); return _overviewPage; };
            _pages["elements"] = () => new ElementsPage(this);
            _pages["network"] = () => new NetworkPage(this);
            _pages["about"] = () => new AboutPage(this);
        }

        private FrameworkElement BuildRoot()
        {
            // No outer 1 px stroke: with a layered window the stroke is clipped by the rounded
            // geometry and ends up looking broken at the corners. The reference design has no
            // window border either - the rounded surface alone separates the window from desktop.
            Border shell = new Border
            {
                Background = Theme.BgBaseBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(Theme.WindowRadius),
                ClipToBounds = true
            };

            // Border.CornerRadius only rounds the border's own brush; children (the title bar and
            // sidebar Grids) would paint square corners straight over it. Clip the content to the
            // same rounded geometry, and keep the clip in sync with the window size.
            shell.SizeChanged += (sender, e) =>
            {
                if (shell.ActualWidth <= 0 || shell.ActualHeight <= 0) return;
                shell.Clip = new RectangleGeometry(
                    new Rect(0, 0, shell.ActualWidth, shell.ActualHeight),
                    Theme.WindowRadius, Theme.WindowRadius);
            };

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            FrameworkElement titleBar = BuildTitleBar();
            Grid.SetRow(titleBar, 0);
            // 左上角标记比标题栏高，会伸到内容区上方；标题栏必须画在内容之后才不会被盖住。
            Panel.SetZIndex(titleBar, 1);
            root.Children.Add(titleBar);

            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FrameworkElement sidebar = BuildSidebar();
            Grid.SetColumn(sidebar, 0);
            body.Children.Add(sidebar);

            _host.VerticalContentAlignment = VerticalAlignment.Top;
            // 内容左右各留 24（页面自身 PagePadding 也是 24，合计 48），设备卡右边缘与窗口右边缘
            // 同样是 48。顶部不再额外下移：页头文字的墨迹上边缘要和侧栏第一个导航项的顶边对齐。
            _host.Margin = new Thickness(24, 0, 24, 0);
            Grid.SetColumn(_host, 1);
            body.Children.Add(_host);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            // 标记画在 shell 上而不是标题栏里：它比 48 px 的条高，且要按内容区的坐标对齐
            // （左边 = 导航胶囊左边缘，上边 = 传感器胶囊上边缘）。
            Image mark = BuildAppMark(MarkWidth);
            if (mark != null)
            {
                mark.HorizontalAlignment = HorizontalAlignment.Left;
                mark.VerticalAlignment = VerticalAlignment.Top;
                mark.Margin = new Thickness(MarkLeft, MarkTop, 0, 0);
                mark.IsHitTestVisible = false;   // 装饰件：点击要落到下面的标题栏才能拖动窗口
                Panel.SetZIndex(mark, 2);
                root.Children.Add(mark);
            }

            // Dialog layer: sits above the whole shell (title bar included) so a panel is really
            // centred in the window, and dims what is behind it without touching page layout.
            _overlay = new Grid { Visibility = Visibility.Collapsed };
            _overlay.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x00, 0x00, 0x00))
            });
            _overlayContent = new ContentControl
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _overlay.Children.Add(_overlayContent);

            // 提示层在最上：保存结果等提示不再用系统 MessageBox（浅色、模态、跟深色壳子割裂）。

            Grid layers = new Grid();
            layers.Children.Add(root);
            layers.Children.Add(_overlay);

            shell.Child = layers;
            return shell;
        }

        public bool IsOverlayOpen
        {
            get { return _overlay != null && _overlay.Visibility == Visibility.Visible; }
        }

        /// <summary>Shows a panel centred in the window with a dimming backdrop (no OS dialog).</summary>
        public void ShowOverlay(FrameworkElement content)
        {
            if (content == null || _overlay == null) return;

            _agentPanelShown = false;

            _overlayContent.Content = content;
            _overlay.Visibility = Visibility.Visible;

            // 入场动效：与下拉弹层同一套关键帧曲线（慢起 → 轻微过冲 → 落回）。
            ScaleTransform scale = new ScaleTransform(0.965, 0.965);
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransform = scale;
            content.Opacity = 0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Theme.EnterPop(1.006));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Theme.EnterPop(1.006));
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(Theme.MotionFadeMs)) { EasingFunction = Theme.EnterFade() });

            content.Loaded += (sender, e) => content.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.First));
        }

        public void CloseOverlay()
        {
            if (_overlay == null) return;

            _overlay.Visibility = Visibility.Collapsed;
            _overlayContent.Content = null;
            _agentPanelShown = false;
        }

        public void ShowNetworkSettings()
        {
            if (IsOverlayOpen) return;
            ShowOverlay(new NetworkSettingsDialog(this).Build());
        }

        /// <summary>
        /// Title-bar bubble: opens the feedback form as a panel over the window. The form keeps its own
        /// draft, attachment and preview logic; the panel only supplies the frame and a close button.
        /// </summary>
        public void ShowFeedback()
        {
            if (IsOverlayOpen) return;
            if (_feedbackPage == null) _feedbackPage = new FeedbackPage(this, true);
            ShowOverlay(BuildFeedbackPanel());
        }

        /// <summary>
        /// Frame for the feedback panel: title row with a close button, then the form. 880×640 keeps the
        /// attachment rows and the syntax preview usable without turning the panel into a second window.
        /// </summary>
        private FrameworkElement BuildFeedbackPanel()
        {
            Border panel = new Border
            {
                Width = 880,
                Height = 640,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true
            };

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Text = "反馈",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            Button close = PanelCloseButton("FeedbackClose");
            close.Click += (sender, e) => CloseOverlay();
            Grid.SetColumn(close, 1);
            head.Children.Add(close);

            grid.Children.Add(head);
            Grid.SetRow(_feedbackPage, 1);
            grid.Children.Add(_feedbackPage);
            panel.Child = grid;
            return panel;
        }

        /// <summary>Small ✕ for panel headers: a third the weight of the caption button, same colour rules.</summary>
        private static Button PanelCloseButton(string automationId)
        {
            Button close = new Button
            {
                Width = 26,
                Height = 26,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                FocusVisualStyle = null,
                Foreground = Theme.TextSecondaryBrush,
                Template = BuildCaptionTemplate(),
                ToolTip = "关闭"
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(close, automationId);
            System.Windows.Automation.AutomationProperties.SetName(close, "关闭");
            close.Content = SmallCloseGlyph(close);
            close.MouseEnter += (sender, e) => close.Foreground = Theme.TextPrimaryBrush;
            close.MouseLeave += (sender, e) => close.Foreground = Theme.TextSecondaryBrush;
            return close;
        }

        /// <summary>Title-bar icon button: same box and hover as the caption buttons, custom glyph.</summary>
        private static Button IconButton(string automationId, string name, string tip,
                                         Func<Button, FrameworkElement> glyph, Action action)
        {
            Button button = new Button
            {
                Width = 36,
                Height = 33,
                Foreground = Theme.TextSecondaryBrush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                FocusVisualStyle = null,
                Template = BuildCaptionTemplate(),
                ToolTip = tip
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Content = glyph(button);
            button.MouseEnter += (sender, e) => button.Foreground = Theme.TextPrimaryBrush;
            button.MouseLeave += (sender, e) => button.Foreground = Theme.TextSecondaryBrush;
            button.Click += (sender, e) => action();
            Interactions.AttachSpring(button, 1.12, 0.94);
            return button;
        }

        /// <summary>
        /// Chat bubble with three dots, taken from the owner's reference art and scaled to the caption
        /// row: filled silhouette so it stays legible at 16 px, dots cut out with even-odd so they do not
        /// disappear into the body. Fill follows the button foreground like the ✕ does, which keeps it in
        /// the same grey -> white hover language as the other title-bar controls.
        /// </summary>
        private static FrameworkElement BuildFeedbackGlyph(Button owner)
        {
            Canvas canvas = new Canvas { Width = 17, Height = 17 };
            System.Windows.Media.PathGeometry shape = System.Windows.Media.PathGeometry.CreateFromGeometry(
                Geometry.Parse(
                "M401.493333 725.333333H213.333333a85.333333 85.333333 0 0 1-85.333333-85.333333V213.333333" +
                "a85.333333 85.333333 0 0 1 85.333333-85.333333h640a85.333333 85.333333 0 0 1 85.333334 85.333333" +
                "v426.666667a85.333333 85.333333 0 0 1-85.333334 85.333333h-187.818666l-96.853334 140.885334" +
                "a42.666667 42.666667 0 0 1-70.314666 0L401.493333 725.333333z" +
                "M320 490.581333a42.666667 42.666667 0 1 0 0-85.333333 42.666667 42.666667 0 0 0 0 85.333333z" +
                "m213.333333 0a42.666667 42.666667 0 1 0 0-85.333333 42.666667 42.666667 0 0 0 0 85.333333z" +
                "m213.333334 0a42.666667 42.666667 0 1 0 0-85.333333 42.666667 42.666667 0 0 0 0 85.333333z"));
            shape.FillRule = FillRule.EvenOdd;
            System.Windows.Shapes.Path bubble = new System.Windows.Shapes.Path
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Data = shape
            };
            bubble.SetBinding(Shape.FillProperty,
                new System.Windows.Data.Binding("Foreground") { Source = owner });
            Canvas.SetLeft(bubble, 0.5);
            Canvas.SetTop(bubble, 0.5);
            canvas.Children.Add(bubble);
            return canvas;
        }

        private static FrameworkElement SmallCloseGlyph(Button owner)
        {
            Canvas canvas = new Canvas { Width = 12, Height = 12 };
            canvas.Children.Add(SmallLine(owner, 2, 2, 10, 10));
            canvas.Children.Add(SmallLine(owner, 10, 2, 2, 10));
            return canvas;
        }

        private static Line SmallLine(Button owner, double x1, double y1, double x2, double y2)
        {
            Line line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            line.SetBinding(Shape.StrokeProperty,
                new System.Windows.Data.Binding("Foreground") { Source = owner });
            return line;
        }

        /// <summary>设备状态卡右上角齿轮的入口。第一个设置项是指示器样式。</summary>
        public void ShowDeviceSettings()
        {
            if (IsOverlayOpen) return;
            ShowOverlay(new DeviceSettingsDialog(this).Build());
        }

        /// <summary>
        /// 设备状态卡的指示器样式改了：直接通知那份已经建好的概览页，不重建它。
        /// 不能通过 _cache 找 —— 保存设置会清空页缓存，而屏幕上的旧实例还在，
        /// 从缓存里取不到就会“下拉选了没反应”。
        /// </summary>
        public void ApplyIndicatorStyle()
        {
            if (_overviewPage != null) _overviewPage.ApplyIndicatorStyle();
        }

        private FrameworkElement BuildTitleBar()
        {
            Grid bar = new Grid { Background = Theme.BgElevatedBrush };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                // 右上角三个要素的右边缘对齐内容区右边缘：减掉的 36 是 36 px 按钮里 ✕ 的
                // 右侧留白，对齐的是看得见的线条边缘。
                Margin = new Thickness(0, 0, 36, 0),
                RenderTransform = new TranslateTransform(0, 10)
            };

            // 芯片是按钮：点开状态面板，里面才放着「刷新 / 启动 Agent」。
            SolidColorBrush idleChip = new SolidColorBrush(Color.FromArgb(0x22, 0x2B, 0xD6, 0x7B));
            SolidColorBrush hoverChip = new SolidColorBrush(Color.FromArgb(0x38, 0x2B, 0xD6, 0x7B));
            idleChip.Freeze();
            hoverChip.Freeze();

            _agentStatus.Text = "传感器检测中…";
            _agentStatus.FontSize = 11.5;
            _agentStatus.Foreground = Theme.TextSecondaryBrush;

            _agentPill = new Button
            {
                Content = _agentStatus,
                Cursor = Cursors.Hand,
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = idleChip,
                BorderThickness = new Thickness(0),
                FocusVisualStyle = null,
                ToolTip = "传感器状态：点开查看并重启",
                Template = PillTemplate()
            };
            AutomationProperties.SetAutomationId(_agentPill, "AgentPill");
            _agentPill.MouseEnter += (sender, e) => _agentPill.Background = hoverChip;
            _agentPill.MouseLeave += (sender, e) => _agentPill.Background = idleChip;
            _agentPill.Click += (sender, e) => ShowAgentStatus();
            right.Children.Add(_agentPill);

            // Feedback lives in the title bar, not in the sidebar: it is opened on demand and closed
            // again, while the sidebar holds the screens that stay on screen.
            Button feedback = IconButton("FeedbackPill", "发送反馈", "发送反馈", BuildFeedbackGlyph, ShowFeedback);
            feedback.Margin = new Thickness(0, 0, 8, 0);
            right.Children.Add(feedback);

            right.Children.Add(CaptionButton(false, () => WindowState = WindowState.Minimized));
            right.Children.Add(CaptionButton(true, Close));

            Grid.SetColumn(right, 1);
            bar.Children.Add(right);

            bar.MouseLeftButtonDown += (sender, e) =>
            {
                if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
                else if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); }
            };

            return bar;
        }

        /// <summary>圆角半透明芯片：只画背景与内边距，不要系统按钮的灰色底盘。</summary>
        private static ControlTemplate PillTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        /// <summary>
        /// 标题栏左上角的图标：读构建时嵌进来的 64 px 帧（csc /resource）。用 exe 图标资源只能
        /// 拿到 32 px，缩到 20 px 会发虚。取不到就返回 null，标题栏退回纯文字。
        /// </summary>
        private static Image BuildAppMark(double size)
        {
            try
            {
                using (System.IO.Stream stream = typeof(SettingsWindow).Assembly
                    .GetManifestResourceStream("QuadVitals.Settings.mark.png"))
                {
                    if (stream == null) return null;

                    System.Windows.Media.Imaging.BitmapFrame frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                        stream, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    frame.Freeze();

                    Image image = new Image
                    {
                        Source = frame,
                        Width = size,
                        // 素材归一化到全白后压在深底上偏刺眼，这里压一档；再暗就调这个数。
                        Opacity = 0.86,
                        Stretch = Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center,
                        SnapsToDevicePixels = true
                    };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                    return image;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Button CaptionButton(bool isClose, Action action)
        {
            Button button = new Button
            {
                // 自绘图标放到系统标题栏的量级：可见尺寸 11 / 12 px、线宽 3.0，
                // 按钮 36×33 承载（再小字形的圆头就开始发糊）。
                Width = 36,
                Height = 33,
                Margin = new Thickness(0, 0, 0, 0),
                Foreground = Theme.TextSecondaryBrush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                // 默认 Aero2 模板 hover 会铺一层浅色底，在深色标题栏上很脏；
                // 白板模板下只改前景色，不出现任何底色。
                Template = BuildCaptionTemplate(),
                ToolTip = isClose ? "关闭" : "最小化"
            };

            System.Windows.Automation.AutomationProperties.SetAutomationId(
                button, isClose ? "CaptionClose" : "CaptionMinimize");
            System.Windows.Automation.AutomationProperties.SetName(
                button, isClose ? "关闭" : "最小化");
            button.Content = isClose ? BuildCloseGlyph(button) : BuildMinimizeGlyph(button);
            button.MouseEnter += (sender, e) => button.Foreground = Theme.TextPrimaryBrush;
            button.MouseLeave += (sender, e) => button.Foreground = Theme.TextSecondaryBrush;
            button.Click += (sender, e) => action();
            Interactions.AttachSpring(button, 1.12, 0.94);
            return button;
        }

        /// <summary>最小化：一条圆头粗横线。字体里的 en dash 只有 1 px 高，放大后仍然发虚。</summary>
        private static FrameworkElement BuildMinimizeGlyph(Button owner)
        {
            Canvas canvas = new Canvas { Width = 17, Height = 17 };
            canvas.Children.Add(CaptionLine(owner, 2.5, 8.5, 14.5, 8.5));
            return canvas;
        }

        /// <summary>关闭：两条圆头 45° 线交叉，笔画加粗后显得饱满。</summary>
        private static FrameworkElement BuildCloseGlyph(Button owner)
        {
            Canvas canvas = new Canvas { Width = 17, Height = 17 };
            canvas.Children.Add(CaptionLine(owner, 3, 3, 14, 14));
            canvas.Children.Add(CaptionLine(owner, 14, 3, 3, 14));
            return canvas;
        }

        private static Line CaptionLine(Button owner, double x1, double y1, double x2, double y2)
        {
            Line line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeThickness = 3.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            line.SetBinding(Shape.StrokeProperty,
                new System.Windows.Data.Binding("Foreground") { Source = owner });
            return line;
        }

        /// <summary>标题栏按钮模板：透明背景，只负责把字形居中画出。</summary>
        private static ControlTemplate BuildCaptionTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            template.VisualTree = border;
            return template;
        }

        private FrameworkElement BuildSidebar()
        {
            Border sidebar = new Border
            {
                Background = Theme.BgElevatedBrush,
                // 导航文字距窗口左边缘 48 px：36 + 导航项自身的 12 内边距 = 48，
                // 与标题栏品牌名同一条对齐线；滑块跟着一起内缩 12。
                // 顶部 64 = 页头块的高度（标题行 ~26 + 4 + 副标题行 ~16 + 页头下边距 16 + 2），
                // 让第一个导航项的顶边落在第一张卡片的上边缘，和内容区一起“从卡片开始”。
                Padding = new Thickness(36, 64, 36, 12)
            };

            // 滑块在列表背后，随选中项上下滑动（单色品牌绿，不需要逐项换色）。
            _navGliderShift = new TranslateTransform(0, 0);
            _navGlider = new Border
            {
                Height = 44,
                CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Top,
                Background = BuildNavGliderBrush(),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Theme.AccentGreen,
                    BlurRadius = 18,
                    ShadowDepth = 0,
                    Opacity = 0.35
                },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                RenderTransform = _navGliderShift
            };
            AutomationProperties.SetAutomationId(_navGlider, "NavGlider");

            Grid host = new Grid();
            host.Children.Add(_navGlider);

            StackPanel stack = new StackPanel();
            AddNav(stack, "overview", "概览");
            AddNav(stack, "elements", "显示项");
            AddNav(stack, "network", "通用设置");
            AddNav(stack, "about", "关于");

            host.Children.Add(stack);
            sidebar.Child = host;
            return sidebar;
        }

        private static Brush BuildNavGliderBrush()
        {
            // 左端实、右端透：文字只落在实色区，行尾自然淡出（和预览里的单色版一致）。
            LinearGradientBrush brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0x1D, 0xB9, 0x54), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0x1D, 0xB9, 0x54), 0.65));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x5A, 0x1D, 0xB9, 0x54), 1));
            brush.Freeze();
            return brush;
        }

        private void MoveNavGlider(int index)
        {
            if (_navGlider == null || _navGliderShift == null) return;
            _navGlider.Visibility = Visibility.Visible;
            double target = index * NavStep;

            if (!_navGliderPlaced)
            {
                // 首次显示不滑动：窗口一打开就落位，然后 200 ms 淡入（不要从概览飞过去）。
                _navGliderPlaced = true;
                _navGliderShift.BeginAnimation(TranslateTransform.YProperty, null);
                _navGliderShift.Y = target;
                _navGlider.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
                return;
            }

            if (Math.Abs(_navGliderShift.Y - target) < 0.5) return;

            // 关键：先把“现在看到的位置”写成基准值，再开新动画。
            // BeginAnimation(null) 会把属性打回旧的基准值（上一次落位那一行），
            // 连续点几下就会先弹回原位再滑——看起来就是“乱跳”。
            double from = _navGliderShift.Y;
            _navGliderShift.BeginAnimation(TranslateTransform.YProperty, null);
            _navGliderShift.Y = from;

            // 侧栏滑轨：350 ms cubic-bezier(0.22, 1, 0.36, 1)——快起、长尾、不过冲，
            // 不给 BackEase 那种回弹：位置动画一旦过冲，滑块会越过目标行再退回来。
            DoubleAnimationUsingKeyFrames slide = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(350)
            };
            slide.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0)));
            slide.KeyFrames.Add(new SplineDoubleKeyFrame(target, KeyTime.FromPercent(1.0))
            {
                KeySpline = new KeySpline(0.22, 1, 0.36, 1)
            });
            _navGliderShift.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void AddNav(StackPanel stack, string key, string title)
        {
            SidebarItem item = new SidebarItem(title);
            item.Clicked += selected => ShowPage(key);
            _navItems.Add(item);
            item.Tag = key;
            stack.Children.Add(item);
        }

        public void ShowPage(string key)
        {
            Func<FrameworkElement> factory;
            if (!_pages.TryGetValue(key, out factory)) return;

            _currentPage = key;

            int selectedIndex = 0;
            for (int i = 0; i < _navItems.Count; i++)
            {
                SidebarItem nav = _navItems[i];
                bool selected = (string)nav.Tag == key;
                nav.Selected = selected;
                if (selected) selectedIndex = i;
            }
            MoveNavGlider(selectedIndex);

            FrameworkElement page;
            if (!_cache.TryGetValue(key, out page))
            {
                page = factory();
                _cache[key] = page;
            }
            else
            {
                IRefreshable refreshable = page as IRefreshable;
                if (refreshable != null) refreshable.Refresh();
            }

            _host.Content = page;
        }

        public AppSettings CurrentSettings { get { return _settings; } }

        public void ReloadSettings()
        {
            LoadSettings();
            _cache.Clear();
            ShowPage(_currentPage);
        }

        /// <summary>Saves and tells the user what will and will not take effect immediately.</summary>
        /// <summary>
        /// Persists a change made by an auto-saving control (no 保存 button, no page rebuild, no
        /// toast). Used by the 通用设置 page, where flipping a switch IS the confirmation — a
        /// toast on every toggle would be noise. Band width is not involved, so the
        /// "re-tick the toolbar" prompt cannot be needed here.
        /// </summary>
        /// <summary>
        /// Restarts Explorer so the band is recreated with the width that was just saved. Killing
        /// the shell is enough — Windows starts it again within a second or two — but the fallback
        /// launch covers the case where it does not. This window is a separate process and stays
        /// alive; the band re-registers itself from the registry, no manual re-tick needed.
        /// </summary>
        private static void RestartExplorer()
        {
            try
            {
                foreach (System.Diagnostics.Process shell in System.Diagnostics.Process.GetProcessesByName("explorer"))
                {
                    try { shell.Kill(); }
                    catch (Exception) { }
                }

                System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(2000);
                    try
                    {
                        if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
                        {
                            System.Diagnostics.Process.Start("explorer.exe");
                        }
                    }
                    catch (Exception)
                    {
                        // the shell will come back on its own on the next logon at the latest
                    }
                });
            }
            catch (Exception)
            {
                // Killing the shell can be denied (locked-down session): the saved width still
                // applies the next time Explorer starts, so there is nothing else to do here.
            }
        }

        /// <summary>
        /// Saves an edit that can change the band's width (element spacing) and rebuilds the bar
        /// when it did: the width is fixed at creation time, so a spacing change only shows up
        /// after Explorer restarts the band.
        /// </summary>
        public void SaveSettingsAndRebuild(AppSettings edited)
        {
            if (edited == null) return;
            int before = BandLayout.TotalWidth(_settings);
            edited.Save(AppSettings.DefaultPath);
            _settings = edited;
            _cache.Clear();
            if (BandLayout.TotalWidth(edited) != before) RestartExplorer();
        }

        public void SaveSettingsQuietly(AppSettings edited)
        {
            if (edited == null) return;
            edited.Save(AppSettings.DefaultPath);
            _settings = edited;
            _cache.Clear();
        }

        public void SaveSettings(AppSettings edited, int widthBefore)
        {
            int widthAfter = BandLayout.TotalWidth(edited);
            edited.Save(AppSettings.DefaultPath);
            _settings = edited;
            _cache.Clear();

            if (widthAfter != widthBefore)
            {
                // 宽度只在 band 创建时确定，所以宽度一变就直接重启 Explorer 让它按新宽度重建；
                // 任务栏闪一下并当场变成新宽度本身就是反馈，不要弹窗要求手动重新勾选。
                RestartExplorer();
            }

            ShowPage(_currentPage);
        }

        /// <param name="persistent">
        /// Must be true for anything the user has to see: those become a centred dialog. Passing
        /// false requests a transient toast, which is now **silent on purpose** — the corner and
        /// bottom slots both covered something, and every
        /// success path already shows its result in the page itself.
        /// </param>
        public void ShowNotice(string text, bool persistent = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!persistent) return;
            ShowOverlay(new NoticeDialog(this).Build("提示", text));
        }

        /// <summary>当前通道状态：每次都重新读快照，面板与芯片用同一个口子，不会两边说法不一致。</summary>
        public AgentStateInfo ReadAgentState()
        {
            if (_statusReader == null && (DateTime.UtcNow - _statusReaderAttemptUtc).TotalSeconds > 2)
            {
                _statusReaderAttemptUtc = DateTime.UtcNow;
                _statusReader = MetricsChannel.Reader.TryOpen();
            }

            AgentStateInfo info = new AgentStateInfo();
            info.ChannelOpen = _statusReader != null;
            MetricsSnapshot snapshot = _statusReader == null ? null : _statusReader.TryRead();
            if (snapshot == null) return info;

            info.HasSnapshot = true;
            info.AgeSeconds = snapshot.Age(DateTime.UtcNow).TotalSeconds;
            info.LastUpdateLocal = snapshot.TimestampUtc.ToLocalTime();
            info.Alive = snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout);
            // 看门狗看的是“能信任的”标志：标记有效但数值越界，也算这一项没有数据。
            info.Flags = MetricSanity.EffectiveFlags(snapshot);
            return info;
        }

        /// <summary>标题栏芯片点开的面板。</summary>
        public void ShowAgentStatus()
        {
            ShowOverlay(new AgentStatusDialog(this).Build(ReadAgentState()));
            _agentPanelShown = true;
        }

        /// <summary>
        /// 「启动 Agent」：拉起进程，等它写出第一帧再刷新面板。计划任务不可用时（开发版）
        /// 这里直接启动 exe，温度会显示 --，但其余指标正常。
        /// </summary>
        public void StartAgentFromDialog()
        {
            string error = AgentLauncher.Start();
            if (error != null)
            {
                ShowNotice(error, true);
                return;
            }

            ScheduleAgentPanelRefresh();
        }

        public void SetAgentStatus(bool alive, double ageSeconds)
        {
            // 芯片只说健康度：秒数会每帧跳，放在面板里。
            _agentStatus.Text = alive ? "传感器 · 正常" : "传感器 · 离线";
            _agentStatus.Foreground = alive ? Theme.AccentGreenBrush : Theme.AccentRedBrush;
        }

        /// <summary>
        /// 每帧调一次：离线就按退避表自动拉起 Agent（0/3/10/30/60 秒，共 5 次）。
        /// 次数用完就不再动手，交给面板里的手动按钮。
        /// </summary>
        private void TrackAgentHealth(AgentStateInfo state)
        {
            // 旧进程已经退出、新进程还没写出第一帧：这段“离线”是重启的一部分，
            // 自动恢复如果这时插一脚，会和按钮拉起的实例抢同一个计划任务。
            if (_agentRestartInProgress)
            {
                _deadTicks = 0;
                return;
            }

            if (state.Alive)
            {
                _deadTicks = 0;
                _autoStartAttempts = 0;
                _nextAutoStartUtc = DateTime.MinValue;
                TrackMetricHealth(state);
                return;
            }

            _metricWatch.AgentOffline();
            _deadTicks++;
            if (!AgentAutoRecovery.ShouldAttempt(false, _deadTicks, _autoStartAttempts,
                    DateTime.UtcNow, _nextAutoStartUtc)) return;

            _autoStartAttempts++;
            _nextAutoStartUtc = DateTime.UtcNow.AddSeconds(AgentAutoRecovery.DelayAfterAttempt(_autoStartAttempts));

            if (AgentLauncher.Start() == null) ScheduleAgentPanelRefresh();
        }

        /// <summary>
        /// 面板上的「重启传感器」：先让正在运行的 Agent 自己退出，再用登录计划任务拉起一个新进程。
        /// 温度与显卡的读取会话都在进程启动时建立，驱动被重置或 NVML 掉线后只有重开进程能重新读到；
        /// 只让 Agent 再发布一帧（原来的「刷新」）改不了这件事。
        /// </summary>
        public void RestartAgentFromDialog()
        {
            RestartAgentFromDialog(null);
        }

        /// <param name="reason">
        /// Non-null when the data watchdog asked for this restart: the attempt stays silent and
        /// leaves a line in <c>agent-auto.log</c> instead of a dialog over whatever is full-screen.
        /// </param>
        private void RestartAgentFromDialog(string reason)
        {
            if (_agentRestartInProgress) return;
            if (reason == null) _metricWatch.Reset();
            _agentRestartInProgress = true;
            _deadTicks = 0;

            System.Threading.Tasks.Task.Run(() =>
            {
                string error = RestartAgent();
                TryOnDispatcher(() =>
                {
                    _agentRestartInProgress = false;
                    if (reason != null) LogAgentRecovery(reason, error);
                    if (error != null)
                    {
                        // 自动尝试不在全屏游戏上弹窗；原因留在 agent-auto.log。
                        if (reason == null) ShowNotice(error, true);
                        return;
                    }
                    ScheduleAgentPanelRefresh(250);
                });
            });
        }

        /// <summary>
        /// Agent 活着但某一项指标停了：持续缺失才换进程重新建立采样会话，单帧抖动不动手。
        /// 只对这台机器本来该有数据的指标生效（CPU/内存/系统盘、曾经有效过的、NVIDIA 显卡）。
        /// </summary>
        private void TrackMetricHealth(AgentStateInfo state)
        {
            MetricFlags missing;
            if (!_metricWatch.Observe(state.Flags, true, HardwareInfo.HasNvidiaGpu,
                    DateTime.UtcNow, out missing)) return;
            RestartAgentFromDialog(MetricWatchdog.Describe(missing));
        }

        /// <summary>Breadcrumb for watchdog-triggered restarts; capped like the agent's temperature log.</summary>
        private static void LogAgentRecovery(string reason, string error)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(AppSettings.DefaultPath);
                if (string.IsNullOrEmpty(dir)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "agent-auto.log");
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > 64 * 1024) File.Delete(path);
                File.AppendAllText(path,
                    DateTime.Now.ToString("s") + "  检测到 " + reason + " 数据不可用，自动重启：" +
                    (error == null ? "完成" : "失败 - " + error) + Environment.NewLine);
            }
            catch (Exception)
            {
                // 诊断日志失败不能影响重启本身
            }
        }

        /// <summary>Blocking half of the restart; runs on a worker thread, never on the UI thread.</summary>
        private static string RestartAgent()
        {
            AgentLauncher.RequestExit();
            if (!AgentLauncher.WaitForExit(6000))
            {
                return "传感器没有退出：可能被安全软件拦住。请稍后重试，或手动结束 QuadVitals.Agent.exe";
            }

            // Only frames published after the old process is gone count as proof the new one
            // started. A frame the old process wrote between the click and its exit would
            // otherwise look like success and let auto-recovery race the restart.
            DateTime exitedUtc = DateTime.UtcNow;
            string error = AgentLauncher.Start();
            if (error != null) return error;

            if (!AgentLauncher.WaitForStart(5000))
            {
                return "传感器已请求启动，但进程没有出现。请检查登录计划任务是否被禁用";
            }
            if (!WaitForFrameAfter(exitedUtc, 12000))
            {
                return "传感器已重新启动，但还没有写出新数据。如果温度仍是 --，请检查登录计划任务是否在运行";
            }
            return null;
        }

        /// <summary>True once a frame newer than <paramref name="afterUtc"/> is alive on the channel.</summary>
        private static bool WaitForFrameAfter(DateTime afterUtc, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen())
                {
                    if (reader != null)
                    {
                        MetricsSnapshot snapshot = reader.TryRead();
                        if (snapshot != null && snapshot.TimestampUtc > afterUtc &&
                            snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout))
                        {
                            return true;
                        }
                    }
                }
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        /// <summary>Marshals back to the window thread; a window that closed mid-restart has nowhere to go.</summary>
        private void TryOnDispatcher(Action action)
        {
            if (action == null) return;
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(action);
            }
            catch (Exception)
            {
                // Shutting down: the restart itself already finished, only the panel is gone.
            }
        }

        private void ScheduleAgentPanelRefresh(int delayMs = 900)
        {
            DispatcherTimer wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            wait.Tick += (sender, e) =>
            {
                wait.Stop();
                if (IsOverlayOpen && _agentPanelShown) ShowAgentStatus();
            };
            wait.Start();
        }
    }

    internal interface IRefreshable
    {
        void Refresh();
    }
}
