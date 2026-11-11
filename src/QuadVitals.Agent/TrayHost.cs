using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Tray presence for the agent (design: tray agent + logon task). Deliberately thin:
    /// the icon shows status and offers the entries v1 needs today; the WPF dashboard and the
    /// settings window arrive in M3, so those menu items are disabled rather than lying.
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        private readonly AgentRuntime _runtime;
        private readonly Settings _settings;
        private Action _quit;
        private readonly NotifyIcon _icon;
        private readonly Icon _baseIcon;
        private readonly WinFormsTimer _statusTimer;
        private readonly SynchronizationContext _uiContext;
        private bool _disposed;

        public TrayHost(AgentRuntime runtime, Settings settings, Action quit)
        {
            _runtime = runtime;
            _settings = settings;
            _quit = quit;
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            _baseIcon = LoadIcon();
            _icon = new NotifyIcon
            {
                Icon = _baseIcon,
                Text = "QuadVitals",
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            _icon.DoubleClick += (sender, e) => OpenDashboard();

            // Tooltip refresh: cheap string build, once per two seconds is plenty.
            _statusTimer = new WinFormsTimer { Interval = 2000 };
            _statusTimer.Tick += (sender, e) => UpdateTooltip();
            _statusTimer.Start();

            UpdateTooltip();
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem status = new ToolStripMenuItem("状态：启动中…");
            status.Enabled = false;
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem dashboard = new ToolStripMenuItem("打开仪表盘", null, (sender, e) => OpenDashboard());
            menu.Items.Add(dashboard);

            ToolStripMenuItem feedback = new ToolStripMenuItem("发送反馈", null, (sender, e) => OpenDashboard("--feedback"));
            menu.Items.Add(feedback);

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("打开设置文件", null, (sender, e) => OpenSettingsFile());
            menu.Items.Add(settingsItem);

            ToolStripMenuItem diagnostics = new ToolStripMenuItem("诊断信息…", null, (sender, e) => OpenDiagnostics());
            menu.Items.Add(diagnostics);

            menu.Items.Add(new ToolStripMenuItem("重启资源管理器", null, (sender, e) => RestartExplorer()));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (sender, e) =>
            {
                if (_quit != null) _quit();
                else Quit();
            }));

            _statusItem = status;
            return menu;
        }

        private ToolStripMenuItem _statusItem;

        private void UpdateTooltip()
        {
            MetricsSnapshot snapshot = _runtime.Last;
            if (snapshot == null)
            {
                SetText("QuadVitals：等待第一帧…");
                return;
            }

            bool alive = snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout);
            string cpu = snapshot.Has(MetricFlags.CpuValid) ? MetricFormat.Percent(snapshot.CpuPercent) : MetricFormat.Invalid;
            string memory = snapshot.Has(MetricFlags.MemoryValid) ? MetricFormat.Percent(snapshot.MemoryPercent) : MetricFormat.Invalid;
            string temp = snapshot.Has(MetricFlags.TemperatureValid) ? MetricFormat.Temperature(snapshot.TemperatureC) : MetricFormat.Invalid;
            string up = snapshot.Has(MetricFlags.NetworkValid) ? MetricFormat.Rate(snapshot.NetUpBytesPerSec) : MetricFormat.Invalid;
            string down = snapshot.Has(MetricFlags.NetworkValid) ? MetricFormat.Rate(snapshot.NetDownBytesPerSec) : MetricFormat.Invalid;

            SetText(string.Format("QuadVitals  CPU {0}  内存 {1}  {2}\n↑{3}  ↓{4}{5}",
                cpu, memory, temp, up, down, alive ? string.Empty : "\n（数据陈旧）"));

            if (_statusItem != null)
            {
                _statusItem.Text = string.Format("CPU {0}  内存 {1}  {2}", cpu, memory, temp);
            }
        }

        private void SetText(string text)
        {
            // NotifyIcon.Text is capped at 63 chars by the shell; longer text throws.
            if (text.Length > 63) text = text.Substring(0, 60) + "…";
            if (_icon != null) _icon.Text = text;
        }

        public void SetQuitAction(Action quit)
        {
            _quit = quit;
        }

        /// <summary>
        /// The diagnostics entry now opens the dashboard's dark window instead of a system
        /// MessageBox. Keeping the content in the Settings process means the tray and the
        /// dashboard use one visual language and one snapshot reader.
        /// </summary>
        private static void OpenDiagnostics()
        {
            OpenDashboard("--diagnostics");
        }

        /// <summary>Launches the WPF dashboard/settings window.</summary>
        private static void OpenDashboard(string arguments = null)
        {
            try
            {
                string baseDir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                string[] candidates =
                {
                    System.IO.Path.Combine(baseDir, "QuadVitals.Settings.exe"),
                    System.IO.Path.Combine(baseDir, "..", "ui", "QuadVitals.Settings.exe")
                };
                foreach (string candidate in candidates)
                {
                    string full = System.IO.Path.GetFullPath(candidate);
                    if (System.IO.File.Exists(full))
                    {
                        if (string.IsNullOrEmpty(arguments)) System.Diagnostics.Process.Start(full);
                        else System.Diagnostics.Process.Start(full, arguments);
                        return;
                    }
                }
                MessageBox.Show("找不到 QuadVitals.Settings.exe（先运行 scripts\\build-ui.ps1）",
                    "QuadVitals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开仪表盘：" + ex.Message, "QuadVitals",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Restarts the shell, which is the only way to rebuild a taskbar that stopped repainting.
        /// The stopping part runs in a detached powershell so this process never waits for the
        /// shell to die, and explorer is started again only when it did not come back on its own
        /// (an unconditional start would open an extra folder window).
        /// </summary>
        private static void RestartExplorer()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(
                    "powershell.exe",
                    "-NoProfile -WindowStyle Hidden -Command \"Start-Sleep -Milliseconds 300; " +
                    "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue; " +
                    "Start-Sleep -Seconds 2; " +
                    "if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) " +
                    "{ Start-Process explorer.exe }\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                System.Diagnostics.Process.Start(info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法重启资源管理器：" + ex.Message, "QuadVitals Agent",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenSettingsFile()
        {
            try
            {
                if (!System.IO.File.Exists(Settings.DefaultPath))
                {
                    _settings.Save(Settings.DefaultPath);
                }
                System.Diagnostics.Process.Start("notepad.exe", "\"" + Settings.DefaultPath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开设置文件：" + ex.Message, "QuadVitals Agent",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static Icon LoadIcon()
        {
            // The tray draws at ~16 px, so it uses a dedicated tight-cropped variant
            // (assets/icon/tray.ico, deployed next to the exe). The app icon keeps the macOS
            // plate; using it in the tray would halve the visible glyph.
            try
            {
                string baseDir = Path.GetDirectoryName(Application.ExecutablePath);
                string trayPath = Path.Combine(baseDir, "tray.ico");
                if (File.Exists(trayPath))
                {
                    using (FileStream stream = File.OpenRead(trayPath))
                    {
                        return new Icon(stream);
                    }
                }
            }
            catch (Exception)
            {
                // fall back to the exe icon below
            }

            try
            {
                Icon fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (fromExe != null) return fromExe;
            }
            catch (Exception)
            {
                // fall through to the stock icon
            }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_statusTimer != null) { _statusTimer.Stop(); _statusTimer.Dispose(); }
            if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
            if (_baseIcon != null) _baseIcon.Dispose();
        }

        /// <summary>Runs the tray message loop; returns when the user exits.</summary>
        public void RunMessageLoop(Action onExit)
        {
            // NOTE: EnableVisualStyles/SetCompatibleTextRenderingDefault must run BEFORE the first
            // IWin32Window is created (here: NotifyIcon), so the caller does that in Program.RunTray.
            Application.Run();
            if (onExit != null) onExit();
        }

        /// <summary>Marshals a call onto the tray UI thread.</summary>
        public void Post(Action action)
        {
            if (action == null) return;
            _uiContext.Post(_ => action(), null);
        }

        /// <summary>Requests the message loop to stop and lets the caller clean up.</summary>
        public void Quit()
        {
            Post(() =>
            {
                if (_icon != null) _icon.Visible = false;
                Application.ExitThread();
            });
        }
    }
}
