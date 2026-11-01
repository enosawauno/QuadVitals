using System;
using System.Globalization;
using System.Text;

namespace QuadVitals.Core
{
    /// <summary>
    /// Turns raw SMBIOS / registry strings into the short hardware signatures the overview page
    /// prints under the CPU and memory tiles:
    ///
    ///   CPU    "Intel Core i9-13900K · 32 T"
    ///   Memory "G.Skill DDR5 4200 · 31 / 64 GB"
    ///
    /// Pure string work, no IO: the reading itself lives in the dashboard (see
    /// QuadVitals.Settings/HardwareInfo.cs) so the contract library stays testable and
    /// dependency-free.
    /// </summary>
    public static class HardwareNaming
    {
        /// <summary>
        /// Strips the vendor marketing noise from a CPU name: "13th Gen Intel(R) Core(TM)
        /// i9-13900K" becomes "Intel Core i9-13900K"; the clock suffix and "16-Core Processor"
        /// tail are dropped as well (the core count is printed separately as "N T").
        /// </summary>
        public static string CpuName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            string text = raw.Replace("(R)", " ").Replace("(TM)", " ")
                            .Replace("\u00AE", " ").Replace("\u2122", " ")
                            .Replace("CPU ", string.Empty);

            // "13th Gen Intel Core i9-13900K" / "12th Gen ..." / "11th Gen ..."
            text = StripGenerationPrefix(text);

            // "i9-13900K @ 3.00GHz", "7950X 16-Core Processor"
            int at = text.IndexOf('@');
            if (at > 0) text = text.Substring(0, at);
            text = StripCoreSuffix(text);

            text = CollapseSpaces(text);
            return text.Trim();
        }

        /// <summary>
        /// "Intel Core i9-13900K · 16核 · 32线程"。核心数在前，因为口头报的就是「16 核 32 线程」；
        /// 任一项缺失时只显示知道的部分。
        /// </summary>
        public static string CpuSignature(string rawName, int physicalCores, int logicalProcessors)
        {
            string name = CpuName(rawName);
            string counts = CoreCountLabel(physicalCores, logicalProcessors);
            if (name.Length == 0) return counts;
            if (counts.Length == 0) return name;
            return name + " · " + counts;
        }

        /// <summary>"16核 · 32线程" / "32线程" / "16核" / "" — whichever parts are known.</summary>
        public static string CoreCountLabel(int physicalCores, int logicalProcessors)
        {
            if (physicalCores > 0 && logicalProcessors > 0)
            {
                return physicalCores.ToString(CultureInfo.InvariantCulture) + "核 · " +
                       logicalProcessors.ToString(CultureInfo.InvariantCulture) + "线程";
            }
            if (logicalProcessors > 0)
            {
                return logicalProcessors.ToString(CultureInfo.InvariantCulture) + "线程";
            }
            if (physicalCores > 0)
            {
                return physicalCores.ToString(CultureInfo.InvariantCulture) + "核";
            }
            return string.Empty;
        }

        /// <summary>SMBIOS memory type (Win32_PhysicalMemory.SMBIOSMemoryType) to a DDR label.</summary>
        public static string MemoryTypeName(int smbiosType)
        {
            switch (smbiosType)
            {
                case 0x18: return "DDR3";     // 24
                case 0x1A: return "DDR4";     // 26
                case 0x1B: return "LPDDR";    // 27
                case 0x1C: return "LPDDR2";   // 28
                case 0x1D: return "LPDDR3";   // 29
                case 0x1E: return "LPDDR4";   // 30
                case 0x20: return "DDR2";     // 32
                case 0x22: return "DDR5";     // 34
                case 0x23: return "LPDDR5";   // 35
                default: return string.Empty;
            }
        }

        /// <summary>
        /// "G.Skill DDR5 4200": vendor, generation and speed, each dropped when unknown so the
        /// line never ends up with dangling separators.
        /// </summary>
        public static string MemoryLabel(string vendor, string partNumber, int smbiosType, int speedMhz)
        {
            StringBuilder builder = new StringBuilder();
            string brand = CleanVendor(vendor);
            if (brand.Length > 0) builder.Append(brand);

            string type = MemoryTypeName(smbiosType);
            if (type.Length > 0)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(type);
            }
            else if (builder.Length == 0 && !string.IsNullOrEmpty(partNumber))
            {
                // No usable SMBIOS type (common on VMs / locked firmware): the part number is
                // the only thing that identifies the sticks.
                builder.Append(CollapseSpaces(partNumber).Trim());
            }

