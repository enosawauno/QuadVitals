using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using QuadVitals.Core;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 元素列表的拖动排序。单独放一个文件是因为 ElementsPage.cs 已经超过仓库的
    /// 1200 行源码红线（脚本按非空行算），而这些方法只被列表那几个鼠标事件调用，边界很干净。
    /// </summary>
    internal sealed partial class ElementsPage
    {
        /// <summary>长按多久算“抓住”（非编辑模式下 → 进编辑模式并接着拖）。</summary>
        private const int LongPressMs = 800;

        /// <summary>
        /// 长按期间允许的抖动幅度。8 px 太松：800 ms 里手很容易超过，计时器静默作废，
        /// 表现就是“长按没反应、拖不动”。
        /// </summary>
        private const int HoldSlopPx = 24;

        /// <summary>按住多久才开始“鼓起”（轻点不该有形变）。</summary>
        private const int HoldCueDelayMs = 220;

        /// <summary>按住时的鼓起幅度，用来回应“可以拖了”。</summary>
        private const double HoldScale = 1.035;

        /// <summary>被拖方块相对指针抬高几像素，让它看起来是“被拿起来的”而不是行里的第 5 个。</summary>
        private const double DragLiftPx = 6;

        /// <summary>
        /// 编辑模式下按下后要走多远才算“拖”。0 的话单击也会立刻起拖：方块弹起、留一个洞、
        /// 后面的方块让位，松手又弹回去 —— 只是点一下选中，看着却像被拖了一下。
        /// </summary>
        private const int DragStartSlopPx = 8;

        /// <summary>补位滑动的时长。不加动画时方块是“瞬间跳一格”，看着生硬。</summary>
        private const int ReflowMs = 140;

        /// <summary>插入位置/换排的迟滞宽度：指针在边界上来回晃 8 px 不会反复翻转。</summary>
        private const double DropHysteresisPx = 8;

        private FrameworkElement _dragTile;
        private int _dragIndex = -1;
        private bool _dragging;
        private bool _pendingDrag;
        private Point _pressPoint;
        private Canvas _dragLayer;
        private FrameworkElement _dragPlaceholder;
        private Point _grabOffset;
        private int _insertIndex = -1;
        private System.Windows.Threading.DispatcherTimer _pressTimer;

        /// <summary>正在正常收尾：此时 ReleaseMouseCapture 触发的 LostMouseCapture 不算“打断”。</summary>
        private bool _endingDrag;

        /// <summary>正在重挂方块（先摘出行、再放进浮层）：这两个动作本身会释放捕获。</summary>
        private bool _startingDrag;

        /// <summary>当前显示着“按住”反馈的方块；没有反馈时为 null（不能随便清别人的变换，编辑模式下那是抖动）。</summary>
        private FrameworkElement _holdCueTile;

        private void BeginPress(FrameworkElement tile, int index, MouseButtonEventArgs e)
        {
            // 上一轮手势没收尾（例如松手落在窗口外）会留下半截状态；先清掉，不要拿它继续用。
            if (_dragging)
            {
                AbortDrag();
                return;   // AbortDrag 重建了列表，这次按下对应的方块已经失效
            }
            _pendingDrag = false;

            _dragTile = tile;
            _dragIndex = index;
            _dragging = false;
            _pressPoint = e.GetPosition(this);

            // 按下就捕获：不捕获的话，松手落在方块外会收不到 Up，`_pendingDrag` 一直挂着，
            // 之后鼠标只是划过方块就会被当成“拖拽开始”（没拖，后面的方块乱跳 + 抛异常）。
            try { tile.CaptureMouse(); } catch (InvalidOperationException) { }

            if (_editMode)
            {
                // 编辑模式：按下先当“点击”，指针真的走动后再起拖（否则单击也会弹起、冒洞）。
                _pendingDrag = true;
                return;
            }

            // 非编辑模式：长按 LongPressMs = 进入编辑模式（就地抖动）并接着同一个手势拖动（iOS 桌面那条路）
            if (_pressTimer != null) { _pressTimer.Stop(); }
            _pressTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(LongPressMs)
            };
            _pressTimer.Tick += (sender, args) =>
            {
                _pressTimer.Stop();
                if (_dragIndex < 0 || _dragTile == null) return;

                // 长按时的“鼓起”留给 ApplyJiggle 接着用（它会把当前缩放当作起点），
                // 这里只放弃所有权，不能清 RenderTransform，否则会先回弹一下再放大。
                _holdCueTile = null;
                // 就地开编辑模式：**不重建方块**，所以下面这个 capture 手势不会断
                SetEditMode(true);
                StartDrag(_dragTile, _dragIndex);
            };
            ShowHoldCue(tile);
            _pressTimer.Start();
        }

        private void StartDrag(FrameworkElement tile, int index)
        {
            // 兜底：不在行里的方块（已经在浮层里/已被重建）不能当拖动起点。
            // 没有这个守卫时，一个漏掉的 Up 会让 StartDrag 对着浮层里的方块跑，
            // TransformToAncestor(_rows) 抛“指定的 Visual 不是此 Visual 的上级”。
            if (!IsInRows(tile)) return;

            _dragTile = tile;
            _dragIndex = index;
            _insertIndex = index;
            _dragging = true;
            _pendingDrag = false;
            _holdCueTile = null;   // 鼓起交给 PopGrab 接着放大，不再归“长按反馈”管

            // 停掉抖动（否则动画会抢 TranslateTransform），并把方块从行里摘出来放进浮层：
            // 行里换成一个虚线占位块，其余方块自动往前补位。
            Border border = tile as Border;
            if (border != null)
            {
                RotateTransform rotate;
                if (_rotateOf.TryGetValue(border, out rotate) && rotate != null)
                {
                    rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    rotate.Angle = 0;
                }
                TranslateTransform drift;
                if (_driftOf.TryGetValue(border, out drift) && drift != null)
                {
                    drift.BeginAnimation(TranslateTransform.XProperty, null);
                    drift.X = 0;
                    drift.Y = 0;
                }
            }

            Point tilePos = tile.TransformToAncestor(_rows).Transform(new Point(0, 0));
            Point mousePos = Mouse.GetPosition(_rows);
            _grabOffset = new Point(mousePos.X - tilePos.X, mousePos.Y - tilePos.Y);

            // 先记下移除前的布局位置：下面把方块摘出行会让后面的方块左移 72 px，这一段也要滑动而不是跳。
            Dictionary<FrameworkElement, Point> before = CaptureLayout();

            // 顺序很重要：摘出行 → 放进浮层 → 最后才捕获。先捕获再摘会让 Remove 释放捕获，
            // 触发 LostMouseCapture，把刚起头的拖拽当成“被系统打断”取消掉。
            _startingDrag = true;
            Panel parent = tile.Parent as Panel;
            if (parent != null) parent.Children.Remove(tile);
            tile.Width = TileSize;
            tile.Height = TileSize;
            Canvas.SetLeft(tile, tilePos.X);
            Canvas.SetTop(tile, tilePos.Y - DragLiftPx);
            _dragLayer.Children.Add(tile);
            try { tile.CaptureMouse(); } catch (InvalidOperationException) { }
            _startingDrag = false;

            ApplyDragVisual(tile);
            PopGrab(tile);

            EnsurePlaceholder();
            MovePlaceholder(index, before);
        }

        /// <summary>
        /// 被拖起来的方块要看起来“离手了”：去掉 ✕ 徽标与选中绿环、加投影、略透明、抬高 6 px。
        /// 否则它、选中态、占位块三者都是绿色方块，分不清哪个在手里、哪个是落点。
        /// </summary>
        private void ApplyDragVisual(FrameworkElement tile)
        {
            Border border = tile as Border;
            if (border == null) return;

            Grid content = _contentOf.ContainsKey(border) ? _contentOf[border] : null;
            Border badge;
            if (content != null && _badgeOf.TryGetValue(border, out badge) && badge != null)
            {
                content.Children.Remove(badge);
            }
            _badgeOf.Remove(border);

            border.BorderBrush = Brushes.Transparent;
            border.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.55
            };
            border.Opacity = 0.9;
        }

        /// <summary>按住 220 ms 后鼓起 3.5%：轻点没有形变，按住不放才有“抓住了”的回应。</summary>
        private void ShowHoldCue(FrameworkElement tile)
        {
            Border border = tile as Border;
            if (border == null) return;

            ScaleTransform scale = new ScaleTransform(1, 1);
            TransformGroup group = new TransformGroup();
            group.Children.Add(scale);
            border.RenderTransform = group;
            border.RenderTransformOrigin = new Point(0.5, 0.5);
            _scaleOf[border] = scale;
            _holdCueTile = tile;

            DoubleAnimation grow = new DoubleAnimation(HoldScale, TimeSpan.FromMilliseconds(160))
            {
                BeginTime = TimeSpan.FromMilliseconds(HoldCueDelayMs),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        /// <summary>撤掉“按住”反馈（轻点/位移超限/手势被打断）。</summary>
        private void ClearHoldCue()
        {
            FrameworkElement tile = _holdCueTile;
            _holdCueTile = null;
            if (tile == null) return;

            Border border = tile as Border;
            if (border == null) return;
            ScaleTransform scale;
            if (!_scaleOf.TryGetValue(border, out scale) || scale == null) return;

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scaleOf.Remove(border);
            border.RenderTransform = null;
        }

        /// <summary>清掉拖拽状态（占位块、浮层、索引）。落点由调用方先取走再调这里。</summary>
        private void CleanupDrag()
        {
            _dragging = false;
            _pendingDrag = false;
            _dragIndex = -1;
            _dragTile = null;
            _insertIndex = -1;

            if (_dragPlaceholder != null)
            {
                Panel holder = _dragPlaceholder.Parent as Panel;
                if (holder != null) holder.Children.Remove(_dragPlaceholder);
                _dragPlaceholder = null;
            }
            if (_dragLayer != null) _dragLayer.Children.Clear();
        }

        /// <summary>
        /// 手势被打断（捕获丢失、窗口失活）：把占位块和浮动方块收拾掉。
        /// 不收拾的话界面上会留一个“洞”和一个飘着的方块，下一次点击还会接着一个不存在的拖拽。
        /// </summary>
        private void AbortDrag()
        {
            if (_endingDrag || _startingDrag) return;
            if (_pressTimer != null) { _pressTimer.Stop(); }
            _pendingDrag = false;
            ClearHoldCue();

            if (!_dragging)
            {
                FrameworkElement captured = _dragTile;
                _endingDrag = true;
                if (captured != null)
                {
                    try { captured.ReleaseMouseCapture(); } catch (InvalidOperationException) { }
                }
                _endingDrag = false;
                _dragTile = null;
                _dragIndex = -1;
                return;
            }

            CleanupDrag();
            RefreshList();
        }

        private void EnsurePlaceholder()
        {
            if (_dragPlaceholder != null) return;

            // 落点必须看起来是“洞”，不能是又一块绿色方块：实心 Border（绿底 + 1 px 绿边）
            // 会与选中态（2 px 绿边）、被拖方块撞成三个绿块，分不清落点。Border 画不了虚线，
            // 所以和「＋」方块一样，用 Rectangle 画虚线。
            Grid holder = new Grid
            {
                Width = TileSize,
                Height = TileSize,
                Margin = new Thickness(0, 0, TileGap, 0),
                IsHitTestVisible = false
            };
            holder.Children.Add(new System.Windows.Shapes.Rectangle
            {
                RadiusX = 12,
                RadiusY = 12,
                Stroke = Theme.AccentGreenBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(0x14, 0x1D, 0xB9, 0x54))
            });

            // 占位块换槽时也走同一套补位动画，否则洞里那一格会“瞬移”。
            TranslateTransform reflow = new TranslateTransform(0, 0);
            holder.RenderTransform = reflow;
            _reflowOf[holder] = reflow;
            _dragPlaceholder = holder;
        }

        /// <summary>
        /// 把占位块放到 insertIndex（移除被拖项之后的索引）。槽位不能直接用 index - 4：
        /// 从第一排拖进第二排时，被拖项原本不在第二排，直接减会比实际落点少一格（占位块跑到前一个位置）。
        /// </summary>
        /// <summary>拖动开始前：被拖元素原本在第一排吗。</summary>
        private bool DragFromTop { get { return _dragIndex >= 0 && _dragIndex < _topCount; } }

        /// <summary>指针最后落在哪一排（`ComputeInsertIndex` 写入；`MovePlaceholder` 以此分排）。</summary>
        private bool _insertBottom;

        /// <summary>拖走之后第一排还剩几个（插入位置以这个为界分排）。</summary>
        private int PostTopCount { get { return DragFromTop ? _topCount - 1 : _topCount; } }

        private void MovePlaceholder(int insertIndex, Dictionary<FrameworkElement, Point> before = null)
        {
            if (_dragPlaceholder == null) return;
            if (before == null) before = CaptureLayout();
            _insertIndex = insertIndex;

            // 分排用“拖走后第一排还剩几个”，不是固定的 `TopRowSlots`：第一排可以不完整，
            // 删第一排的元素也不会把第二排的拉上来（`_topCount` 自己是会变的）。
            int postTop = PostTopCount;
            // 拖动期间没有空洞（按下时已压紧）。槽位要数“移除被拖项之后的下标”，不能直接减固定值：
            // 从第一排拖到第二排尾部时，被拖项原本不在第二排，直接减会少一格。
            bool second = _insertBottom;
            Panel row = second ? _rowBottom : _rowTop;

            Panel current = _dragPlaceholder.Parent as Panel;
            if (current != null) current.Children.Remove(_dragPlaceholder);

            int slot = 0;
            foreach (object child in row.Children)
            {
                FrameworkElement element = child as FrameworkElement;
                if (element == null || element == _dragPlaceholder) continue;
                if (!(element.Tag is int)) continue;   // 跳过虚线「＋」方块

                int original = (int)element.Tag;
                int after = original < _dragIndex ? original : original - 1;
                if (after < insertIndex) slot++;
            }

            if (slot > row.Children.Count) slot = row.Children.Count;
            row.Children.Insert(slot, _dragPlaceholder);

            AnimateReflow(before);
        }

        /// <summary>
        /// 元素在**当前布局**里的位置（行 + 行内序号）。
        /// 不能用 `Tag`（草稿下标）算：把第一排的第一个抽走，后面元素在草稿里的下标全减 1，
        /// 第五个会“变成”第四个 —— 按下标算就会把它当成要上搬到第一排，第二排因此上下左右乱飘。
        /// 按子元素实际顺序算，方块只会在它所在的那一排里平移。
        /// </summary>
        private Point SlotPosition(FrameworkElement element)
        {
            double cell = TileSize + TileGap;
            int bottom = _rowBottom.Children.IndexOf(element);
            if (bottom >= 0) return new Point(bottom * cell, cell);

            int top = _rowTop.Children.IndexOf(element);
            if (top < 0) return new Point(0, 0);
            return new Point(top * cell, 0);
        }

        private Dictionary<FrameworkElement, Point> CaptureLayout()
        {
            Dictionary<FrameworkElement, Point> map = new Dictionary<FrameworkElement, Point>();
            Panel[] rows = { _rowTop, _rowBottom };
            foreach (Panel row in rows)
            {
                foreach (object child in row.Children)
                {
                    FrameworkElement element = child as FrameworkElement;
                    if (element == null) continue;
                    if (element != _dragPlaceholder && !(element.Tag is int)) continue;   // 跳过「＋」方块
                    map[element] = SlotPosition(element);
                }
            }
            return map;
        }

        private bool IsInRows(FrameworkElement element)
        {
            return _rowTop.Children.Contains(element) || _rowBottom.Children.Contains(element);
        }

        /// <summary>
        /// 补位动画（FLIP）：布局已经把方块放到新位置了，这里先给它一个“反向位移”画在旧位置，
        /// 再用 140 ms 缓出滑回去（不缓动就是“瞬间跳一格”，看着生硬）。
        /// 连续换槽时把当前动画偏移叠上去，避免每次都从头跳一下。
        /// </summary>
        private void AnimateReflow(Dictionary<FrameworkElement, Point> before)
        {
            if (before == null || before.Count == 0) return;

            foreach (KeyValuePair<FrameworkElement, Point> pair in before)
            {
                FrameworkElement element = pair.Key;
                if (element != _dragPlaceholder && !IsInRows(element)) continue;

                Point after = SlotPosition(element);

                TranslateTransform reflow;
                if (!_reflowOf.TryGetValue(element, out reflow) || reflow == null) continue;

                // 上一段动画还没跑完时把当前偏移接上去，方块不会“重新跳一下”；
                // 但必须夹在一格以内：一连串换槽（手在阈值上来回额）叠下去会把方块带到几格以外。
                double cell = TileSize + TileGap;
                double carryX = Math.Max(-cell, Math.Min(cell, reflow.X));
                double carryY = Math.Max(-cell, Math.Min(cell, reflow.Y));
                double dx = pair.Value.X - after.X + carryX;
                double dy = pair.Value.Y - after.Y + carryY;

                reflow.BeginAnimation(TranslateTransform.XProperty, null);
                reflow.BeginAnimation(TranslateTransform.YProperty, null);

                // 只对“一格以内”的移动做滑动；一次跳好几格（拖得快）就直接就位。
                // 否则方块会拖着一串未跑完的位移在后面“飘”。
                if (Math.Abs(dx) > cell + DropHysteresisPx || Math.Abs(dy) > cell + DropHysteresisPx)
                {
                    reflow.X = 0;
                    reflow.Y = 0;
                    continue;
                }

                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                {
                    reflow.X = 0;
                    reflow.Y = 0;
                    continue;
                }

                reflow.X = dx;
                reflow.Y = dy;
                reflow.BeginAnimation(TranslateTransform.XProperty, ReflowAnimation());
                reflow.BeginAnimation(TranslateTransform.YProperty, ReflowAnimation());
            }
        }

        /// <summary>
        /// 补位用的缓出动画。抽成方法是为了让离屏探针能直接断言“真的是缓动、不是线性”
        /// （只验证 HasAnimatedProperties 分不出 CubicEase 和线性）。
        /// </summary>
        private static DoubleAnimation ReflowAnimation()
        {
            return new DoubleAnimation(0, TimeSpan.FromMilliseconds(ReflowMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
        }

        /// <summary>
        /// 拖动时方块跟着鼠标走：按 Canvas 定位（位移不动布局，所以其余方块不会被推着抖），
        /// 同时把占位块挪到当前插入位置，后面的方块自动往前补。
        /// </summary>
        private void FollowMouse(Point pagePoint)
        {
            if (_dragTile == null || _dragLayer == null) return;

            Point inLayer = TranslatePoint(pagePoint, _rows);
            Canvas.SetLeft(_dragTile, inLayer.X - _grabOffset.X);
            Canvas.SetTop(_dragTile, inLayer.Y - _grabOffset.Y - DragLiftPx);

            int index = ComputeInsertIndex(inLayer);
            if (index != _insertIndex)
            {
                _insertIndex = index;
                MovePlaceholder(index);
            }
        }

        /// <summary>鼠标位置 → 插入索引（已排除正在拖的那一个，即“移除后的索引”）。</summary>
        private int ComputeInsertIndex(Point pointInLayer)
        {
            bool second = IsOverBottomRow(pointInLayer);
            int baseSlot = second ? PostTopCount : 0;

            // 按**固定格子**算视觉槽位，不看方块当前摆在哪里（否则是一条反馈回路）。
            double cell = TileSize + TileGap;
            int rowSlot = (int)Math.Floor(pointInLayer.X / cell);
            if (rowSlot < 0) rowSlot = 0;

            // 列迟滞：停在格子边界上时不要来回翻（±8 px 死区）。
            if (_insertIndex >= 0)
            {
                int currentRowSlot = _insertIndex - baseSlot;
                if (currentRowSlot < 0) currentRowSlot = 0;
                if (rowSlot == currentRowSlot + 1 && pointInLayer.X < (currentRowSlot + 1) * cell + DropHysteresisPx) rowSlot = currentRowSlot;
                else if (rowSlot == currentRowSlot - 1 && pointInLayer.X > currentRowSlot * cell - DropHysteresisPx) rowSlot = currentRowSlot;
            }

            int visualSlot = baseSlot + rowSlot;
            int index = visualSlot;                  // 无空洞：视觉槽位就是紧凑下标
            int max = _draft.Items.Count - 1;
            if (index > max) index = max;
            if (index < 0) index = 0;
            // 记住指针在哪一排：插入下标会被夹到 max，光靠下标分不出“第一排末尾 / 第二排开头”。
            _insertBottom = second;
            return index;
        }

        /// <summary>指针是否已经落进第二排。元素 ≤ 4 个时没有第二排（列表里不会有空位）。</summary>
        private bool IsOverBottomRow(Point pointInLayer)
        {
            if (_draft.Items.Count <= _topCount) return false;
            Point top = _rowBottom.TransformToAncestor(_rows).Transform(new Point(0, 0));

            // 迟滞：已经在下排就得往上走出 8 px 才回上排，反之亦然。
            // 没有它时，指针停在两排交界上（y ≈ 72）会上下反复翻转，
            // 占位块在两排之间跳、两排的方块跟着上下左右乱飘。
            bool currentlyBottom = _insertIndex >= PostTopCount;
            return currentlyBottom
                ? pointInLayer.Y > top.Y - DropHysteresisPx
                : pointInLayer.Y > top.Y + DropHysteresisPx;
        }

        private void MovePress(FrameworkElement tile, MouseEventArgs e)
        {
            Point now = e.GetPosition(this);

            // 悬停（没按键）不可能是拖拽：捕获残留或漏掉 Up 时在这里自愈，
            // 否则鼠标只是划过方块就会把它拎起来。
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                if (_dragging || _pendingDrag) AbortDrag();
                return;
            }

            if (!_dragging)
            {
                if (_pendingDrag && ExceedsDragSlop(now))
                {
                    _pendingDrag = false;
                    StartDrag(tile, _dragIndex);
                    FollowMouse(now);
                    return;
                }

                // 没长按就移动 = 普通点击，取消长按计时，避免误入拖拽状态
                if (Math.Abs(now.X - _pressPoint.X) + Math.Abs(now.Y - _pressPoint.Y) > HoldSlopPx && _pressTimer != null)
                {
                    _pressTimer.Stop();
                    ClearHoldCue();
                }
                return;
            }

            FollowMouse(now);
        }

        /// <summary>指针是否已经走出“这只是一次点击”的范围。</summary>
        private bool ExceedsDragSlop(Point now)
        {
            double dx = now.X - _pressPoint.X;
            double dy = now.Y - _pressPoint.Y;
            return dx * dx + dy * dy > DragStartSlopPx * DragStartSlopPx;
        }

        /// <summary>手指是否已经走出“这只是一次点击”的范围（离屏探针用）。</summary>
        private bool ExceedsDragSlopForTest(Point pressPoint, Point nowPoint)
        {
            _pressPoint = pressPoint;
            return ExceedsDragSlop(nowPoint);
        }

        private void EndPress(FrameworkElement tile, MouseButtonEventArgs e)
        {
            if (_pressTimer != null) _pressTimer.Stop();
            _pendingDrag = false;

            if (!_dragging)
            {
                // 没进入拖拽：当普通点击处理（选中），交给方块自带的点击处理。
                // 按下时捕获过鼠标，这里必须放掉，否则后续的悬停/点击都会被这个方块吃掉。
                _endingDrag = true;
                try { tile.ReleaseMouseCapture(); } catch (InvalidOperationException) { }
                _endingDrag = false;
                ClearHoldCue();
                _dragTile = null;
                _dragIndex = -1;
                return;
            }

            // 先取走落点再释放捕获：ReleaseMouseCapture 会同步触发 LostMouseCapture → AbortDrag，
            // 那时索引已经被清空了，落点就丢了。
            int from = _dragIndex;
            int to = _insertIndex;
            _endingDrag = true;
            try { tile.ReleaseMouseCapture(); } catch (InvalidOperationException) { }
            _endingDrag = false;

            CleanupDrag();

            if (to != from) MoveElement(from, to);
            else
            {
                _selectedIndex = from;
                RefreshList();
                BuildEditor();
            }
            e.Handled = true;
        }

        /// <summary>把第 from 个元素移到第 to 个位置（拖动排序的唯一写入口，便于离屏断言）。</summary>
        private void MoveElement(int from, int to)
        {
            if (from < 0 || to < 0 || from >= _draft.Items.Count || to >= _draft.Items.Count) return;
            if (from == to) return;

            // 拖动可以跨排：排归属（churn）按“是否掉进第一排”重算，而不是固定 4。
            bool fromTop = from < _topCount;
            int postTop = fromTop ? _topCount - 1 : _topCount;
            bool toTop = to <= postTop;

            BandElement item = _draft.Items[from];
            _draft.Items.RemoveAt(from);
            _draft.Items.Insert(to, item);
            _selectedIndex = to;

            _topCount = postTop + (toTop ? 1 : 0);
            if (_topCount > TopRowSlots) _topCount = TopRowSlots;
            if (_topCount < 0) _topCount = 0;

            RefreshList();
            BuildEditor();
        }
    }
}
