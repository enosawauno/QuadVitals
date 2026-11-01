using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 显示相关的读法：当前显示模式（EnumDisplaySettings）、注册表里的 EDID、以及与 NVML 的显存查询。
    /// 这三块都要 P/Invoke 或注册表遍历，和 WMI 那层分开，任何一块失败只影响对应字段。
    /// </summary>
    internal sealed partial class HardwareReport
    {
        // ---- 显示器 EDID / 显示模式 ---------------------------------------------------

        /// <summary>
        /// DEVMODEW 的真实布局：dmBitsPerPel / dmPelsWidth / dmPelsHeight 是 DWORD 而不是 WORD，
        /// 打印机那一支是个 16 字节的 union。字段顺序写错会读到垃圾（实测：刷新率 544997536 Hz、
        /// 当前分辨率 1025×1025），所以这里按 Win32 头文件的顺序逐字段列出来。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion;
            public short DriverVersion;
            public short Size;
            public short DriverExtra;
            public int Fields;
            public short Orientation;
            public short PaperSize;
            public short PaperLength;
            public short PaperWidth;
            public short Scale;
            public short Copies;
            public short DefaultSource;
            public short PrintQuality;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TTOption;
            public short Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int BitsPerPel;
            public int PelsWidth;
            public int PelsHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int IcmMethod;
            public int IcmIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }

        private const int EnumCurrentSettings = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

        private static bool TryReadDisplayMode(out int width, out int height, out int bits, out int hertz)
        {
            width = 0;
            height = 0;
            bits = 0;
            hertz = 0;
            try
            {
                DevMode mode = new DevMode();
                mode.Size = (short)Marshal.SizeOf(typeof(DevMode));
                if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode)) return false;
                width = mode.PelsWidth;
                height = mode.PelsHeight;
                bits = mode.BitsPerPel;
                hertz = mode.DisplayFrequency;
                return width > 0 && height > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 从注册表读该显示器的 EDID（枚举不区分大小写、可能有多份副本，取第一份能解析的）。
        /// 只取首选时序的分辨率与制造周/年：刷新率一律用当前显示模式，不从 EDID 反推。
        /// </summary>
        private static bool TryReadEdid(string code, out int maxWidth, out int maxHeight,
                                        out int week, out int year, out string vendor)
        {
            maxWidth = 0;
            maxHeight = 0;
            week = 0;
            year = 0;
            vendor = string.Empty;
            if (string.IsNullOrEmpty(code)) return false;

            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                           @"SYSTEM\CurrentControlSet\Enum\DISPLAY"))
                {
                    if (root == null) return false;
                    using (RegistryKey vendorKey = root.OpenSubKey(code))
                    {
                        if (vendorKey == null) return false;
                        foreach (string instance in vendorKey.GetSubKeyNames())
                        {
                            using (RegistryKey parameters = vendorKey.OpenSubKey(
                                       instance + "\\Device Parameters"))
                            {
                                if (parameters == null) continue;
                                byte[] edid = parameters.GetValue("EDID") as byte[];
                                if (edid == null || edid.Length < 128) continue;
                                maxWidth = edid[0x36 + 2] | ((edid[0x36 + 4] & 0xF0) << 4);
                                maxHeight = edid[0x36 + 5] | ((edid[0x36 + 7] & 0xF0) << 4);
                                week = edid[16];
                                year = 1990 + edid[17];
                                vendor = ParseEdidVendor(edid);
                                return maxWidth > 0 && maxHeight > 0;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        private static string ParseEdidVendor(byte[] edid)
        {
            if (edid == null || edid.Length < 10) return string.Empty;
            int packed = (edid[8] << 8) | edid[9];
            char first = (char)(((packed >> 10) & 0x1F) + 'A' - 1);
            char second = (char)(((packed >> 5) & 0x1F) + 'A' - 1);
            char third = (char)((packed & 0x1F) + 'A' - 1);
            return new string(new[] { first, second, third });
        }

        // ---- NVML ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlMemory
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        private static extern int NvmlInit();

        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        private static extern int NvmlShutdown();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
        private static extern int NvmlDeviceGetCount(out uint count);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
        private static extern int NvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
        private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

        /// <summary>显卡名 → 显存总量（“16 GB（16376 MB）”）。没有 NVML 时返回 `--`。</summary>
        private static Dictionary<string, string> NvmlDevices()
        {
            Dictionary<string, string> devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (NvmlInit() != 0) return devices;
                try
                {
                    uint count;
                    if (NvmlDeviceGetCount(out count) != 0) return devices;
                    for (uint i = 0; i < count && i < 4; i++)
                    {
                        IntPtr handle;
                        if (NvmlDeviceGetHandleByIndex(i, out handle) != 0) continue;
                        StringBuilder name = new StringBuilder(96);
                        if (NvmlDeviceGetName(handle, name, (uint)name.Capacity) != 0) continue;
                        NvmlMemory memory;
                        if (NvmlDeviceGetMemoryInfo(handle, out memory) != 0 || memory.Total == 0) continue;
                        long megabytes = (long)(memory.Total / (1024L * 1024L));
                        devices[name.ToString()] = HardwareNaming.CapacityGiB((long)memory.Total) + "（" +
                                                   megabytes.ToString(CultureInfo.InvariantCulture) + " MB）";
                    }
                }
                finally
                {
                    NvmlShutdown();
                }
            }
            catch (Exception)
            {
                // 没有 nvml.dll / 调用失败：显存显示 --，其余字段照常
            }
            return devices;
        }

        private static string NvmlMemoryOf(Dictionary<string, string> devices, string adapterName)
        {
            if (string.IsNullOrEmpty(adapterName) || devices.Count == 0) return MetricFormat.Invalid;
            string value;
            if (devices.TryGetValue(adapterName.Trim(), out value)) return value;
            foreach (KeyValuePair<string, string> pair in devices)
            {
                if (adapterName.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pair.Key.IndexOf(adapterName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return pair.Value;
                }
            }
            return MetricFormat.Invalid;
        }

        // ---- 小工具 -----------------------------------------------------------------

        /// <summary>WMI 的磁盘序列号带一个尾点（“0025_38BB_…9330.”），比对与显示都先去掉。</summary>
        private static string TrimSerial(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return serial;
            return serial.Trim().TrimEnd('.');
        }

        private static Dictionary<string, object> PhysicalDiskFor(string model, string serial)
        {
            List<Dictionary<string, object>> disks = WmiObjects(
                "SELECT FriendlyName, SerialNumber, MediaType, BusType FROM MSFT_PhysicalDisk",
                "root\\Microsoft\\Windows\\Storage");
            foreach (Dictionary<string, object> disk in disks)
            {
                if (!string.IsNullOrEmpty(serial) &&
                    string.Equals(TrimSerial(Str(disk, "SerialNumber")), serial, StringComparison.OrdinalIgnoreCase))
                {
                    return disk;
                }
            }
            foreach (Dictionary<string, object> disk in disks)
            {
                string friendly = Str(disk, "FriendlyName");
                if (!string.IsNullOrEmpty(friendly) && !string.IsNullOrEmpty(model) &&
                    string.Equals(friendly.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return disk;
                }
            }
            return null;
        }

        private static string MediaName(int mediaType)
        {
            switch (mediaType)
            {
                case 3: return "HDD";
                case 4: return "SSD";
                case 5: return "SCM";
                default: return MetricFormat.Invalid;
            }
        }

        private static string BusName(int busType)
        {
            switch (busType)
            {
                case 7: return "USB";
                case 8: return "RAID";
                case 11: return "SATA";
                case 14: return "虚拟";
                case 17: return "NVMe";
                default: return busType > 0
                    ? "总线 " + busType.ToString(CultureInfo.InvariantCulture)
                    : MetricFormat.Invalid;
            }
        }

        private static string Str(Dictionary<string, object> row, string key)
        {
            object value;
            if (row == null || !row.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int ToInt(Dictionary<string, object> row, string key)
        {
            return ToInt(Str(row, key));
        }

        private static int ToInt(string text)
        {
            int value;
            if (string.IsNullOrEmpty(text)) return 0;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            double parsed;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return (int)parsed;
            return 0;
        }

        private static long ToLong(Dictionary<string, object> row, string key)
        {
            string text = Str(row, key);
            long value;
            if (string.IsNullOrEmpty(text)) return 0;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            double parsed;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return (long)parsed;
            return 0;
        }

        /// <summary>WMI 里的字节数组字符串（WmiMonitorID 的 Name/ID）→ 去掉 0 结尾的文本。</summary>
        private static string WideString(Dictionary<string, object> row, string key)
        {
            object value;
            if (row == null || !row.TryGetValue(key, out value) || value == null) return null;
            ushort[] codes = value as ushort[];
            if (codes != null)
            {
                StringBuilder text = new StringBuilder(codes.Length);
                foreach (ushort code in codes)
                {
                    if (code == 0) break;
                    text.Append((char)code);
                }
                return text.ToString().Trim();
            }
            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                StringBuilder text = new StringBuilder(bytes.Length);
                foreach (byte b in bytes)
                {
                    if (b == 0) break;
                    text.Append((char)b);
                }
                return text.ToString().Trim();
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>占位值（System Product Name 之类）一律视为未提供。</summary>
        private static string Text(string value)
        {
            return HardwareNaming.IsPlaceholder(value) ? MetricFormat.Invalid : value.Trim();
        }

        private static string Join(string separator, string first, string second)
        {
            string left = first ?? string.Empty;
            string right = second ?? string.Empty;
            bool leftEmpty = left.Length == 0 || left == MetricFormat.Invalid;
            bool rightEmpty = right.Length == 0 || right == MetricFormat.Invalid;
            if (leftEmpty && rightEmpty) return MetricFormat.Invalid;
            if (leftEmpty) return right;
            if (rightEmpty) return left;
            return left + separator + right;
        }
    }
}
