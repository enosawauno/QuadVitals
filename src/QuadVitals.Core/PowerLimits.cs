using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// 硬件 TDP 表：功耗卡的分段条按真实 TDP 做分母，而不是两路互相分摊。
    /// 只登记常见桌面型号；未登记型号回落到 125 W / 320 W，并在 tooltip 里写明分母。
    /// </summary>
    public static class PowerLimits
    {
        public const double DefaultCpuTdpWatts = 125.0;
        public const double DefaultGpuTdpWatts = 320.0;
        public const double DefaultCpuMaxWatts = 253.0;

        /// <summary>
        /// 按型号查表；查不到时返回默认值。表里没有的型号不代表它真是 125 W，
        /// 所以调用方还应该问一下 <see cref="CpuTdpKnown"/>，在界面上把“估算”标出来。
        /// </summary>
        public static double CpuTdp(string model)
        {
            double watts;
            return TryCpuTdp(model, out watts) ? watts : DefaultCpuTdpWatts;
        }

        /// <summary>该型号是否在表里（有确定值，而不是回落）。</summary>
        public static bool CpuTdpKnown(string model)
        {
            double watts;
            return TryCpuTdp(model, out watts);
        }

        private static bool TryCpuTdp(string model, out double watts)
        {
            watts = 0;
            if (string.IsNullOrEmpty(model)) return false;

            // Intel 13/14 代 K 系列与上代 i9：PBP 125 W。
            if (Has(model, "13900") || Has(model, "13700") || Has(model, "12900") || Has(model, "14900") ||
                Has(model, "14700") || Has(model, "13600") || Has(model, "14600"))
            {
                watts = 125.0;
                return true;
            }

            // 非 K 的桌面 i5：PBP 65 W。
            if (Has(model, "13400") || Has(model, "14400") || Has(model, "12400"))
            {
                watts = 65.0;
                return true;
            }

            // AMD AM5：7600/7700 105 W，X3D 120 W，7900X/7950X 170 W。
            if (Has(model, "7800X3D") || Has(model, "9800X3D"))
            {
                watts = 120.0;
                return true;
            }
            if (Has(model, "7950X") || Has(model, "7900X"))
            {
                watts = 170.0;
                return true;
            }
            if (Has(model, "7600") || Has(model, "7700"))
            {
                watts = 105.0;
                return true;
            }
            if (Has(model, "9600") || Has(model, "9700"))
            {
                watts = 65.0;
                return true;
            }
            if (Has(model, "9900X") || Has(model, "9950X"))
            {
                watts = 120.0;
                return true;
            }

            // AMD AM4：5600/5700 65 W，5800/5900/5950 105 W。
            if (Has(model, "5800") || Has(model, "5900") || Has(model, "5950"))
            {
                watts = 105.0;
                return true;
            }
            if (Has(model, "5600") || Has(model, "5700"))
            {
                watts = 65.0;
                return true;
            }

            return false;
        }

        private static bool Has(string model, string token)
        {
            return model.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Decode the low 15 bits of an Intel package power MSR (0x614 TDP, 0x610 PL1): the unit
        /// is 1/8 W. Zero or a nonsensical value (raw 0x7FFF is the "unlimited" encoding on some
        /// boards) returns 0 so the caller keeps its model-table fallback.
        /// </summary>
        public static double DecodePackageWatts(uint eax, uint edx)
        {
            ulong value = ((ulong)edx << 32) | eax;
            uint raw = (uint)(value & 0x7FFF);
            if (raw == 0) return 0;
            double watts = raw / 8.0;
            return watts >= 5.0 && watts <= 1000.0 ? watts : 0;
        }

        /// <summary>CPU 的最大持续功耗上限（13900K 为 PL2 253 W），用于分段条分母。</summary>
        public static double CpuMaxPower(string model)
        {
            if (string.IsNullOrEmpty(model)) return DefaultCpuMaxWatts;
            if (model.IndexOf("13900", StringComparison.OrdinalIgnoreCase) >= 0 ||
                model.IndexOf("13700", StringComparison.OrdinalIgnoreCase) >= 0 ||
                model.IndexOf("14900", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 253.0;
            }
            if (model.IndexOf("12900", StringComparison.OrdinalIgnoreCase) >= 0) return 241.0;
            if (model.IndexOf("7950X", StringComparison.OrdinalIgnoreCase) >= 0) return 230.0;
            return DefaultCpuMaxWatts;
        }

        public static double GpuTdp(string model)
        {
            if (string.IsNullOrEmpty(model)) return DefaultGpuTdpWatts;
            if (model.IndexOf("4090", StringComparison.OrdinalIgnoreCase) >= 0) return 450.0;
            if (model.IndexOf("4080", StringComparison.OrdinalIgnoreCase) >= 0) return 320.0;
            if (model.IndexOf("4070 Ti", StringComparison.OrdinalIgnoreCase) >= 0) return 285.0;
            if (model.IndexOf("4070", StringComparison.OrdinalIgnoreCase) >= 0) return 200.0;
            if (model.IndexOf("3090", StringComparison.OrdinalIgnoreCase) >= 0) return 350.0;
            if (model.IndexOf("3080", StringComparison.OrdinalIgnoreCase) >= 0) return 320.0;
            if (model.IndexOf("3070", StringComparison.OrdinalIgnoreCase) >= 0) return 220.0;
            return DefaultGpuTdpWatts;
        }
    }
}
