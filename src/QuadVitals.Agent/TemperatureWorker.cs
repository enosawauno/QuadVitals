using System;
using System.Threading;

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
        private DateTime _sampleUtc = DateTime.MinValue;
        private string _reason;
        private int _intervalMs;
        private readonly TemperatureLog _log = new TemperatureLog();
        private DateTime _failedSinceUtc = DateTime.MinValue;
        private DateTime _lastReopenUtc = DateTime.MinValue;
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
                Name = "QuadVitals.Temperature"
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

                if (_stop.Wait(_intervalMs)) return;
            }
        }

        /// <summary>
        /// Keeps a silent sensor from staying silent: LHM keeps answering with empty values once
        /// its driver handle went stale, so the only cure is a fresh Open(). Runs on this thread,
        /// once a minute at most, and never blocks a sample that is working.
        /// </summary>
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