            if (speedMhz > 0)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(speedMhz.ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        /// <summary>"G.Skill DDR5 4200 · 31 / 64 GB" (whole gigabytes).</summary>
        public static string MemorySignature(string vendor, string partNumber, int smbiosType, int speedMhz,
                                             long usedBytes, long totalBytes)
        {
            string label = MemoryLabel(vendor, partNumber, smbiosType, speedMhz);
            string capacity = MemoryPairWhole(usedBytes, totalBytes);
            bool hasCapacity = capacity != MetricFormat.Invalid;
            if (label.Length == 0) return hasCapacity ? capacity : MetricFormat.Invalid;
            if (!hasCapacity) return label;
            return label + " · " + capacity;
        }

        /// <summary>Whole-gigabyte used/total pair: "31 / 64 GB".</summary>
        public static string MemoryPairWhole(long usedBytes, long totalBytes)
        {
            if (usedBytes < 0 || totalBytes <= 0) return MetricFormat.Invalid;
            const double perGb = 1024.0 * 1024.0 * 1024.0;
            long used = (long)Math.Round(usedBytes / perGb, MidpointRounding.AwayFromZero);
            long total = (long)Math.Round(totalBytes / perGb, MidpointRounding.AwayFromZero);
            return used.ToString(CultureInfo.InvariantCulture) + " / " +
                   total.ToString(CultureInfo.InvariantCulture) + " GB";
        }

        // SMBIOS 厂商串带一堆法律后缀（真实机器上读到的是 "G Skill Intl"），
        // 页面上要的是牌子本身（图二写 "G.Skill"）。只裁后缀 + 少数别名，不改数据本身。
        private static readonly string[] VendorSuffixes =
        {
            " International", " Technology", " Semiconductor", " Co., Ltd.", " Intl", " Inc."
        };

        private static string CleanVendor(string vendor)
        {
            if (string.IsNullOrEmpty(vendor)) return string.Empty;
            string text = CollapseSpaces(vendor).Trim();

            foreach (string suffix in VendorSuffixes)
            {
                if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(0, text.Length - suffix.Length).Trim();
                    break;
                }
            }

            if (string.Equals(text, "G Skill", StringComparison.OrdinalIgnoreCase)) text = "G.Skill";
            // Firmware often reports placeholders instead of an empty string.
            switch (text.ToLowerInvariant())
            {
                case "unknown":
                case "undefined":
                case "not specified":
                case "n/a":
                case "to be filled by o.e.m.":
                case "default string":
                    return string.Empty;
            }
            if (text.Length > 24) text = text.Substring(0, 24).Trim();
            return text;
        }

        private static string StripGenerationPrefix(string text)
        {
            // "13th Gen ", "2nd Gen ", "1st Gen "
            int genEnd = text.IndexOf(" Gen ", StringComparison.Ordinal);
            if (genEnd > 0 && genEnd <= 4)
            {
                string prefix = text.Substring(0, genEnd);
                bool digitsOnly = prefix.Length > 0;
                for (int i = 0; i < prefix.Length; i++)
                {
                    char c = prefix[i];
                    if (!char.IsDigit(c) && c != 's' && c != 't' && c != 'r' && c != 'n' && c != 'd' && c != 'h')
                    {
                        digitsOnly = false;
                        break;
                    }
                }
                if (digitsOnly) text = text.Substring(genEnd + 5);
            }
            return text;
        }

        private static string StripCoreSuffix(string text)
        {
            // "7950X 16-Core Processor", "i5-12400 6-Core Processor"
            int core = text.IndexOf("-Core Processor", StringComparison.OrdinalIgnoreCase);
            if (core > 0)
            {
                int start = core;
                while (start > 0 && (char.IsDigit(text[start - 1]) || text[start - 1] == ' ')) start--;
                text = text.Substring(0, start);
            }
            const string processor = " Processor";
            if (text.EndsWith(processor, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - processor.Length);
            }
            return text;
        }

