using System;

namespace QuadVitals.Core
{
    /// <summary>How a metric should be coloured on the dashboard (matches the overview card tones).</summary>
    public enum MetricTone
    {
        Normal = 0,
        Warning = 1,
        Danger = 2
    }

    /// <summary>
    /// Thresholds used by the local-environment cards:
    /// CPU warns earlier than memory, and generic percentage metrics (GPU / disk) sit at 80/90.
    /// </summary>
    public static class MetricTones
    {
        /// <summary>CPU: 70% warn, 80% danger.</summary>
        public static MetricTone Cpu(double percent)
        {
            if (percent >= 80) return MetricTone.Danger;
            if (percent >= 70) return MetricTone.Warning;
            return MetricTone.Normal;
        }

        /// <summary>Memory: 70% warn, 85% danger.</summary>
        public static MetricTone Memory(double percent)
        {
            if (percent >= 85) return MetricTone.Danger;
            if (percent >= 70) return MetricTone.Warning;
            return MetricTone.Normal;
        }

        /// <summary>Everything else measured in percent (GPU utilisation, disk usage): 80/90.</summary>
        public static MetricTone Generic(double percent)
        {
            if (percent >= 90) return MetricTone.Danger;
            if (percent >= 80) return MetricTone.Warning;
            return MetricTone.Normal;
        }
    }

    /// <summary>
    /// Fan RPM bands. A motherboard fan header has no idea what is bolted to it, so the bands are
    /// absolute and deliberately generous: a 120 mm case fan idles around 600-900 RPM and tops out
    /// near 1500, while a 3000+ RPM header is a pump or a small high-speed fan. The card shows
    /// those separately, so the colour still means "this channel is running hard".
    /// </summary>
    public static class FanZones
    {
        /// <summary>Full-scale RPM used by the bar and the colour bands.</summary>
        public const int NominalMaxRpm = 2000;

        /// <summary>70% of full scale: yellow from here.</summary>
        public const int WarningRpm = 1400;

        /// <summary>80% of full scale: red from here.</summary>
        public const int DangerRpm = 1600;

        public static MetricTone Tone(int rpm)
        {
            if (rpm >= DangerRpm) return MetricTone.Danger;
            if (rpm >= WarningRpm) return MetricTone.Warning;
            return MetricTone.Normal;
        }

        /// <summary>0..1 for the bar; a stopped or unknown channel reads 0.</summary>
        public static double Fraction(int rpm)
        {
            if (rpm <= 0) return 0;
            double value = (double)rpm / NominalMaxRpm;
            return value > 1 ? 1 : value;
        }
    }

    /// <summary>
    /// Temperature bands for the adaptive text colour. The numbers describe the hardware, not a
    /// per-machine calibration: the CPU follows the 100 C throttle point of Intel/AMD desktop
    /// parts (warn 80, danger 90), the GPU follows the ~83-88 C point where NVIDIA cards start
    /// clocking down (warn 75, danger 83). Everything is in Celsius, and NaN is Normal so a
    /// missing sample never paints as an alert.
    /// </summary>
    public static class TemperatureZones
    {
        public const double CpuWarning = 80.0;
        public const double CpuDanger = 90.0;
        public const double GpuWarning = 75.0;
        public const double GpuDanger = 83.0;

        public static MetricTone Cpu(double celsius)
        {
            return Zone(celsius, CpuWarning, CpuDanger);
        }

        public static MetricTone Gpu(double celsius)
        {
            return Zone(celsius, GpuWarning, GpuDanger);
        }

        /// <summary>Band text lines carry metric keys; anything that is not a temperature is Normal.</summary>
        public static MetricTone For(string metricKey, double celsius)
        {
            if (string.Equals(metricKey, "temperature", StringComparison.OrdinalIgnoreCase))
                return Cpu(celsius);
            if (string.Equals(metricKey, "gpuTemp", StringComparison.OrdinalIgnoreCase))
                return Gpu(celsius);
            return MetricTone.Normal;
        }

        private static MetricTone Zone(double celsius, double warning, double danger)
        {
            if (double.IsNaN(celsius)) return MetricTone.Normal;
            if (celsius >= danger) return MetricTone.Danger;
            if (celsius >= warning) return MetricTone.Warning;
            return MetricTone.Normal;
        }
    }
}
