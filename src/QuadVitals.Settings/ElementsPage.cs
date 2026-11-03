using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;
using WpfButton = System.Windows.Controls.Button;


namespace QuadVitals.Settings
{
    /// <summary>
    /// 显示项：edit the ordered element list that defines the taskbar band.
    /// Everything is edited on a draft copy; 保存 writes settings.json and the band picks it up
    /// within a few seconds (it watches the file), except width changes which need the band to be
    /// re-added (S3).
    /// </summary>
    internal sealed partial class ElementsPage : UserControl, IRefreshable
    {
        private readonly SettingsWindow _window;
        private readonly StackPanel _rows = new StackPanel { Orientation = Orientation.Vertical };
        private readonly StackPanel _rowTop = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly StackPanel _rowBottom = new StackPanel { Orientation = Orientation.Horizontal };
        // 元素间距：条级参数，和元素列表同一张卡。草稿在 Load() 里才建，所以先留空、Load 时再填。
        private TextBox _spacingBox;
        private readonly StackPanel _editor = new StackPanel();
        private AppSettings _draft;
        private int _selectedIndex = -1;
        private int _widthOnLoad;
        private bool _editMode;
        private FlatButton _editToggle;

        private readonly List<Border> _tiles = new List<Border>();
        private readonly Dictionary<Border, Grid> _contentOf = new Dictionary<Border, Grid>();
        private readonly Dictionary<Border, Border> _badgeOf = new Dictionary<Border, Border>();
        private readonly Dictionary<Border, ScaleTransform> _scaleOf = new Dictionary<Border, ScaleTransform>();
        private readonly Dictionary<Border, TranslateTransform> _driftOf = new Dictionary<Border, TranslateTransform>();
        private readonly Dictionary<Border, RotateTransform> _rotateOf = new Dictionary<Border, RotateTransform>();

        /// <summary>拖动补位动画用的位移（按 FrameworkElement 存，因为占位块是 Grid 不是 Border）。</summary>
        private readonly Dictionary<FrameworkElement, TranslateTransform> _reflowOf = new Dictionary<FrameworkElement, TranslateTransform>();

        /// <summary>第一排最多 5 个：这 5 个上任务栏（`enabled = true`），顺序与任务栏一致。</summary>
        private const int TopRowSlots = 5;

        /// <summary>第二排最多 10 个（备用元素，不上任务栏）。</summary>
        private const int BottomRowSlots = 10;

        /// <summary>元素总数上限 = 5 + 10 = **15**（不是总共 10 个）。到达后虚线「＋」消失。</summary>
        private const int MaxElements = TopRowSlots + BottomRowSlots;

        private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
        {
            { BandElementType.Bar, "柱状" },
            { BandElementType.Pie, "环形" },
            { BandElementType.Text, "文本" }
        };

        /// <summary>指标下拉里“网络上下行”的伪 key：选它 = 一个文本元素带上行/下行两行。</summary>
        private const string NetworkMetricKey = "network";

        /// <summary>
        /// 指标下拉里“温度”的伪 key：CPU 温度与 GPU 温度不再各占一个下拉项，
        /// 而是一个元素的两行，行内再选用哪个传感器。
        /// </summary>
        private const string TemperaturePairKey = "temperaturePair";

        /// <summary>指标下拉里“磁盘 I/O”的伪 key：一个文本元素带读 / 写两行（同网络上下行的结构）。</summary>
        private const string DiskIoPairKey = "diskIoPair";

        /// <summary>指标键 → 方块底下的短名（54 px 宽塞不下“CPU 占用”这类全名，会溢出色块）。
        /// 图形类型由上面的迷你预览表达，完整名字（类型 · 指标）在 tooltip 里。</summary>
        private static readonly Dictionary<string, string> ShortMetricNames = new Dictionary<string, string>
        {
            { "cpu", "CPU" },
            { "memory", "内存" },
            { "temperature", "温度" },
            { "netUp", "上行" },
            { "netDown", "下行" },
            { "gpu", "显卡" },
            { "gpuMemory", "显存" },
            { "gpuTemp", "GPU" },
            { "disk", "磁盘" },
            { "diskIo", "磁盘IO" },
            { "diskIoRead", "读" },
            { "diskIoWrite", "写" },
            { "temperaturePair", "温度" }
        };

        public ElementsPage(SettingsWindow window)
        {
            _window = window;
            // 窗口失活（Alt+Tab、被系统弹窗抢焦点）会丢鼠标捕获：不主动收拾，占位块和浮动方块会留在列表里。
            _window.Deactivated += (sender, e) => AbortDrag();

            // 拖拽兜底：即使方块没拿到鼠标捕获（或指针跑得比它快），页面级事件也能把它拉回来。
            PreviewMouseMove += (sender, e) => { if (_dragging) FollowMouse(e.GetPosition(this)); };
            PreviewMouseLeftButtonUp += (sender, e) =>
            {
                if (!_dragging || _dragTile == null) return;
                EndPress(_dragTile, e);
                e.Handled = true;   // 别让方块自己的 Up 再跑一遍（否则重复选中/重建）
            };

            Content = BuildLayout();
            Load();
        }

