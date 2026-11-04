using System.Collections.Generic;

namespace QuadVitals.Core
{
    /// <summary>
    /// The lines the agent's "diagnostics" box prints about a snapshot.
    ///
    /// The box used to print every raw field, which produced two kinds of nonsense that were
    /// reported from the outside: a network rate next to an empty adapter name (the flag said the
    /// network was never sampled, but the stored numbers were printed anyway), and a flags list that
    /// answered "which are valid" when the reader is asking "what is missing".
    ///
    /// Pure formatting, kept in Core so the wording can be asserted without starting the agent.
    /// </summary>
    public static class AgentDiagnostics
    {
        /// <summary>Metrics that should always be there once the agent is up and warm.</summary>
        private static readonly KeyValuePair<MetricFlags, string>[] AlwaysExpected =
        {
            new KeyValuePair<MetricFlags, string>(MetricFlags.CpuValid, "CPU"),
            new KeyValuePair<MetricFlags, string>(MetricFlags.MemoryValid, "内存"),
            new KeyValuePair<MetricFlags, string>(MetricFlags.TemperatureValid, "温度"),
            new KeyValuePair<MetricFlags, string>(MetricFlags.NetworkValid, "网络"),
            new KeyValuePair<MetricFlags, string>(MetricFlags.GpuValid, "显卡"),
            new KeyValuePair<MetricFlags, string>(MetricFlags.GpuTemperatureValid, "显卡温度"),
            new KeyValuePair<MetricFlags, string>(MetricFlags.DiskValid, "磁盘"),
            new KeyValuePair<MetricFlags, string>(MetricFlags.DiskIoValid, "磁盘读写")
        };

        /// <summary>
        /// Upload/download line. Both numbers are meaningless without the validity flag: the byte
        /// counters keep their last value, so printing them makes an unsampled network look alive.
        /// </summary>
        public static string NetworkLine(MetricsSnapshot snapshot)
        {
            if (snapshot == null) return MetricFormat.Invalid;
            if (!snapshot.Has(MetricFlags.NetworkValid)) return MetricFormat.Invalid;
            return "↑" + MetricFormat.Rate(snapshot.NetUpBytesPerSec) +
                   "   ↓" + MetricFormat.Rate(snapshot.NetDownBytesPerSec);
        }

        /// <summary>
        /// True only when the agent explicitly reported a non-elevated token. An older agent that
        /// predates the flag says nothing, so the caller keeps its old generic wording instead of
        /// claiming the process is unelevated.
        /// </summary>
        public static bool IsKnownUnelevated(MetricsSnapshot snapshot)
        {
            return snapshot != null && snapshot.Has(MetricFlags.ElevationKnown) &&
                   !snapshot.Has(MetricFlags.Elevated);
        }

        /// <summary>Why the CPU temperature tile is empty: no admin token vs. a dead sensor session.</summary>
        /// <remarks>
        /// 两行都控制在十个字内：格子说明行不允许换行，长了只能被裁。
        /// </remarks>
        public static string TemperatureUnavailableLine(MetricsSnapshot snapshot)
        {
            if (IsKnownUnelevated(snapshot)) return "未提权，读不到温度";
            return "温度读取失败";
        }

        /// <summary>
        /// Why the fan card is empty. Three different cases: no admin token, an elevated agent whose
        /// LHM session is dead (temperature is missing too), and a healthy session with no fan
        /// channels on this board.
        /// </summary>
        public static string FanUnavailableLine(MetricsSnapshot snapshot)
        {
            if (IsKnownUnelevated(snapshot)) return "传感器未提权，风扇不可用";
            if (snapshot != null && snapshot.Has(MetricFlags.ElevationKnown) &&
                !snapshot.Has(MetricFlags.TemperatureValid))
            {
                return "风扇数据读取失败，请重启传感器";
            }
            return "未读取到主板风扇";
        }

        /// <summary>Which always-expected metrics are missing, e.g. "温度、网络"; "全部有效" when none.</summary>
        public static string MissingLine(MetricsSnapshot snapshot)
        {
            if (snapshot == null) return "尚无采样数据";
            List<string> missing = new List<string>();
            for (int i = 0; i < AlwaysExpected.Length; i++)
            {
                if (!snapshot.Has(AlwaysExpected[i].Key)) missing.Add(AlwaysExpected[i].Value);
            }
            if (missing.Count == 0) return "全部有效";
            return string.Join("、", missing.ToArray());
        }

        /// <summary>
        /// The first frames after a restart legitimately lack temperature (5 s rhythm) and network
        /// (one sampling window). Saying so beats letting it read as a failure.
        /// </summary>
        public static string WarmupLine(int ticks, MetricsSnapshot snapshot, int periodMs)
        {
            if (snapshot == null || ticks > 3) return null;
            bool temperature = snapshot.Has(MetricFlags.TemperatureValid);
            bool network = snapshot.Has(MetricFlags.NetworkValid);
            if (temperature && network) return null;
            return "刚启动（第 " + ticks + " 帧，" + periodMs + " ms 一拍）：" +
                   "温度按 5 秒节拍、网络要一个采样周期，先别当故障";
        }
    }
}
