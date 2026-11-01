using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// 磁盘 I/O 条的“走马循环”参数：
    ///
    /// - 条是**绝对刻度**：`速率 / 50 MB/s`，满格就代表 50 MB/s（不是“填满即到底”的百分比）。
    /// - 低于 0.1 MB/s 当没在动：不点亮格子，也不显示 0.0 这种假数字。
    /// - 30 → 50 MB/s 之间波浪**淡入**：30 时完全是静态色阶，50 时完全是滚动相位，中间插值，
    ///   所以跨过阈值时不会突然切换（“无缝丝滑衔接”）。
    /// - 滚动速度随速率变快（12 s/圈 → 0.4 s/圈，按 log10 归一化到 2000 MB/s）。
    ///
    /// 纯函数放在 Core 里，界面和断言用同一份数值，不各写一套。
    /// </summary>
    public static class IoWave
    {
        public const double IdleBelowMbPerSecond = 0.1;
        public const double FadeStartMbPerSecond = 30;
        public const double EnterMbPerSecond = 50;

        public const double RollMaxSeconds = 12;
        public const double RollMinSeconds = 0.4;
        public const double RollFullScaleMbPerSecond = 2000;

        /// <summary>
        /// 掉回阈值以下后还继续滚动多久：把那“最后一格落到右端”走完，再把填充交回给平滑缩回，
        /// 否则从满格直接掉到按速率的格子会看着像断了一下。
        /// </summary>
        public const double SettleMilliseconds = 250;

        /// <summary>转速用最近几个采样的平均，避免数字一秒一变、转速跟着抖。</summary>
        public const int RollSampleCount = 5;

        /// <summary>最近 N 个采样的平均（空集合返回 0）。</summary>
        public static double Average(System.Collections.Generic.IList<double> samples)
        {
            if (samples == null || samples.Count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < samples.Count; i++) sum += IsUsable(samples[i]) ? samples[i] : 0;
            return sum / samples.Count;
        }

        private const double BytesPerMegabyte = 1024.0 * 1024.0;

        /// <summary>读写取较大者（单向流量也算在跑），单位 MB/s。</summary>
        public static double PeakMbPerSecond(double readBytesPerSec, double writeBytesPerSec)
        {
            double read = IsUsable(readBytesPerSec) ? readBytesPerSec : 0;
            double write = IsUsable(writeBytesPerSec) ? writeBytesPerSec : 0;
            double peak = Math.Max(read, write);
            return peak <= 0 ? 0 : peak / BytesPerMegabyte;
        }

        /// <summary>0..1；≤ 0.1 MB/s 视为空条，50 MB/s 及以上满格。</summary>
        public static double FillFraction(double mbPerSecond)
        {
            if (!IsUsable(mbPerSecond) || mbPerSecond < IdleBelowMbPerSecond) return 0;
            double fraction = mbPerSecond / EnterMbPerSecond;
            return fraction > 1 ? 1 : fraction;
        }

        /// <summary>
        /// 是否进入走马档：**必须到 50 MB/s**。30 就开会让条提前填满，低速的瞬时尖峰会让它
        /// 反复涨满又缩回（参考实现里就是这么否掉“从 30 开始”的）。
        /// </summary>
        public static bool ShouldWave(double mbPerSecond)
        {
            return IsUsable(mbPerSecond) && mbPerSecond >= EnterMbPerSecond;
        }

        /// <summary>0..1 的波浪可见度：30 MB/s 以下 0，50 MB/s 及以上 1，中间线性淡入（掉档收尾时会用到）。</summary>
        public static double WaveOpacity(double mbPerSecond)
        {
            if (!IsUsable(mbPerSecond) || mbPerSecond <= FadeStartMbPerSecond) return 0;
            if (mbPerSecond >= EnterMbPerSecond) return 1;
            return (mbPerSecond - FadeStartMbPerSecond) / (EnterMbPerSecond - FadeStartMbPerSecond);
        }

        /// <summary>转速（圈/秒）：流量越大转得越快，两端夹紧。</summary>
        public static double RollCyclesPerSecond(double mbPerSecond)
        {
            double mb = IsUsable(mbPerSecond) ? Math.Max(0, mbPerSecond) : 0;
            double ratio = Math.Log10(1 + mb) / Math.Log10(RollFullScaleMbPerSecond + 1);
            double seconds = RollMaxSeconds - (RollMaxSeconds - RollMinSeconds) * ratio;
            if (seconds < RollMinSeconds) seconds = RollMinSeconds;
            if (seconds > RollMaxSeconds) seconds = RollMaxSeconds;
            return 1.0 / seconds;
        }

        /// <summary>这一段该取色阶的哪个位置（0..1）：静态相位与滚动相位按 releaseFloor 插值。</summary>
        public static double ColorPosition(int index, int total, double phaseCycles, double waveOpacity, double release)
        {
            if (total < 2) total = 2;
            double staticPosition = (double)index / (total - 1);
            double raw = index - phaseCycles * total;
            double wrapped = raw - Math.Floor(raw / total) * total;
            double rollPosition = wrapped / total;
            double floor = 1 - Clamp01(waveOpacity);
            double blend = 1 - Math.Max(Clamp01(release), floor);
            return staticPosition + (rollPosition - staticPosition) * blend;
        }

        private static double Clamp01(double value)
        {
            if (!IsUsable(value)) return 0;
            if (value < 0) return 0;
            return value > 1 ? 1 : value;
        }

        private static bool IsUsable(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