        private void Load()
        {
            _draft = _window.CurrentSettings.Clone();
            // 第一排有几个人：看现有设置里 enabled 的个数（只是“前几个上任务栏”的表达方式）。
            _topCount = 0;
            foreach (BandElement item in _draft.Items) { if (item.Enabled) _topCount++; }
            if (_topCount > TopRowSlots) _topCount = TopRowSlots;
            if (_topCount > _draft.Items.Count) _topCount = _draft.Items.Count;
            _widthOnLoad = BandLayout.TotalWidth(_draft);
            _selectedIndex = _draft.Items.Count > 0 ? 0 : -1;
            RefreshList();
            BuildEditor();
            if (_spacingBox != null) _spacingBox.Text = _draft.Band.HorizontalSpacing.ToString();
        }

        /// <summary>
        /// 能不能删这个元素：列表至少留一个（否则任务栏条会变成 0 元素），并且**第一个绑定 CPU 的元素**不能删。
        /// 两条都不满足时删除入口（✕）也不渲染。
        /// </summary>
        private bool CanDeleteElement(int index)
        {
            if (index < 0 || index >= _draft.Items.Count) return false;
            if (_draft.Items.Count <= 1) return false;
            return index != IndexOfFirstCpuElement();
        }

        /// <summary>第一个 `metric = cpu` 的元素下标（指标 key 见 `MetricCatalog`），没有则 -1。</summary>
        private int IndexOfFirstCpuElement()
        {
            for (int i = 0; i < _draft.Items.Count; i++)
            {
                BandElement element = _draft.Items[i];
                if (element == null) continue;
                if (string.Equals(element.Metric, "cpu", StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        /// <summary>为什么不能删（提示框正文用；与 CanDeleteElement 的两条锁一一对应）。</summary>
        private string CannotDeleteReason(int index)
        {
            if (_draft.Items.Count <= 1)
            {
                return "元素列表至少要保留一个；删到 0 个，任务栏条就没有东西可画了。";
            }
            if (index == IndexOfFirstCpuElement())
            {
                return "第一个「CPU 占用」元素是默认元素，不能删除。要换样式或颜色，直接改它就行。";
            }
            return "这个元素不能删除。";
        }
        /// <summary>
        /// 编辑态是否持续抖动（iOS 卸载态）。开着会干扰选中与悬停；
        /// 改回 true 恢复 ±2.2° / 128 ms + ±1.2 px / 172 ms 的循环动画。
        /// </summary>
        private static readonly bool EditJiggleEnabled = true;

        /// <summary>元素列表里每个元素块的边长（圆角正方形；列表卡 202 高放得下“标题 + 方块行 + 按钮行”）。</summary>
        private const double TileSize = 64;
        private const double TileGap = 8;

        // 列表卡按内容撑高：两排方块 + 「元素间距」一行。固定高度会在方块与字段之间留下
        // 一段死空白，卡片越高越明显。
        private const double ListCardMinHeight = 202;

        private FrameworkElement BuildLayout()
        {
            Grid root = new Grid
            {
                Margin = new Thickness(Theme.PagePadding, 0, Theme.PagePadding, Theme.PagePadding)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.CardGap) });
            // 参数卡按内容撑高（VerticalAlignment=Top），剩下的高度留成卡片下方的空白，
            // 页脚固定在最下面一行。
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            FrameworkElement heading = new PageHeading("显示项", "编辑任务栏显示内容");
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            // 上下放置：列表在上、参数在下，两张卡同宽（撑满页面宽）。
            FrameworkElement listCard = BuildListCard();
            Grid.SetRow(listCard, 1);
            root.Children.Add(listCard);

            Border editorBorder = new Border
            {
                Background = Theme.SurfaceCardBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(Theme.CardRadius),
                // 底部多留 8 px：最后一排控件与卡片圆角之间要有呼吸空间，不能贴着边。
                Padding = new Thickness(Theme.CardPadding, Theme.CardPadding,
                                        Theme.CardPadding, Theme.CardPadding + 8),
                // 卡片贴着内容收，不跟随行高拉伸；行高减去卡片高度就是它下面的空白。
                VerticalAlignment = VerticalAlignment.Top
            };
            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                // WPF 的默认聚焦框是一圈点状黑环（在深色卡片里非常显眼）；参数卡里的输入框各有自己的焦点样式，
                // 这里只去掉“点了卡片内部就多一圈虚线”的噪声。
                FocusVisualStyle = null,
                Content = _editor
            };
            editorBorder.Child = scroll;
            Grid.SetRow(editorBorder, 3);
            root.Children.Add(editorBorder);

            FrameworkElement footer = BuildFooter();   // build once: its children are shared fields
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);

            return root;
        }

        private FrameworkElement BuildListCard()
        {
            // 无描边（ui-design §2：卡片是纯色块，靠间距和圆角分开）。手写的 1 px #444
            // 在深色页面上看着像一圈黑边。
            Border card = new Border
            {
                Background = Theme.SurfaceCardBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(16)
            };

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 标题行：左边「元素列表」，右边编辑模式开关。
            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "元素列表",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush,
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 0);
            header.Children.Add(title);

