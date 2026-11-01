using System;
using System.Collections.Generic;

namespace QuadVitals.Core
{
    /// <summary>The fixed metric catalog.</summary>
    public enum MetricId
    {
        Cpu = 0,
        Memory = 1,
        Temperature = 2,
        NetUp = 3,
        NetDown = 4,
        Gpu = 5,
        GpuMemory = 6,
        GpuTemperature = 7,
        Disk = 8,
        /// <summary>System-wide disk read + write throughput (v2 snapshot field).</summary>
        DiskIo = 9,
        /// <summary>System-wide disk read throughput, bytes/s (the pair's first line).</summary>
        DiskIoRead = 10,
        /// <summary>System-wide disk write throughput, bytes/s (the pair's second line).</summary>
        DiskIoWrite = 11
    }

    public enum MetricKind
    {
        Percent,
        Temperature,
        Rate
    }

    /// <summary>
    /// One catalog entry. design: the catalog is closed at compile time — adding a metric
    /// means adding an entry here (plus collection, formatting, tests, display name) and
    /// shipping a build. There is deliberately no perf-counter path.
    /// </summary>
    public sealed class MetricInfo
    {
        public MetricId Id { get; private set; }
        /// <summary>Stable key used in settings.json ("cpu", "netUp", ...).</summary>
        public string Key { get; private set; }
        /// <summary>Chinese display name for the settings UI.</summary>
        public string DisplayName { get; private set; }
        public MetricKind Kind { get; private set; }
        public double MinValue { get; private set; }
        public double MaxValue { get; private set; }

        internal MetricInfo(MetricId id, string key, string displayName, MetricKind kind,
                            double minValue, double maxValue)
        {
            Id = id;
            Key = key;
            DisplayName = displayName;
            Kind = kind;
            MinValue = minValue;
            MaxValue = maxValue;
        }

        /// <summary>Formats a raw value for the band; out-of-range or NaN becomes "--".</summary>
        public string Format(double value)
        {
            switch (Kind)
            {
                case MetricKind.Percent: return MetricFormat.Percent(value);
                case MetricKind.Temperature: return MetricFormat.Temperature(value);
                default: return MetricFormat.Rate(value);
            }
        }

        public string FormatDetailed(double value)
        {
            switch (Kind)
            {
                case MetricKind.Percent: return MetricFormat.PercentDetailed(value);
                case MetricKind.Temperature: return MetricFormat.TemperatureDetailed(value);
                default: return MetricFormat.Rate(value);
            }
        }

        /// <summary>Clamps a value into this metric's range; NaN stays NaN.</summary>
        public double Clamp(double value)
        {
            if (double.IsNaN(value)) return value;
            if (value < MinValue) return MinValue;
            if (value > MaxValue) return MaxValue;
            return value;
        }

        public override string ToString()
        {
            return Key + " (" + DisplayName + ")";
        }
    }

    public static class MetricCatalog
    {
        // Order matters: it is the order the settings UI offers.
        private static readonly MetricInfo[] Entries =
        {
            new MetricInfo(MetricId.Cpu,         "cpu",         "CPU 占用",  MetricKind.Percent,     0, 100),
            new MetricInfo(MetricId.Memory,      "memory",      "内存占用",  MetricKind.Percent,     0, 100),
            new MetricInfo(MetricId.Temperature, "temperature", "CPU 温度",  MetricKind.Temperature, 0, 120),
            new MetricInfo(MetricId.NetUp,       "netUp",       "上行速度",  MetricKind.Rate,        0, double.MaxValue),
            new MetricInfo(MetricId.NetDown,     "netDown",     "下行速度",  MetricKind.Rate,        0, double.MaxValue),
            new MetricInfo(MetricId.Gpu,         "gpu",         "显卡占用",  MetricKind.Percent,     0, 100),
            new MetricInfo(MetricId.GpuMemory,   "gpuMemory",   "显存占用",  MetricKind.Percent,     0, 100),
            new MetricInfo(MetricId.GpuTemperature, "gpuTemp",  "GPU 温度",  MetricKind.Temperature, 0, 120),
            new MetricInfo(MetricId.Disk,        "disk",        "磁盘占用",  MetricKind.Percent,     0, 100),
            // 磁盘 I/O 在界面上是一个下拉项、两行文字（读 / 写），这两个条目只给那两行用。
            // 合并后的 diskIo 保留给旧配置与原生渲染，不在下拉里出现（design 修订）。
            new MetricInfo(MetricId.DiskIo,      "diskIo",      "磁盘 I/O",  MetricKind.Rate,        0, double.MaxValue),
            new MetricInfo(MetricId.DiskIoRead,  "diskIoRead",  "磁盘读取",  MetricKind.Rate,        0, double.MaxValue),
            new MetricInfo(MetricId.DiskIoWrite, "diskIoWrite", "磁盘写入",  MetricKind.Rate,        0, double.MaxValue)
        };