        private static string CollapseSpaces(string text)
        {
            StringBuilder builder = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            foreach (char c in text)
            {
                if (c == ' ')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        // ---- 硬件信息页（详见 docs/ui-design.md §6.6 与 files/待落地/硬件信息页设计报告.md） ----
        // 下面全部是纯字符串/数值处理，不读任何设备：采集在 Settings/HardwareReport.cs，
        // 这样断言能脱离 WMI 跑。

        /// <summary>
        /// 固件占位值判定：SMBIOS 写不进真值时给的是这一串固定字符串，直接显示等于把“未知”当成数据。
        /// 命中即视为未提供。
        /// </summary>
        public static bool IsPlaceholder(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            string text = value.Trim();
            if (text.Length == 0) return true;
            foreach (string known in PlaceholderValues)
            {
                if (string.Equals(text, known, StringComparison.OrdinalIgnoreCase)) return true;
            }
            // 全 0 的 GUID / 全 0 的十六进制串
            bool allZero = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '0' && c != '-' && c != ':' && c != ' ') { allZero = false; break; }
            }
            if (allZero) return true;
            // “0x00000000”
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                bool zeroHex = true;
                for (int i = 2; i < text.Length; i++) if (text[i] != '0') { zeroHex = false; break; }
                if (zeroHex) return true;
            }
            return false;
        }

        private static readonly string[] PlaceholderValues =
        {
            "to be filled by o.e.m.", "to be filled", "default string", "system product name",
            "system version", "system serial number", "not specified", "not available", "n/a",
            "none", "unknown", "undefined", "oem", "chassis serial number", "base board serial number"
        };

        /// <summary>内存/总容量：1024 进制，整数 GB（“64 GB”）。</summary>
        public static string CapacityGiB(long bytes)
        {
            if (bytes <= 0) return MetricFormat.Invalid;
            const double perGb = 1024.0 * 1024.0 * 1024.0;
            long gb = (long)Math.Round(bytes / perGb, MidpointRounding.AwayFromZero);
            return gb.ToString(CultureInfo.InvariantCulture) + " GB";
        }

        /// <summary>硬盘容量：按厂商标称的 10^9 计（“1000 GB”）。</summary>
        public static string CapacityDecimalGb(long bytes)
        {
            if (bytes <= 0) return MetricFormat.Invalid;
            long gb = (long)Math.Round(bytes / 1000000000.0, MidpointRounding.AwayFromZero);
            return gb.ToString(CultureInfo.InvariantCulture) + " GB";
        }

        /// <summary>“4200 MHz”；配置值与标称值不同时两个都写，相同的只写一个。</summary>
        public static string MemoryClock(int configuredMhz, int ratedMhz)
        {
            if (configuredMhz <= 0 && ratedMhz <= 0) return MetricFormat.Invalid;
            if (ratedMhz > 0 && configuredMhz > 0 && ratedMhz != configuredMhz)
            {
                return configuredMhz.ToString(CultureInfo.InvariantCulture) + " MHz（标称 " +
                       ratedMhz.ToString(CultureInfo.InvariantCulture) + " MHz）";
            }
            int value = configuredMhz > 0 ? configuredMhz : ratedMhz;
            return value.ToString(CultureInfo.InvariantCulture) + " MHz";
        }

        /// <summary>缓存：≥ 1 MB 时按整数 MB 写，否则按 KB。</summary>
        public static string CacheMb(int kilobytes)
        {
            if (kilobytes <= 0) return MetricFormat.Invalid;
            if (kilobytes >= 1024)
            {
                long mb = (long)Math.Round(kilobytes / 1024.0, MidpointRounding.AwayFromZero);
                return mb.ToString(CultureInfo.InvariantCulture) + " MB";
            }
            return kilobytes.ToString(CultureInfo.InvariantCulture) + " KB";
        }

        /// <summary>WMI 的 CurrentVoltage 以 0.1 V 为单位：14 → “1.40 V”，0 表示未知。</summary>
        public static string Voltage(int tenthsOfVolt)
        {
            if (tenthsOfVolt <= 0) return MetricFormat.Invalid;
            return (tenthsOfVolt / 10.0).ToString("0.00", CultureInfo.InvariantCulture) + " V";
        }

        /// <summary>“34.1 英寸（80 × 33 厘米）”：厘米来自 EDID / WmiMonitorBasicDisplayParams。</summary>
        public static string ScreenSize(int widthCm, int heightCm)
        {
            if (widthCm <= 0 || heightCm <= 0) return MetricFormat.Invalid;
            double diagonal = Math.Sqrt((double)widthCm * widthCm + (double)heightCm * heightCm) / 2.54;
            string inches = diagonal.ToString("0.0", CultureInfo.InvariantCulture);
            return inches + " 英寸（" + widthCm.ToString(CultureInfo.InvariantCulture) + " × " +
                   heightCm.ToString(CultureInfo.InvariantCulture) + " 厘米）";
        }

        /// <summary>“43:18（2.39:1）”：最简整数比 + 两位小数，避免“21:9”这类不准确的近似叫法。</summary>
        public static string AspectRatio(int width, int height)
        {
            if (width <= 0 || height <= 0) return MetricFormat.Invalid;
            int a = width;
            int b = height;
            while (b != 0)
            {
                int t = b;
                b = a % b;
                a = t;
            }
            int divisor = a <= 0 ? 1 : a;
            double ratio = (double)width / height;
            return (width / divisor).ToString(CultureInfo.InvariantCulture) + ":" +
                   (height / divisor).ToString(CultureInfo.InvariantCulture) + "（" +
                   ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1）";
        }

        /// <summary>只有日期：yyyy/MM/dd。</summary>
        public static string DateOnly(DateTime? value)
        {
            if (!value.HasValue) return MetricFormat.Invalid;
            return value.Value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        }

        /// <summary>日期 + 时分：yyyy/MM/dd HH:mm。</summary>
        public static string DateAndTime(DateTime? value)
        {
            if (!value.HasValue) return MetricFormat.Invalid;
            return value.Value.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>序列号掩码：首尾各留 4 位，中间用 **** 代替；足够短时只留首尾各 2 位。</summary>
        public static string MaskSerial(string value)
        {
            if (IsPlaceholder(value)) return MetricFormat.Invalid;
            string text = value.Trim();
            int keep = text.Length >= 12 ? 4 : 2;
            if (text.Length <= keep * 2) return "****";
            return text.Substring(0, keep) + "****" + text.Substring(text.Length - keep);
        }

        /// <summary>MAC 掩码：前 3 组与最后 1 组可见，中间两组用 * 代替。</summary>
        public static string MaskMac(string value)
        {
            if (string.IsNullOrEmpty(value)) return MetricFormat.Invalid;
            string text = value.Trim();
            string[] parts = text.Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 6) return text.Length > 8 ? text.Substring(0, 8) + "…" : text;
            return parts[0] + ":" + parts[1] + ":" + parts[2] + ":**:**:" + parts[5];
        }

        /// <summary>
        /// PCI 厂商 ID → 厂商名。比自己猜字符串可靠：同一块卡在 WMI 里的名字写法因驱动而异，
        /// 但 PNPDeviceID 里的 VEN_xxxx 是硬件给的。
        /// </summary>
        public static string PciVendorName(string pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) return string.Empty;
            int at = pnpDeviceId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || at + 8 > pnpDeviceId.Length) return string.Empty;
            string ven = pnpDeviceId.Substring(at + 4, 4).ToUpperInvariant();
            switch (ven)
            {
                case "10DE": return "NVIDIA";
                case "1002": return "AMD";
                case "8086": return "Intel";
                case "10EC": return "Realtek";
                case "14E4": return "Broadcom";
                case "15B3": return "Mellanox";
                case "1A03": return "ASPEED";
                case "1AF4": return "Red Hat";
                case "80EE": return "VirtualBox";
                case "1414": return "Microsoft";
                default: return string.Empty;
            }
        }

        /// <summary>显示器代号：“DISPLAY\DELA1A3\5&amp;…\Device Parameters” → “DELA1A3”。</summary>
        public static string MonitorCode(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return string.Empty;
            string[] parts = instanceName.Split('\\');
            if (parts.Length >= 2 && parts[0].StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
            {
                return parts[1];
            }
            return string.Empty;
        }
    }
}
