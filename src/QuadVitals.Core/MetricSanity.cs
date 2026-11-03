using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// Turns a snapshot's validity flags into the flags the dashboard may trust. The agent sets a
    /// flag only when it considers the sample usable, but a stuck sampler can still publish NaN or
    /// a value outside any physical range. The dashboard uses this view for its data watchdog, so
    /// an "impossible" number counts the same as a missing one instead of being drawn as real.
    /// </summary>
    public static class MetricSanity
    {
        public static MetricFlags EffectiveFlags(MetricsSnapshot snapshot)
        {
            if (snapshot == null) return MetricFlags.None;

            MetricFlags flags = snapshot.Flags;
            if (Has(flags, MetricFlags.CpuValid) && !Percent(snapshot.CpuPercent)) flags &= ~MetricFlags.CpuValid;
            if (Has(flags, MetricFlags.MemoryValid) && !Percent(snapshot.MemoryPercent)) flags &= ~MetricFlags.MemoryValid;
            if (Has(flags, MetricFlags.GpuValid) && !Percent(snapshot.GpuLoadPercent)) flags &= ~MetricFlags.GpuValid;
            if (Has(flags, MetricFlags.DiskValid) && !Percent(snapshot.DiskPercent)) flags &= ~MetricFlags.DiskValid;
            if (Has(flags, MetricFlags.TemperatureValid) && !Celsius(snapshot.TemperatureC)) flags &= ~MetricFlags.TemperatureValid;
            if (Has(flags, MetricFlags.GpuTemperatureValid) && !Celsius(snapshot.GpuTemperatureC)) flags &= ~MetricFlags.GpuTemperatureValid;
            if (Has(flags, MetricFlags.NetworkValid) &&
                (!Rate(snapshot.NetUpBytesPerSec) || !Rate(snapshot.NetDownBytesPerSec)))
            {
                flags &= ~MetricFlags.NetworkValid;
            }
            return flags;
        }

        private static bool Has(MetricFlags flags, MetricFlags flag)
        {
            return (flags & flag) == flag;
        }

        private static bool Percent(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value <= 100f;
        }

        private static bool Celsius(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > -40f && value < 150f;
        }

        private static bool Rate(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;
        }
    }
}
