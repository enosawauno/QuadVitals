using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        private const int AttachParentProcess = -1;

        [STAThread]   // WinForms message loop + NotifyIcon require an STA thread
        private static int Main(string[] args)
        {
            // A WinExe has no console of its own; when the caller wants text output (probe mode,
            // scripts, --exit) borrow the parent console so nothing has to change downstream.
            bool consoleMode = HasFlag(args, "--console") || ReadIntArg(args, "--seconds", 0) > 0 ||
                               HasFlag(args, "--exit") || HasFlag(args, "--status");

            if (consoleMode)
            {
                AttachParentConsole();
            }

            if (HasFlag(args, "--exit"))
            {
                bool asked = SingleInstance.PokeQuit();
                Console.WriteLine(asked ? "已请求运行中的 Agent 退出" : "没有正在运行的 Agent");
                return asked ? 0 : 1;
            }

            if (HasFlag(args, "--status"))
            {
                return PrintStatus();
            }

            if (HasFlag(args, "--restart-probe-spawn"))
            {
                // Diagnostics: exercises the whole handoff (spawn successor, parent exits, child
                // waits for the mutex). The successor inherits this process' elevation.
                AgentRestarter spawnProbe = new AgentRestarter();
                bool spawned = spawnProbe.Request("手动探针（含拉起）",
                    () => Environment.Exit(0), true);
                Console.WriteLine(spawned ? "已拉起后继进程并退出" : "被 1 小时守卫拦下（探针）");
                System.Threading.Thread.Sleep(60000);   // keep the parent alive if the quit hook is late
                return spawned ? 0 : 1;
            }

            if (HasFlag(args, "--restart-probe"))
            {
                // Diagnostics: exercises the restart guard (stamp file + log) without spawning.
                // Delete %APPDATA%\QuadVitals\agent-restart.txt afterwards.
                AgentRestarter probe = new AgentRestarter();
                bool requested = probe.Request("手动探针", null, false);
                Console.WriteLine(requested ? "已请求重启（探针）" : "被 1 小时守卫拦下（探针）");
                return requested ? 0 : 1;
            }

            // Successor of an automatic restart: wait for the old process to release the
            // single-instance mutex before starting to sample.
            int waitFor = ReadIntArg(args, "--wait-for-pid", 0);
            if (waitFor > 0) WaitForProcessExit(waitFor);

            using (SingleInstance single = SingleInstance.Acquire())
            {
                if (!single.IsOwner)
                {
                    // a second launch must not start a second sampler.
                    bool poked = SingleInstance.PokeActivate();
                    Console.WriteLine(poked ? "QuadVitals Agent 已在运行，已切换到已有实例" : "QuadVitals Agent 已在运行");
                    return 0;
                }

                if (consoleMode) return RunConsole(args);

                try
                {
                    return RunTray(single);
                }
                catch (Exception ex)
                {
                    // A tray agent has nowhere to show an exception; leave a breadcrumb instead of
                    // dying silently (this is exactly how the MTA/STA bug hid the first time).
                    LogCrash(ex);
                    return 1;
                }
            }
        }

        // ---- tray mode ---------------------------------------------------------------

        private static int RunTray(SingleInstance single)
        {
            // Must happen before any window/NotifyIcon exists, otherwise WinForms throws.
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            List<string> warnings;
            Settings settings = Settings.Load(Settings.DefaultPath, out warnings);

            string temperatureError;
            TemperatureSource temperature = TemperatureSource.Create(out temperatureError);

            string gpuError;
            GpuSource gpu = GpuSource.Create(out gpuError);

            // Diagnostics: the temperature sensor can only work elevated, and "no data" looks the
            // same whether the process lacks the token or the driver refused — record which.
            new TemperatureLog().Report("启动：提权=" + (IsElevated() ? "是" : "否") +
                " LHM=" + (temperature.IsAvailable ? "可用" : (temperature.UnavailableReason ?? "不可用")) +
                " NVML=" + (gpu.IsAvailable ? "可用" : (gpu.UnavailableReason ?? "不可用")));

            bool elevated = IsElevated();
            AgentRestarter restarter = new AgentRestarter();
            Action quitAgent = null;
            Action<string> onStall = reason =>
            {
                if (!elevated)
                {
                    new TemperatureLog().Report("温度长时间无数据，但进程未提权，跳过自动重启（" + reason + "）");
                    return;
                }
                restarter.Request(reason, () => { Action quit = quitAgent; if (quit != null) quit(); });
            };

            using (AgentLoop loop = new AgentLoop(settings, temperature, gpu,
                       TemperatureWorker.DefaultIntervalMs, onStall))
            using (SettingsMonitor monitor = new SettingsMonitor(Settings.DefaultPath))
            using (AgentRuntime runtime = new AgentRuntime(loop, settings.RefreshMs, monitor))
            using (gpu)
            {
                runtime.Start();

                using (TrayHost tray = new TrayHost(runtime, settings, null))
                {
                    quitAgent = tray.Quit;
                    tray.SetQuitAction(() => tray.Quit());
                    single.ActivateRequested += () => tray.Post(tray.ShowStatusNow);
                    single.QuitRequested += tray.Quit;
                    // 任务栏右键的「重启传感器」：换一个新进程重建温度与显卡的读取会话。手动请求不受
                    // 一小时自动重启预算的限制，也不消耗它。
                    single.RestartRequested += () =>
                        restarter.Request("手动重启传感器", () => { Action quit = quitAgent; if (quit != null) quit(); },
                            true, true);
                    single.StartWatching();

                    tray.RunMessageLoop(null);
                }
            }

            return 0;
        }

        // ---- console / probe mode ----------------------------------------------------

        private static int RunConsole(string[] args)
        {
            int seconds = ReadIntArg(args, "--seconds", 0);
            int intervalMs = ReadIntArg(args, "--interval", 0);
            bool quiet = HasFlag(args, "--quiet");

            List<string> settingsWarnings;
            Settings settings = Settings.Load(Settings.DefaultPath, out settingsWarnings);
            foreach (string warning in settingsWarnings) Console.WriteLine("[settings] " + warning);

            int period = intervalMs > 0 ? intervalMs : settings.RefreshMs;

            string temperatureError;
            TemperatureSource temperature = TemperatureSource.Create(out temperatureError);
            if (temperatureError != null) Console.WriteLine("[temperature] " + temperatureError);

            string gpuError;
            GpuSource gpu = GpuSource.Create(out gpuError);
            if (gpuError != null) Console.WriteLine("[gpu] " + gpuError);

            Console.WriteLine("QuadVitals Agent（控制台/探针模式）");
            Console.WriteLine("  settings   : " + settings);
            Console.WriteLine("  period     : " + period + " ms");
            Console.WriteLine("  network    : " + settings.NetworkAdapter);
            Console.WriteLine("  temperature: " + (temperature.IsAvailable ? "LHM 可用" : "不可用（" + temperature.UnavailableReason + "）"));
            Console.WriteLine("  gpu        : " + (gpu.IsAvailable ? gpu.Name : "不可用（" + gpu.UnavailableReason + "）"));
            Console.WriteLine("  channel    : " + MetricsChannel.MapName);
            Console.WriteLine();

            if (!quiet)
            {
                List<string> adapters = NetworkSampler.ListCandidates();
                if (adapters.Count > 1)
                {
                    Console.WriteLine("候选网卡（" + adapters.Count + "）：");
                    foreach (string adapter in adapters) Console.WriteLine("  - " + adapter);
                    Console.WriteLine();
                }
            }

            using (AgentLoop loop = new AgentLoop(settings, temperature, gpu))
            using (SettingsMonitor monitor = new SettingsMonitor(Settings.DefaultPath))
            using (AgentRuntime runtime = new AgentRuntime(loop, period, monitor, intervalMs <= 0))
            using (gpu)
            {
                DateTime startedUtc = DateTime.UtcNow;
                int counter = 0;

                runtime.Ticked += snapshot =>
                {
                    counter++;
                    if (!quiet) PrintTick(counter, startedUtc, snapshot);
                };

                bool stop = false;
                ConsoleCancelEventHandler onCancel = (sender, e) =>
                {
                    e.Cancel = true;
                    stop = true;
                };
                Console.CancelKeyPress += onCancel;

                runtime.Start();

                while (!stop)
                {
                    if (seconds > 0 && (DateTime.UtcNow - startedUtc).TotalSeconds >= seconds) break;
                    System.Threading.Thread.Sleep(100);
                }

                Console.CancelKeyPress -= onCancel;
                Console.WriteLine();
                Console.WriteLine("agent 退出（{0} 次采样）", runtime.TickCount);
            }

            return 0;
        }

        /// <summary>Reads the last published snapshot and prints it (no sampler is started).</summary>
        private static int PrintStatus()
        {
            using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen())
            {
                if (reader == null)
                {
                    Console.WriteLine("没有 Agent 在运行（共享内存通道不存在）");
                    return 1;
                }

                MetricsSnapshot snapshot = reader.TryRead();
                if (snapshot == null)
                {
                    Console.WriteLine("通道存在但读不到一致的快照");
                    return 2;
                }

                bool alive = snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout);
                Console.WriteLine("QuadVitals Agent " + (alive ? "在线" : "已离线（数据陈旧）"));
                Console.WriteLine("  " + snapshot);
                Console.WriteLine("  age      : " + (int)snapshot.Age(DateTime.UtcNow).TotalMilliseconds + " ms");
                Console.WriteLine("  memory   : " + MetricFormat.MemoryPair(snapshot.MemoryUsedBytes, snapshot.MemoryTotalBytes));
                Console.WriteLine("  temp     : " + (snapshot.Has(MetricFlags.TemperatureValid) ? MetricFormat.TemperatureDetailed(snapshot.TemperatureC) : MetricFormat.Invalid));
                Console.WriteLine("  power    : " + (snapshot.Has(MetricFlags.CpuPowerValid) && snapshot.CpuPowerWatts > 0
                    ? snapshot.CpuPowerWatts.ToString("0.0") + " W"
                    : MetricFormat.Invalid));
                Console.WriteLine("  gpu      : " + (snapshot.Has(MetricFlags.GpuValid)
                    ? MetricFormat.PercentDetailed(snapshot.GpuLoadPercent) + "  显存 " + MetricFormat.PercentDetailed(snapshot.GpuMemoryPercent)
                    : MetricFormat.Invalid));
                Console.WriteLine("  gpuPower : " + (snapshot.Has(MetricFlags.GpuPowerValid) && snapshot.GpuPowerWatts > 0
                    ? snapshot.GpuPowerWatts.ToString("0.0") + " W"
                    : MetricFormat.Invalid));
                Console.WriteLine("  gpuClock : " + (snapshot.Has(MetricFlags.GpuClockValid) && snapshot.GpuClockMhz > 0
                    ? snapshot.GpuClockMhz.ToString("0") + " MHz"
                    : MetricFormat.Invalid));
                Console.WriteLine("  gpuTemp  : " + (snapshot.Has(MetricFlags.GpuTemperatureValid) ? MetricFormat.TemperatureDetailed(snapshot.GpuTemperatureC) : MetricFormat.Invalid));
                Console.WriteLine("  disk     : " + (snapshot.Has(MetricFlags.DiskValid)
                    ? MetricFormat.PercentDetailed(snapshot.DiskPercent) + "  " + snapshot.DiskUsedGb + " / " + snapshot.DiskTotalGb + " GB"
                    : MetricFormat.Invalid));
                Console.WriteLine("  diskIo   : " + (snapshot.Has(MetricFlags.DiskIoValid)
                    ? MetricFormat.Rate(snapshot.DiskIoReadBytesPerSec) + " 读 / " + MetricFormat.Rate(snapshot.DiskIoWriteBytesPerSec) + " 写"
                    : MetricFormat.Invalid));
                if (snapshot.Volumes != null && snapshot.Volumes.Length > 0)
                {
                    foreach (MetricsSnapshot.VolumeIo volume in snapshot.Volumes)
                    {
                        Console.WriteLine("  volume " + volume.Letter + ": " + (volume.Valid
                            ? MetricFormat.Rate(volume.ReadBytesPerSec) + " 读 / " + MetricFormat.Rate(volume.WriteBytesPerSec) + " 写"
                            : MetricFormat.Invalid));
                    }
                }
                return alive ? 0 : 3;
            }
        }

        private static void PrintTick(int tick, DateTime startedUtc, MetricsSnapshot snapshot)
        {
            string cpu = snapshot.Has(MetricFlags.CpuValid) ? MetricFormat.Percent(snapshot.CpuPercent) : MetricFormat.Invalid;
            string memory = snapshot.Has(MetricFlags.MemoryValid) ? MetricFormat.Percent(snapshot.MemoryPercent) : MetricFormat.Invalid;
            string temp = snapshot.Has(MetricFlags.TemperatureValid) ? MetricFormat.Temperature(snapshot.TemperatureC) : MetricFormat.Invalid;
            string up = snapshot.Has(MetricFlags.NetworkValid) ? MetricFormat.Rate(snapshot.NetUpBytesPerSec) : MetricFormat.Invalid;
            string down = snapshot.Has(MetricFlags.NetworkValid) ? MetricFormat.Rate(snapshot.NetDownBytesPerSec) : MetricFormat.Invalid;
            string gpu = snapshot.Has(MetricFlags.GpuValid) ? MetricFormat.Percent(snapshot.GpuLoadPercent) : MetricFormat.Invalid;
            string disk = snapshot.Has(MetricFlags.DiskValid) ? MetricFormat.Percent(snapshot.DiskPercent) : MetricFormat.Invalid;

            Console.WriteLine(
                "#{0,3} t={1,5:F1}s cpu={2,4} mem={3,4} temp={4,5} up={5,10} down={6,10} gpu={7,5} disk={8,5}  {9}",
                tick,
                (DateTime.UtcNow - startedUtc).TotalSeconds,
                cpu, memory, temp, up, down, gpu, disk,
                snapshot.Has(MetricFlags.MemoryValid)
                    ? MetricFormat.MemoryPair(snapshot.MemoryUsedBytes, snapshot.MemoryTotalBytes)
                    : string.Empty);
        }

        /// <summary>
        /// Blocks until the given process is gone (bounded). Used by the successor of an automatic
        /// restart so the single-instance mutex is free when it starts sampling.
        /// </summary>
        private static void WaitForProcessExit(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (!process.WaitForExit(30000)) process.WaitForExit(30000);
                }
            }
            catch (Exception)
            {
                // Already gone / never existed: start anyway.
            }
            System.Threading.Thread.Sleep(300);
        }

        private static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity identity =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void LogCrash(Exception ex)
        {
            try
            {
                string dir = Path.GetDirectoryName(Settings.DefaultPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "agent-crash.log"),
                    DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine);
            }
            catch (Exception)
            {
                // logging must never mask the original failure
            }
        }

        private static void AttachParentConsole()
        {
            AttachConsole(AttachParentProcess);
            try
            {
                Stream stdout = Console.OpenStandardOutput();
                StreamWriter writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(writer);
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // No console to attach to (double-clicked with --console): stay silent.
            }
        }

        private static bool HasFlag(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int ReadIntArg(string[] args, string name, int fallback)
        {
            if (args == null) return fallback;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    int value;
                    if (int.TryParse(args[i + 1], out value) && value > 0) return value;
                }
            }
            return fallback;
        }
    }
}