        public static IReadOnlyList<MetricInfo> All { get { return Entries; } }

        public static MetricInfo Get(MetricId id)
        {
            for (int i = 0; i < Entries.Length; i++)
            {
                if (Entries[i].Id == id) return Entries[i];
            }
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        /// <summary>Key lookup used when parsing settings.json.</summary>
        public static bool TryGet(string key, out MetricInfo info)
        {
            info = null;
            if (string.IsNullOrEmpty(key)) return false;
            for (int i = 0; i < Entries.Length; i++)
            {
                if (string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    info = Entries[i];
                    return true;
                }
            }
            return false;
        }

        public static bool IsKnownKey(string key)
        {
            MetricInfo ignored;
            return TryGet(key, out ignored);
        }

        /// <summary>Reads one metric out of a snapshot honouring its validity flag.</summary>
        public static bool TryReadValue(MetricsSnapshot snapshot, MetricId id, out double value)
        {
            value = double.NaN;
            if (snapshot == null) return false;

            switch (id)
            {
                case MetricId.Cpu:
                    if (!snapshot.Has(MetricFlags.CpuValid)) return false;
                    value = snapshot.CpuPercent;
                    return true;
                case MetricId.Memory:
                    if (!snapshot.Has(MetricFlags.MemoryValid)) return false;
                    value = snapshot.MemoryPercent;
                    return true;
                case MetricId.Temperature:
                    if (!snapshot.Has(MetricFlags.TemperatureValid)) return false;
                    value = snapshot.TemperatureC;
                    return true;
                case MetricId.NetUp:
                    if (!snapshot.Has(MetricFlags.NetworkValid)) return false;
                    value = snapshot.NetUpBytesPerSec;
                    return true;
                case MetricId.NetDown:
                    if (!snapshot.Has(MetricFlags.NetworkValid)) return false;
                    value = snapshot.NetDownBytesPerSec;
                    return true;
                case MetricId.Gpu:
                    if (!snapshot.Has(MetricFlags.GpuValid)) return false;
                    value = snapshot.GpuLoadPercent;
                    return true;
                case MetricId.GpuMemory:
                    if (!snapshot.Has(MetricFlags.GpuValid)) return false;
                    value = snapshot.GpuMemoryPercent;
                    return true;
                case MetricId.GpuTemperature:
                    if (!snapshot.Has(MetricFlags.GpuTemperatureValid)) return false;
                    value = snapshot.GpuTemperatureC;
                    return true;
                case MetricId.Disk:
                    if (!snapshot.Has(MetricFlags.DiskValid)) return false;
                    value = snapshot.DiskPercent;
                    return true;
                case MetricId.DiskIo:
                    if (!snapshot.Has(MetricFlags.DiskIoValid)) return false;
                    value = snapshot.DiskIoReadBytesPerSec + (double)snapshot.DiskIoWriteBytesPerSec;
                    return true;
                case MetricId.DiskIoRead:
                    if (!snapshot.Has(MetricFlags.DiskIoValid)) return false;
                    value = snapshot.DiskIoReadBytesPerSec;
                    return true;
                case MetricId.DiskIoWrite:
                    if (!snapshot.Has(MetricFlags.DiskIoValid)) return false;
                    value = snapshot.DiskIoWriteBytesPerSec;
                    return true;
                default:
                    return false;
            }
        }
    }
}
