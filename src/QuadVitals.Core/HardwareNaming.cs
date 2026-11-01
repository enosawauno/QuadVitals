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
    }
}
