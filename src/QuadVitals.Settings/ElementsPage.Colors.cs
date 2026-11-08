using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuadVitals.UI;
using QuadVitals.Core;
using AppSettings = QuadVitals.Core.Settings;
using WpfButton = System.Windows.Controls.Button;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 显示项页里的配色行（十六进制输入 + 预设色块 + 点色块开取色器）。
    ///
    /// 单独一个文件是为了让 ElementsPage 留在 700 行以内；这里只碰颜色，不碰元素结构。
    /// </summary>
    internal sealed partial class ElementsPage
    {
        private FrameworkElement ColorRow(string label, string role, Func<string> getter, Action<string> setter)
        {
            Grid grid = FieldRow(label);

            StackPanel stack = new StackPanel { Orientation = Orientation.Horizontal };

            // 取色入口做成真正的 Button（而不是可点击的 Border）：键盘可聚焦、UIA 可 Invoke，
            // 无障碍与自动化测试都不需模拟鼠标坐标。外观仍是一个色块。
            WpfButton swatch = new WpfButton
            {
                // 26 = 十六进制输入框与预设色块的高度，三者齐平，否则色块行看着参差。
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Theme.ToColor(getter(), Theme.AccentGreen)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "点击取色",
                Focusable = true,
                Template = SwatchTemplate()
            };
            AutomationProperties.SetAutomationId(swatch, "ColorSwatch");
            AutomationProperties.SetName(swatch, label);

            TextBox box = new DarkTextBox
            {
                Text = getter(),
                // 96 是旧的（左右内边距 6）；内边距改 10 后 9 位十六进制会被裁，加宽到 108。
                Width = 108,
                FontFamily = Theme.MonoFont,
                FontSize = 11.5,
                Height = 26,
                // 左内边距与下拉/数字框一致（10），整列文字左边线对齐。
                Padding = new Thickness(10, 3, 10, 3)
            };
            AutomationProperties.SetAutomationId(box, "ColorHex");
            AutomationProperties.SetName(box, label);

            // 先声明：下面的取色器回调与 Commit 都要用它刷新选中态（色板对象在 layout 末尾才构建）。
            PaletteChips chips = null;

            void Commit()
            {
                // 接受 #AARRGGBB / #RRGGBB / RRGGBB / rgb(r,g,b) / rgba(...)，统一存成 #AARRGGBB。
                string canonical;
                if (!ColorText.TryParse(box.Text, out canonical))
                {
                    box.Text = getter();
                    return;
                }
                box.Text = canonical;
                setter(canonical);
                swatch.Background = new SolidColorBrush(Theme.ToColor(canonical, Theme.AccentGreen));
                RefreshList();
                if (chips != null) chips.UpdateSelection();
            }

            box.LostFocus += (sender, e) => Commit();
            box.KeyDown += (sender, e) => { if (e.Key == Key.Enter) Commit(); };

            // 点色块开取色器；色块、取色器、十六进制输入框三条路径必须写同一个值，
            // 否则同一个颜色在不同入口里算出不同的写法。
            swatch.Click += (sender, e) =>
            {
                string fallback = label == "外边框" || label == "描边色"
                    ? AppSettings.DefaultWhite
                    : AppSettings.DefaultGreen;
                ColorPickerDialog picker = new ColorPickerDialog(_window, label, role, getter(), fallback, hex =>
                {
                    setter(hex);
                    box.Text = hex;
                    swatch.Background = new SolidColorBrush(Theme.ToColor(hex, Theme.AccentGreen));
                    RefreshList();
                    // 取色器确认时可能刚把颜色记进预设（“添加到颜色预设”开关）：重建这一行，
                    // 否则颜色已经写进 settings.json，界面上还是原来的几个色块。
                    if (chips != null) chips.Fill();
                });
                _window.ShowOverlay(picker.Build());
            };

            StackPanel palette = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            // 色板按角色从 settings.json 的 colorPresets 读：可加（末位＋）、可删（右键 / ✕ / Delete），
            // 当前值的色块带选中环；三条入口（取色器 / 十六进制 / 预设色块）写同一个值。
            chips = new PaletteChips(_window, palette, role, getter, colour =>
            {
                box.Text = colour;
                setter(colour);
                swatch.Background = new SolidColorBrush(Theme.ToColor(colour, Theme.AccentGreen));
                RefreshList();
                if (chips != null) chips.UpdateSelection();
            }, 26, new Thickness(3, 0, 3, 0), true);   // 26 = 十六进制输入框的高度，色块与它等高
            chips.Fill();

            stack.Children.Add(swatch);
            stack.Children.Add(box);
            stack.Children.Add(palette);

            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
            return grid;
        }

        private static ControlTemplate SwatchTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(WpfButton));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfButton.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, Theme.BorderSubtleBrush);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            template.VisualTree = border;
            return template;
        }
    }
}
