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
    /// - after each restart the next attempt waits 3 / 5 / 10 / 30 seconds and stops after
    ///   <see cref="MaxAttempts"/>;
    /// - CPU, memory and the system drive are always expected; GPU load when the adapter is
    ///   NVIDIA (the only source for it) or when it was valid once in this session; fan channels
    ///   once they were valid in this session (a fanless board never triggers a restart).
    /// </summary>
    public sealed class MetricWatchdog
    {
        public const int GraceSeconds = 10;
        public const int HealthyResetSeconds = 60;
        public const int MaxAttempts = 5;

        /// <summary>
        /// 温度与风扇共用一次 LHM 全量采样：两样一起缺说明这一拍还没到（满载时一次 Update 会
        /// 明显变慢），而不是风扇单独坏掉。等 Agent 自己重开会话（静默 20 s 后一次），满这个
        /// 秒数才动进程；只缺风扇（温度正常）仍按 <see cref="GraceSeconds"/> 处理。
        /// </summary>
        public const int LhmStallSeconds = 30;
        public static readonly int[] RetryDelaysSeconds = { 3, 5, 10, 30 };

        // Temperature has its own recovery in the agent, and network/power/clock can legitimately
        // disappear (unplugged cable, idle sensor) without a process restart being able to help.
        // Fans are watched only after they were seen once, so a board without fan headers is safe.
        private const MetricFlags KnownMetrics =
            MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.GpuValid | MetricFlags.DiskValid |
            MetricFlags.FanValid;

        private MetricFlags _seenValid;
        // 连续重启都救不回来的指标：本会话不再期待它，否则一个换不回来的版本会让看门狗
        // 把 5 次配额用完再来一轮（真实发生过：风扇曾经有效，换回不带风扇的 Agent 之后每轮都重启）。
        private MetricFlags _givenUp;
        private DateTime _invalidSinceUtc = DateTime.MinValue;
        // 温度+风扇同时缺（同一帧的两种输出）单独计时，不跟着风扇的 10 s 宽限走。
        private DateTime _lhmMissingSinceUtc = DateTime.MinValue;
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
            _lhmMissingSinceUtc = DateTime.MinValue;
            _nextAttemptUtc = DateTime.MinValue;
            _healthySinceUtc = DateTime.MinValue;
            _attempts = 0;
            _seenValid = MetricFlags.None;
            _givenUp = MetricFlags.None;
            Missing = MetricFlags.None;
        }

        /// <summary>
        /// The agent was replaced: whatever the previous process could report says nothing about the
        /// new one, so forget the "seen valid once" memory and let it prove itself again.
        /// </summary>
        public void AgentRestarted()
        {
            _invalidSinceUtc = DateTime.MinValue;
            _lhmMissingSinceUtc = DateTime.MinValue;
            _healthySinceUtc = DateTime.MinValue;
            _seenValid = MetricFlags.None;
            Missing = MetricFlags.None;
        }

        /// <summary>Keeps the attempt budget, but forgets the current outage while the agent is gone.</summary>
        public void AgentOffline()
        {
            _invalidSinceUtc = DateTime.MinValue;
            _lhmMissingSinceUtc = DateTime.MinValue;
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

            MetricFlags expected = (MetricFlags.CpuValid | MetricFlags.MemoryValid |
                                    MetricFlags.DiskValid | _seenValid) & ~_givenUp;
            if (expectNvidiaGpu) expected |= MetricFlags.GpuValid;

            MetricFlags reported = expected & ~flags;

            // 温度与风扇是同一帧的两个输出：两样一起缺是“采样没到”，不是风扇坏了。
            // 风扇那一位拆出去单独计时，免得一次满载就被当成风扇缺失、10 秒后重启整个进程；
            // 对外的缺失集合仍写风扇，日志与面板据此说明是哪一项。
            bool lhmStalled = (reported & MetricFlags.FanValid) != 0 &&
                              (flags & MetricFlags.TemperatureValid) == 0;
            MetricFlags generic = lhmStalled ? (reported & ~MetricFlags.FanValid) : reported;
            missing = reported;
            Missing = reported;

            if (generic == MetricFlags.None && lhmStalled)
            {
                _invalidSinceUtc = DateTime.MinValue;
                _healthySinceUtc = DateTime.MinValue;
                if (_lhmMissingSinceUtc == DateTime.MinValue) _lhmMissingSinceUtc = nowUtc;
                if ((nowUtc - _lhmMissingSinceUtc).TotalSeconds < LhmStallSeconds) return false;
                if (_attempts >= MaxAttempts)
                {
                    _givenUp |= MetricFlags.FanValid;
                    return false;
                }
                if (_nextAttemptUtc != DateTime.MinValue && nowUtc < _nextAttemptUtc) return false;
                _attempts++;
                int stallIndex = _attempts - 1;
                if (stallIndex >= RetryDelaysSeconds.Length) stallIndex = RetryDelaysSeconds.Length - 1;
                _nextAttemptUtc = nowUtc.AddSeconds(RetryDelaysSeconds[stallIndex]);
                return true;
            }

            _lhmMissingSinceUtc = DateTime.MinValue;

            if (generic == MetricFlags.None)
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
            if (_attempts >= MaxAttempts)
            {
                // 配额用完还在缺：这一项本会话不再追（写一次日志由调用方做）。
                _givenUp |= generic;
                return false;
            }
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
            if ((missing & MetricFlags.FanValid) != 0) return "风扇";
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
