using System;
using System.Diagnostics;
using System.Globalization;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// One tick = read every metric, build a snapshot. A metric that fails leaves its validity
    /// flag off, so the band shows "--" instead of a fake zero.
    ///
    /// Temperature is the only slow sampler here; it runs on its own thread (TemperatureWorker)
    /// and this class just reads the latest cached value, which keeps ticks at the target period.
    /// </summary>
    internal sealed class AgentLoop : IDisposable
    {
        private readonly CpuSampler _cpu = new CpuSampler();
        private readonly PerCoreCpuSampler _perCore = new PerCoreCpuSampler();
        private readonly MemorySampler _memory = new MemorySampler();
        private readonly NetworkWorker _network;
        private readonly TemperatureWorker _temperature;
        private readonly GpuSource _gpu;
        private readonly DiskSampler _disk = new DiskSampler();
        private readonly DiskIoSampler _diskIo = new DiskIoSampler();
        private readonly VolumeIoSampler _volumeIo = new VolumeIoSampler();
        private readonly Stopwatch _stage = new Stopwatch();
        private readonly bool _elevated;
        private Settings _settings;

        public AgentLoop(Settings settings, TemperatureSource temperature)
            : this(settings, temperature, null, TemperatureWorker.DefaultIntervalMs)
        {
        }

        public AgentLoop(Settings settings, TemperatureSource temperature, GpuSource gpu,
                         int temperatureIntervalMs = TemperatureWorker.DefaultIntervalMs,
                         Action<string> temperatureStall = null, bool elevated = false)
        {
            _settings = settings ?? Settings.CreateDefault();
            _elevated = elevated;
            _network = new NetworkWorker(_settings.NetworkAdapter, SampleIntervalFor(_settings));
            _temperature = new TemperatureWorker(temperature, temperatureIntervalMs, temperatureStall);
            _gpu = gpu;
        }

        public TemperatureWorker Temperature { get { return _temperature; } }

        public string NetworkAdapterName { get { return _network.AdapterName; } }

        /// <summary>
        /// Applies the fields that affect sampling (currently the network adapter selector).
        /// Called on the publisher thread when <see cref="SettingsMonitor"/> reports a change;
        /// changing the selector resets the byte deltas so the next sample is not a giant
        /// mixed-adapter jump.
        /// </summary>
        public void ApplySettings(Settings settings)
        {
            if (settings == null) return;
            _settings = settings;
            // The worker applies these on its own thread; the publisher must not touch the sampler.
            _network.UpdateSelector(settings.NetworkAdapter);
            _network.SetInterval(SampleIntervalFor(settings));
        }

        /// <summary>
        /// Same window as before the move: one sample per refresh period, i.e. the rate covers the
        /// period the band is about to paint. Shorter windows would be fresher but noisier, and
        /// this change is only about *where* the sampling happens, not about its semantics.
        /// </summary>
        private static int SampleIntervalFor(Settings settings)
        {
            return settings.RefreshMs;
        }

        public MetricsSnapshot Tick(DateTime nowUtc)
        {
            MetricsSnapshot snapshot = new MetricsSnapshot
            {
                TimestampUtc = nowUtc,
                Flags = MetricFlags.AgentAlive | MetricFlags.ElevationKnown
            };
            if (_elevated) snapshot.Flags |= MetricFlags.Elevated;

            TickTimings timings = new TickTimings();
            _stage.Restart();

            double userPercent;
            double systemPercent;
            double cpu = _cpu.Sample(out userPercent, out systemPercent);
            if (!double.IsNaN(cpu))
            {
                snapshot.CpuPercent = (float)Math.Round(cpu, 1);
                if (!double.IsNaN(userPercent)) snapshot.CpuUserPercent = (float)Math.Round(userPercent, 1);
                if (!double.IsNaN(systemPercent)) snapshot.CpuSystemPercent = (float)Math.Round(systemPercent, 1);
                snapshot.Flags |= MetricFlags.CpuValid;

                PerCoreCpuSampler.CoreSample cores = _perCore.Sample();
                if (cores.Total.Length > 0)
                {
                    snapshot.CorePercents = cores.Total;
                    snapshot.CoreUserPercents = cores.User;
                    snapshot.CoreSystemPercents = cores.System;
                }
            }
            timings.CpuMs = Lap();

            double memoryPercent;
            long usedBytes, totalBytes;
            if (_memory.TrySample(out memoryPercent, out usedBytes, out totalBytes))
            {
                snapshot.MemoryPercent = (float)Math.Round(memoryPercent, 1);
                snapshot.MemoryUsedBytes = usedBytes;
                snapshot.MemoryTotalBytes = totalBytes;
                snapshot.Flags |= MetricFlags.MemoryValid;
            }
            timings.MemoryMs = Lap();

            double up, down;
            if (_network.TryGet(nowUtc, out up, out down))
            {
                snapshot.NetUpBytesPerSec = up;
                snapshot.NetDownBytesPerSec = down;
                snapshot.Flags |= MetricFlags.NetworkValid;
            }
            snapshot.AdapterName = _network.AdapterName ?? string.Empty;
            timings.NetworkMs = Lap();

            double celsius;
            string reason;
            if (_temperature.TryGet(nowUtc, TimeSpan.FromSeconds(12), out celsius, out reason))
            {
                snapshot.TemperatureC = (float)Math.Round(celsius, 1);
                snapshot.Flags |= MetricFlags.TemperatureValid;
            }

            // 功耗与温度同一个 5 s 节拍：读不到就保持 0（= 未知），不编数字。
            double watts;
            if (_temperature.TryGetPower(nowUtc, TimeSpan.FromSeconds(12), out watts))
            {
                snapshot.CpuPowerWatts = (float)Math.Round(watts, 1);
                snapshot.Flags |= MetricFlags.CpuPowerValid;
            }

            MetricsSnapshot.FanReading[] fans;
            if (_temperature.TryGetFans(nowUtc, TimeSpan.FromSeconds(12), out fans))
            {
                snapshot.Fans = fans;
                snapshot.Flags |= MetricFlags.FanValid;
            }
            timings.TemperatureMs = Lap();

            if (_gpu != null)
            {
                double gpuLoad, gpuMemory, gpuCelsius;
                double gpuPower, gpuClock;
                _gpu.Sample(out gpuLoad, out gpuMemory, out gpuCelsius, out gpuPower, out gpuClock);
                if (!double.IsNaN(gpuLoad))
                {
                    snapshot.GpuLoadPercent = (float)Math.Round(gpuLoad, 1);
                    snapshot.Flags |= MetricFlags.GpuValid;
                }
                if (!double.IsNaN(gpuMemory)) snapshot.GpuMemoryPercent = (float)Math.Round(gpuMemory, 1);
                if (!double.IsNaN(gpuCelsius))
                {
                    snapshot.GpuTemperatureC = (float)Math.Round(gpuCelsius, 1);
                    snapshot.Flags |= MetricFlags.GpuTemperatureValid;
                }
                if (!double.IsNaN(gpuPower))
                {
                    snapshot.GpuPowerWatts = (float)Math.Round(gpuPower, 1);
                    snapshot.Flags |= MetricFlags.GpuPowerValid;
                }
                if (!double.IsNaN(gpuClock))
                {
                    snapshot.GpuClockMhz = (float)Math.Round(gpuClock, 0);
                    snapshot.Flags |= MetricFlags.GpuClockValid;
                }
            }
            timings.GpuMs = Lap();

            double diskPercent;
            int diskUsedGb, diskTotalGb;
            if (_disk.TrySample(nowUtc, out diskPercent, out diskUsedGb, out diskTotalGb))
            {
                snapshot.DiskPercent = (float)Math.Round(diskPercent, 1);
                snapshot.DiskUsedGb = diskUsedGb;
                snapshot.DiskTotalGb = diskTotalGb;
                snapshot.Flags |= MetricFlags.DiskValid;
            }
            timings.DiskMs = Lap();

            double diskRead, diskWrite;
            if (_diskIo.TrySample(nowUtc, out diskRead, out diskWrite))
            {
                snapshot.DiskIoReadBytesPerSec = (float)diskRead;
                snapshot.DiskIoWriteBytesPerSec = (float)diskWrite;
                snapshot.Flags |= MetricFlags.DiskIoValid;
            }
            MetricsSnapshot.VolumeIo[] volumes = _volumeIo.Sample(nowUtc);
            if (volumes.Length > 0) snapshot.Volumes = volumes;
            timings.DiskIoMs = Lap();

            LastTimings = timings;
            return snapshot;
        }

        /// <summary>
        /// Stage cost of the most recent tick, in milliseconds. Only the diagnostics in
        /// <see cref="AgentRuntime"/> read this: a frame long enough for the band to blink "--"
        /// has to be attributed to one sampler instead of guessed at.
        /// </summary>
        public TickTimings LastTimings { get; private set; }

        private double Lap()
        {
            double ms = _stage.Elapsed.TotalMilliseconds;
            _stage.Restart();
            return ms;
        }

        public void Dispose()
        {
            if (_volumeIo != null) _volumeIo.Dispose();
            if (_network != null) _network.Dispose();
            if (_temperature != null) _temperature.Dispose();
        }
    }

    /// <summary>
    /// Milliseconds spent in each stage of one tick. Diagnostics only: it exists so a long
    /// frame can be attributed to a specific sampler instead of guessed at.
    /// </summary>
    internal struct TickTimings
    {
        public double CpuMs;
        public double MemoryMs;
        public double NetworkMs;
        public double TemperatureMs;
        public double GpuMs;
        public double DiskMs;
        public double DiskIoMs;

        public double SumMs { get { return CpuMs + MemoryMs + NetworkMs + TemperatureMs + GpuMs + DiskMs + DiskIoMs; } }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "cpu={0:F1} mem={1:F1} net={2:F1} temp={3:F1} gpu={4:F1} disk={5:F1} diskio={6:F1}",
                CpuMs, MemoryMs, NetworkMs, TemperatureMs, GpuMs, DiskMs, DiskIoMs);
        }
    }
}