            _editToggle = new FlatButton("编辑")
            {
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(_editToggle, "ElementsEditToggle");
            AutomationProperties.SetName(_editToggle, "编辑元素列表");
            _editToggle.Click += (sender, e) =>
            {
                SetEditMode(!_editMode);
            };
            Grid.SetColumn(_editToggle, 1);
            header.Children.Add(_editToggle);

            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // 两排方块：上排 = 上任务栏的元素（最多 5 个），下排 = 备用元素。
            _rowTop.Margin = new Thickness(0, 0, 0, 8);
            _rows.Children.Add(_rowTop);
            _rows.Children.Add(_rowBottom);
            Grid.SetRow(_rows, 1);
            grid.Children.Add(_rows);

            // 拖动浮层：被拖的方块进这里跟着鼠标走，行里只留一个虚线占位块让后面的方块前补。
            // **必须可命中测试**：浮层不可命中时，里面的方块 `CaptureMouse()` 会失败（祖先 IsHitTestVisible=false），
            // 之后的 MouseMove 就送不到它身上，方块看起来“拖不动”。
            _dragLayer = new Canvas();
            Grid.SetRow(_dragLayer, 1);
            grid.Children.Add(_dragLayer);

            // 第二排方块之后是条级参数：间距由元素宽度 + 间距累加决定整条宽度，所以它跟着元素
            // 列表在一起，而不是单独占一张卡。改动进草稿，点保存时和元素一起生效。
            FrameworkElement spacing = BuildSpacingRow();
            Grid.SetRow(spacing, 2);
            grid.Children.Add(spacing);

            // 新增走虚线方块，删除/排序走编辑模式。

            // 空白处**不**触发编辑模式：卡片不挂鼠标事件。

            card.Child = grid;
            return card;
        }

        private FrameworkElement BuildFooter()
        {
            // 页脚只保留两个动作按钮。
            StackPanel footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, Theme.CardGap, 0, 0)
            };

            FlatButton reload = new FlatButton("放弃改动");
            reload.Margin = new Thickness(0, 0, 8, 0);
            reload.Click += (sender, e) => _window.ReloadSettings();
            footer.Children.Add(reload);

            FlatButton save = new FlatButton("保存", true);
            save.Click += (sender, e) =>
            {
                _draft.Normalize();
                _draft.Save(AppSettings.DefaultPath);
                _window.SaveSettings(_draft.Clone(), _widthOnLoad);
            };
            footer.Children.Add(save);

