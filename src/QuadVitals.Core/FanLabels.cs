using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// What a fan channel is physically connected to. LHM 0.9.2 only exposes positional names
    /// ("Fan #2"), so this is the vocabulary the agent fills in from the board model and the
    /// dashboard turns into text.
    /// </summary>
    public static class FanKinds
    {
        public const int Unknown = 0;
        public const int CpuFan = 1;
        public const int CpuOpt = 2;
        public const int ChassisFan1 = 3;
        public const int ChassisFan2 = 4;
        public const int ChassisFan3 = 5;
        public const int ChassisFan4 = 6;
        public const int AioPump = 7;
        public const int WaterPump = 8;

        /// <summary>
        /// Pumps run near full speed by design, so the fan RPM bands say nothing about them.
        /// The card treats them as "running / stopped" instead of colouring a 3300 RPM pump red.
        /// </summary>
        public static bool IsPump(int kind)
        {
            return kind == AioPump || kind == WaterPump;
        }
    }

    /// <summary>
    /// 三档颜色带防抖：转速在临界点上下抖动时不跟着闪。
    ///
    /// 单纯比较阈值会让 1399 ↔ 1401 在绿/黄之间每帧翻一次；这里要求**越过边界 60 转以上**才换档，
    /// 所以临界抖动保持原档，真的要变快/变慢时才换。状态在卡片里，随设置重载重置。
    /// </summary>
    public sealed class FanToneLatch
    {
        /// <summary>How far past an edge the RPM must go before the band changes.</summary>
        public const int MarginRpm = 60;

        private MetricTone _tone = MetricTone.Normal;

        public MetricTone Tone { get { return _tone; } }

        /// <summary>空态、停转、换通道之后重新从绿色开始判，不沿用上一路的档位。</summary>
        public void Reset()
        {
            _tone = MetricTone.Normal;
        }

        public MetricTone Update(int rpm)
        {
            switch (_tone)
            {
                case MetricTone.Normal:
                    if (rpm >= FanZones.DangerRpm + MarginRpm) _tone = MetricTone.Danger;
                    else if (rpm >= FanZones.WarningRpm + MarginRpm) _tone = MetricTone.Warning;
                    break;

                case MetricTone.Warning:
                    if (rpm >= FanZones.DangerRpm + MarginRpm) _tone = MetricTone.Danger;
                    else if (rpm < FanZones.WarningRpm - MarginRpm) _tone = MetricTone.Normal;
                    break;

                case MetricTone.Danger:
                    if (rpm < FanZones.DangerRpm - MarginRpm)
                    {
                        _tone = rpm < FanZones.WarningRpm - MarginRpm ? MetricTone.Normal : MetricTone.Warning;
                    }
                    break;
            }
            return _tone;
        }
    }

    /// <summary>
    /// Board model → fan header names. The SuperIO channel order is vendor-specific and the
    /// library does not ship labels, so the only honest source is a per-board table. Unknown
    /// boards fall back to "通道 N": a wrong name is worse than a channel number.
    /// </summary>
    public static class FanLabels
    {
        /// <summary>Display name for one channel; <paramref name="channel"/> is used when kind is unknown.</summary>
        public static string For(int kind, int channel)
        {
            switch (kind)
            {
                case FanKinds.CpuFan: return "CPU_FAN";
                case FanKinds.CpuOpt: return "CPU_OPT";
                case FanKinds.ChassisFan1: return "CHA_FAN1";
                case FanKinds.ChassisFan2: return "CHA_FAN2";
                case FanKinds.ChassisFan3: return "CHA_FAN3";
                case FanKinds.ChassisFan4: return "CHA_FAN4";
                case FanKinds.AioPump: return "AIO_PUMP";
                case FanKinds.WaterPump: return "W_PUMP+";
                default: return "通道 " + channel;
            }
        }

        /// <summary>
        /// Channel order for this board. ASUS ROG STRIX Z690-E exposes seven Nuvoton NCT6798D
        /// channels: CPU_OPT, CPU_FAN, CHA_FAN1..4, AIO_PUMP. The two headers that actually
        /// report on the reference machine (a 1000 RPM radiator fan and a 3300 RPM pump) land on
        /// CPU_FAN and AIO_PUMP, which is what the wiring implies.
        /// </summary>
        public static int KindForBoard(string boardProduct, int channel)
        {
            if (string.IsNullOrEmpty(boardProduct)) return FanKinds.Unknown;
            if (boardProduct.IndexOf("Z690-E", StringComparison.OrdinalIgnoreCase) < 0) return FanKinds.Unknown;

            switch (channel)
            {
                case 1: return FanKinds.CpuOpt;
                case 2: return FanKinds.CpuFan;
                case 3: return FanKinds.ChassisFan1;
                case 4: return FanKinds.ChassisFan2;
                case 5: return FanKinds.ChassisFan3;
                case 6: return FanKinds.ChassisFan4;
                case 7: return FanKinds.AioPump;
                default: return FanKinds.Unknown;
            }
        }
    }
}
