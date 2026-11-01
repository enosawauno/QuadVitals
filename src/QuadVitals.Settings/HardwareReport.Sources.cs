using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 采集底层（WMI / 注册表 / EDID / NVML / 小工具）与字段模型分开放在这个 partial 里：
    /// 段落构建在 HardwareReport.cs，这里只放"怎么读"和"怎么转"，两边都保持在 700 行以内。
    /// </summary>
    internal sealed partial class HardwareReport
    {
        // ---- 盘符 -------------------------------------------------------------------

        /// <summary>盘符到物理盘号的对应关系，按字母排序（C: 在最前）。</summary>
        internal sealed class DriveLetter
        {
            public string Name;
            public int DiskIndex;
        }

        private static List<DriveLetter> DriveLetters()
        {
            List<DriveLetter> letters = new List<DriveLetter>();

            // 分区 → 硬盘：Win32_DiskDriveToDiskPartition 的 Antecedent 是盘、Dependent 是分区。
            // 盘 ID 在关联路径里是转义过的（“\\.\PHYSICALDRIVE2”），而 Win32_DiskDrive.DeviceID
            // 是未转义的（“\\.\PHYSICALDRIVE2”），字符串比对必然对不上；
            // 所以只取 PHYSICALDRIVE 后面的数字，跟 Index 是同一个东西。
            Dictionary<string, int> indexByPartition = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> driveToPartition = WmiObjects(
                "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition");
            foreach (Dictionary<string, object> row in driveToPartition)
            {
                int index = PhysicalDriveNumber(WmiPathValue(Str(row, "Antecedent"), "DeviceID"));
                string partitionPath = WmiPathValue(Str(row, "Dependent"), "DeviceID");
                if (index < 0 || partitionPath == null) continue;
                indexByPartition[partitionPath] = index;
            }

            // 分区 → 盘符：Antecedent 是分区、Dependent 是逻辑盘。网络盘没有分区关联，天然被排除。
            List<Dictionary<string, object>> partitionToLogical = WmiObjects(
                "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");
            foreach (Dictionary<string, object> row in partitionToLogical)
            {
                string partitionPath = WmiPathValue(Str(row, "Antecedent"), "DeviceID");
                string letter = WmiPathValue(Str(row, "Dependent"), "DeviceID");
                if (partitionPath == null || string.IsNullOrEmpty(letter)) continue;
                int index;
                if (!indexByPartition.TryGetValue(partitionPath, out index)) continue;
                letters.Add(new DriveLetter { Name = letter.Trim(), DiskIndex = index });
            }

            letters.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return letters;
        }

        /// <summary>“PHYSICALDRIVE2” → 2；取不到返回 -1。</summary>
        private static int PhysicalDriveNumber(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return -1;
            const string marker = "PHYSICALDRIVE";
            int at = deviceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            int start = at + marker.Length;
            int end = start;
            while (end < deviceId.Length && char.IsDigit(deviceId[end])) end++;
            if (end == start) return -1;
            return ToInt(deviceId.Substring(start, end - start));
        }

        /// <summary>
        /// 从 CIM 关联路径里取出某个键的值：
        /// <c>HOST\root\cimv2:Win32_LogicalDisk.DeviceID="C:"</c> → <c>C:</c>。
        /// </summary>
        private static string WmiPathValue(string path, string key)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string marker = key + "=" + "\u0022";
            int start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += marker.Length;
            int end = path.IndexOf('\u0022', start);
            if (end <= start) return null;
            return path.Substring(start, end - start);
        }

        // ---- 采集底层 ---------------------------------------------------------------

        private static MetricsSnapshot ReadSnapshot()
        {
            try
            {
                using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen())
                {
                    if (reader == null) return null;
                    return reader.TryRead();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Temperature(MetricsSnapshot snapshot)
        {
            if (snapshot == null) return MetricFormat.Invalid;
            if (!snapshot.IsAlive(DateTime.UtcNow, MetricsSnapshot.AliveTimeout)) return MetricFormat.Invalid;
            if (!snapshot.Has(MetricFlags.TemperatureValid)) return MetricFormat.Invalid;
            return snapshot.TemperatureC.ToString("0.0", CultureInfo.InvariantCulture) + " ℃";
        }

        private static ManagementScope Scope(string ns)
        {
            ConnectionOptions options = new ConnectionOptions { Timeout = TimeSpan.FromSeconds(QueryTimeoutSeconds) };
            ManagementScope scope = new ManagementScope("\\\\localhost\\" + ns, options);
            scope.Connect();
            return scope;
        }

        private static List<Dictionary<string, object>> WmiObjects(string query, string ns = "root\\cimv2")
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(Scope(ns), new ObjectQuery(query)))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (PropertyData property in item.Properties)
                        {
                            row[property.Name] = property.Value;
                        }
                        rows.Add(row);
                        item.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                // 这一条查询失败：返回已经拿到的行，调用方按空值降级。
            }
            return rows;
        }

        private static string WmiValue(string className, string propertyName, string ns = "root\\cimv2")
        {
            List<Dictionary<string, object>> rows = WmiObjects(
                "SELECT " + propertyName + " FROM " + className, ns);
            if (rows.Count == 0) return null;
            object value = rows[0].ContainsKey(propertyName) ? rows[0][propertyName] : null;
            // 数值属性回来是 UInt16/UInt32（NumberOfCores、CurrentVoltage…），`as string` 会得到 null，
            // 于是整排 CPU 字段变成 `--`。统一走 Convert 转换。
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static DateTime? WmiDate(string className, string propertyName)
        {
            List<Dictionary<string, object>> rows = WmiObjects(
                "SELECT " + propertyName + " FROM " + className);
            if (rows.Count == 0) return null;
            object value;
            if (!rows[0].TryGetValue(propertyName, out value) || value == null) return null;
            if (value is DateTime) return (DateTime)value;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            try
            {
                // SELECT 单列时 System.Management 不把 CIM_DATETIME 转成 DateTime，而是原样给出
                // “20251128000000.000000+000”；这种格式 DateTime.TryParse 认不出来，要用官方转换器。
                // 转换器已经把串里的 UTC 偏移算进去了，再 ToLocalTime 会再加一次 8 小时
                // （实测：启动时间 10:06 被显示成 18:06，安装日期跨到第二天）。
                return ManagementDateTimeConverter.ToDateTime(text);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string RegistryProcessorName()
        {
            return RegistryProcessorValue("ProcessorNameString");
        }

        private static string RegistryProcessorVendor()
        {
            return RegistryProcessorValue("VendorIdentifier");
        }

        private static string RegistryProcessorIdentifier()
        {
            return RegistryProcessorValue("Identifier");
        }

        private static string RegistryProcessorValue(string name)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                           @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (key == null) return null;
                    return key.GetValue(name) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string CpuVendor(string vendorIdentifier)
        {
            if (string.IsNullOrEmpty(vendorIdentifier)) return MetricFormat.Invalid;
            if (vendorIdentifier.IndexOf("GenuineIntel", StringComparison.OrdinalIgnoreCase) >= 0) return "Intel";
            if (vendorIdentifier.IndexOf("AuthenticAMD", StringComparison.OrdinalIgnoreCase) >= 0) return "AMD";
            return vendorIdentifier.Trim();
        }

        private static string CoreLabel()
        {
            int cores = ToInt(WmiValue("Win32_Processor", "NumberOfCores"));
            int threads = ToInt(WmiValue("Win32_Processor", "NumberOfLogicalProcessors"));
            if (cores <= 0 && threads <= 0) return MetricFormat.Invalid;
            if (cores > 0 && threads > 0)
            {
                return cores.ToString(CultureInfo.InvariantCulture) + " 核 / " +
                       threads.ToString(CultureInfo.InvariantCulture) + " 线程";
            }
            return (cores > 0 ? cores : threads).ToString(CultureInfo.InvariantCulture) +
                   (cores > 0 ? " 核" : " 线程");
        }

        private static string Clock(string megahertz)
        {
            int value = ToInt(megahertz);
            return value > 0 ? value.ToString(CultureInfo.InvariantCulture) + " MHz" : MetricFormat.Invalid;
        }

        private static string BitWidth(int bits)
        {
            return bits > 0 ? bits.ToString(CultureInfo.InvariantCulture) + " 位" : MetricFormat.Invalid;
        }

        private static string OverviewMemory()
        {
            List<Dictionary<string, object>> modules = WmiObjects(
                "SELECT Capacity, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            long total = 0;
            int clock = 0;
            foreach (Dictionary<string, object> module in modules)
            {
                total += ToLong(module, "Capacity");
                int value = ToInt(module, "ConfiguredClockSpeed");
                if (value > clock) clock = value;
            }
            if (total <= 0) return MetricFormat.Invalid;
            string capacity = HardwareNaming.CapacityGiB(total);
            if (clock <= 0 || modules.Count == 0) return capacity;
            return capacity + " · " + clock.ToString(CultureInfo.InvariantCulture) + " MHz × " +
                   modules.Count.ToString(CultureInfo.InvariantCulture);
        }

        private static string OverviewSystemDisk()
        {
            int index = SystemDiskIndex();
            List<Dictionary<string, object>> disks = WmiObjects(
                "SELECT Model, Size, Index FROM Win32_DiskDrive");
            foreach (Dictionary<string, object> disk in disks)
            {
                if (ToInt(disk, "Index") != index) continue;
                string model = Str(disk, "Model");
                string vendor = HardwareBrands.DriveVendor(model);
                string size = HardwareNaming.CapacityDecimalGb(ToLong(disk, "Size"));
                if (!string.IsNullOrEmpty(vendor) && vendor != HardwareBrands.Unrecognized)
                {
                    return size + "（" + vendor + " " + Text(model) + "）";
                }
                return size + "（" + Text(model) + "）";
            }
            return MetricFormat.Invalid;
        }

        /// <summary>系统盘所在的物理盘号（Win32_DiskDrive.Index），判定不了返回 -1。</summary>
        private static int SystemDiskIndex()
        {
            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrEmpty(windows)) return -1;
                string letter = windows.Substring(0, 2);   // "C:"
                List<Dictionary<string, object>> partitions = WmiObjects(
                    "ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + letter +
                    "'} WHERE ResultClass=Win32_DiskPartition");
                foreach (Dictionary<string, object> partition in partitions)
                {
                    string id = Str(partition, "DeviceID");
                    if (string.IsNullOrEmpty(id)) continue;
                    List<Dictionary<string, object>> disks = WmiObjects(
                        "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + id +
                        "'} WHERE ResultClass=Win32_DiskDrive");
                    foreach (Dictionary<string, object> disk in disks)
                    {
                        return ToInt(disk, "Index");
                    }
                }
            }
            catch (Exception)
            {
                // 关联查询失败就退回 0 号盘
            }
            return 0;
        }

        private static string OverviewGpu()
        {
            List<Dictionary<string, object>> adapters = WmiObjects(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController");
            Dictionary<string, string> nvml = NvmlDevices();
            foreach (Dictionary<string, object> adapter in adapters)
            {
                string name = Str(adapter, "Name");
                if (IsVirtualDisplay(name, Str(adapter, "PNPDeviceID"))) continue;
                string memory = NvmlMemoryOf(nvml, name);
                return memory == MetricFormat.Invalid ? Text(name) : Text(name) + "（" + memory + "）";
            }
            return MetricFormat.Invalid;
        }

        private static string OverviewDisplay()
        {
            List<Dictionary<string, object>> ids = WmiObjects(
                "SELECT InstanceName, ManufacturerName, UserFriendlyName FROM WmiMonitorID", "root\\wmi");
            if (ids.Count == 0) return MetricFormat.Invalid;
            string name = WideString(ids[0], "UserFriendlyName");
            string vendor = MonitorVendor(WideString(ids[0], "ManufacturerName"));
            return Join(" ", vendor, Text(name));
        }

        private static string OverviewNetwork(MetricsSnapshot snapshot)
        {
            string measuring = snapshot != null ? snapshot.AdapterName : null;
            if (string.IsNullOrEmpty(measuring)) return MetricFormat.Invalid;
            // 快照给的是连接名（“以太网 2”），页面上要的是网卡型号；用 NetConnectionID 对上之后
            // 显示型号，对不上就退回连接名。
            List<Dictionary<string, object>> adapters = WmiObjects(
                "SELECT Name, NetConnectionID, MACAddress FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
            foreach (Dictionary<string, object> adapter in adapters)
            {
                if (!IsMeasuring(adapter, measuring)) continue;
                string name = Str(adapter, "Name");
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return measuring;
        }

        private static bool IsMeasuring(Dictionary<string, object> adapter, string measuring)
        {
            string connection = Str(adapter, "NetConnectionID");
            if (!string.IsNullOrEmpty(connection) &&
                string.Equals(connection, measuring, StringComparison.OrdinalIgnoreCase)) return true;
            string name = Str(adapter, "Name");
            return !string.IsNullOrEmpty(name) &&
                   name.IndexOf(measuring, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FirstDeviceName(string className)
        {
            List<Dictionary<string, object>> rows = WmiObjects("SELECT Name FROM " + className);
            foreach (Dictionary<string, object> row in rows)
            {
                string name = Str(row, "Name");
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return MetricFormat.Invalid;
        }

        private static string MonitorVendor(string threeLetterCode)
        {
            if (string.IsNullOrEmpty(threeLetterCode)) return MetricFormat.Invalid;
            switch (threeLetterCode.ToUpperInvariant())
            {
                case "DEL": return "戴尔";
                case "SAM": return "三星";
                case "GSM": return "LG";
                case "AUS": return "华硕";
                case "ACR": return "宏碁";
                case "BNQ": return "明基";
                case "PHL": return "飞利浦";
                case "HWP": return "惠普";
                case "LEN": return "联想";
                case "MSI": return "微星";
                case "AOC": return "AOC";
                case "IVM": return "iiyama";
                case "VSC": return "优派";
                default: return threeLetterCode.Trim();
            }
        }

        private static bool IsVirtualDisplay(string name, string pnpDeviceId)
        {
            if (!string.IsNullOrEmpty(pnpDeviceId) &&
                pnpDeviceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrEmpty(name)) return true;
            string text = name.ToLowerInvariant();
            return text.Contains("todesk") || text.Contains("virtual") || text.Contains("remote") ||
                   text.Contains("basic display");
        }

        private static bool IsVirtualNetwork(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string text = name.ToLowerInvariant();
            return text.Contains("vmware") || text.Contains("virtualbox") || text.Contains("vethernet") ||
                   text.Contains("wintun") || text.Contains("wireguard") || text.Contains("loopback") ||
                   text.Contains("vpn") || text.Contains("tap-") || text.Contains("tunnel");
        }

        private static string PnpVendorHint(string pnpDeviceId)
        {
            // 认不出 VEN_ 时至少让品牌表有机会命中原串（有些驱动把厂商写进 instance id）。
            return pnpDeviceId ?? string.Empty;
        }

        private static string LinkSpeed(long bitsPerSecond)
        {
            if (bitsPerSecond <= 0) return MetricFormat.Invalid;
            if (bitsPerSecond > 100L * 1000 * 1000 * 1000) return MetricFormat.Invalid;  // Int64.MaxValue = 未知
            double mbps = bitsPerSecond / 1000000.0;
            if (mbps >= 1000) return (mbps / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " Gbps";
            return mbps.ToString("0", CultureInfo.InvariantCulture) + " Mbps";
        }

        private static string ManufactureDate(int week, int year)
        {
            if (year <= 0) return MetricFormat.Invalid;
            if (week <= 0 || week > 53)
            {
                return year.ToString(CultureInfo.InvariantCulture) + " 年";
            }
            int month = (week + 3) / 4;
            if (month < 1) month = 1;
            if (month > 12) month = 12;
            return year.ToString(CultureInfo.InvariantCulture) + "/" +
                   month.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
