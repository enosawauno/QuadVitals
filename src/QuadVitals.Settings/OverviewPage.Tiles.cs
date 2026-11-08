using System;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 概览四格的“这块显示哪个指标”。每个硬件能选的指标由 `DeviceMetricChoices` 决定，
    /// 选择存在 settings.json 的 `deviceMetrics` 里；这里只负责按选择把值、单位、比例、说明和
    /// 标签画上去，读不到数据时照旧写原因、不编数字。
    /// </summary>
    internal sealed partial class OverviewPage
    {
        /// <summary>I/O 转速用的短窗口样本（取最近 5 个采样的平均，避免转速跟着秒级抖动）。</summary>
        private readonly System.Collections.Generic.List<double> _ioSamples = new System.Collections.Generic.List<double>();
        /// <summary>掉档后的滚动保持截止时间（250 ms 收尾节拍）。</summary>
        private DateTime _ioWaveHoldUntil = DateTime.MinValue;
        /// <summary>上一帧是否在滚动，用来判断“刚刚掉档”。</summary>
        private bool _ioWasActive;

        private DeviceMetricChoices Choices
        {
            get
            {
                AppSettings settings = _window.CurrentSettings;
                return settings != null && settings.DeviceMetrics != null
                    ? settings.DeviceMetrics
                    : new DeviceMetricChoices();
            }
        }

        private static void ApplyLabel(MetricTile tile, string hardware, string metric)
        {
            tile.Label = hardware + " · " + DeviceMetricChoices.DisplayName(metric);
        }

        private void RenderCpuTile(MetricsSnapshot snapshot)
        {
            string metric = DeviceMetricChoices.Keep(Choices.Cpu, DeviceMetricChoices.CpuOptions);
            ApplyLabel(_cpuTile, "CPU", metric);

            switch (metric)
            {
                case DeviceMetricChoices.Temperature:
                    if (snapshot.Has(MetricFlags.TemperatureValid))
                    {
                        double celsius = snapshot.TemperatureC;
                        _cpuTile.SetValue(celsius.ToString("0"), "℃",
                            Math.Min(1.0, Math.Max(0.0, celsius / 100.0)), CpuNoteText(),
                            TemperatureZones.Cpu(celsius));
                    }
                    else
                    {
                        _cpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0,
                            AgentDiagnostics.TemperatureUnavailableLine(snapshot));
                    }
                    break;

                case DeviceMetricChoices.Frequency:
                    CpuClock.Reading clock = CpuClock.Read();
                    if (clock.CurrentMhz > 0)
                    {
                        double fraction = clock.MaxMhz > 0 ? Math.Min(1.0, (double)clock.CurrentMhz / clock.MaxMhz) : 0;
                        // 最高频率不跟说明行挤：它是静态硬件参数，说明行已经要放型号、核数与线程数。
                        if (clock.MaxMhz > 0)
                        {
                            _cpuTile.ToolTip = "处理器最高频率 " +
                                (clock.MaxMhz / 1000.0).ToString("0.0") + " GHz";
                        }
                        _cpuTile.SetValue((clock.CurrentMhz / 1000.0).ToString("0.0"), "GHz", fraction,
                            CpuNoteText(), MetricTone.Normal);
                    }
                    else
                    {
                        _cpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "频率数据读取失败");
                    }
                    break;

                case DeviceMetricChoices.Power:
                    if (snapshot.Has(MetricFlags.CpuPowerValid) && snapshot.CpuPowerWatts > 0)
                    {
                        double watts = snapshot.CpuPowerWatts;
                        // 条形按 250 W 归一化（i9 桌面平台 PL2 量级）；归一化只影响条形长度，真值在数字上。
                        _cpuTile.SetValue(watts.ToString("0.0"), "W", Math.Min(1.0, watts / 250.0),
                            CpuNoteText(), MetricTone.Normal);
                    }
                    else
                    {
                        _cpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "功耗数据读取失败");
                    }
                    break;

                default:
                    _cpuTile.SetValue(PercentNumber(snapshot.CpuPercent), "%", snapshot.CpuPercent / 100.0,
                        CpuNoteText(), MetricTones.Cpu(snapshot.CpuPercent));
                    break;
            }
        }

        private void RenderMemoryTile(MetricsSnapshot snapshot)
        {
            string metric = DeviceMetricChoices.Keep(Choices.MemoryUsed, DeviceMetricChoices.MemoryOptions);
            ApplyLabel(_memoryTile, "内存", metric);

            double totalGb = snapshot.MemoryTotalBytes > 0 ? snapshot.MemoryTotalBytes / 1073741824.0 : 0;
            double usedGb = snapshot.MemoryUsedBytes > 0 ? snapshot.MemoryUsedBytes / 1073741824.0 : 0;

            if (metric == DeviceMetricChoices.Used && totalGb > 0)
            {
                _memoryTile.SetValue(usedGb.ToString("0.0"), "GB", usedGb / totalGb,
                    MemoryNoteText(snapshot.MemoryUsedBytes, snapshot.MemoryTotalBytes),
                    MetricTones.Memory(snapshot.MemoryPercent));
                return;
            }

            _memoryTile.SetValue(PercentNumber(snapshot.MemoryPercent), "%", snapshot.MemoryPercent / 100.0,
                MemoryNoteText(snapshot.MemoryUsedBytes, snapshot.MemoryTotalBytes),
                MetricTones.Memory(snapshot.MemoryPercent));
        }

        private void RenderGpuTile(MetricsSnapshot snapshot)
        {
            string metric = DeviceMetricChoices.Keep(Choices.Gpu, DeviceMetricChoices.GpuOptions);
            ApplyLabel(_gpuTile, "显卡", metric);

            if (!snapshot.Has(MetricFlags.GpuValid))
            {
                _gpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0, GpuUnavailableText());
                return;
            }

            switch (metric)
            {
                case DeviceMetricChoices.Memory:
                    _gpuTile.SetValue(PercentNumber(snapshot.GpuMemoryPercent), "%", snapshot.GpuMemoryPercent / 100.0,
                        GpuNoteText(snapshot), MetricTones.Generic(snapshot.GpuMemoryPercent));
                    break;

                case DeviceMetricChoices.Temperature:
                    if (snapshot.Has(MetricFlags.GpuTemperatureValid))
                    {
                        double celsius = snapshot.GpuTemperatureC;
                        _gpuTile.SetValue(celsius.ToString("0"), "℃",
                            Math.Min(1.0, Math.Max(0.0, celsius / 100.0)), GpuNoteText(snapshot),
                            TemperatureZones.Gpu(celsius));
                    }
                    else
                    {
                        _gpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "显卡温度读取失败");
                    }
                    break;

                case DeviceMetricChoices.Power:
                    if (snapshot.Has(MetricFlags.GpuPowerValid) && snapshot.GpuPowerWatts > 0)
                    {
                        double watts = snapshot.GpuPowerWatts;
                        // 条形按 450 W 归一化（RTX 4080 一类卡的上限量级）。
                        _gpuTile.SetValue(watts.ToString("0.0"), "W", Math.Min(1.0, watts / 450.0),
                            GpuNoteText(snapshot), MetricTone.Normal);
                    }
                    else
                    {
                        _gpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "显卡功耗读取失败");
                    }
                    break;

                case DeviceMetricChoices.Frequency:
                    if (snapshot.Has(MetricFlags.GpuClockValid) && snapshot.GpuClockMhz > 0)
                    {
                        double mhz = snapshot.GpuClockMhz;
                        // 单位用兆赫兹：整数 + 小号 MHz，和 --status 的输出口径一致。
                        // 条形按 3000 MHz 归一化（消费级显卡核心频率的上限量级）。
                        _gpuTile.SetValue(mhz.ToString("0"), "MHz", Math.Min(1.0, mhz / 3000.0),
                            GpuNoteText(snapshot), MetricTone.Normal);
                    }
                    else
                    {
                        _gpuTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "显卡频率读取失败");
                    }
                    break;

                default:
                    _gpuTile.SetValue(PercentNumber(snapshot.GpuLoadPercent), "%", snapshot.GpuLoadPercent / 100.0,
                        GpuNoteText(snapshot), MetricTones.Generic(snapshot.GpuLoadPercent));
                    break;
            }
        }

        private void RenderDiskTile(MetricsSnapshot snapshot)
        {
            string metric = DeviceMetricChoices.Keep(Choices.Disk, DeviceMetricChoices.DiskOptions);
            ApplyLabel(_diskTile, "硬盘", metric);

            if (!snapshot.Has(MetricFlags.DiskValid))
            {
                _diskTile.SetValue(MetricFormat.Invalid, string.Empty, 0, "系统盘容量读取失败");
                return;
            }

            switch (metric)
            {
                case DeviceMetricChoices.Free:
                    double free = Math.Max(0, 100.0 - snapshot.DiskPercent);
                    _diskTile.SetValue(PercentNumber(free), "%", free / 100.0,
                        DiskNoteText(snapshot, DeviceMetricChoices.Free),
                        MetricTones.Generic(free));
                    break;

                case DeviceMetricChoices.Io:
                    double read = Math.Max(0, snapshot.DiskIoReadBytesPerSec);
                    double write = Math.Max(0, snapshot.DiskIoWriteBytesPerSec);
                    // 条形按较大单向到 50 MB/s 的进度（单向流量也算在跑），
                    // 过了 50 MB/s 条满格并开始走马循环，30→50 之间波浪淡入。
                    double peakMb = IoWave.PeakMbPerSecond(read, write);
                    // 转速看最近 5 个采样的平均，不看瞬时值。
                    _ioSamples.Add(peakMb);
                    while (_ioSamples.Count > IoWave.RollSampleCount) _ioSamples.RemoveAt(0);
                    double averageMb = IoWave.Average(_ioSamples);

                    // 档位：**到 50 MB/s 才开始滚**（30 就开会把条提前填满，低速尖峰会让它反复涨缩）。
                    bool active = IoWave.ShouldWave(peakMb);
                    DateTime nowUtc = DateTime.UtcNow;
                    if (active)
                    {
                        _ioWaveHoldUntil = DateTime.MinValue;
                    }
                    else if (_ioWasActive)
                    {
                        // 刚掉档：先保持 250 ms 滚动（最后一格落到右端），再交给平滑缩回。
                        _ioWaveHoldUntil = nowUtc.AddMilliseconds(IoWave.SettleMilliseconds);
                    }
                    _ioWasActive = active;
                    bool waving = active || nowUtc < _ioWaveHoldUntil;
                    // 磁盘 I/O 的走马是读数本身的一部分（一眼看出“在读写”），保持常驻实时循环。
                    // 风扇那团动效属于装饰，改成在风扇设置里选「常驻动画 / 悬停播放」。
                    _diskTile.IoWaveOnHover = false;
                    _diskTile.SetIoWave(waving ? IoWave.WaveOpacity(peakMb) : 0,
                        waving ? IoWave.RollCyclesPerSecond(averageMb) : 0);
                    // 大数字放合计吞吐，说明行拆开读写；单位单独传（两种字号）。
                    string ioUnit;
                    string ioValue = MetricFormat.RatePartsFixed(read + write, out ioUnit);
                    _diskTile.SetValue(ioValue, ioUnit, IoWave.FillFraction(peakMb),
                        "读 " + MetricFormat.Rate(read) + " · 写 " + MetricFormat.Rate(write),
                        MetricTone.Normal);
                    break;

                default:
                    // 切回占用/可用时把走马停掉，否则波浪会留在格子上。
                    _diskTile.SetIoWave(0, 0);
                    _diskTile.SetValue(PercentNumber(snapshot.DiskPercent), "%", snapshot.DiskPercent / 100.0,
                        DiskNoteText(snapshot, DeviceMetricChoices.Usage), MetricTones.Generic(snapshot.DiskPercent));
                    break;
            }
        }
    }
}
