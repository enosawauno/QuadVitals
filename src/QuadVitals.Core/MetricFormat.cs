using System;
using System.Globalization;

namespace QuadVitals.Core
{
    /// <summary>
    /// Number formatting shared by the band and the dashboard, so both render "1.2 MB/s"
    /// and never "1.20 MB/s" or "1 MB/s" depending on the caller.
    /// Rules follow the XMeters look the band copies: the unit is scaled, the
    /// number keeps at most one decimal and drops a trailing ".0".
    /// </summary>
    public static class MetricFormat
    {
        public const string Invalid = "--";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Bytes per second, auto-scaled: B/s, KB/s, MB/s, GB/s.</summary>
        public static string Rate(double bytesPerSecond)
        {
            string unit;
            string number = RateParts(bytesPerSecond, out unit);
            return string.IsNullOrEmpty(unit) ? number : number + " " + unit;
        }

        /// <summary>
        /// Same scaling as <see cref="Rate"/>, but returns the number alone and the unit via
        /// <paramref name="unit"/>: the overview tiles draw them at different sizes.
        /// </summary>
        public static string RateParts(double bytesPerSecond, out string unit)
        {
            double value = RateScale(bytesPerSecond, out unit);
            if (double.IsNaN(value)) return Invalid;
            return TrimNumber(value);
        }

        /// <summary>
        /// 与 <see cref="RateParts"/> 同刻度，但数值固定一位小数（3 → "3.0"）。
        /// 概览四格的大数字用它：整数/一位小数混排时字形宽度会一秒一跳，看上去数字在抽。
        /// </summary>
        public static string RatePartsFixed(double bytesPerSecond, out string unit)
        {
            double value = RateScale(bytesPerSecond, out unit);
            if (double.IsNaN(value)) return Invalid;
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>统一刻度：1024 进位，返回已缩放的数值；非法输入返回 NaN 且 unit 为空。</summary>
        private static double RateScale(double bytesPerSecond, out string unit)
        {
            unit = string.Empty;
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond < 0)
            {
                return double.NaN;
            }

            double value = bytesPerSecond;
            unit = "B/s";

            if (value >= 1024.0)
            {
                value /= 1024.0; unit = "KB/s";
                if (value >= 1024.0)
                {
                    value /= 1024.0; unit = "MB/s";
                    if (value >= 1024.0)
                    {
                        value /= 1024.0; unit = "GB/s";
                    }
                }
            }

            return value;
        }

        /// <summary>Percent without a decimal point ("12%"); invalid yields "--".</summary>
        public static string Percent(double percent)
        {
            if (!IsFinite(percent)) return Invalid;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            return ((int)Math.Round(percent, MidpointRounding.AwayFromZero)).ToString(Inv) + "%";
        }

        /// <summary>Percent with one decimal ("12.3%"), used by the dashboard gauges.</summary>
        public static string PercentDetailed(double percent)
        {
            if (!IsFinite(percent)) return Invalid;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            return TrimNumber(percent) + "%";
        }

        /// <summary>Temperature in Celsius, integer degrees ("55°C"); invalid yields "--".</summary>
        public static string Temperature(double celsius)
        {
            if (!IsFinite(celsius)) return Invalid;
            return ((int)Math.Round(celsius, MidpointRounding.AwayFromZero)).ToString(Inv) + "\u00B0C";
        }

        /// <summary>Temperature with one decimal ("55.4°C") for the dashboard.</summary>
        public static string TemperatureDetailed(double celsius)
        {
            if (!IsFinite(celsius)) return Invalid;
            return TrimNumber(celsius) + "\u00B0C";
        }

        /// <summary>Byte counts for tooltips: "12.4 GB".</summary>
        public static string Bytes(long bytes)
        {
            if (bytes < 0) return Invalid;
            double value = bytes;
            string unit = "B";
            if (value >= 1024.0) { value /= 1024.0; unit = "KB"; }
            if (value >= 1024.0) { value /= 1024.0; unit = "MB"; }
            if (value >= 1024.0) { value /= 1024.0; unit = "GB"; }
            if (value >= 1024.0) { value /= 1024.0; unit = "TB"; }
            return TrimNumber(value) + " " + unit;
        }

        /// <summary>"31.9 GB / 32.0 GB" style memory line for tooltips and the dashboard.</summary>
        public static string MemoryPair(long usedBytes, long totalBytes)
        {
            if (usedBytes < 0 || totalBytes <= 0) return Invalid;
            return Bytes(usedBytes) + " / " + Bytes(totalBytes);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>One decimal max, trailing ".0" removed: 1 -> "1", 1.25 -> "1.3".</summary>
        public static string TrimNumber(double value)
        {
            double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            string text = rounded.ToString("0.0", Inv);
            if (text.EndsWith(".0", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 2);
            }
            return text;
        }
    }
}
