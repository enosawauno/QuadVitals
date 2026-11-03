using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using QuadVitals.Core;
using QuadVitals.UI;

namespace QuadVitals.Settings
{
    /// <summary>
    /// The tray's "diagnostics" entry used to be a system MessageBox: white, unstyled, and larger
    /// than the app's own panels. This window is the same snapshot in a small dark card so the
    /// tray and the dashboard do not look like two different products.
    /// </summary>
    internal sealed class DiagnosticsWindow : Window
    {
        private const double CardWidth = 560;
        private const double ShadowRoom = 24;

        public DiagnosticsWindow()
        {
            Title = "诊断信息";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = CardWidth + ShadowRoom * 2;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 780;
            FontFamily = Theme.UiFont;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            Content = BuildCard();
            PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };
            Loaded += (sender, e) => Activate();
        }

        private FrameworkElement BuildCard()
        {
            List<string> warnings;
            QuadVitals.Core.Settings settings = QuadVitals.Core.Settings.Load(QuadVitals.Core.Settings.DefaultPath, out warnings) ?? QuadVitals.Core.Settings.CreateDefault();

            MetricsSnapshot snapshot = null;
            bool channelOpen = false;
            using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen())
            {
                if (reader != null)
                {
                    channelOpen = true;
                    snapshot = reader.TryRead();
                }
            }

            bool alive = snapshot != null && snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout);
            string age = snapshot == null
                ? string.Empty
                : "（" + (int)snapshot.Age(DateTime.UtcNow).TotalMilliseconds + " ms 前）";

            StackPanel content = new StackPanel();

            TextBlock title = new TextBlock
            {
                Text = "诊断信息",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimaryBrush
            };
            AutomationProperties.SetAutomationId(title, "DiagnosticsTitle");
            Border titleBar = new Border
            {
                Child = title,
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 0, 0, 12),
                Cursor = Cursors.SizeAll
            };
            titleBar.MouseLeftButtonDown += (sender, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            content.Children.Add(titleBar);

            TextBlock intro = new TextBlock
            {
                Text = "传感器进程最近一帧的读取结果，用来判断哪一项没有数据。某项显示 -- 表示这一项没有有效值。",
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            AutomationProperties.SetAutomationId(intro, "DiagnosticsIntro");
            content.Children.Add(intro);

            content.Children.Add(new Border
            {
                Height = 1,
                Background = Theme.BorderSubtleBrush,
                Margin = new Thickness(0, 0, 0, 14)
            });

            string status = alive
                ? "在线 · 最近一帧 " + age
                : (channelOpen ? "离线 · 通道在，但没有新帧" : "离线 · 共享内存通道不存在");
            content.Children.Add(Row("状态", status, alive ? Theme.AccentGreenBrush : Theme.AccentRedBrush, "DiagnosticsStatus"));
            content.Children.Add(Row("版本", ProductVersion.SemVer, null, "DiagnosticsVersion"));

            string cpu = snapshot != null && snapshot.Has(MetricFlags.CpuValid)
                ? MetricFormat.PercentDetailed(snapshot.CpuPercent)
                : MetricFormat.Invalid;
            content.Children.Add(Row("CPU", cpu));

            string memory = snapshot != null && snapshot.Has(MetricFlags.MemoryValid)
                ? MetricFormat.PercentDetailed(snapshot.MemoryPercent) + "  " +
                  MetricFormat.MemoryPair(snapshot.MemoryUsedBytes, snapshot.MemoryTotalBytes)
                : MetricFormat.Invalid;
            content.Children.Add(Row("内存", memory));

            string temperature = snapshot != null && snapshot.Has(MetricFlags.TemperatureValid)
                ? MetricFormat.TemperatureDetailed(snapshot.TemperatureC)
                : MetricFormat.Invalid + "（需要管理员权限，或传感器未返回）";
            content.Children.Add(Row("温度", temperature, null, "DiagnosticsTemperature"));

            content.Children.Add(Row("网速", AgentDiagnostics.NetworkLine(snapshot)));
            content.Children.Add(Row("网卡", snapshot == null || string.IsNullOrEmpty(snapshot.AdapterName)
                ? "—"
                : snapshot.AdapterName));

            string sampled = snapshot == null
                ? "—"
                : snapshot.TimestampUtc.ToLocalTime().ToString("HH:mm:ss") + " " + age;
            content.Children.Add(Row("采样时间", sampled));
            content.Children.Add(Row("缺失", AgentDiagnostics.MissingLine(snapshot), null, "DiagnosticsMissing"));
            content.Children.Add(Row("采样周期", settings.RefreshMs + " ms（设置改动自动重载）"));

            content.Children.Add(new Border
            {
                Height = 1,
                Background = Theme.BorderSubtleBrush,
                Margin = new Thickness(0, 6, 0, 14)
            });

            content.Children.Add(Row("设置文件", QuadVitals.Core.Settings.DefaultPath));
            content.Children.Add(Row("共享内存", MetricsChannel.MapName, null, "DiagnosticsSharedMemory"));

            TextBlock note = new TextBlock
            {
                Text = "温度与风扇来自 LHM，需要管理员权限并按 5 秒节拍更新；显卡来自 NVML；其余为本机采样。",
                FontSize = 11,
                Foreground = Theme.TextTertiaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetAutomationId(note, "DiagnosticsNote");
            content.Children.Add(note);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            FlatButton close = new FlatButton("关闭");
            close.MinWidth = 88;
            AutomationProperties.SetAutomationId(close, "DiagnosticsClose");
            close.Click += (sender, e) => Close();
            buttons.Children.Add(close);
            content.Children.Add(buttons);

            Border card = new Border
            {
                Width = CardWidth,
                Background = Theme.SurfaceCardBrush,
                CornerRadius = new CornerRadius(Theme.CardRadius),
                Padding = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(ShadowRoom),
                SnapsToDevicePixels = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 28,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Direction = 270
                },
                Child = content
            };
            return card;
        }

        private static FrameworkElement Row(string label, string value, Brush valueBrush = null, string automationId = null)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock name = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Theme.TextTertiaryBrush
            };
            TextBlock text = new TextBlock
            {
                Text = value ?? string.Empty,
                FontSize = 12.5,
                Foreground = valueBrush ?? Theme.TextPrimaryBrush,
                TextWrapping = TextWrapping.Wrap
            };
            if (!string.IsNullOrEmpty(automationId)) AutomationProperties.SetAutomationId(text, automationId);

            Grid.SetColumn(name, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(name);
            row.Children.Add(text);
            return row;
        }
    }
}