            return footer;
        }

        /// <summary>
        /// 「元素间距」：0–40 px，越界夹紧；输入进草稿，保存时与元素一起落盘。改它会改变整条宽度，
        /// 保存后的重建由 SettingsWindow.SaveSettings 里那条“宽度变了就重启 Explorer”处理。
        /// </summary>
        private FrameworkElement BuildSpacingRow()
        {
            Grid row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            // 72 = 方块 64 + 行距 8：输入框正好落在方块网格的第二列，和虚线「＋」同一条左边缘。
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileSize + TileGap) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = "元素间距",
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            TextBox box = new DarkTextBox
            {
                Width = 72,
                // 整行统一 12 px：DarkTextBox 默认 12.5，数字会比旁边的标签和单位大一号。
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _spacingBox = box;
            AutomationProperties.SetAutomationId(box, "ElementsSpacingBox");
            AutomationProperties.SetName(box, "元素间距");
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            // 输入框后面只留一句范围说明：单位不单独占位，免得读成“px 0–40”两截。
            TextBlock hint = new TextBlock
            {
                Text = "0–40；改动后任务栏条会重建一次",
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(hint, 2);
            row.Children.Add(hint);

            Action commit = () =>
            {
                int parsed;
                if (!int.TryParse(box.Text, out parsed)) parsed = _draft.Band.HorizontalSpacing;
                if (parsed < 0) parsed = 0;
                if (parsed > 40) parsed = 40;
                box.Text = parsed.ToString();
                _draft.Band.HorizontalSpacing = parsed;
            };
            box.LostFocus += (sender, e) => commit();
            box.KeyDown += (sender, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) commit();
            };
            return row;
        }

        // ---- list ---------------------------------------------------------------------

        /// <summary>
        /// 重新渲染两排。删除后**后面的元素要自己补回去**，虚线「＋」只留一个、跟在实体元素后面。
        /// </summary>
        /// <summary>
        /// 第一排（上任务栏）里有几个元素。删第一排的元素时只把**第一排内部**后面的往前补（`_topCount--`），
        /// 不会把第二排的拉上来。
        /// </summary>
        private int _topCount = TopRowSlots;

        private void RefreshList()
        {
            _rowTop.Children.Clear();
            _rowBottom.Children.Clear();
            _tiles.Clear();
            _contentOf.Clear();
            _badgeOf.Clear();
            _scaleOf.Clear();
            _driftOf.Clear();
            _rotateOf.Clear();
            _reflowOf.Clear();
            if (_dragLayer != null) _dragLayer.Children.Clear();

            if (_topCount > TopRowSlots) _topCount = TopRowSlots;
            if (_topCount > _draft.Items.Count) _topCount = _draft.Items.Count;
            if (_topCount < 0) _topCount = 0;

            // 前 `_topCount` 个 = 第一排 = 上任务栏（enabled = true）；其余下排。
            for (int i = 0; i < _draft.Items.Count; i++)
            {
                _draft.Items[i].Enabled = i < _topCount;
                Panel row = i < _topCount ? _rowTop : _rowBottom;
                row.Children.Add(BuildListItem(_draft.Items[i], i, i));
            }

            // 每排最多一个虚线「＋」，各自紧跟在那一排最后一个元素后面。
            if (_topCount < TopRowSlots && _draft.Items.Count < MaxElements) _rowTop.Children.Add(BuildAddTile(true));
            if (_draft.Items.Count < MaxElements) _rowBottom.Children.Add(BuildAddTile(false));
        }

        /// <summary>左上角的删除按钮（仅编辑模式）：点击先弹二次确认，再真的删。</summary>
        private FrameworkElement BuildDeleteBadge(BandElement element, int index)
        {
            Border badge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                BorderBrush = Theme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, -6, 0),
                Cursor = Cursors.Hand,
                ToolTip = "删除元素"
            };
            AutomationProperties.SetAutomationId(badge, "ElementDeleteBadge");
            AutomationProperties.SetName(badge, "删除元素 " + (index + 1));
            badge.Child = new TextBlock
            {
                Text = "✕",
                FontSize = 10,
                Foreground = Theme.TextPrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            // 编辑模式下方块是“按下即拖”：这里必须把 LeftButtonDown 拦下，否则点 ✕ 会立刻进入拖拽
            // 并捕获鼠标；松手时事件按捕获元素派发给方块，✕ 的 Up 永远不触发，删除确认框就再也弹不出来。
            badge.MouseLeftButtonDown += (sender, e) => { e.Handled = true; };

            badge.MouseLeftButtonUp += (sender, e) =>
            {
                // 按**元素本身**删，不按构建时记下的下标：
                // 列表可能在弹窗打开期间变化（拖动/新增/删除），下标会指到别人身上。
                int current = _draft.Items.IndexOf(element);
                if (current < 0) { e.Handled = true; return; }

                // 受保护元素（列表里最后一个 / 第一个 CPU 元素）不弹“删除后需要重新添加才能恢复”——
                // 那句话的预设是“确实能删”，而这里删不掉，提示就等于在骗人。
                if (!CanDeleteElement(current))
                {
                    _window.ShowOverlay(new NoticeDialog(_window).Build("不能删除", CannotDeleteReason(current)));
                    e.Handled = true;
                    return;
                }

                // 弹窗里的名字与方块底下那行字用同一套描述（指标），否则“删哪个”还得靠序号猜。
                string metricName = DescribeMetric(element);
                _window.ShowOverlay(new ConfirmDialog(_window).Build(
                    "删除元素",
                    "确定删除「元素 " + (current + 1) + " · " + metricName + "」吗？删除后需要重新添加才能恢复。",
                    "删除",
                    () => RemoveElement(element),
                    true));
                e.Handled = true;
            };
            return badge;
        }

        /// <summary>iOS 卸载态抖动：±2.2° 旋转 + ±1.2 px 漂移，每个方块错开相位，不然整排一起呼吸很假。</summary>
        private void ApplyJiggle(FrameworkElement tile, int index)
        {
            RotateTransform rotate = new RotateTransform(0);
            TranslateTransform drift = new TranslateTransform(0, 0);
            // 长按时的“鼓起”要接着用：新组从当前缩放起步，否则进编辑模式会先回弹到 1.0 再放大。
            double fromScale = 1.0;
            Border border = tile as Border;
            if (border != null)
            {
                ScaleTransform previous;
                if (_scaleOf.TryGetValue(border, out previous) && previous != null) fromScale = previous.ScaleX;
            }

            ScaleTransform scale = new ScaleTransform(fromScale, fromScale);
            TranslateTransform reflow = new TranslateTransform(0, 0);
            TransformGroup group = new TransformGroup();
            group.Children.Add(rotate);
            group.Children.Add(drift);
            group.Children.Add(scale);
            group.Children.Add(reflow);
            tile.RenderTransform = group;
            tile.RenderTransformOrigin = new Point(0.5, 0.5);
            _reflowOf[tile] = reflow;

            if (border != null)
            {
                _scaleOf[border] = scale;
                _driftOf[border] = drift;
                _rotateOf[border] = rotate;
            }

            // 编辑态不持续抖动：变换组仍然要建（缩放/补位动画都挂在它上面），只是不起旋转/漂移动画。
            if (!EditJiggleEnabled) return;

            TimeSpan begin = TimeSpan.FromMilliseconds((index % 7) * 41);
            SineEase ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            tile.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-2.2, 2.2,
                TimeSpan.FromMilliseconds(128))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = begin,
                EasingFunction = ease
            });
            drift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1.2, 1.2,
                TimeSpan.FromMilliseconds(172))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = begin
            });
        }

        /// <summary>
        /// 抓住方块时的“hover 一下”：快速鼓起到 1.08、过冲后回落到 1.05 停住，
        /// 作为“已经抓住、可以拖”的视觉反馈。
        /// </summary>
        private void PopGrab(FrameworkElement tile)
        {
            Border border = tile as Border;
            ScaleTransform scale = null;
            if (border != null) _scaleOf.TryGetValue(border, out scale);
            if (scale == null) return;

            DoubleAnimationUsingKeyFrames pop = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(180)
            };
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromPercent(0.45),
                new SineEase { EasingMode = EasingMode.EaseOut }));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromPercent(1.0),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        /// <summary>按索引删除（编辑模式的 ✕ 确认后调用）。</summary>
        /// <summary>
        /// 选中一个元素：**只换描边，不重建列表**。
        /// 以前点击走 `RefreshList()`，每个方块都是新实例 → 抖动动画从头开始、偏移植零，
        /// 点一下就能看到旁边的方块“抽一下”。
        /// </summary>
        private void SelectElement(int index)
        {
            if (index < 0 || index >= _draft.Items.Count) return;
            if (index == _selectedIndex) return;

            _selectedIndex = index;
            for (int i = 0; i < _tiles.Count; i++)
            {
                Border tile = _tiles[i];
                if (tile == null) continue;
                tile.BorderBrush = i == _selectedIndex ? Theme.AccentGreenBrush : Brushes.Transparent;
            }
            BuildEditor();
        }

        private void RemoveElementAt(int index)
        {
            // 两道锁都在 CanDeleteElement 里：第一个 CPU 元素不可删、列表至少留一个。
            if (!CanDeleteElement(index)) return;
            RemoveElementCore(index);
        }

        /// <summary>
        /// 按**对象**删除：确认弹窗打开期间列表可能已经变了（拖动/新增/删除），
        /// 用下标去删会删到已经坐在那个位置上别的元素。
        /// </summary>
        private void RemoveElement(BandElement target)
        {
            int index = _draft.Items.IndexOf(target);
            if (index < 0) return;
            if (!CanDeleteElement(index)) return;
            RemoveElementCore(index);
        }

        private void RemoveElementCore(int index)
        {
            // 删第一排的元素：只有**第一排内部**后面的往前补，第二排不动（`_topCount--`）。
            // 删第二排的元素：第二排内部往前补。
            bool fromTop = index < _topCount;
            _draft.Items.RemoveAt(index);
            if (fromTop) _topCount--;
            if (_topCount < 0) _topCount = 0;
            if (_selectedIndex > index) _selectedIndex--;
            if (_selectedIndex >= _draft.Items.Count) _selectedIndex = _draft.Items.Count - 1;
            if (_selectedIndex < 0) _selectedIndex = 0;
            RefreshList();
            BuildEditor();
        }

        /// <summary>虚线方框：与元素方块同尺寸，点击在对应那一排新增。第一排满了就不显示上排那个。</summary>
        private FrameworkElement BuildAddTile(bool topRow)
        {
            return BuildDashedAddTile("ElementAddTile", topRow ? "新增元素（第一排）" : "新增元素（第二排）",
                                      () => AddElement(topRow));
        }

        private FrameworkElement BuildDashedAddTile(string automationId, string name, Action onClick)
        {
            Border tile = new Border
            {
                Width = TileSize,
                Height = TileSize,
                Margin = new Thickness(0, 0, TileGap, 0),
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = name
            };
            AutomationProperties.SetAutomationId(tile, automationId);
            AutomationProperties.SetName(tile, name);

            // Border 不支持虚线描边，叠一层 Rectangle 画虚线。
            Grid content = new Grid();
            content.Children.Add(new System.Windows.Shapes.Rectangle
            {
                RadiusX = 12,
                RadiusY = 12,
                Stroke = Theme.BorderStrongBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false
            });
            content.Children.Add(new TextBlock
            {
                Text = "+",
                FontSize = 24,
                Foreground = Theme.TextTertiaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            tile.Child = content;

            tile.MouseLeftButtonDown += (sender, e) => { e.Handled = true; };   // 别让卡片的长按也醒
            tile.MouseLeftButtonUp += (sender, e) =>
            {
                if (onClick != null) onClick();
                e.Handled = true;
            };
            return tile;
        }

        /// <summary>
        /// 元素列表里的一项：圆角正方形块，里面画这个元素的迷你预览（柱/环/文本/间隔）与主色，
        /// 底下标序号与类型。点击 = 选中，选中后下方的参数卡联动。
        /// </summary>
        /// <summary>
        /// 元素列表里的一格：64×64 圆角正方形（两排要 202 px 高放得下），上半是迷你预览、下半是「序号·类型」。
        /// 点击 = 选中，下方参数卡联动。
        /// </summary>
        private FrameworkElement BuildListItem(BandElement element, int index, int visualSlot)
        {
            string typeName;
            if (!TypeNames.TryGetValue(element.Type ?? string.Empty, out typeName)) typeName = element.Type;
            string shortName = DescribeShortMetric(element);

            bool selected = (index == _selectedIndex);
            Border tile = new Border
            {
                Width = TileSize,
                Height = TileSize,
                Margin = new Thickness(0, 0, TileGap, 0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Theme.SurfaceHover),
                BorderThickness = new Thickness(2),
                BorderBrush = selected ? Theme.AccentGreenBrush : Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = typeName + " · " + DescribeMetric(element)
            };
            AutomationProperties.SetAutomationId(tile, "ElementTile");
            AutomationProperties.SetName(tile, (visualSlot + 1) + " " + shortName);

            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border preview = new Border
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, 2),
                Child = BuildTilePreview(element)
            };
            Grid.SetRow(preview, 0);
            content.Children.Add(preview);

            TextBlock caption = new TextBlock
            {
                Text = (visualSlot + 1) + "·" + shortName,
                Width = TileSize - 10,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = element.Enabled ? Theme.TextSecondaryBrush : Theme.TextTertiaryBrush
            };
            Grid.SetRow(caption, 1);
            content.Children.Add(caption);

            // 编辑态的视觉（抖动 + 左上角 ✕）在 SetEditMode 里就地加/去，**不重建方块**：
            // 否则长按后元素被替换，鼠标手势会断。
            tile.Child = content;
            tile.Tag = index;
            _tiles.Add(tile);
            _contentOf[tile] = content;
            if (_editMode) ApplyEditVisual(tile, index, true);

            tile.MouseLeftButtonUp += (sender, e) =>
            {
                if (_dragging) return;   // 拖拽结束由 MouseUp 处理（_pendingDrag 不拦，否则普通点击选不中）
                SelectElement(index);
                // 不把事件标成 Handled：同一个方块上的 EndPress 要接着跑（释放鼠标捕获、清 _pendingDrag）。
                // 标了 Handled 后 EndPress 就不会被调，一次点击会把 _pendingDrag 永远留成 true。
            };

            // 两种拖动入口都要监听到：编辑模式 = 按下即拖；非编辑模式 = 长按 LongPressMs 后进编辑模式接着拖。
            tile.MouseLeftButtonDown += (sender, e) =>
            {
                // 方块自己处理长按/拖拽；标成 Handled，否则列表卡的“空白处长按”也会被一起唤醒。
                e.Handled = true;
                BeginPress(tile, index, e);
            };
            tile.MouseMove += (sender, e) => MovePress(tile, e);
            tile.MouseLeftButtonUp += (sender, e) => EndPress(tile, e);
            // 捕获丢失（Alt+Tab、弹层抢焦点）时把手势复位，否则占位块与浮动方块会留在列表里。
            // 正常松手时 _endingDrag 为 true，这里什么都不做。
            tile.LostMouseCapture += (sender, e) => AbortDrag();

            return tile;
        }

        /// <summary>编辑态视觉：抖动 + 左上角 ✕。就地增删，不重建方块。</summary>
        private void ApplyEditVisual(Border tile, int index, bool on)
        {
            Grid content = _contentOf.ContainsKey(tile) ? _contentOf[tile] : tile.Child as Grid;
            if (content == null) return;

            if (on)
            {
                // 不能删的不给入口：留一个空 ✕ 在那里、点了没反应，比没有 ✕ 更让人困惑。
                if (!_badgeOf.ContainsKey(tile) && CanDeleteElement(index))
                {
                    Border badge = (Border)BuildDeleteBadge(_draft.Items[index], index);
                    content.Children.Add(badge);
                    _badgeOf[tile] = badge;
                }
                ApplyJiggle(tile, index);
            }
            else
            {
                Border badge;
                if (_badgeOf.TryGetValue(tile, out badge) && badge != null) content.Children.Remove(badge);
                _badgeOf.Remove(tile);
                _scaleOf.Remove(tile);
                tile.RenderTransform = null;
            }
        }

        /// <summary>统一开关编辑模式（就地改视觉，保持当前鼠标手势不断）。</summary>
        private void SetEditMode(bool on)
        {
            _editMode = on;
            if (_editToggle != null) _editToggle.Content = on ? "完成" : "编辑";

            for (int i = 0; i < _tiles.Count; i++)
            {
                if (_tiles[i] == null) continue;
                ApplyEditVisual(_tiles[i], i, on);
            }
        }

        /// <summary>
        /// 与任务栏渐变环同一条色阶：0% 绿 #41D484 → 80% 黄 #F6A748 → 90% 红 #D24747（之后保持红）。
        /// 只用于元素方块里的迷你预览。
        /// </summary>
        /// <summary>Sample value shown inside a text element's tile preview (never live data).</summary>
        private static string PreviewSample(string metric)
        {
            // Two rows of the same number look like a bug, so each metric gets its own sample.
            if (string.Equals(metric, "netUp", StringComparison.OrdinalIgnoreCase)) return MetricFormat.Rate(5 * 1024);
            if (string.Equals(metric, "netDown", StringComparison.OrdinalIgnoreCase)) return MetricFormat.Rate(11 * 1024);
            if (string.Equals(metric, "temperature", StringComparison.OrdinalIgnoreCase)) return MetricFormat.Temperature(65);
            if (string.Equals(metric, "gpuTemp", StringComparison.OrdinalIgnoreCase)) return MetricFormat.Temperature(55);
            if (string.Equals(metric, "diskIo", StringComparison.OrdinalIgnoreCase)) return MetricFormat.Rate(12 * 1024 * 1024);
            return MetricFormat.Percent(65);
        }

        private static Color RampTone(double percent)
        {
            Color safe = Theme.AccentGreenSoft;
            Color warn = Theme.AccentOrange;
            Color danger = Theme.AccentRed;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            double t;
            Color a, b;
            if (percent < 80) { a = safe; b = warn; t = percent / 80.0; }
            else if (percent < 90) { a = warn; b = danger; t = (percent - 80) / 10.0; }
            else return danger;
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private static FrameworkElement BuildTilePreview(BandElement element)
        {
            // Every preview is drawn into the SAME 40x40 box, so the Viewbox scales them all by
            // the same factor: the bar tile used to be a 27x40 canvas against the ring's 43x43,
            // so the CPU tile read as visibly smaller than its neighbours.
            const double box = 40.0;
            Canvas canvas = new Canvas { Margin = new Thickness(8), Width = box, Height = box };

            if (element.IsType(BandElementType.Bar))
            {
                SolidColorBrush fillBar = new SolidColorBrush(Theme.ToColor(element.Colors == null ? null : element.Colors.Fill, Theme.AccentGreen));
                // 3 thick bars, each 7 px wide (the ring preview's stroke), round ends, 3 px apart.
                const int columns = 3;
                const double width = 7.0;
                const double gap = 3.0;
                double total = columns * width + (columns - 1) * gap;
                double left = (box - total) / 2.0;
                double[] heights = { 24.0, 36.0, 28.0 };
                for (int i = 0; i < columns; i++)
                {
                    Rectangle bar = new Rectangle
                    {
                        Width = width,
                        Height = heights[i],
                        Fill = fillBar,
                        RadiusX = width / 2.0,
                        RadiusY = width / 2.0
                    };
                    Canvas.SetLeft(bar, left + i * (width + gap));
                    Canvas.SetBottom(bar, 2);
                    canvas.Children.Add(bar);
                }
            }
            else if (element.IsType(BandElementType.Pie))
            {
                // Gradient ring colour follows the occupancy ramp (preview fixed 65% = normal).
                bool gradientStyle = PieStyle.Canonical(element.Style) == PieStyle.Gradient;
                Color pieColor = Theme.ToColor(element.Colors == null ? null : element.Colors.Fill, Theme.AccentGreen);
                const double stroke = 7.0;
                // 33 + stroke 7 => the ring's outer edge is exactly the 40 px box.
                const double diameter = box - stroke;
                const double centre = box / 2.0;
                double radius = (diameter - stroke) / 2.0;

                Ellipse track = new Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Stroke = new SolidColorBrush(gradientStyle ? Color.FromRgb(0x45, 0x45, 0x45) : pieColor),
                    StrokeThickness = stroke,
                    Opacity = gradientStyle ? 0.9 : 0.95
                };
                // The stroke is centred on the ellipse outline, so the shape has to sit half a
                // stroke inside the box: otherwise the track's centre is at 16.5 while the arc is
                // centred on 20 and the two circles look off-centre from each other.
                Canvas.SetLeft(track, stroke / 2.0);
                Canvas.SetTop(track, stroke / 2.0);
                canvas.Children.Add(track);

                if (gradientStyle)
                {
                    // One continuous arc + a gradient along it (segmenting by angle showed seams).
                    const double previewPercent = 65.0;
                    double sweep = previewPercent / 100.0 * 360.0;
                    Point start = new Point(centre, centre - radius);
                    Point end = new Point(centre + radius * Math.Cos((sweep - 90) * Math.PI / 180.0),
                                          centre + radius * Math.Sin((sweep - 90) * Math.PI / 180.0));
                    LinearGradientBrush gradient = new LinearGradientBrush
                    {
                        StartPoint = new Point(start.X / box, start.Y / box),
                        EndPoint = new Point(end.X / box, end.Y / box),
                        MappingMode = BrushMappingMode.RelativeToBoundingBox
                    };
                    gradient.GradientStops.Add(new GradientStop(RampTone(0), 0.0));
                    gradient.GradientStops.Add(new GradientStop(RampTone(previewPercent * 0.5), 0.5));
                    gradient.GradientStops.Add(new GradientStop(RampTone(previewPercent), 1.0));
                    gradient.Freeze();

                    PathGeometry geometry = new PathGeometry();
                    PathFigure figure = new PathFigure { StartPoint = start, IsClosed = false };
                    figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
                                                       sweep > 180, SweepDirection.Clockwise, true));
                    geometry.Figures.Add(figure);
                    canvas.Children.Add(new Path
                    {
                        Data = geometry,
                        Stroke = gradient,
                        StrokeThickness = stroke,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    });
                }
            }
            else if (element.IsType(BandElementType.Text))
            {
                // Text tiles used to be two featureless dashes. Draw a sample line per row instead
                // ("5 KB/s" / "65℃"), with the same up/down triangle and colour the band uses, so
                // the tile says what it is even before you read the caption.
                double y = element.Lines != null && element.Lines.Count > 1 ? 9.0 : 15.0;
                if (element.Lines != null)
                {
                    foreach (TextLine line in element.Lines)
                    {
                        string sample = PreviewSample(line.Metric);
                        if (string.Equals(line.Indicator, Indicator.Up, StringComparison.OrdinalIgnoreCase)) sample = "▲ " + sample;
                        else if (string.Equals(line.Indicator, Indicator.Down, StringComparison.OrdinalIgnoreCase)) sample = "▼ " + sample;

                        TextBlock text = new TextBlock
                        {
                            Text = sample,
                            FontSize = 10,
                            FontFamily = Theme.UiFont,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(Theme.ToColor(line.Color, Theme.AccentBlue))
                        };
                        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Canvas.SetLeft(text, Math.Max(0, (box - text.DesiredSize.Width) / 2.0));
                        Canvas.SetTop(text, y);
                        canvas.Children.Add(text);
                        y += 14;
                    }
                }
            }

            return new Viewbox
            {
                Child = canvas,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static string DescribeMetric(BandElement element)
        {
            if (element.IsType(BandElementType.Text))
            {
                // 两行都是温度时合并成一个“温度”，否则标题会变成“CPU 温度 / GPU 温度”（大长）。
                if (IsTemperaturePair(element)) return "温度";
                if (IsNetworkPair(element)) return "网络上下行";
                List<string> names = new List<string>();
                if (element.Lines != null)
                {
                    foreach (TextLine line in element.Lines)
                    {
                        MetricInfo info;
                        names.Add(MetricCatalog.TryGet(line.Metric, out info) ? info.DisplayName : line.Metric);
                    }
                }
                return string.Join(" / ", names.ToArray());
            }

            MetricInfo metric;
            if (MetricCatalog.TryGet(element.Metric, out metric)) return metric.DisplayName;
            return element.Metric ?? "—";
        }

        /// <summary>
        /// 方块下面那行短名：这个元素测的是什么。图形类型已经由上面的迷你预览画出来了，
        /// 底下的字再写「柱状 / 环形」只是重复；换指标时这行字跟着变，才看得出这一格是什么。
        /// 两行文本若正好是上行+下行，合写成「网速」（分开写就变成「上行/下行」，54 px 放不下）。
        /// </summary>
        private static string DescribeShortMetric(BandElement element)
        {
            if (element == null) return "—";
            if (element.IsType(BandElementType.Text))
            {
                if (IsTemperaturePair(element)) return "温度";
                List<string> parts = new List<string>();
                bool up = false;
                bool down = false;
                bool read = false;
                bool write = false;
                if (element.Lines != null)
                {
                    foreach (TextLine line in element.Lines)
                    {
                        if (IsMetric(line.Metric, "netUp")) up = true;
                        else if (IsMetric(line.Metric, "netDown")) down = true;
                        else if (IsMetric(line.Metric, "diskIoRead")) read = true;
                        else if (IsMetric(line.Metric, "diskIoWrite")) write = true;
                        string name = ShortMetricNameOf(line.Metric);
                        if (!parts.Contains(name)) parts.Add(name);
                    }
                }
                if (up && down) return "网速";
                if (read && write) return "磁盘IO";
                if (parts.Count == 0) return "文本";
                return string.Join("/", parts.ToArray());
            }
            return ShortMetricNameOf(element.Metric);
        }

        /// <summary>指标键 → 短名；不在表里的（今后新增的指标）退回目录里的全名，不至于显示成空。</summary>
        private static string ShortMetricNameOf(string metricKey)
        {
            string name;
            if (metricKey != null && ShortMetricNames.TryGetValue(metricKey, out name)) return name;
            MetricInfo info;
            return MetricCatalog.TryGet(metricKey, out info) ? info.DisplayName : (metricKey ?? "—");
        }

        // ---- list actions -------------------------------------------------------------

        private void AddElement(bool topRow = true)
        {
            BandElement element = new BandElement
            {
                Id = "item-" + (_draft.Items.Count + 1),
                Type = BandElementType.Text,
                Enabled = true,
                Width = 52,
                RefreshMs = _draft.RefreshMs,
                Colors = new ElementColors
                {
                    Fill = SystemTheme.AccentColor(),
                    Outline = AppSettings.DefaultWhite,
                    Kernel = AppSettings.DefaultSystemRed
                },
                Lines = new List<TextLine>
                {
                    new TextLine { Metric = "temperature", Color = "#FFFFA94D", Indicator = Indicator.None }
                }
            };

            if (topRow && _topCount < TopRowSlots)
            {
                _draft.Items.Insert(_topCount, element);   // 插到第一排末尾
                _selectedIndex = _topCount;
                _topCount++;
            }
            else
            {
                _draft.Items.Add(element);                 // 接在第二排末尾
                _selectedIndex = _draft.Items.Count - 1;
            }
            RefreshList();
            BuildEditor();
        }

        // ---- editor: see ElementsPage.Editor.cs ------------------------------------------
        public void Refresh()
        {
            Load();
        }
    }
}
