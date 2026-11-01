using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 常用工具：一类工具一张全宽卡片，卡内横向排工具格，装不下的收进最后一格的「更多工具」。
    ///
    /// Column count is derived from the card's inner width instead of being fixed, so the same rule
    /// holds at the 1280 default (seven columns) and at the 980 minimum (five): when the count drops
    /// below the group size the overflow slot appears by itself. Tiles are placeholders until the
    /// real tool registry and its dialogs land; clicking one only names what is missing.
    /// </summary>
    internal sealed class ToolsPage : UserControl
    {
        /// <summary>Slot pitch: 116 px of tile plus 8 px of gap. Six CJK characters fit in a slot.</summary>
        private const double SlotPitch = 124;

        private const int MaxColumns = 8;
        private const int MinColumns = 2;

        private static readonly ToolGroup[] Groups =
        {
            new ToolGroup("系统工具", new[]
            {
                "硬件信息", "传感器诊断", "日志查看", "重启资源管理器", "恢复默认布局", "检查更新"
            }),
            new ToolGroup("网络工具", new[]
            {
                "网络测速", "网络修复", "DNS 重置", "联网管理", "适配器信息"
            }),
            new ToolGroup("其他工具", new[]
            {
                "取色器", "显示项模板", "诊断包"
            })
        };

        private readonly SettingsWindow _window;
        private readonly StackPanel _root = new StackPanel();

        public ToolsPage(SettingsWindow window)
        {
            _window = window;
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(Theme.PagePadding, 0, Theme.PagePadding, Theme.PagePadding),
                Content = _root
            };
            Build();
        }

        /// <summary>Columns that fit in the given inner width; the last column always holds 更多工具 last.</summary>
        internal static int ColumnsFor(double innerWidth)
        {
            if (double.IsNaN(innerWidth) || innerWidth <= 0) return 7;
            int columns = (int)Math.Floor((innerWidth + 8) / SlotPitch);
            if (columns < MinColumns) columns = MinColumns;
            if (columns > MaxColumns) columns = MaxColumns;
            return columns;
        }

        /// <summary>
        /// How many real tools a group shows: everything when it fits, otherwise one slot less than
        /// the column count because the last slot becomes 更多工具.
        /// </summary>
        internal static int VisibleCount(int total, int columns)
        {
            if (total <= 0) return 0;
            if (total <= columns) return total;
            return Math.Max(0, columns - 1);
        }

        private void Build()
        {
            _root.Children.Clear();
            _root.Children.Add(new PageHeading("常用工具", "按用途分组的快捷动作"));

            for (int groupIndex = 0; groupIndex < Groups.Length; groupIndex++)
            {
                ToolGroup group = Groups[groupIndex];
                Card card = new Card(group.Title);
                card.Margin = new Thickness(0, 0, 0, groupIndex == Groups.Length - 1 ? 0 : Theme.CardGap);

                UniformGrid grid = new UniformGrid { Columns = ColumnsFor(0) };
                AutomationProperties.SetAutomationId(grid, "ToolGrid" + groupIndex);

                // Reflow only when the column count really changed: assigning Columns inside
                // SizeChanged re-triggers layout, and rebuilding every tile on every pixel would
                // drop keyboard focus while the window is being resized.
                // The index is copied into a local: a for-loop variable is one variable for the whole
                // loop, so a closure reading it would stamp every rebuilt tile with the exit value.
                int index = groupIndex;
                int applied = grid.Columns;
                grid.SizeChanged += (sender, e) =>
                {
                    int columns = ColumnsFor(e.NewSize.Width);
                    if (columns == applied) return;
                    applied = columns;
                    grid.Columns = columns;
                    RenderGroup(grid, group, index, columns);
                };

                RenderGroup(grid, group, index, grid.Columns);

                card.Content.Children.Add(grid);
                _root.Children.Add(card);
            }
        }

        private void RenderGroup(UniformGrid grid, ToolGroup group, int groupIndex, int columns)
        {
            grid.Children.Clear();

            int shown = VisibleCount(group.Tools.Count, columns);
            for (int i = 0; i < shown; i++)
            {
                string name = group.Tools[i];
                grid.Children.Add(BuildTile(name, "tool-" + groupIndex + "-" + i, false));
            }

            if (shown < group.Tools.Count)
            {
                int hidden = group.Tools.Count - shown;
                grid.Children.Add(BuildTile("更多工具", "tool-" + groupIndex + "-more", true,
                    "还有 " + hidden + " 个"));
            }
        }

        private FrameworkElement BuildTile(string name, string automationId, bool more, string tooltip = null)
        {
            Button tile = new Button
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 8, 4, 8),
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
                Template = BuildTileTemplate()
            };
            AutomationProperties.SetAutomationId(tile, automationId);
            AutomationProperties.SetName(tile, name);
            if (!string.IsNullOrEmpty(tooltip))
            {
                tile.ToolTip = tooltip;
                ToolTipService.SetInitialShowDelay(tile, 300);
            }

            StackPanel stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            if (more)
            {
                // Four squares = "there are more": same footprint as a tool glyph, but it reads as
                // a grid rather than a missing icon.
                stack.Children.Add(BuildMoreGlyph());
            }
            else
            {
                FrameworkElement icon = ToolIcons.Build(name, Theme.TextPrimaryBrush);
                stack.Children.Add(icon ?? BuildPlaceholderGlyph());
            }

            stack.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                Foreground = Theme.TextPrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 10, 0, 0)
            });

            tile.Content = stack;
            tile.Click += (sender, e) => OnTileClick(name, more);
            return tile;
        }

        /// <summary>Dashed rounded square: the icon is not designed yet, so the slot says so visually.</summary>
        private static FrameworkElement BuildPlaceholderGlyph()
        {
            return new Rectangle
            {
                Width = 24,
                Height = 24,
                RadiusX = 6,
                RadiusY = 6,
                Stroke = Theme.TextTertiaryBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true
            };
        }

        private static FrameworkElement BuildMoreGlyph()
        {
            UniformGrid squares = new UniformGrid
            {
                Rows = 2,
                Columns = 2,
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            for (int i = 0; i < 4; i++)
            {
                squares.Children.Add(new Border
                {
                    Background = Theme.TextSecondaryBrush,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(2)
                });
            }
            return squares;
        }

        private static ControlTemplate BuildTileTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));

            FrameworkElementFactory chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "chrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(Theme.ControlRadius));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            chrome.AppendChild(presenter);
            template.VisualTree = chrome;

            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            Setter hoverFill = new Setter(Border.BackgroundProperty, Theme.SurfaceHoverBrush);
            hoverFill.TargetName = "chrome";
            hover.Setters.Add(hoverFill);
            template.Triggers.Add(hover);

            Trigger focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            Setter focusEdge = new Setter(Border.BorderBrushProperty, Theme.AccentGreenBrush);
            focusEdge.TargetName = "chrome";
            focus.Setters.Add(focusEdge);
            template.Triggers.Add(focus);

            return template;
        }

        private void OnTileClick(string name, bool more)
        {
            if (more)
            {
                _window.ShowNotice("更多工具面板尚未接入");
                return;
            }
            if (name == "硬件信息")
            {
                _window.ShowOverlay(new HardwareInfoDialog(_window).Build());
                return;
            }
            _window.ShowNotice("『" + name + "』尚未接入");
        }

        private sealed class ToolGroup
        {
            public ToolGroup(string title, IList<string> tools)
            {
                Title = title;
                Tools = tools;
            }

            public string Title { get; private set; }
            public IList<string> Tools { get; private set; }
        }
    }
}
