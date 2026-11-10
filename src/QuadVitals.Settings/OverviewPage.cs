using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Threading.Tasks;
using System.Windows.Threading;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 概览：上面一张全宽“设备状态”卡，四格依次是 CPU / 内存 / 显卡 / 磁盘（小标签 + 大数值 +
    /// 单位 + 分段条 + 说明）；下面左侧是“当前网速”卡，
    /// 带 60 秒 sparkline。显存/显卡温度进任务栏与 Tooltip，概览只占一格。
    /// 数据来自共享内存，与任务栏条同源。
    /// </summary>
    internal sealed partial class OverviewPage : UserControl, IRefreshable, IDisposable
    {
        // 曲线点密度 = 实时采集率：每个 tick 落一个点（默认 1 Hz），60 点 = 60 秒窗口。
        // 降到“2 秒一个点”会让曲线的时间分辨率只有数值的一半，所以宁可让 EMA + 二项平滑
        // 去压锯齿，也不靠降采样。
        private const int HistoryLength = 60;
        private const int HistogramEveryTicks = 1;
        private const string MaskedIp = "••••••••••";
        private const double IpValueFontSize = 15;

        private readonly SettingsWindow _window;

        private readonly MetricTile _cpuTile = new MetricTile("CPU · 占用", Theme.AccentGreen);
        private readonly MetricTile _memoryTile = new MetricTile("内存 · 占用", Theme.AccentGreen);
        private readonly MetricTile _gpuTile = new MetricTile("显卡 · 占用", Theme.AccentGreen);
        private readonly MetricTile _diskTile = new MetricTile("磁盘 · 占用", Theme.AccentGreen);
        private readonly FanCard _fanCard = new FanCard();
        private readonly PowerCard _powerCard = new PowerCard();

        private readonly TextBlock _downValue = new TextBlock();
        private readonly TextBlock _upValue = new TextBlock();
        // 上下行各一张小图，各自缩放；不再共用刻度，也不会互相压住端点。
        private readonly TextBlock _upPeak = new TextBlock();
        private readonly TextBlock _downPeak = new TextBlock();
        private readonly Sparkline _upSpark = new Sparkline(Theme.AccentBlue, double.NaN, double.NaN);
        private readonly Sparkline _downSpark = new Sparkline(Theme.AccentGreenSoft, double.NaN, double.NaN);

        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly DispatcherTimer _ipTimer = new DispatcherTimer();
        private Window _host;
        private bool _renderSuspended;
        private readonly TextBlock _lanIpValue = new TextBlock();
        private readonly TextBlock _publicIpValue = new TextBlock();
        private FlatButton _ipCopy;
        private FlatButton _eyeButton;
        private FlatButton _settingsButton;
        private FlatButton _deviceSettingsButton;
        private Line _eyeSlash;
        private bool _ipsMasked;
        private bool _publicIpFetching;
        private DateTime _publicIpFetchedUtc = DateTime.MinValue;
        private string _lanIp = string.Empty;
        private string _publicIp = string.Empty;
        private string _lanAdapterName = string.Empty;
        private DateTime _lanCheckedUtc = DateTime.MinValue;

        private static readonly Brush IpValueBrush = IpValueColor();

        private static Brush IpValueColor()
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            brush.Freeze();
            return brush;
        }

        private static readonly string[] PublicIpEndpoints =
        {
            "https://ipv4.icanhazip.com",
            "https://icanhazip.com",
            "https://ifconfig.me/ip"
        };

        // 曲线历史是进程级共享的：换页或重建页面不丢点，进程退出时落盘，下次打开接着右端继续跑。
        private static NetHistory _sharedHistory;
        private static DateTime _historySavedUtc = DateTime.MinValue;
        private static bool _historyDirty;
        private const int HistorySaveSeconds = 5;
        private int _tick;
        private MetricsChannel.Reader _reader;
        private DateTime _readerAttemptUtc = DateTime.MinValue;

        public OverviewPage(SettingsWindow window)
        {
            _window = window;
            _fanCard.OpenSettings = () => _window.ShowFanSettings();
            // 面积填充：两张图分开画，不会互相遮盖；基线贴底后，面积自然就是
            // “从 0 到峰值的轮廓”。
            _upSpark.ShowArea = true;
            _downSpark.ShowArea = true;
            // 时间轴固定 60 槽：点数不足时右侧对齐、步长固定，不拉伸曲线；
            // 左侧空隙由布局吸收，不改曲线的生长方式。
            _upSpark.SlotCount = HistoryLength;
            _downSpark.SlotCount = HistoryLength;
            Content = BuildLayout();
            ApplyIndicatorStyle();

            // 刷新周期与 Agent 的发布周期是同一个设置值：曲线每个周期落一个点，
            // 数值也按同一节奏更新，两边不会出现“曲线比数字慢”的情况。
            _timer.Interval = RefreshInterval();
            _timer.Tick += (sender, e) => Refresh();

            _ipTimer.Interval = TimeSpan.FromMinutes(5);
            _ipTimer.Tick += (sender, e) => RefreshPublicIp(true);

            // 窗口变窄时说明行要换字号：布局完成后再量，ActualWidth 才是真实列宽。
            SizeChanged += (sender, e) => FitCaptionFont();

            Loaded += (sender, e) =>
            {
                // 只在页面上屏时跑定时器：页面被重建/换页时不会再留一个后台定时器继续往
                // 已经不可见的曲线里塞点。
                _timer.Start();
                Refresh();
                RefreshPublicIp(false);
                StartHardwareInfoLoad();
                _ipTimer.Start();
                HookHostWindow();
            };
            Unloaded += (sender, e) =>
            {
                _timer.Stop();
                _ipTimer.Stop();
                UnhookHostWindow();
                SaveHistory(true);
            };
        }

        /// <summary>
        /// 最小化后不再每秒刷一遍：概览每次刷新都会让重绘区域交回合成，而窗口不在屏幕上时
        /// 这一次重绘的像素没人看得到。恢复时立即刷一次，不留一秒空白。
        /// </summary>
        private void HookHostWindow()
        {
            UnhookHostWindow();
            _host = Window.GetWindow(this);
            if (_host == null) return;
            _host.StateChanged += OnHostStateChanged;
            _host.Activated += OnHostActivated;
            _host.Deactivated += OnHostDeactivated;
            // Fullscreen policy changes arrive through the shared environment, not through focus:
            // the dashboard can be inactive for a normal window (keep painting) or a fullscreen
            // one (stop painting) and the difference must be visible here.
            RenderEnvironment.Changed += OnRenderEnvironmentChanged;
            // The policy is applied when the page is shown, before this subscription exists; if a
            // fullscreen window was already in front, the first Changed event would never arrive.
            _renderSuspended = RenderEnvironment.PauseInFullscreen && RenderEnvironment.FullscreenActive;
        }

        private void UnhookHostWindow()
        {
            if (_host == null) return;
            _host.StateChanged -= OnHostStateChanged;
            _host.Activated -= OnHostActivated;
            _host.Deactivated -= OnHostDeactivated;
            RenderEnvironment.Changed -= OnRenderEnvironmentChanged;
            _host = null;
        }

        private void OnHostDeactivated(object sender, EventArgs e)
        {
            // Stop painting, keep sampling: the 1 Hz timer still reads the snapshot and pushes
            // history points, so the curve has no gap when the user comes back. "全局重绘" and
            // "全屏停止动画" both ignore this event on purpose.
            if (RenderEnvironment.PauseWhenUnfocused) _renderSuspended = true;
            _ipTimer.Stop();
        }

        private void OnHostActivated(object sender, EventArgs e)
        {
            _renderSuspended = RenderEnvironment.PauseInFullscreen && RenderEnvironment.FullscreenActive;
            if (_renderSuspended)
            {
                _ipTimer.Stop();
                return;
            }
            if (!IsVisible) return;
            if (!_timer.IsEnabled) _timer.Start();
            if (!_ipTimer.IsEnabled) _ipTimer.Start();
            Refresh();
            RefreshPublicIp(false);
        }

        /// <summary>
        /// The policy can change while the dashboard is already in the background (switch from
        /// 全局重绘 to 失焦停止动画 without touching the window), and a fullscreen app can appear
        /// or leave while focus stays put. Both arrive as one environment change.
        /// </summary>
        private void OnRenderEnvironmentChanged()
        {
            bool fullscreen = RenderEnvironment.PauseInFullscreen && RenderEnvironment.FullscreenActive;
            bool unfocused = RenderEnvironment.PauseWhenUnfocused && _host != null && !_host.IsActive;
            bool suspended = fullscreen || unfocused;
            if (suspended == _renderSuspended) return;

            _renderSuspended = suspended;
            if (suspended)
            {
                _ipTimer.Stop();
                return;
            }

            if (_host != null && _host.WindowState != WindowState.Minimized && IsVisible)
            {
                // The 1 Hz timer keeps running through a fullscreen freeze (Refresh() early-returns
                // while suspended), so restart it only when the window is minimized; a stopped
                // timer here may be deliberate (page not on screen, or a test driving the card).
                if (!_ipTimer.IsEnabled) _ipTimer.Start();
                Refresh();
            }
        }

        private void OnHostStateChanged(object sender, EventArgs e)
        {
            if (_host == null) return;
            if (_host.WindowState == WindowState.Minimized)
            {
                _timer.Stop();
                _ipTimer.Stop();
                return;
            }
            if (!_timer.IsEnabled) _timer.Start();
            // 失焦时只恢复采样，不在后台重新开始公网 IP 查询；`Activated` 会把它接回来。
            if (!_ipTimer.IsEnabled && !_renderSuspended) _ipTimer.Start();
            Refresh();
        }

        /// <summary>
        /// 四格的占用指示器样式：settings.json 的 indicatorStyle（胶囊条 / 16 段小分段）。
        /// 设置弹窗里改完直接调这里，不重建页面 —— 重建会把正在开着的弹窗一起拆掉。
        /// 返回当前生效的样式，供断言直接用。
        /// </summary>
        public MetricBarStyle ApplyIndicatorStyle()
        {
            MetricBarStyle style = string.Equals(_window.CurrentSettings.IndicatorStyle,
                QuadVitals.Core.Settings.IndicatorCapsule, StringComparison.OrdinalIgnoreCase)
                ? MetricBarStyle.Capsule
                : MetricBarStyle.Segments;
            _cpuTile.BarStyle = style;
            _memoryTile.BarStyle = style;
            _gpuTile.BarStyle = style;
            _diskTile.BarStyle = style;
            // 通用设置的这一条控制所有分段指示器，风扇与功耗卡也必须跟随。
            _fanCard.SetBarStyle(style);
            _powerCard.SetBarStyle(style);
            return style;
        }

        private FrameworkElement BuildLayout()
        {
            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(Theme.PagePadding, 0, Theme.PagePadding, Theme.PagePadding)
            };

            // 全宽四格
            Card device = new Card("设备状态", "\uE7F4");
            device.HeaderRight.Content = BuildDeviceHeader();
            AutomationProperties.SetAutomationId(device, "DeviceCard");
            // 四张卡统一高度：内容较少的设备卡补到与网速/风扇/功耗卡同一底边。
            device.MinHeight = 204;
            HoverReveal.Attach(device, _deviceSettingsButton);
            Grid strip = new Grid();
            MetricTile[] tiles = { _cpuTile, _gpuTile, _memoryTile, _diskTile };
            for (int i = 0; i < tiles.Length; i++)
            {
                if (i > 0) strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(tiles[i], i * 2);
                strip.Children.Add(tiles[i]);
            }
            device.Content.Children.Add(strip);

            // 网络单独一张卡，放左下（宽度 1.15 份），右侧是风扇卡
            Grid lower = new Grid { Margin = new Thickness(0, Theme.CardGap, 0, 0) };
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.CardGap) });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // 窄窗口时风扇卡换到第二行，不叠在网络卡上。
            lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Card network = new Card("当前网速", "\uE774");
            network.HeaderRight.Content = BuildIpHeader();
            AutomationProperties.SetAutomationId(network, "NetworkCard");
            HoverReveal.Attach(network, _settingsButton);
            // 上行、下行各占一行：左边只留实时数值，右边各自一张小图。
            // 卡片高度仍绑定设备卡，宽度仍占左下 1.15 份，其他布局不动。
            Grid speeds = new Grid { MinHeight = 124 };
            // 文本列固定 104 px（Auto 会随数字长度变化，图表左边界跟着左右跳）；
            // 间距 4 px，图表区左边界因此可以整体左移。
            speeds.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            speeds.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            speeds.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            speeds.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            // 上下行之间留 12 px：两条曲线波谷同时下探时不会粘在一起。
            speeds.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            speeds.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            // 内容在卡片里居中略偏上：底部多留 4 px。
            speeds.Margin = new Thickness(0, 0, 0, 4);

            // 每行 = 实时值 + 下面一行峰值小字；峰值与**数值文本**同列对齐（不是对齐三角）。
            speeds.Children.Add(BuildSpeedBlock(_upValue, _upPeak, Theme.AccentBlue, true, 0));
            speeds.Children.Add(BuildSpeedBlock(_downValue, _downPeak, Theme.AccentGreenSoft, false, 2));

            _upSpark.HorizontalAlignment = HorizontalAlignment.Stretch;
            _upSpark.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(_upSpark, 0);
            Grid.SetColumn(_upSpark, 2);
            speeds.Children.Add(_upSpark);

            _downSpark.HorizontalAlignment = HorizontalAlignment.Stretch;
            _downSpark.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(_downSpark, 2);
            Grid.SetColumn(_downSpark, 2);
            speeds.Children.Add(_downSpark);

            network.Content.Children.Add(speeds);
            // 用 MinHeight 而不是 Height：绑定别人的实际高度等于禁止自己长高，
            // 高 DPI 下内容更高时会被裁掉（125%/150% 一直没复看，就是因为这里没出错看起来也正常）。
            network.SetBinding(FrameworkElement.MinHeightProperty,
                new System.Windows.Data.Binding(nameof(FrameworkElement.ActualHeight)) { Source = device });

            Grid.SetColumn(network, 0);
            lower.Children.Add(network);

            // 风扇卡填右列：与网络卡同高（网络卡高 = 设备卡高）。
            _fanCard.SetBinding(FrameworkElement.MinHeightProperty,
                new System.Windows.Data.Binding(nameof(FrameworkElement.ActualHeight)) { Source = network });
            Grid.SetColumn(_fanCard, 2);
            lower.Children.Add(_fanCard);

            // 功耗卡与网速卡同宽：直接占左下那一列，不再对半切——
            // 半宽会把 16 段条压成小点，读数也挤成两行小字。
            _powerCard.Margin = new Thickness(0, Theme.CardGap, 0, 0);
            _powerCard.SetBinding(FrameworkElement.MinHeightProperty,
                new System.Windows.Data.Binding(nameof(FrameworkElement.ActualHeight)) { Source = network });
            Grid.SetRow(_powerCard, 1);
            Grid.SetColumn(_powerCard, 0);
            lower.Children.Add(_powerCard);

            // 标题 + IP 一行需要固定宽度；窗口变窄时让网络卡占满整行，风扇卡换到下一行，
            // 避免两张卡叠在一起。
            lower.SizeChanged += (sender, e) =>
            {
                bool narrow = lower.ActualWidth < 920;
                int span = narrow ? 3 : 1;
                if (Grid.GetColumnSpan(network) != span) Grid.SetColumnSpan(network, span);
                // 窄窗口也让功耗卡紧跟网速卡，宽度与网速卡一致，风扇排到功耗卡之后。
                int fanRow = narrow ? 2 : 0;
                if (Grid.GetRow(_fanCard) != fanRow) Grid.SetRow(_fanCard, fanRow);
                if (Grid.GetColumnSpan(_fanCard) != span) Grid.SetColumnSpan(_fanCard, span);
                Thickness margin = narrow ? new Thickness(0, Theme.CardGap, 0, 0) : new Thickness(0);
                if (_fanCard.Margin != margin) _fanCard.Margin = margin;

                if (Grid.GetRow(_powerCard) != 1) Grid.SetRow(_powerCard, 1);
                int powerSpan = narrow ? 3 : 1;
                if (Grid.GetColumnSpan(_powerCard) != powerSpan) Grid.SetColumnSpan(_powerCard, powerSpan);
            };

            // ScrollViewer 会把唯一子元素拉满视口高度，包一层 StackPanel 让它按内容高度收。
            StackPanel root = new StackPanel();
            root.Children.Add(new PageHeading("仪表盘", "设备状态、实时网速与风扇"));
            root.Children.Add(device);
            root.Children.Add(lower);

            scroll.Content = root;
            return scroll;
        }

        /// <summary>One line of the network card: coloured triangle + live value, no Chinese label.</summary>
        /// <summary>
        /// 一行速度：上排是三角 + 实时数值，下排是峰值小字。
        /// 峰值放在**数值所在的列**，所以左边缘和数字对齐，而不是和三角对齐。
        /// </summary>
        private FrameworkElement BuildSpeedBlock(TextBlock value, TextBlock peak, Color accent, bool up, int row)
        {
            // 三角 + 数值 + 峰值这一组整体贴到底部：底边与曲线的 0 基线对齐。
            Grid block = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            // 显式给三角列 13 px（三角 6 + 右边距 7）：用 Auto 的话，跨两列的那一行会让
            // Auto 列为 0 宽，峰值就贴到最左边去了。
            block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
            block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            block.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            block.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            FrameworkElement speed = BuildSpeedValue(value, accent, up);
            Grid.SetRow(speed, 0);
            Grid.SetColumnSpan(speed, 2);
            block.Children.Add(speed);

            peak.FontSize = 10.5;
            peak.Foreground = Theme.TextTertiaryBrush;
            peak.HorizontalAlignment = HorizontalAlignment.Left;
            peak.Margin = new Thickness(0, 3, 0, 0);
            peak.Text = "峰值 " + MetricFormat.Invalid;
            Grid.SetRow(peak, 1);
            Grid.SetColumn(peak, 1);
            block.Children.Add(peak);

            Grid.SetRow(block, row);
            Grid.SetColumn(block, 0);
            return block;
        }

        private FrameworkElement BuildSpeedValue(TextBlock value, Color accent, bool up)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            SolidColorBrush fill = new SolidColorBrush(accent);
            fill.Freeze();
            PointCollection points = new PointCollection();
            if (up)
            {
                points.Add(new Point(0, 5));
                points.Add(new Point(6, 5));
                points.Add(new Point(3, 0));
            }
            else
            {
                points.Add(new Point(0, 0));
                points.Add(new Point(6, 0));
                points.Add(new Point(3, 5));
            }
            Polygon triangle = new Polygon
            {
                Points = points,
                Fill = fill,
                Width = 6,
                Height = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            Grid.SetColumn(triangle, 0);
            row.Children.Add(triangle);

            value.Text = "--";
            value.FontSize = 18;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = Theme.TextPrimaryBrush;
            value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            return row;
        }

        private FrameworkElement BuildIpHeader()
        {
            Border block = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1, 0, 0, 0),
                // 左边距/内边距 8+8：竖线两侧到这行两端各 8 px，标题与「局域网」不再一宽一窄。
                Padding = new Thickness(8, 0, 0, 0),
                Margin = new Thickness(8, 0, 0, 0),
                Height = 22,
                // 不要固定 MinWidth：比卡片可用宽度还宽时，最右的齿轮会被挤出卡片
                VerticalAlignment = VerticalAlignment.Center
            };

            // 整行统一 8 px 节奏：标题│(8) 局域网 8 IP (8)│(8) 公网 8 IP (8) [复制] [眼睛] …… [齿轮]。
            // 值列是 Auto（下界 = 掩码宽度）：IP 比掩码宽时列宽跟着内容走，所以切掩码不会推动图标，
            // 也不会像固定列宽那样在分隔竖线一侧留下 10 px 空当（看上去竖线不居中）。
            Grid grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 局域网
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 局域网 IP
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) }); // 分隔竖线
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 公网
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 公网 IP
            // 值 → 图标留 16：IP 信息与操作图标分成两组，不再挤在同一串里。
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); // 值 → 图标组
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); // 复制
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); // 眼睛
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); // 齿轮

            AddIpHeaderText(grid, 0, "局域网", _lanIpValue, "LanIpValue");

            Border separator = new Border
            {
                Width = 1,
                Height = 18,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(separator, 2);
            grid.Children.Add(separator);

            AddIpHeaderText(grid, 3, "公网", _publicIpValue, "PublicIpValue");

            _ipCopy = BuildIconButton("\uE8C8", "CopyIps", "复制局域网和公网 IP", "复制两个 IP", 11);
            WireCopy(_ipCopy, BuildClipboardText);
            SetCopyEnabled(_ipCopy, false);
            Grid.SetColumn(_ipCopy, 6);
            grid.Children.Add(_ipCopy);

            _eyeButton = CreateIconButton("ToggleIpMask", "显示或隐藏 IP", "隐藏 IP");
            Path eyeShape = new Path
            {
                Data = Geometry.Parse("M 1,8 C 4,2 12,2 15,8 C 12,14 4,14 1,8 Z"),
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent
            };
            eyeShape.SetBinding(Shape.StrokeProperty,
                new System.Windows.Data.Binding("Foreground") { Source = _eyeButton });
            Ellipse pupil = new Ellipse { Width = 5, Height = 5 };
            pupil.SetBinding(Shape.FillProperty,
                new System.Windows.Data.Binding("Foreground") { Source = _eyeButton });
            Canvas.SetLeft(pupil, 5.5);
            Canvas.SetTop(pupil, 5.5);
            _eyeSlash = new Line
            {
                X1 = 2,
                Y1 = 2,
                X2 = 14,
                Y2 = 14,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed
            };
            _eyeSlash.SetBinding(Shape.StrokeProperty,
                new System.Windows.Data.Binding("Foreground") { Source = _eyeButton });
            Canvas eyeContent = new Canvas { Width = 16, Height = 16 };
            eyeContent.Children.Add(eyeShape);
            eyeContent.Children.Add(pupil);
            eyeContent.Children.Add(_eyeSlash);
            _eyeButton.Content = eyeContent;
            _eyeButton.Click += (sender, e) => ToggleIpMask();
            Grid.SetColumn(_eyeButton, 7);
            grid.Children.Add(_eyeButton);

            // 齿轮从占位变为入口：网卡与刷新间隔的弹窗就挂在这张卡上，位置不变（仍是标题行最右）。
            _settingsButton = BuildIconButton("\uE713", "IpSettings", "网络设置", "网络设置", 12);
            _settingsButton.Click += (sender, e) => _window.ShowNetworkSettings();
            Grid.SetColumn(_settingsButton, 9);
            grid.Children.Add(_settingsButton);

            block.Child = grid;
            return block;
        }

        /// <summary>
        /// 值列的最小宽度：取掩码与最长 IPv4（255.255.255.255）的较大者。掩码比真实 IP 短，
        /// 若只按掩码宽度设下界，切到掩码时列会缩、右侧图标会左移（看起来就是抖动）。
        /// </summary>
        private static double IpMaskWidth()
        {
            return Math.Ceiling(Math.Max(IpTextWidth(MaskedIp), IpTextWidth("255.255.255.255")));
        }

        private static double IpTextWidth(string text)
        {
            FormattedText measured = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(Theme.UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                IpValueFontSize,
                IpValueBrush,
                1.0);
            return measured.WidthIncludingTrailingWhitespace;
        }

        private void AddIpHeaderText(Grid grid, int firstColumn, string label, TextBlock value, string automationId)
        {
            TextBlock caption = new TextBlock
            {
                Text = label,
                FontSize = IpValueFontSize,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(caption, firstColumn);
            grid.Children.Add(caption);

            value.Text = MetricFormat.Invalid;
            value.FontSize = IpValueFontSize;
            value.FontFamily = Theme.UiFont;
            // 标签保持次级灰，IP 数值提亮 + 半粗：深色卡片上纯灰小字看着吃力。
            value.Foreground = IpValueBrush;
            value.FontWeight = FontWeights.SemiBold;
            value.VerticalAlignment = VerticalAlignment.Center;
            value.MinWidth = IpMaskWidth();
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(value, automationId);
            Grid.SetColumn(value, firstColumn + 1);
            grid.Children.Add(value);
        }

        /// <summary>
        /// 设备状态卡标题行右侧的齿轮：与网速卡的齿轮同一套图标按钮，点开的是这张卡的设置弹窗
        /// （目前只有壳，内容待接入）。放在 HeaderRight 里所以跟标题同一行、靠右对齐。
        /// </summary>
        private FrameworkElement BuildDeviceHeader()
        {
            _deviceSettingsButton = BuildIconButton("\uE713", "DeviceSettings", "设备状态设置", "设备状态设置", 12);
            _deviceSettingsButton.HorizontalAlignment = HorizontalAlignment.Right;
            _deviceSettingsButton.Click += (sender, e) => _window.ShowDeviceSettings();
            return _deviceSettingsButton;
        }

        private FlatButton BuildIconButton(string glyph, string automationId, string automationName, string tooltip, int fontSize)
        {
            FlatButton button = CreateIconButton(automationId, automationName, tooltip);
            TextBlock icon = CreateIconText(glyph, fontSize);
            button.Content = icon;
            icon.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding("Foreground") { Source = button });
            return button;
        }

        private FlatButton CreateIconButton(string automationId, string automationName, string tooltip)
        {
            FlatButton button = new FlatButton(string.Empty);
            button.Width = 22;
            button.Height = 22;
            button.Padding = new Thickness(0);
            button.Background = Brushes.Transparent;
            button.Foreground = Theme.TextSecondaryBrush;
            button.BorderThickness = new Thickness(0);
            button.Cursor = Cursors.Hand;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.ToolTip = tooltip;
            button.MouseEnter += (sender, e) =>
            {
                if (button.IsEnabled) button.Foreground = Theme.TextPrimaryBrush;
            };
            button.MouseLeave += (sender, e) =>
            {
                if (button.IsEnabled) button.Foreground = Theme.TextSecondaryBrush;
            };
            AutomationProperties.SetAutomationId(button, automationId);
            AutomationProperties.SetName(button, automationName);
            return button;
        }

        private static TextBlock CreateIconText(string glyph, int fontSize)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = fontSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private string BuildClipboardText()
        {
            List<string> values = new List<string>();
            if (!IsInvalidIp(_lanIp)) values.Add(_lanIp);
            if (!IsInvalidIp(_publicIp)) values.Add(_publicIp);
            return values.Count == 0 ? string.Empty : string.Join(Environment.NewLine, values);
        }

        private void ApplyIpDisplay()
        {
            _lanIpValue.Text = FormatIpDisplay(_lanIp, MetricFormat.Invalid);
            _publicIpValue.Text = _publicIpFetching && IsInvalidIp(_publicIp)
                ? "获取中…"
                : FormatIpDisplay(_publicIp, MetricFormat.Invalid);
            StyleIpValue(_lanIpValue);
            StyleIpValue(_publicIpValue);
            SetCopyEnabled(_ipCopy, !IsInvalidIp(_lanIp) || !IsInvalidIp(_publicIp));
        }

        private string FormatIpDisplay(string ip, string fallback)
        {
            if (IsInvalidIp(ip)) return fallback;
            return _ipsMasked ? MaskedIp : ip;
        }

        /// <summary>
        /// 掩码前后保持同一字号与对齐：改字号/对齐会让 TextBlock 重新测量，切换时整行会抖。
        /// 值列的宽度由 MinWidth（最长 IPv4）兜底，掩码再短也不会缩小列宽。
        /// </summary>
        private static void StyleIpValue(TextBlock value)
        {
            value.FontSize = IpValueFontSize;
            value.TextAlignment = TextAlignment.Left;
        }

        private void ToggleIpMask()
        {
            _ipsMasked = !_ipsMasked;
            ApplyIpDisplay();
            if (_eyeSlash != null)
            {
                _eyeSlash.Visibility = _ipsMasked ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_eyeButton != null)
            {
                string name = _ipsMasked ? "显示 IP" : "隐藏 IP";
                _eyeButton.ToolTip = name;
                AutomationProperties.SetName(_eyeButton, name);
            }
        }

        private static bool IsInvalidIp(string text)
        {
            return string.IsNullOrEmpty(text) || text == MetricFormat.Invalid || text == "获取中…";
        }

        private static void SetCopyEnabled(Button button, bool enabled)
        {
            if (button == null) return;
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1.0 : 0.45;
            button.Foreground = enabled ? Theme.TextSecondaryBrush : Theme.TextTertiaryBrush;
        }

        private void WireCopy(Button button, Func<string> getValue)
        {
            button.Click += (sender, e) =>
            {
                string text = getValue();
                if (IsInvalidIp(text)) return;

                bool copied;
                try
                {
                    Clipboard.SetText(text);
                    copied = true;
                }
                catch (Exception)
                {
                    copied = false;
                }

                TextBlock icon = button.Content as TextBlock;
                if (icon != null) icon.Text = copied ? "\uE73E" : "\uE711";
                button.Foreground = copied ? Theme.AccentGreenBrush : Theme.AccentRedBrush;
                button.IsEnabled = false;
                button.Opacity = 1.0;

                DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                timer.Tick += (sender2, e2) =>
                {
                    timer.Stop();
                    if (icon != null) icon.Text = "\uE8C8";
                    button.Foreground = Theme.TextTertiaryBrush;
                    SetCopyEnabled(button, !IsInvalidIp(getValue()));
                };
                timer.Start();
            };
        }

        /// <summary>
        /// Reads CPU/memory identity once on a worker thread (WMI can take hundreds of
        /// milliseconds), then re-runs Refresh so the captions pick the strings up. Never
        /// touches the shared-memory snapshot.
        /// </summary>
        /// <summary>显卡说明行：型号 + 温度（温度读不到时只写型号）。</summary>
        private static string GpuNoteText(MetricsSnapshot snapshot)
        {
            string name = HardwareInfo.GpuName;
            if (string.IsNullOrEmpty(name)) name = "显卡";
            if (snapshot.Has(MetricFlags.GpuTemperatureValid))
                return name + " · " + snapshot.GpuTemperatureC.ToString("F0", CultureInfo.InvariantCulture) + "℃";
            return name;
        }

        private static string GpuUnavailableText()
        {
            string name = HardwareInfo.GpuName;
            if (string.IsNullOrEmpty(name)) return "未检测到显卡";
            return name + " · 显卡占用读取失败";
        }

        private static string DiskNoteText(MetricsSnapshot snapshot, string metric)
        {
            string drive = HardwareInfo.SystemDrive;
            if (string.IsNullOrEmpty(drive)) drive = "系统盘";
            if (snapshot.DiskTotalGb <= 0) return drive + " · 容量未知";

            // 说明行必须跟大字同一个口径：选“可用”时写可用容量，选“占用”时写已用容量。
            bool free = string.Equals(metric, DeviceMetricChoices.Free, StringComparison.OrdinalIgnoreCase);
            int amount = free ? Math.Max(0, snapshot.DiskTotalGb - snapshot.DiskUsedGb) : snapshot.DiskUsedGb;
            return drive + (free ? " 可用 " : " 已用 ") + amount.ToString(CultureInfo.InvariantCulture) +
                   " / " + snapshot.DiskTotalGb.ToString(CultureInfo.InvariantCulture) + " GB";
        }

        private void StartHardwareInfoLoad()
        {
            if (HardwareInfo.IsLoaded) return;
            Task.Run(() =>
            {
                HardwareInfo.Load();
                Dispatcher.BeginInvoke(new Action(() => Refresh()));
            });
        }

        public void Refresh()
        {
            MetricsSnapshot snapshot = ReadSnapshot(out bool channelOpen);
            if (_renderSuspended)
            {
                RecordHistory(snapshot);
                return;
            }
            UpdateLanIp(snapshot);
            bool alive = snapshot != null && snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout);

            _window.SetAgentStatus(alive, snapshot == null ? 0 : snapshot.Age(DateTime.UtcNow).TotalSeconds);

            if (!alive)
            {
                _cpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "传感器未运行",
                    MetricTone.Normal, Theme.AccentOrangeBrush);
                _memoryTile.SetValue(MetricFormat.Invalid, string.Empty, 0,
                    channelOpen ? "等待传感器数据" : "共享内存通道不存在",
                    MetricTone.Normal, Theme.AccentOrangeBrush);
                _gpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "传感器未运行",
                    MetricTone.Normal, Theme.AccentOrangeBrush);
                _diskTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "传感器未运行",
                    MetricTone.Normal, Theme.AccentOrangeBrush);
                _downValue.Text = MetricFormat.Invalid;
                _upValue.Text = MetricFormat.Invalid;
                _fanCard.Update(snapshot, false);
                _powerCard.Update(snapshot, false);
                FitCaptionFont();
                return;
            }

            // 说明行是硬件签名：「Intel Core i9-13900K · 24核 · 32线程」/
            // 「G.Skill DDR5 4200 · 31 / 64 GB」；刷新周期不进说明行（数字每秒都在变，
            // 它不是硬件身份的一部分）。
            // 四格各自显示哪个指标由设置弹窗的四个下拉决定（deviceMetrics），每帧按当前选择重画，
            // 所以改完下拉不用重开页面。
            RenderCpuTile(snapshot);
            RenderMemoryTile(snapshot);

            // 显卡走 NVML（需 NVIDIA 驱动），磁盘是系统盘容量；没有数据时写原因，不编数字。
            RenderGpuTile(snapshot);
            RenderDiskTile(snapshot);
            _fanCard.PreferredChannel = _window.CurrentSettings.FanChannel;
            _fanCard.Update(snapshot, true);
            _powerCard.Update(snapshot, true);

            bool netValid = snapshot.Has(MetricFlags.NetworkValid);
            _downValue.Text = netValid ? MetricFormat.Rate(snapshot.NetDownBytesPerSec) : MetricFormat.Invalid;
            _upValue.Text = netValid ? MetricFormat.Rate(snapshot.NetUpBytesPerSec) : MetricFormat.Invalid;

            // 曲线样式是设置项：每次刷新都跟随，改了设置不用重启窗口。
            bool smoothCurve = !string.Equals(_window.CurrentSettings.CurveStyle,
                AppSettings.CurvePolyline, StringComparison.OrdinalIgnoreCase);

            // 先入历史再写文案：峰值由 History 更新，先写会少一个 tick（出现“峰值比当前值小”）。
            // 每个刷新周期无条件落一个点：页面定时器与 Agent 定时器有毫秒级相位差，
            // 若“没检测到新帧就不入队”，时间轴会出现断崖和丢点。快照本身最多 1 秒旧，
            // 直接用它绘图，时间轴的密度才是稳定的。
            RecordHistory(snapshot);

            // 两张图各自按可见窗口缩放，互不影响。
            _upSpark.Smooth = smoothCurve;
            _downSpark.Smooth = smoothCurve;

            NetHistory history = History;
            double[] upValues = history.UpValues();
            double[] downValues = history.DownValues();
            DateTime[] stamps = history.Stamps();
            _upSpark.SetValues(upValues);
            _downSpark.SetValues(downValues);
            _upPeak.Text = "峰值 " + MetricFormat.Rate(Max(upValues));
            _downPeak.Text = "峰值 " + MetricFormat.Rate(Max(downValues));
            _upSpark.SetLabels(TakeLabels(stamps, upValues));
            _downSpark.SetLabels(TakeLabels(stamps, downValues));

            FitCaptionFont();
        }

        /// <summary>
        /// 四格说明行共用一号字：按设计字号 11.5 先量一遍，任何一格放不下就整排一起缩
        /// （每次 0.25，最低 8.5）。缩到底仍放不下才交给省略号，这样窄窗口不会出现
        /// “一行四种字号”，也不会把型号、核数与线程数截掉一半。
        /// </summary>
        private void FitCaptionFont()
        {
            MetricTile[] tiles = { _cpuTile, _memoryTile, _gpuTile, _diskTile };
            double size = MetricTile.CaptionFontSizeDefault;
            while (size > MetricTile.CaptionFontSizeFloor + 0.001)
            {
                if (CaptionsFit(tiles, size)) break;
                size -= 0.25;
            }
            for (int i = 0; i < tiles.Length; i++)
            {
                if (Math.Abs(tiles[i].CaptionFontSize - size) > 0.01) tiles[i].CaptionFontSize = size;
            }
        }

        private static bool CaptionsFit(MetricTile[] tiles, double size)
        {
            for (int i = 0; i < tiles.Length; i++)
            {
                MetricTile tile = tiles[i];
                // 还没布局（ActualWidth = 0）时不做判断：等 SizeChanged 或下一帧再量。
                if (tile.ActualWidth <= 0) continue;
                if (tile.CaptionWidthAt(size) > tile.ActualWidth) return false;
            }
            return true;
        }

        /// <summary>
        /// The data half of a refresh: one point per tick, independent of whether the window is
        /// painting. Keeping this on the suspended path is what stops the network curve from
        /// having a hole after the user alt-tabs away for a minute.
        /// </summary>
        private void RecordHistory(MetricsSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout)) return;
            _tick++;
            if (_tick % HistogramEveryTicks != 0) return;
            // 历史里存**原始采样值**：峰值标签与悬停提示都取自这个窗口，存平滑值会出现
            // “峰值 2 MB/s 但当前 2.7 MB/s”这种自相矛盾的读数。平滑只在绘制时做一次
            // （`Sparkline` 的二项平滑），曲线样式仍然只差画法。
            History.Push(snapshot.TimestampUtc, snapshot.NetUpBytesPerSec, snapshot.NetDownBytesPerSec);
            _historyDirty = true;
            SaveHistory(false);
        }

        /// <summary>"38%" -> "38"; values are already clamped by MetricFormat.</summary>
        private static string PercentNumber(double percent)
        {
            string text = MetricFormat.Percent(percent);
            return text.EndsWith("%", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
        }

        private MetricsSnapshot ReadSnapshot(out bool channelOpen)
        {
            if (_reader == null && (DateTime.UtcNow - _readerAttemptUtc).TotalSeconds > 2)
            {
                _readerAttemptUtc = DateTime.UtcNow;
                _reader = MetricsChannel.Reader.TryOpen();
            }
            channelOpen = _reader != null;
            if (_reader == null) return null;
            return _reader.TryRead();
        }

        /// <summary>与曲线点数一一对应：时刻 + 数值，供悬停 tooltip 使用。</summary>
        private string[] TakeLabels(DateTime[] stamps, double[] values)
        {
            string[] labels = new string[values.Length];
            int first = stamps.Length - values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                DateTime stamp = stamps[first + i];
                string time = stamp == DateTime.MinValue ? "--:--:--" : stamp.ToLocalTime().ToString("HH:mm:ss");
                labels[i] = time + "  ·  " + MetricFormat.Rate(values[i]);
            }
            return labels;
        }

        /// <summary>Agent 发布周期 = 设置里的 refreshMs；页面按同一周期刷新与落点。</summary>
        private TimeSpan RefreshInterval()
        {
            int period = _window.CurrentSettings.RefreshMs;
            if (period < AppSettings.MinRefreshMs) period = AppSettings.DefaultRefreshMs;
            if (period > AppSettings.MaxRefreshMs) period = AppSettings.MaxRefreshMs;
            return TimeSpan.FromMilliseconds(period);
        }

        private static double Max(double[] values)
        {
            double max = 0;
            for (int i = 0; i < values.Length; i++) if (values[i] > max) max = values[i];
            return max;
        }

        /// <summary>
        /// CPU tile caption: the detected model plus the logical thread count. Hardware identity
        /// is read once on a worker thread (WMI is slow), so before it lands the caption falls
        /// back to the plain "整机占用" without the refresh period.
        /// </summary>
        private static string CpuNoteText()
        {
            string signature = HardwareInfo.CpuSignature;
            return string.IsNullOrEmpty(signature) ? "整机占用" : signature;
        }

        /// <summary>
        /// Memory tile caption: modules + whole-gigabyte usage ("G.Skill DDR5 4200 · 31 / 64 GB").
        /// Without module info it degrades to the capacity pair alone.
        /// </summary>
        private static string MemoryNoteText(long usedBytes, long totalBytes)
        {
            string capacity = HardwareNaming.MemoryPairWhole(usedBytes, totalBytes);
            string label = HardwareInfo.MemoryLabel;
            if (string.IsNullOrEmpty(label)) return capacity;
            if (string.Equals(capacity, MetricFormat.Invalid, StringComparison.Ordinal)) return label;
            return label + " · " + capacity;
        }

        private string RefreshPeriodText()
        {
            double seconds = RefreshInterval().TotalSeconds;
            return seconds < 1 ? seconds.ToString("0.0") + " 秒" : seconds.ToString("0") + " 秒";
        }

        /// <summary>
        /// 进程级曲线存储：页面重建（保存设置会清页缓存）不丢点；进程退出时落盘一次，
        /// 下次打开直接右端接上。
        /// </summary>
        private static NetHistory History
        {
            get
            {
                if (_sharedHistory == null) _sharedHistory = NetHistory.Load(NetHistory.DefaultPath);
                return _sharedHistory;
            }
        }

        /// <summary>
        /// 运行中每 5 秒最多落盘一次（把强杀能丢的点压到 5 秒内），关窗时无条件落盘，
        /// 这样“关闭前拍快照”不依赖定时器刚好走到保存点。
        /// </summary>
        private static bool _historySaving;

        private static void SaveHistory(bool force)
        {
            if (_sharedHistory == null) return;

            DateTime now = DateTime.UtcNow;
            if (!force)
            {
                if (!_historyDirty) return;
                if ((now - _historySavedUtc).TotalSeconds < HistorySaveSeconds) return;
            }

            if (force || !_historySaving)
            {
                // 落盘不能占用 UI 线程：这台机器上写 %APPDATA% 偶尔要 300–500 ms，正好是风扇
                // “每转几圈顿一下”的间隔。序列化仍在调用线程（微秒级），只有写文件搬去后台。
                NetHistory snapshot = _sharedHistory.Snapshot();
                if (force)
                {
                    snapshot.Save(NetHistory.DefaultPath);
                }
                else
                {
                    _historySaving = true;
                    Task.Run(() =>
                    {
                        try { snapshot.Save(NetHistory.DefaultPath); }
                        catch (Exception) { }
                        finally { _historySaving = false; }
                    });
                }
                _historySavedUtc = now;
                _historyDirty = false;
            }
        }

        /// <summary>窗口关闭时由 SettingsWindow 调用，保证最后几秒的采样也进快照。</summary>
        internal static void FlushHistory()
        {
            SaveHistory(true);
        }

        /// <summary>Exposed so the fan settings dialog can read the live channel list.</summary>
        public FanCard FanCard { get { return _fanCard; } }

        public void Dispose()
        {
            _timer.Stop();
            _ipTimer.Stop();
            UnhookHostWindow();
            _fanCard.Dispose();
            _powerCard.Dispose();
            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }
        }
    }
}
