using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// Watches the per-metric validity flags of a live snapshot and decides when restarting the
    /// agent is the only remaining cure.
    ///
    /// The agent process can be perfectly healthy while one sampler is dead: NVML fails to
    /// initialise, a driver gets reset by a game, or a sensor stops answering. The offline
    /// watchdog cannot see that (the heartbeat is still fresh), so this one looks at the flags.
    ///
    /// Restarting is deliberately the last resort:
    /// - a flag has to stay missing for <see cref="GraceSeconds"/>; a single-frame glitch is ignored;
    /// - after each restart the next attempt waits 3 / 10 / 30 / 60 seconds and stops after
    ///   <see cref="MaxAttempts"/>;
    /// - only the four device tiles are watched: CPU, memory and the system drive always; GPU
    ///   load when the adapter is NVIDIA (the only source for it) or when it was valid once in
    ///   this session, even if the first session never initialised NVML.
    /// </summary>
    public sealed class MetricWatchdog
    {
        public const int GraceSeconds = 10;
        public const int HealthyResetSeconds = 60;
        public const int MaxAttempts = 5;
        public static readonly int[] RetryDelaysSeconds = { 3, 10, 30, 60 };

        // Only the four device tiles are watched. Temperature has its own recovery in the agent,
        // and network/power/clock can legitimately disappear (unplugged cable, idle sensor) without
        // a process restart being able to help; watching those would only add restart loops.
        private const MetricFlags KnownMetrics =
            MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.GpuValid | MetricFlags.DiskValid;

        private MetricFlags _seenValid;
        private DateTime _invalidSinceUtc = DateTime.MinValue;
        private DateTime _nextAttemptUtc = DateTime.MinValue;
        private DateTime _healthySinceUtc = DateTime.MinValue;
        private int _attempts;

        /// <summary>The flags that were missing on the last observation.</summary>
        public MetricFlags Missing { get; private set; }

        public int Attempts { get { return _attempts; } }

        /// <summary>Clears the attempt budget as well; used when a person asked for a restart.</summary>
        public void Reset()
        {
            _invalidSinceUtc = DateTime.MinValue;
            _nextAttemptUtc = DateTime.MinValue;
            _healthySinceUtc = DateTime.MinValue;
            _attempts = 0;
            Missing = MetricFlags.None;
        }

        /// <summary>Keeps the attempt budget, but forgets the current outage while the agent is gone.</summary>
        public void AgentOffline()
        {
            _invalidSinceUtc = DateTime.MinValue;
            _healthySinceUtc = DateTime.MinValue;
            Missing = MetricFlags.None;
        }

        /// <summary>
        /// Feeds one dashboard tick. Returns true exactly when a restart should be issued now.
        /// </summary>
        /// <param name="expectNvidiaGpu">
        /// True when the machine has an NVIDIA adapter, so missing GPU load is a sampler failure
        /// rather than "this hardware has no NVML at all".
        /// </param>
        public bool Observe(MetricFlags flags, bool agentAlive, bool expectNvidiaGpu,
                            DateTime nowUtc, out MetricFlags missing)
        {
            missing = MetricFlags.None;
            Missing = MetricFlags.None;

            if (!agentAlive)
            {
                AgentOffline();
                return false;
            }

            _seenValid |= flags & KnownMetrics;

            MetricFlags expected = MetricFlags.CpuValid | MetricFlags.MemoryValid |
                                   MetricFlags.DiskValid | _seenValid;
            if (expectNvidiaGpu) expected |= MetricFlags.GpuValid;

            missing = expected & ~flags;
            Missing = missing;

            if (missing == MetricFlags.None)
            {
                _invalidSinceUtc = DateTime.MinValue;
                if (_healthySinceUtc == DateTime.MinValue) _healthySinceUtc = nowUtc;
                if (_attempts > 0 &&
                    (nowUtc - _healthySinceUtc).TotalSeconds >= HealthyResetSeconds)
                {
                    _attempts = 0;
                    _nextAttemptUtc = DateTime.MinValue;
                }
                return false;
            }

            _healthySinceUtc = DateTime.MinValue;
            if (_invalidSinceUtc == DateTime.MinValue) _invalidSinceUtc = nowUtc;
            if ((nowUtc - _invalidSinceUtc).TotalSeconds < GraceSeconds) return false;
            if (_attempts >= MaxAttempts) return false;
            if (_nextAttemptUtc != DateTime.MinValue && nowUtc < _nextAttemptUtc) return false;

            _attempts++;
            int index = _attempts - 1;
            if (index >= RetryDelaysSeconds.Length) index = RetryDelaysSeconds.Length - 1;
            _nextAttemptUtc = nowUtc.AddSeconds(RetryDelaysSeconds[index]);
            return true;
        }

        /// <summary>Readable name for the first missing metric; used in the diagnostic log.</summary>
        public static string Describe(MetricFlags missing)
        {
            if ((missing & MetricFlags.CpuValid) != 0) return "CPU";
            if ((missing & MetricFlags.MemoryValid) != 0) return "内存";
            if ((missing & MetricFlags.GpuValid) != 0) return "显卡";
            if ((missing & MetricFlags.DiskValid) != 0) return "磁盘";
            if ((missing & MetricFlags.TemperatureValid) != 0) return "温度";
            if ((missing & MetricFlags.NetworkValid) != 0) return "网络";
            if ((missing & MetricFlags.GpuTemperatureValid) != 0) return "显卡温度";
            if ((missing & MetricFlags.DiskIoValid) != 0) return "磁盘读写";
            if ((missing & MetricFlags.CpuPowerValid) != 0) return "CPU 功耗";
            if ((missing & MetricFlags.GpuPowerValid) != 0) return "显卡功耗";
            if ((missing & MetricFlags.GpuClockValid) != 0) return "显卡频率";
            return "指标";
        }
    }
}
