using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Adapter + refresh-rate dialog, opened from the gear in the "当前网速" card header
    /// (moved out of the 网络与温度 page). Fixed 3:2 panel, centred in the window by
    /// <see cref="SettingsWindow.ShowOverlay"/>; "保存" writes settings.json and closes itself,
    /// then the agent hot-reloads within a second.
    /// </summary>
    internal sealed class NetworkSettingsDialog
    {
        // 弹窗尺寸 640×427（3:2），文字、控件、按钮保持原大小。
        public const double DialogWidth = 640;
        public const double DialogHeight = 427;   // 640 / 1.5，保持 3:2

        private static readonly KeyValuePair<string, string>[] Curves =
        {
            new KeyValuePair<string, string>(AppSettings.CurveSmooth, "圆滑曲线"),
            new KeyValuePair<string, string>(AppSettings.CurvePolyline, "折线")
        };

        private static readonly KeyValuePair<string, string>[] Rates =
        {
            new KeyValuePair<string, string>("1000", "1 秒（默认）"),
            new KeyValuePair<string, string>("500", "0.5 秒"),
            new KeyValuePair<string, string>("2000", "2 秒"),
            new KeyValuePair<string, string>("5000", "5 秒")
        };

        private readonly SettingsWindow _window;

        public NetworkSettingsDialog(SettingsWindow window)
        {
            _window = window;
        }

        public FrameworkElement Build()
        {
            AppSettings settings = _window.CurrentSettings;

            Border panel = new Border
            {
                Width = DialogWidth,
                Height = DialogHeight,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                SnapsToDevicePixels = true
            };

            // 标题与字段贴顶、按钮贴底，中间整块留空（后面要在中间加东西）。
            // 所以是 三行 = 内容 / 弹性空行 / 按钮，grid 自己铺满面板（不居中）。
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "网络设置",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            StackPanel fields = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

            ComboBox adapter = BuildAdapterBox(settings);
            fields.Children.Add(FieldLabel("网卡"));
            fields.Children.Add(adapter);
            fields.Children.Add(new TextBlock
            {
                Text = "自动模式统计所有物理网卡的 IPv4+IPv6 总量，虚拟机/VPN 适配器会被排除。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0)
            });

            ComboBox rate = BuildRateBox(settings);
            TextBlock rateLabel = FieldLabel("刷新间隔");
            rateLabel.Margin = new Thickness(0, 18, 0, 8);
            fields.Children.Add(rateLabel);
            fields.Children.Add(rate);

            fields.Children.Add(new TextBlock
            {
                Text = "数值刷新、传感器采样与曲线采样共用这个周期（曲线共记录 60 个点）。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0)
            });

            ComboBox curve = BuildCurveBox(settings);
            TextBlock curveLabel = FieldLabel("曲线样式");
            curveLabel.Margin = new Thickness(0, 18, 0, 8);
            fields.Children.Add(curveLabel);
            fields.Children.Add(curve);

            // 标题与字段合成一组放在第一行，中间那行留给后续内容。
            StackPanel content = new StackPanel();
            content.Children.Add(title);
            content.Children.Add(fields);
            Grid.SetRow(content, 0);
            grid.Children.Add(content);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0)
            };

            FlatButton cancel = new FlatButton("取消");
            AutomationProperties.SetAutomationId(cancel, "NetworkSettingsCancel");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) => _window.CloseOverlay();
            buttons.Children.Add(cancel);

            FlatButton save = new FlatButton("保存", true);
            AutomationProperties.SetAutomationId(save, "NetworkSettingsSave");
            save.Click += (sender, e) => Save(adapter, rate, curve);
            buttons.Children.Add(save);

            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            return panel;
        }

        private static TextBlock FieldLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                // 标题 → 字段固定 8 px；三个字段完全一致（后两个的 bottom margin 被覆盖成 0 就会贴到框上）。
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private static ComboBox BuildAdapterBox(AppSettings settings)
        {
            ComboBox box = new DarkComboBox();
            AutomationProperties.SetAutomationId(box, "NetworkAdapterSelect");
            AutomationProperties.SetName(box, "网卡");

            box.Items.Add(new ComboBoxItem { Content = "自动（所有物理网卡）", Tag = "auto", Background = Theme.SurfaceHoverBrush, Foreground = Theme.TextPrimaryBrush });
            foreach (string candidate in ListAdapters())
            {
                box.Items.Add(new ComboBoxItem { Content = candidate, Tag = candidate, Background = Theme.SurfaceHoverBrush, Foreground = Theme.TextPrimaryBrush });
            }

            int index = 0;
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (string.Equals((string)((ComboBoxItem)box.Items[i]).Tag, settings.NetworkAdapter,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            box.SelectedIndex = index;
            return box;
        }

        /// <summary>
        /// A refresh period set by hand in settings.json (any 200-10000 ms) is kept as its own
        /// option instead of being silently rounded to the nearest preset on save.
        /// </summary>
        private static ComboBox BuildRateBox(AppSettings settings)
        {
            ComboBox box = new DarkComboBox();
            AutomationProperties.SetAutomationId(box, "RefreshRateSelect");
            AutomationProperties.SetName(box, "刷新间隔");

            string current = settings.RefreshMs.ToString();
            int index = 0;
            for (int i = 0; i < Rates.Length; i++)
            {
                box.Items.Add(new ComboBoxItem
                {
                    Content = Rates[i].Value,
                    Tag = Rates[i].Key,
                    Background = Theme.SurfaceHoverBrush,
                    Foreground = Theme.TextPrimaryBrush
                });
                if (Rates[i].Key == current) index = i;
            }

            bool known = false;
            for (int i = 0; i < Rates.Length; i++)
            {
                if (Rates[i].Key == current) { known = true; break; }
            }
            if (!known)
            {
                box.Items.Add(new ComboBoxItem
                {
                    Content = settings.RefreshMs + " 毫秒（当前）",
                    Tag = current,
                    Background = Theme.SurfaceHoverBrush,
                    Foreground = Theme.TextPrimaryBrush
                });
                index = box.Items.Count - 1;
            }

            box.SelectedIndex = index;
            return box;
        }

        private static ComboBox BuildCurveBox(AppSettings settings)
        {
            ComboBox box = new DarkComboBox();
            AutomationProperties.SetAutomationId(box, "CurveStyleSelect");
            AutomationProperties.SetName(box, "曲线样式");

            int index = 0;
            for (int i = 0; i < Curves.Length; i++)
            {
                box.Items.Add(new ComboBoxItem
                {
                    Content = Curves[i].Value,
                    Tag = Curves[i].Key,
                    Background = Theme.SurfaceHoverBrush,
                    Foreground = Theme.TextPrimaryBrush
                });
                if (string.Equals(Curves[i].Key, settings.CurveStyle, StringComparison.OrdinalIgnoreCase)) index = i;
            }
            box.SelectedIndex = index;
            return box;
        }

        private void Save(ComboBox adapter, ComboBox rate, ComboBox curve)
        {
            // Start from the live settings, not from the snapshot this dialog was built with: the
            // user may have saved something else in between (display items, autostart).
            AppSettings edited = _window.CurrentSettings.Clone();

            ComboBoxItem selectedAdapter = adapter.SelectedItem as ComboBoxItem;
            edited.NetworkAdapter = selectedAdapter == null ? "auto" : (string)selectedAdapter.Tag;

            ComboBoxItem selectedCurve = curve.SelectedItem as ComboBoxItem;
            if (selectedCurve != null) edited.CurveStyle = (string)selectedCurve.Tag;

            ComboBoxItem selectedRate = rate.SelectedItem as ComboBoxItem;
            int milliseconds;
            if (selectedRate != null && int.TryParse((string)selectedRate.Tag, out milliseconds))
            {
                edited.RefreshMs = milliseconds;
            }

            edited.Normalize();

            // Close first: SaveSettings shows its own confirmation box, and the panel must be gone
            // by the time that appears.
            _window.CloseOverlay();
            _window.SaveSettings(edited, BandLayout.TotalWidth(_window.CurrentSettings));
        }

        private static List<string> ListAdapters()
        {
            List<string> names = new List<string>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    names.Add(adapter.Name + " | " + adapter.Description);
                }
            }
            catch (NetworkInformationException)
            {
                // no adapters readable: the combo just offers "auto"
            }
            return names;
        }
    }
}
