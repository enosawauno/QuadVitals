using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 预设色块（可增删），**按配色角色分开存**：用户态（fill）、系统内核（kernel）、
    /// 外边框（outline）、文本行（line）各一份，互不影响——共用一份的话，给柱加一个颜色
    /// 会同时出现在边框和文字行里。
    ///
    /// 交互（每条都必须可发现，否则色块看起来只是装饰）：
    ///   - 左键 / Enter / Space：应用这个颜色；
    ///   - 右键 / 悬停时右上角的小 ✕ / Delete 键：从预设里删除（至少留 1 个）；
    ///   - 末尾「＋」：把当前颜色加进预设（去重，满 16 个末尾淘汰）；
    ///   - 当前颜色正好是某个预设时，该色块带描边环（相当于“选中态”）。
    ///
    /// 色块用可聚焦的 Border 而不是 Button：尺寸只有 16–20 px，Button 的模板与内边距
    /// 会把色块挤小；焦点环自己画（IsKeyboardFocused）。
    /// </summary>
    internal sealed class PaletteChips
    {
        private readonly SettingsWindow _window;
        private readonly Panel _host;
        private readonly string _role;
        private readonly Func<string> _current;
        private readonly Action<string> _pick;
        private readonly double _size;
        private readonly Thickness _margin;
        private readonly bool _editable;
        private readonly List<KeyValuePair<Border, string>> _chips = new List<KeyValuePair<Border, string>>();

        public PaletteChips(SettingsWindow window, Panel host, string role, Func<string> current,
                            Action<string> pick, double size, Thickness margin, bool editable)
        {
            _window = window;
            _host = host;
            _role = ColorRoles.Canonical(role);
            _current = current;
            _pick = pick;
            _size = size;
            _margin = margin;
            _editable = editable;
        }

        /// <summary>该角色当前生效的色板：自定义过就用它，否则退回内置默认 7 色。</summary>
        public static List<string> Resolve(SettingsWindow window, string role)
        {
            AppSettings settings = window == null ? null : window.CurrentSettings;
            ColorPresets presets = settings == null ? null : settings.ColorPresets;
            List<string> list = presets == null ? null : presets.Get(role);
            if (list == null || list.Count == 0) return new List<string>(AppSettings.DefaultPalette);
            return new List<string>(list);
        }

        /// <summary>
        /// 把一个颜色记进该角色的预设（取色器「确定时加入」开关走这里）。
        /// 去重 + 末尾淘汰 16 个。返回是否真的变了。
        /// </summary>
        public static bool RememberPreset(SettingsWindow window, string role, string colour)
        {
            if (window == null) return false;

            List<string> palette = Resolve(window, role);
            if (!PaletteRules.Add(palette, colour, AppSettings.DefaultPalette)) return false;

            AppSettings edited = window.CurrentSettings.Clone();
            if (edited.ColorPresets == null) edited.ColorPresets = new ColorPresets();
            edited.ColorPresets.Set(role, palette);
            edited.Normalize();
            window.SaveSettingsQuietly(edited);
            return true;
        }

        public void Fill()
        {
            _chips.Clear();
            _host.Children.Clear();

            foreach (string colour in Resolve(_window, _role))
            {
                string value = colour;
                Border chip = MakeChip(value);
                _chips.Add(new KeyValuePair<Border, string>(chip, value));
                _host.Children.Add(chip);
            }

            if (_editable) _host.Children.Add(MakeAddChip());
            UpdateSelection();
        }

        /// <summary>把“当前颜色”对应的色块标出来（颜色一变就调，不重建色块）。</summary>
        public void UpdateSelection()
        {
            string current;
            if (!ColorText.TryParse(_current == null ? null : _current(), out current)) current = null;

            for (int i = 0; i < _chips.Count; i++)
            {
                Border chip = _chips[i].Key;
                bool active = current != null &&
                              string.Equals(_chips[i].Value, current, StringComparison.OrdinalIgnoreCase);
                chip.BorderBrush = active
                    ? new SolidColorBrush(ReadableRing(Theme.ToColor(_chips[i].Value, Colors.White)))
                    : Brushes.Transparent;
            }
        }

        /// <summary>白色色块的选中环若也用白色就看不见了，所以按亮度挑深／浅环。</summary>
        private static Color ReadableRing(Color colour)
        {
            double luminance = 0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B;
            return luminance > 150 ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White;
        }

        /// <summary>Corner radius keeps the same proportion at every chip size.</summary>
        private static CornerRadius ChipCornerRadius(double size)
        {
            double radius = Math.Round(size * 0.23);
            if (radius < 3) radius = 3;
            return new CornerRadius(radius);
        }

        private Border MakeChip(string colour)
        {
            Color fill = Theme.ToColor(colour, Theme.TextSecondary);
            bool builtIn = PaletteRules.IsProtected(colour, AppSettings.DefaultPalette);

            Border chip = new Border
            {
                Width = _size,
                Height = _size,
                // 圆角跟着尺寸走，色块放大时形状不变形（16 px → 4、26 px → 6）。
                CornerRadius = ChipCornerRadius(_size),
                Margin = _margin,
                Background = new SolidColorBrush(fill),
                // 环用 1 px：彩色区域 = 总尺寸 - 2，再粗就会把色块本身挤小。
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Focusable = true,
                // 提示只占一行：两行以上会把卡片撑高，色块行与下面的输入框对不齐。
                ToolTip = !_editable
                    ? colour
                    : builtIn
                        ? colour + " 左键应用 · （默认预设，不可删除）"
                        : colour + " 左键应用 · 右键或 Delete 删除"
            };
            AutomationProperties.SetAutomationId(chip, "ColorPreset");
            AutomationProperties.SetName(chip, colour);
            // 不依赖继承：显式给每个色块设短延迟（150 ms），悬停即可出提示。
            ToolTipService.SetInitialShowDelay(chip, 150);
            ToolTipService.SetBetweenShowDelay(chip, 100);

            chip.MouseLeftButtonUp += (sender, e) =>
            {
                if (_pick != null) _pick(colour);
                UpdateSelection();
                e.Handled = true;
            };

            // 内置默认预设不可删：删掉之后没有入口能把它加回来。
            // 自定义色用右键 / Delete 删除；悬停叉会盖住色块本身，说明文字放 tooltip。
            if (_editable && !builtIn)
            {
                chip.MouseRightButtonUp += (sender, e) =>
                {
                    Remove(colour);
                    e.Handled = true;
                };

                chip.KeyDown += (sender, e) =>
                {
                    if (e.Key == Key.Delete || e.Key == Key.Back)
                    {
                        Remove(colour);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Enter || e.Key == Key.Space)
                    {
                        if (_pick != null) _pick(colour);
                        UpdateSelection();
                        e.Handled = true;
                    }
                };
            }
            else if (_editable)
            {
                chip.KeyDown += (sender, e) =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Space)
                    {
                        if (_pick != null) _pick(colour);
                        UpdateSelection();
                        e.Handled = true;
                    }
                };
            }

            if (_editable)
            {
                // 焦点环：键盘 Tab 到这里时能看出落在哪个色块上（失焦后回到选中态）
                chip.IsKeyboardFocusedChanged += (sender, e) =>
                {
                    if (chip.IsKeyboardFocused) chip.BorderBrush = new SolidColorBrush(ReadableRing(fill));
                    else UpdateSelection();
                };
            }

            return chip;
        }

        private Border MakeAddChip()
        {
            Border chip = new Border
            {
                Width = _size,
                Height = _size,
                CornerRadius = ChipCornerRadius(_size),
                Margin = _margin,
                Background = Theme.SurfaceHoverBrush,
                BorderBrush = Theme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Focusable = true,
                ToolTip = "把当前颜色加入预设"
            };
            AutomationProperties.SetAutomationId(chip, "ColorPresetAdd");
            AutomationProperties.SetName(chip, "添加预设");

            chip.Child = new TextBlock
            {
                Text = "+",
                FontSize = _size * 0.72,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextSecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            chip.MouseLeftButtonUp += (sender, e) =>
            {
                Add();
                e.Handled = true;
            };
            chip.KeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    Add();
                    e.Handled = true;
                }
            };
            return chip;
        }

        private void Add()
        {
            string canonical;
            if (_current == null || !ColorText.TryParse(_current(), out canonical)) return;
            if (!RememberPreset(_window, _role, canonical)) return;
            Fill();
        }

        private void Remove(string colour)
        {
            List<string> palette = Resolve(_window, _role);
            if (!PaletteRules.Remove(palette, colour, AppSettings.DefaultPalette)) return;
            if (_window == null) return;

            AppSettings edited = _window.CurrentSettings.Clone();
            if (edited.ColorPresets == null) edited.ColorPresets = new ColorPresets();
            edited.ColorPresets.Set(_role, palette);
            edited.Normalize();
            _window.SaveSettingsQuietly(edited);
            Fill();
        }
    }
}
