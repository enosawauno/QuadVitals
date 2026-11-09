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
    internal sealed partial class SettingsWindow : Window
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
        private FrameworkElement _feedbackPanel;
        private Grid _feedbackHead;
        private TextBlock _feedbackHeading;
        private bool _overlayClosesOnBackdrop;
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
        // 保存后仍留在屏幕上、因此不能立刻 Dispose 的页面；下一次导航/关窗时释放。
        private FrameworkElement _keptPage;

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
            // Auto + ClearTypeHint: let WPF use subpixel AA on the opaque card surfaces even though
            // the window itself is layered for the rounded corners. If the driver refuses, WPF
            // falls back to grayscale on its own.
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
            RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);

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
            // "feedback" and "general" are not pages: the flags still work, they open the title-bar panel
            // and the title-bar gear dialog.
            bool startFeedback = string.Equals(startPage, "feedback", StringComparison.OrdinalIgnoreCase);
            bool startGeneral = string.Equals(startPage, "general", StringComparison.OrdinalIgnoreCase);
            ShowPage(_pages.ContainsKey(startPage) ? startPage : "overview");
            if (startFeedback) Loaded += (sender, e) => ShowFeedback();
            if (startGeneral) Loaded += (sender, e) => ShowGeneralSettings();

            // 关闭前把网速曲线落盘：下次打开接着上一张图的右端继续跑。
            Closed += (sender, e) =>
            {
                OverviewPage.FlushHistory();
                DisposeKeptPage();
            };

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
                // RDP 接入/断开、Tier 与电源变化不会都发事件；每秒读一次足够便宜。
                ApplyRenderPolicy();
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
            Border backdrop = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x00, 0x00, 0x00))
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(backdrop, "OverlayBackdrop");
            // Only panels that ask for it close on an outside click: the settings dialogs have a
            // draft and a 取消 button, and dimming them by accident would throw that away.
            backdrop.MouseLeftButtonUp += (sender, e) =>
            {
                if (_overlayClosesOnBackdrop) CloseOverlay();
            };
            _overlay.Children.Add(backdrop);
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
        public void ShowOverlay(FrameworkElement content, bool backdropCloses = false)
        {
            if (content == null || _overlay == null) return;
            _overlayClosesOnBackdrop = backdropCloses;

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

        /// <summary>风扇卡右上角齿轮：选择卡片显示哪一路风扇。</summary>
        public void ShowFanSettings()
        {
            if (IsOverlayOpen) return;
            FanCard card = _overviewPage == null ? null : _overviewPage.FanCard;
            ShowOverlay(new FanSettingsDialog(this, card).Build());
        }

        /// <summary>Title-bar gear: the general settings live in a panel, not in a sidebar page.</summary>
        public void ShowGeneralSettings()
        {
            if (IsOverlayOpen) return;
            ShowOverlay(new GeneralSettingsDialog(this).Build());
        }

        /// <summary>需要确认的操作（例如切换成常驻动画）：居中提示 + 确认/取消。</summary>
        public void ShowConfirm(string title, string body, string confirmText, Action onConfirm,
                                bool danger = false, Action onCancel = null)
        {
            ShowOverlay(new ConfirmDialog(this).Build(title, body, confirmText, onConfirm, danger, onCancel));
        }

        /// <summary>确认框，正文由调用方排好（结构化：结论 + 数据 + 问句）。</summary>
        public void ShowConfirmContent(string title, FrameworkElement body, string confirmText, Action onConfirm,
                                       bool danger = false, Action onCancel = null,
                                       FrameworkElement titleExtra = null)
        {
            ShowOverlay(new ConfirmDialog(this).BuildWithContent(title, body, confirmText, onConfirm, danger,
                                                                 onCancel, titleExtra));
        }

        /// <summary>
        /// Title-bar bubble: opens the feedback form as a panel over the window. The form keeps its own
        /// draft, attachment and preview logic; the panel only supplies the frame and a close button.
        /// </summary>
        public void ShowFeedback()
        {
            if (IsOverlayOpen) return;
            if (_feedbackPage == null)
            {
                _feedbackPage = new FeedbackPage(this, true);
                // The card title is the panel's title now ("反馈内容" / "反馈已保存").
                _feedbackPage.HeadingChanged += text =>
                {
                    // The result is a short block; keeping the form's 580 px around it left most of
                    // the panel empty. Let it fit its content until the form comes back, and drop the
                    // title row there - the result carries its own headline and close button.
                    bool result = _feedbackPage.ShowingResult;
                    if (_feedbackHead != null)
                    {
                        _feedbackHead.Visibility = result
                            ? Visibility.Collapsed
                            : Visibility.Visible;
                    }
                    if (_feedbackHeading != null) _feedbackHeading.Text = text;
                    // 结果态也用同一块板：只收标题行，不改面板尺寸（改尺寸会让两次弹出的位置都跳一下）。
                    if (_feedbackPanel != null)
                    {
                        _feedbackPanel.Width = FeedbackPanelWidth;
                        _feedbackPanel.Height = FeedbackPanelHeight;
                    }
                };
                // Built once and reused: a UserControl can only have one logical parent, so rebuilding
                // the frame around the same form is what WPF rejects when the panel is opened twice.
                _feedbackPanel = BuildFeedbackPanel();
            }
            ShowOverlay(_feedbackPanel, true);
        }

        /// <summary>
        /// Frame for the feedback panel: title row with a close button, then the form. 640×427 is the
        /// same board the settings dialogs use; the form scrolls inside it when attachments pile up.
        /// </summary>
        private FrameworkElement BuildFeedbackPanel()
        {
            Border panel = new Border
            {
                Width = FeedbackPanelWidth,
                Height = FeedbackPanelHeight,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true
            };
            AutomationProperties.SetAutomationId(panel, "FeedbackPanel");

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 标题与右上角 ✕：表单态需要它们（这是唯一的显式关闭按钮），结果态不需要——
            // 那边有大标题和倒计时关闭，所以 ShowSuccess 之后整条标题行收起来。
            Grid head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _feedbackHeading = new TextBlock
            {
                Text = "反馈",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(_feedbackHeading);

            Button close = PanelCloseButton("FeedbackClose");
            close.Click += (sender, e) => CloseOverlay();
            Grid.SetColumn(close, 1);
            head.Children.Add(close);

            _feedbackHead = head;
            grid.Children.Add(head);
            Grid.SetRow(_feedbackPage, 1);
            grid.Children.Add(_feedbackPage);
            panel.Child = grid;
            return panel;
        }

        /// <summary>
        /// 与通用设置、网络、设备状态的齿轮弹窗同一块板（640×427）：反馈不再比它们大一圈，
        /// 也不再随结果态缩成另一个尺寸，四个入口的浮层尺寸一致。
        /// </summary>
        private const double FeedbackPanelWidth = 640;
        private const double FeedbackPanelHeight = 427;

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

        /// <summary>Title-bar icon button: same box and hover as the caption buttons, custom glyph.</summary>
        private static Button IconButton(string automationId, string name, string tip,
                                         Func<Button, FrameworkElement> glyph, Action action)
        {
            Button button = new Button
            {
                Width = 30,
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

            // 把 1024 单位的图形等比缩到 16 px 并居中放进 17 px 画布：坐标自己算，不靠
            // Canvas.Left/Top（那套会按整像素/偶数吸附，1 px 的偏差调不上去）。这样气泡的
            // 墨迹中心和齿轮、最小化、关闭一样落在画布 8.5 px 线上。
            Rect bounds = shape.Bounds;
            double scale = 16.0 / Math.Max(bounds.Width, bounds.Height);
            TransformGroup fit = new TransformGroup();
            fit.Children.Add(new ScaleTransform(scale, scale));
            fit.Children.Add(new TranslateTransform(
                (17 - bounds.Width * scale) / 2 - bounds.X * scale,
                (17 - bounds.Height * scale) / 2 - bounds.Y * scale));
            shape.Transform = fit;

            System.Windows.Shapes.Path bubble = new System.Windows.Shapes.Path
            {
                Stretch = Stretch.None,
                Data = shape
            };
            bubble.SetBinding(Shape.FillProperty,
                new System.Windows.Data.Binding("Foreground") { Source = owner });
            canvas.Children.Add(bubble);
            return canvas;
        }

        /// <summary>
        /// 标题栏齿轮：与右边三个图标同一套“实心、粗笔画”的视觉语言。字体里的齿轮字形是细线描边，
        /// 夹在实心气泡与 3 px 的 ✕ 中间看着像另一个控件，所以这里用 17 px 画布拼一个实心齿轮，
        /// 质量与气泡相当（圆盘 + 8 个齿，中间挖孔）。
        /// </summary>
        private static FrameworkElement BuildGearGlyph(Button owner)
        {
            const double size = 17;
            const double center = size / 2;
            // 齿顶半径 7.2、盘半径 5.0：外廓比气泡（16）小一圈，与 ✕ 的 11 px 笔画区组成同一条视觉带。
            Geometry body = new EllipseGeometry(new Point(center, center), 5.0, 5.0);
            for (int i = 0; i < 8; i++)
            {
                RectangleGeometry tooth = new RectangleGeometry(
                    new Rect(center - 1.4, center - 7.2, 2.8, 3.6));
                tooth.Transform = new RotateTransform(i * 45, center, center);
                body = Geometry.Combine(body, tooth, GeometryCombineMode.Union, null);
            }
            Geometry gear = Geometry.Combine(body,
                new EllipseGeometry(new Point(center, center), 2.0, 2.0),
                GeometryCombineMode.Exclude, null);
            gear.Freeze();

            System.Windows.Shapes.Path path = new System.Windows.Shapes.Path
            {
                Data = gear,
                Width = size,
                Height = size
            };
            path.SetBinding(Shape.FillProperty,
                new System.Windows.Data.Binding("Foreground") { Source = owner });

            Canvas canvas = new Canvas { Width = size, Height = size };
            canvas.Children.Add(path);
            return canvas;
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
                // 芯片到齿轮的隔距与四个图标之间一致（8 + 按钮左留白 6.5）：整组看起来是排在一起的。
                Margin = new Thickness(0, 0, 8, 0),
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

            // 通用设置只剩一个开关，挂在标题栏齿轮上，不再占侧栏一整页。四个图标按钮之间不留
            // 外边距（紧凑成一组），间距由按钮宽度（30）减字形（17）得出。
            Button general = IconButton("GeneralPill", "通用设置", "通用设置", BuildGearGlyph, ShowGeneralSettings);
            right.Children.Add(general);

            // Feedback lives in the title bar, not in the sidebar: it is opened on demand and closed
            // again, while the sidebar holds the screens that stay on screen.
            Button feedback = IconButton("FeedbackPill", "发送反馈", "发送反馈", BuildFeedbackGlyph, ShowFeedback);
            right.Children.Add(feedback);

            right.Children.Add(CaptionButton(false, () => WindowState = WindowState.Minimized));
            right.Children.Add(CaptionButton(true, Close));

            Grid.SetColumn(right, 1);
            bar.Children.Add(right);

            bar.MouseLeftButtonDown += (sender, e) =>
            {
                // 双击不最大化：这个窗口的布局是按 1280×853 排的，拉满屏只会把卡片拉伸得很难看。
                if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); }
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
                // 自绘图标放到系统标题栏的量级：可见尺寸 11 / 12 px、线宽 2.5–3.0，
                // 按钮 30×33 承载（再小字形的圆头就开始发糊）。宽度 30 是为了让四个图标
                // 紧凑成一组：字形 17，左右各 6.5。
                Width = 30,
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
            AddNav(stack, "overview", "仪表盘");
            AddNav(stack, "elements", "任务栏");
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

            // 缓存被清空后当前页可能被“保留”在屏幕上；换页时它才真正离开视觉树，这里补 Dispose。
            if (_keptPage != null && ReferenceEquals(_host.Content, _keptPage)) DisposeKeptPage();
            _host.Content = page;
            // 窗口建好之后再读一次 Tier：静态初始化时可能还没完成首帧渲染。
            ApplyRenderPolicy();
        }

        private void ApplyRenderPolicy()
        {
            AppSettings s = _settings;
            RenderEnvironment.ConfigureBatteryPolicy(s == null || s.PauseAnimationsOnBattery != false);

            string policy = s == null ? AppSettings.RenderPolicyAlways : s.RenderPolicy;
            bool pauseWhenUnfocused = string.Equals(policy, AppSettings.RenderPolicyUnfocused,
                StringComparison.OrdinalIgnoreCase);
            bool pauseInFullscreen = string.Equals(policy, AppSettings.RenderPolicyFullscreen,
                StringComparison.OrdinalIgnoreCase);
            // ConfigureRenderPolicy refreshes the environment itself (including the fullscreen
            // probe), so the 1 Hz policy tick never needs a second call.
            RenderEnvironment.ConfigureRenderPolicy(pauseWhenUnfocused, pauseInFullscreen);
        }

        public AppSettings CurrentSettings { get { return _settings; } }

        public void ReloadSettings()
        {
            LoadSettings();
            DisposeCachedPages();
            _cache.Clear();
            ShowPage(_currentPage);
        }

        /// <summary>
        /// 页面被从缓存里丢掉时要先 Dispose：旧页面离开视觉树只停止渲染
        /// （<c>Unloaded</c>），它的 <c>DispatcherTimer</c> 仍被 Dispatcher 持有，
        /// 于是每次保存都多一个每秒读共享内存、更新一棵看不见的树的定时器。
        /// </summary>
        private void DisposeCachedPages(bool keepCurrent = false)
        {
            FrameworkElement current = _host.Content as FrameworkElement;
            bool overviewDisposed = false;
            foreach (FrameworkElement page in _cache.Values)
            {
                if (keepCurrent && ReferenceEquals(page, current))
                {
                    // 当前页还在屏幕上：现在 Dispose 会把它的帧循环/定时器一起杀掉，
                    // 下一次导航或关窗时再释放（否则保存一次动效设置，风扇就永远不转了）。
                    _keptPage = page;
                    continue;
                }
                if (ReferenceEquals(page, _overviewPage)) overviewDisposed = true;
                IDisposable disposable = page as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
            if (overviewDisposed) _overviewPage = null;
        }

        private void DisposeKeptPage()
        {
            if (_keptPage == null) return;
            if (ReferenceEquals(_keptPage, _overviewPage)) _overviewPage = null;
            IDisposable disposable = _keptPage as IDisposable;
            if (disposable != null) disposable.Dispose();
            _keptPage = null;
        }

        /// <summary>Saves and tells the user what will and will not take effect immediately.</summary>
        /// <summary>
        /// Persists a change made by an auto-saving control (no 保存 button, no page rebuild, no
        /// toast). Used by the general settings dialog, where flipping a switch IS the confirmation — a
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
            DisposeCachedPages();
            _cache.Clear();
            ShowPage(_currentPage);
            if (BandLayout.TotalWidth(edited) != before) RestartExplorer();
        }

        public void SaveSettingsQuietly(AppSettings edited)
        {
            if (edited == null) return;
            edited.Save(AppSettings.DefaultPath);
            _settings = edited;
            // 当前页保持存活：动效设置、颜色、调色板都会走这条路，Dispose 掉风扇卡等于让
            // “常驻动画”刚保存就永久停转。其他缓存页照旧释放，导航回来时重建。
            DisposeCachedPages(true);
            _cache.Clear();
            ApplyRenderPolicy();
        }

        public void SaveSettings(AppSettings edited, int widthBefore)
        {
            int widthAfter = BandLayout.TotalWidth(edited);
            edited.Save(AppSettings.DefaultPath);
            _settings = edited;
            DisposeCachedPages();
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

    }

    internal interface IRefreshable
    {
        void Refresh();
    }
}
