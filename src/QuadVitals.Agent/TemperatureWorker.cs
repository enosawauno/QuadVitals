using System;
using System.Threading;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Runs the LibreHardwareMonitor sensor walk on its own thread.
    ///
    /// Why: LHM's Update() over 24 cores costs tens to hundreds of milliseconds (seconds under
    /// load). Called inline it stalls the 1 Hz publish loop, so readers see repeated snapshots and
    /// the CPU delta spans a variable interval. design already says the sampling that is slow
    /// must not sit in the UI's path; the same applies to the publisher's path.
    /// </summary>
    internal sealed class TemperatureWorker : IDisposable
    {
        private readonly TemperatureSource _source;
        private readonly Action<string> _onStall;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly object _gate = new object();
        private const int MinIntervalMs = 500;

        private double _celsius = double.NaN;
        private double _watts = double.NaN;
        private MetricsSnapshot.FanReading[] _fans = new MetricsSnapshot.FanReading[0];
        private DateTime _sampleUtc = DateTime.MinValue;
        private string _reason;
        private int _intervalMs;
        private readonly TemperatureLog _log = new TemperatureLog();
        private DateTime _failedSinceUtc = DateTime.MinValue;
        private DateTime _lastReopenUtc = DateTime.MinValue;
        // 风扇独占的重连：温度正常但一个风扇通道都没有，多半是 SuperIO 这一段没打开
        // （驱动被别的进程占过），换一个 LHM 会话就能回来。
        private DateTime _fansMissingSinceUtc = DateTime.MinValue;
        private bool _sawFanChannels;
        private bool _allZeroReported;
        private DateTime _lastFanReopenUtc = DateTime.MinValue;
        private int _fanReopens;
        private bool _stallReported;

        /// <summary>Silent for this long before the driver session is rebuilt.</summary>
        private const int ReopenAfterMs = 20000;

        /// <summary>At most one rebuild attempt per minute: each one re-extracts the driver.</summary>
        private const int ReopenBackoffMs = 60000;

        /// <summary>Silent for this long means the session is unrecoverable: ask for a new process.</summary>
        private const int StallRestartMs = 10 * 60 * 1000;

        /// <summary>
        /// Default cadence for the LHM sensor walk. LHM cannot refresh a single sensor: every
        /// Update() re-reads the whole CPU node (one MSR round trip per core), which is the
        /// dominant cost of the agent. 5 s keeps budget (< 1% CPU) comfortable while staying
        /// well inside what a temperature display needs.
        /// </summary>
        public const int DefaultIntervalMs = 5000;

        public TemperatureWorker(TemperatureSource source, int intervalMs, Action<string> onStall = null)
        {
            _source = source;
            _onStall = onStall;
            _intervalMs = intervalMs < MinIntervalMs ? DefaultIntervalMs : intervalMs;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "QuadVitals.Temperature",
                // 采样是后台工作：LHM 一次 Update 会在一颗核上跑几百毫秒（32 核逐核读 MSR），
                // 降到 BelowNormal，满载时先让前台界面拿到时间片。
                Priority = ThreadPriority.BelowNormal
            };
            _thread.Start();
        }

        public bool HasSource { get { return _source != null && _source.IsAvailable; } }

        /// <summary>
        /// Reads the latest value without blocking. Returns false when the worker has nothing
        /// fresh enough, which is what turns into "--" in the band.
        /// </summary>
        public bool TryGet(DateTime nowUtc, TimeSpan maxAge, out double celsius, out string reason)
        {
            lock (_gate)
            {
                celsius = _celsius;
                reason = _reason;
                if (double.IsNaN(_celsius)) return false;
                if (_sampleUtc == DateTime.MinValue) return false;
                if (nowUtc - _sampleUtc > maxAge) return false;
                return true;
            }
        }

        /// <summary>CPU 功耗（瓦）；与温度同一个采样节拍下发，NaN 表示这次没采到。</summary>
        public bool TryGetPower(DateTime nowUtc, TimeSpan maxAge, out double watts)
        {
            lock (_gate)
            {
                watts = _watts;
                if (double.IsNaN(_watts)) return false;
                if (_sampleUtc == DateTime.MinValue) return false;
                if (nowUtc - _sampleUtc > maxAge) return false;
                return true;
            }
        }

        /// <summary>Fan channels from the same walk as the temperature (empty = none reported).</summary>
        public bool TryGetFans(DateTime nowUtc, TimeSpan maxAge, out MetricsSnapshot.FanReading[] fans)
        {
            lock (_gate)
            {
                fans = _fans;
                if (fans == null || fans.Length == 0) return false;
                if (_sampleUtc == DateTime.MinValue) return false;
                if (nowUtc - _sampleUtc > maxAge) return false;
                return true;
            }
        }

        private void Loop()
        {
            if (_source == null || !_source.IsAvailable)
            {
                lock (_gate) { _reason = _source == null ? "未配置" : _source.UnavailableReason; }
                return;
            }

            while (!_stop.IsSet)
            {
                double value = double.NaN;
                try
                {
                    value = _source.Sample(DateTime.UtcNow);
                    lock (_gate)
                    {
                        if (double.IsNaN(value))
                        {
                            _reason = _source.UnavailableReason ?? "传感器未返回数据";
                        }
                        else
                        {
                            _celsius = value;
                            _watts = _source.LastPowerWatts;
                            _fans = CopyFans(_source.LastFans);
                            _sampleUtc = DateTime.UtcNow;
                            _reason = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate) { _reason = "温度采集异常: " + ex.GetType().Name; }
                }

                Supervise(value);
                SuperviseFans(value);

                if (_stop.Wait(_intervalMs)) return;
            }
        }

        private static MetricsSnapshot.FanReading[] CopyFans(System.Collections.Generic.IList<MetricsSnapshot.FanReading> source)
        {
            if (source == null || source.Count == 0) return new MetricsSnapshot.FanReading[0];
            MetricsSnapshot.FanReading[] copy = new MetricsSnapshot.FanReading[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                MetricsSnapshot.FanReading fan = source[i];
                copy[i] = fan == null ? null : fan.Clone();
            }
            return copy;
        }

        /// <summary>
        /// Keeps a silent sensor from staying silent: LHM keeps answering with empty values once
        /// its driver handle went stale, so the only cure is a fresh Open(). Runs on this thread,
        /// once a minute at most, and never blocks a sample that is working.
        /// </summary>
        /// <summary>
        /// 温度正常、但一路风扇都读不到时，换一次 LHM 会话。SuperIO 的句柄会被先前的
        /// 非提权实例或驱动重置留在坏状态，重建会话是唯一能自愈的做法。
        /// 判断窗口只有一个采样周期（5 s）：一次空读不算数，下一次仍是空就直接重连，
        /// 卡片最多空 5–10 s。重试间隔 30 s、总共三次，免得“风扇真的全停”时不停重连。
        /// </summary>
        private void SuperviseFans(double value)
        {
            if (double.IsNaN(value)) { _fansMissingSinceUtc = DateTime.MinValue; return; }
            int channels = _source == null ? 0 : _source.LastFanChannelCount;
            int spinning = 0;
            if (_source != null && _source.LastFans != null)
            {
                for (int i = 0; i < _source.LastFans.Count; i++)
                {
                    MetricsSnapshot.FanReading fan = _source.LastFans[i];
                    if (fan != null && fan.Rpm > 0) spinning++;
                }
            }

            // 全 0 不是“读不到”，是“都停了”：写一条归因，但不去重连——重开会话不会让风扇转。
            string allZero = FanDiagnosis.ExplainAllZero(channels, spinning);
            if (allZero == null)
            {
                if (_allZeroReported) { _allZeroReported = false; _log.Report("风扇重新转起来（" + spinning + " 路）"); }
            }
            else if (!_allZeroReported)
            {
                _allZeroReported = true;
                _log.Report(allZero);
            }

            if (channels > 0)
            {
                // 有通道（哪怕全 0）就说明传感器会话是活的，没有理由重连。
                _sawFanChannels = true;
                if (_fanReopens > 0)
                {
                    _log.Report("风扇通道恢复（" + channels + " 路）");
                }
                _fansMissingSinceUtc = DateTime.MinValue;
                _fanReopens = 0;
                return;
            }

            // 一路通道都没有：本会话曾读到过（会话掉了）才值得重连；从没读到过的
            // 主板（笔记本、无风扇头的板子）不追，否则每次启动白重连三次。
            if (!_sawFanChannels) return;

            DateTime now = DateTime.UtcNow;
            if (_fansMissingSinceUtc == DateTime.MinValue) { _fansMissingSinceUtc = now; return; }
            if ((now - _fansMissingSinceUtc).TotalSeconds < 5) return;
            if (_fanReopens >= 3) return;
            if ((now - _lastFanReopenUtc).TotalSeconds < 30) return;

            _lastFanReopenUtc = now;
            _fanReopens++;
            bool reopened = _source.Reopen();
            _log.Report("温度正常但风扇通道消失，重连 LHM：" + (reopened ? "已重开会话" : "失败") +
                        "（第 " + _fanReopens + " 次）");
        }

        private void Supervise(double value)
        {
            DateTime now = DateTime.UtcNow;
            if (!double.IsNaN(value))
            {
                if (_failedSinceUtc != DateTime.MinValue)
                {
                    _failedSinceUtc = DateTime.MinValue;
                    _stallReported = false;
                    _log.Report("温度恢复（" + (_source.LastSensorName ?? "传感器") + "）");
                }
                return;
            }

            if (_failedSinceUtc == DateTime.MinValue)
            {
                _failedSinceUtc = now;
                _log.Report("温度不可用: " + (_source.UnavailableReason ?? "传感器未返回数据") +
                            (_source.LastDiagnostic != null ? "（" + _source.LastDiagnostic + "）" : string.Empty));
                return;
            }

            if ((now - _failedSinceUtc).TotalMilliseconds < ReopenAfterMs) return;
            if ((now - _lastReopenUtc).TotalMilliseconds < ReopenBackoffMs) return;

            _lastReopenUtc = now;
            bool reopened = _source.Reopen();
            _log.Report("重连 LHM: " + (reopened ? "已重开会话，等待数据" : ("失败（" + _source.UnavailableReason + "）")));

            if (_stallReported) return;
            if ((now - _failedSinceUtc).TotalMilliseconds < StallRestartMs) return;

            _stallReported = true;
            int minutes = StallRestartMs / 60000;
            _log.Report("温度已连续 " + minutes + " 分钟无数据，请求重启 Agent 进程");
            Action<string> handler = _onStall;
            if (handler != null) handler("温度连续 " + minutes + " 分钟无数据");
        }

        public void Dispose()
        {
            _stop.Set();
            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(1500);
            }
            _stop.Dispose();
            if (_source != null) _source.Dispose();
        }
    }
}
