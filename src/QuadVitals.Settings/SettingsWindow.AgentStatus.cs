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

            System.Threading.Tasks.Task.Run(() =>
            {
                string error = RestartAgent();
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
                    ScheduleAgentPanelRefresh(250);
                });
            });
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
        private static string RestartAgent()
        {
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
            if (!WaitForFrameAfter(exitedUtc, 12000))
            {
                return "传感器已重新启动，但还没有写出新数据。如果温度仍是 --，请检查登录计划任务是否在运行";
            }
            return null;
        }

        /// <summary>True once a frame newer than <paramref name="afterUtc"/> is alive on the channel.</summary>
        private static bool WaitForFrameAfter(DateTime afterUtc, int timeoutMs)
        {
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
                            return true;
                        }
                    }
                }
                System.Threading.Thread.Sleep(200);
            }
            return false;
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
