using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Shell;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    internal sealed partial class SettingsWindow
    {
        /// <summary>当前通道状态：每次都重新读快照，面板与芯片用同一个口子，不会两边说法不一致。</summary>
        public AgentStateInfo ReadAgentState()
        {
            if (_statusReader == null && (DateTime.UtcNow - _statusReaderAttemptUtc).TotalSeconds > 2)
            {
                _statusReaderAttemptUtc = DateTime.UtcNow;
                _statusReader = MetricsChannel.Reader.TryOpen();
            }

            AgentStateInfo info = new AgentStateInfo();
            info.ChannelOpen = _statusReader != null;
            MetricsSnapshot snapshot = _statusReader == null ? null : _statusReader.TryRead();
            if (snapshot == null) return info;

            info.HasSnapshot = true;
            info.AgeSeconds = snapshot.Age(DateTime.UtcNow).TotalSeconds;
            info.LastUpdateLocal = snapshot.TimestampUtc.ToLocalTime();
            info.Alive = snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout);
            // 看门狗看的是“能信任的”标志：标记有效但数值越界，也算这一项没有数据。
            info.Flags = MetricSanity.EffectiveFlags(snapshot);
            return info;
        }

        /// <summary>标题栏芯片点开的面板。</summary>
        public void ShowAgentStatus()
        {
            ShowOverlay(new AgentStatusDialog(this).Build(ReadAgentState()));
            _agentPanelShown = true;
        }

        /// <summary>
        /// 「启动 Agent」：拉起进程，等它写出第一帧再刷新面板。计划任务不可用时（开发版）
        /// 这里直接启动 exe，温度会显示 --，但其余指标正常。
        /// </summary>
        public void StartAgentFromDialog()
        {
            string error = AgentLauncher.Start();
            if (error != null)
            {
                ShowNotice(error, true);
                return;
            }
            // 新进程 = 新的一套传感器能力：上一轮“见过哪些指标有效”不再作数，
            // 否则换回一个不带风扇的版本会让看门狗一直追一个永远不回来的指标。
            _metricWatch.AgentRestarted();

            ScheduleAgentPanelRefresh();
            VerifyElevationAfterStart(DateTime.UtcNow);
        }

        /// <summary>
        /// 启动后确认第一帧是不是提权实例。回退路径（没有登录计划任务）可能没拿到提权，
        /// 也可能只拿到一个非提权进程：两种都能让“启动成功”与“温度和风扇还是 --”同时成立。
        /// </summary>
        private void VerifyElevationAfterStart(DateTime startedUtc)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                MetricsSnapshot frame = WaitForFrameAfter(startedUtc, 12000);
                if (frame == null || !AgentDiagnostics.IsKnownUnelevated(frame)) return;
                TryOnDispatcher(() => ShowNotice(AgentDiagnostics.NotElevatedAfterRestart, true));
            });
        }

        public void SetAgentStatus(bool alive, double ageSeconds)
        {
            // 芯片只说健康度：秒数会每帧跳，放在面板里。
            _agentStatus.Text = alive ? "传感器 · 正常" : "传感器 · 离线";
            _agentStatus.Foreground = alive ? Theme.AccentGreenBrush : Theme.AccentRedBrush;
        }

        /// <summary>
        /// 每帧调一次：离线就按退避表自动拉起 Agent（0/3/5/10/30 秒，共 5 次）。
        /// 次数用完就不再动手，交给面板里的手动按钮。
        /// </summary>
        private void TrackAgentHealth(AgentStateInfo state)
        {
            // 旧进程已经退出、新进程还没写出第一帧：这段“离线”是重启的一部分，
            // 自动恢复如果这时插一脚，会和按钮拉起的实例抢同一个计划任务。
            if (_agentRestartInProgress)
            {
                _deadTicks = 0;
                return;
            }

            if (state.Alive)
            {
                _deadTicks = 0;
                _autoStartAttempts = 0;
                _nextAutoStartUtc = DateTime.MinValue;
                TrackMetricHealth(state);
                return;
            }

            _metricWatch.AgentOffline();
            _deadTicks++;
            if (!AgentAutoRecovery.ShouldAttempt(false, _deadTicks, _autoStartAttempts,
                    DateTime.UtcNow, _nextAutoStartUtc)) return;

            _autoStartAttempts++;
            _nextAutoStartUtc = DateTime.UtcNow.AddSeconds(AgentAutoRecovery.DelayAfterAttempt(_autoStartAttempts));

            if (AgentLauncher.Start() == null)
            {
                _metricWatch.AgentRestarted();
                ScheduleAgentPanelRefresh();
            }
        }

        /// <summary>
        /// 面板上的「重启传感器」：先让正在运行的 Agent 自己退出，再用登录计划任务拉起一个新进程。
        /// 温度与显卡的读取会话都在进程启动时建立，驱动被重置或 NVML 掉线后只有重开进程能重新读到；
        /// 只让 Agent 再发布一帧（原来的「刷新」）改不了这件事。
        /// </summary>
        public void RestartAgentFromDialog()
        {
            RestartAgentFromDialog(null);
        }

        /// <param name="reason">
        /// Non-null when the data watchdog asked for this restart: the attempt stays silent and
        /// leaves a line in <c>agent-auto.log</c> instead of a dialog over whatever is full-screen.
        /// </param>
        private void RestartAgentFromDialog(string reason)
        {
            if (_agentRestartInProgress) return;
            if (reason == null) _metricWatch.Reset();
            _agentRestartInProgress = true;
            _deadTicks = 0;

            // 面板只有一个动作：换成提权实例。任务缺失或被禁用时新实例会回到未提权，那时才重建
            // 登录计划任务（一次 UAC）；重建放在重启之后，授权框卡住也卡不住已经起来的进程。
            bool repairTask = reason == null && AgentDiagnostics.IsKnownUnelevated(ReadAgentState().Flags);

            System.Threading.Tasks.Task.Run(() =>
            {
                string error;
                try
                {
                    error = RestartAgent(repairTask, reason == null);
                }
                catch (Exception ex)
                {
                    // 工作线程里抛出去会让面板永远停在“重启中…”：按钮已禁用，没人再把它打开。
                    error = "重启传感器时出错：" + ex.Message;
                }
                TryOnDispatcher(() =>
                {
                    _agentRestartInProgress = false;
                    if (reason != null) LogAgentRecovery(reason, error);
                    if (error != null)
                    {
                        // 自动尝试不在全屏游戏上弹窗；原因留在 agent-auto.log。
                        if (reason == null) ShowNotice(error, true);
                        return;
                    }
                    // 新进程 = 新的采样会话：面板上的数据整套重来，旧进程的曲线与峰值不留在上面。
                    // 重启成功就把弹窗关掉：此时面板上没有要看的东西，留着只是挡住已经刷新的概览。
                    if (reason == null)
                    {
                        ResetDisplayedData();
                        if (IsOverlayOpen) CloseOverlay();
                        return;
                    }
                    ScheduleAgentPanelRefresh(250);
                });
            });
        }

        /// <summary>
        /// 重启成功后的“数据重来”：丢掉旧的共享内存读端（旧映射还指向上一轮），再把所有页面
        /// 拆掉重建——概览的曲线、峰值、EMA 与各卡片的“本次会话”状态都从新进程的第一帧重新开始。
        /// 落盘的网速历史不删：它是跨启动的曲线，不是旧进程的缓存。
        /// </summary>
        private void ResetDisplayedData()
        {
            try
            {
                if (_statusReader != null)
                {
                    _statusReader.Dispose();
                    _statusReader = null;
                }
            }
            catch (Exception)
            {
                // 旧读端关不掉不影响新读端的建立；下一次 ReadAgentState 会重试。
            }

            _statusReaderAttemptUtc = DateTime.MinValue;
            ReloadSettings();
        }

        /// <summary>
        /// Agent 活着但某一项指标停了：持续缺失才换进程重新建立采样会话，单帧抖动不动手。
        /// 只对这台机器本来该有数据的指标生效（CPU/内存/系统盘、曾经有效过的、NVIDIA 显卡）。
        /// </summary>
        private void TrackMetricHealth(AgentStateInfo state)
        {
            MetricFlags missing;
            if (!_metricWatch.Observe(state.Flags, true, HardwareInfo.HasNvidiaGpu,
                    DateTime.UtcNow, out missing)) return;
            RestartAgentFromDialog(MetricWatchdog.Describe(missing));
        }

        /// <summary>Breadcrumb for watchdog-triggered restarts; capped like the agent's temperature log.</summary>
        private static void LogAgentRecovery(string reason, string error)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(AppSettings.DefaultPath);
                if (string.IsNullOrEmpty(dir)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "agent-auto.log");
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > 64 * 1024) File.Delete(path);
                File.AppendAllText(path,
                    DateTime.Now.ToString("s") + "  检测到 " + reason + " 数据不可用，自动重启：" +
                    (error == null ? "完成" : "失败 - " + error) + Environment.NewLine);
            }
            catch (Exception)
            {
                // 诊断日志失败不能影响重启本身
            }
        }

        /// <summary>Blocking half of the restart; runs on a worker thread, never on the UI thread.</summary>
        /// <param name="repairTask">
        /// True for a manual restart of an instance that reported itself as unelevated. The task is
        /// rebuilt only when the restart still lands without admin: rebuilding it first put a UAC
        /// prompt in front of the restart, and a prompt left unanswered blocked the whole attempt
        /// (the button stayed on its "restarting" label with no agent running).
        /// </param>
        private static string RestartAgent(bool repairTask, bool waitForSettled)
        {
            bool unelevated;
            MetricsSnapshot firstFrame;
            string error = RestartOnce(out unelevated, out firstFrame);
            if (error != null) return error;

            if (unelevated)
            {
                if (!repairTask) return AgentDiagnostics.NotElevatedAfterRestart;

                // 新实例还是没提权：登录计划任务缺失或被禁用。重建任务（一次 UAC），再换一次进程。
                if (AgentLauncher.RepairLogonTask() != null) return AgentDiagnostics.NotElevatedAfterRestart;

                error = RestartOnce(out unelevated, out firstFrame);
                if (error != null) return error;
                if (unelevated) return AgentDiagnostics.NotElevatedAfterRestart;
            }

            // 温度、风扇与功耗在 5 s 节拍里采样：多等一拍再返回，页面重建时才不是一片 --。
            if (waitForSettled && firstFrame != null) WaitForSettledFrame(firstFrame, 9000);
            return null;
        }

        /// <summary>Temperature, fans and CPU power share one 5 s tick in the agent.</summary>
        private const int TemperatureTickMs = 5000;

        /// <summary>
        /// Waits for a frame published after the first temperature tick of the new process. The first
        /// frame proves the process is alive but has nothing from that tick yet, and the page rebuilt
        /// from it would show `--` for temperature, fans and power.
        /// </summary>
        private static void WaitForSettledFrame(MetricsSnapshot firstFrame, int timeoutMs)
        {
            DateTime target = firstFrame.TimestampUtc.AddMilliseconds(TemperatureTickMs + 500);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen())
                {
                    if (reader != null)
                    {
                        MetricsSnapshot snapshot = reader.TryRead();
                        if (snapshot != null && snapshot.TimestampUtc >= target &&
                            snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout)) return;
                    }
                }
                System.Threading.Thread.Sleep(200);
            }
        }

        /// <summary>One attempt: old instance out, new instance in, first frame checked for elevation.</summary>
        private static string RestartOnce(out bool unelevated, out MetricsSnapshot firstFrame)
        {
            unelevated = false;
            firstFrame = null;
            AgentLauncher.RequestExit();
            if (!AgentLauncher.WaitForExit(6000))
            {
                return "传感器没有退出：可能被安全软件拦住。请稍后重试，或手动结束 QuadVitals.Agent.exe";
            }

            // Only frames published after the old process is gone count as proof the new one
            // started. A frame the old process wrote between the click and its exit would
            // otherwise look like success and let auto-recovery race the restart.
            DateTime exitedUtc = DateTime.UtcNow;
            string error = AgentLauncher.Start();
            if (error != null) return error;

            if (!AgentLauncher.WaitForStart(5000))
            {
                return "传感器已请求启动，但进程没有出现。请检查登录计划任务是否被禁用";
            }
            if (!WaitForFrameAfter(exitedUtc, 12000, out MetricsSnapshot frame))
            {
                return "传感器已重新启动，但还没有写出新数据。如果温度仍是 --，请检查登录计划任务是否在运行";
            }
            // 新实例没拿到管理员权限：进程活着、帧也在写，但温度和风扇注定读不到。
            // 这种结果不能报成“重启成功”。
            firstFrame = frame;
            unelevated = AgentDiagnostics.IsKnownUnelevated(frame);
            return null;
        }

        /// <summary>Waits for a frame newer than <paramref name="afterUtc" /> and returns the one that proved it.</summary>
        private static bool WaitForFrameAfter(DateTime afterUtc, int timeoutMs, out MetricsSnapshot frame)
        {
            frame = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen())
                {
                    if (reader != null)
                    {
                        MetricsSnapshot snapshot = reader.TryRead();
                        if (snapshot != null && snapshot.TimestampUtc > afterUtc &&
                            snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout))
                        {
                            frame = snapshot;
                            return true;
                        }
                    }
                }
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        /// <summary>Same wait, for callers that only need the frame (null when none arrived).</summary>
        private static MetricsSnapshot WaitForFrameAfter(DateTime afterUtc, int timeoutMs)
        {
            MetricsSnapshot frame;
            return WaitForFrameAfter(afterUtc, timeoutMs, out frame) ? frame : null;
        }

        /// <summary>Marshals back to the window thread; a window that closed mid-restart has nowhere to go.</summary>
        private void TryOnDispatcher(Action action)
        {
            if (action == null) return;
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(action);
            }
            catch (Exception)
            {
                // Shutting down: the restart itself already finished, only the panel is gone.
            }
        }

        private void ScheduleAgentPanelRefresh(int delayMs = 900)
        {
            DispatcherTimer wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            wait.Tick += (sender, e) =>
            {
                wait.Stop();
                if (IsOverlayOpen && _agentPanelShown) ShowAgentStatus();
            };
            wait.Start();
        }
    }
}
