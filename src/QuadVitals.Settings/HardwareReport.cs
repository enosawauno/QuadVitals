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
    /// 只读采集本机硬件身份，产出给「硬件信息」弹窗用的一份不可变报告。
    ///
    /// 三条约束，都是从项目现有规则来的：
    /// 1. **不进 IPC**：这些是身份数据，不是指标，256/368 字节的快照不为它让路；Agent 也不参与。
    /// 2. **只读一次**：`Load()` 由后台线程调用，结果缓存到进程结束；只有「重新检测」会重跑。
    /// 3. **失败留空**：每条 WMI 查询独立 try/catch，某一段读不到只让那几个字段显示 `--`，
    ///    整页仍然渲染（WMI 服务被关掉时走空态，不崩溃）。
    ///
    /// 不做的部分（详见 files/待落地/硬件信息页设计报告.md）：内核驱动、SPD/SMBus、原始 SMBIOS
    /// 解析、SMART、CPUID 指令集。
    /// </summary>
    internal sealed partial class HardwareReport
    {
        /// <summary>WMI 单条查询的超时时间；超过就只让这条字段失败。</summary>
        private const int QueryTimeoutSeconds = 4;

        public bool IsLoaded { get; private set; }
        public DateTime LoadedUtc { get; private set; }
        public string Error { get; private set; }
        public IList<HwSection> Sections { get; private set; }

        private HardwareReport()
        {
            Sections = new List<HwSection>();
        }

        /// <summary>采集一次。任何异常都收进 <see cref="Error"/>，不向调用方抛。</summary>
        public static HardwareReport Load()
        {
            HardwareReport report = new HardwareReport();
            try
            {
                MetricsSnapshot snapshot = ReadSnapshot();

                report.Sections.Add(BuildOverview(snapshot));
                report.Sections.Add(BuildCpu(snapshot));
                report.Sections.Add(BuildBoard());
                report.Sections.Add(BuildMemory());
                report.Sections.Add(BuildGpu());
                report.Sections.Add(BuildDisks());
                report.Sections.Add(BuildMonitors());
                report.Sections.Add(BuildNetwork(snapshot));
                report.Sections.Add(BuildAudio());
            }
            catch (Exception ex)
            {
                // WMI 整体不可用（服务被禁用、权限被策略收回）：保留已经建好的段落。
                report.Error = "读取硬件信息失败：" + ex.GetType().Name;
            }

            report.IsLoaded = true;
            report.LoadedUtc = DateTime.UtcNow;
            return report;
        }

        /// <summary>「复制本页」用的纯文本：标签 + 值逐行，空值不再复制。</summary>
        public string PlainText()
        {
            StringBuilder text = new StringBuilder();
            foreach (HwSection section in Sections)
            {
                if (section.Blocks.Count == 0) continue;
                if (text.Length > 0) text.AppendLine();
                text.AppendLine("【" + section.Title + "】");
                foreach (HwBlock block in section.Blocks)
                {
                    if (!string.IsNullOrEmpty(block.Title)) text.AppendLine(block.Title);
                    foreach (HwField field in block.Fields)
                    {
                        text.AppendLine("  " + field.Label + "：" + field.Value);
                    }
                }
            }
            return text.ToString();
        }

        /// <summary>
        /// 导出成 Markdown：标题 + 生成信息 + 每个段落一张表。多实例段落（内存/硬盘/显示器/
        /// 网络/音频）用三级标题分开，这样一份报告既能直接读，也能贴进工单。
        /// </summary>
        public string ToMarkdown(DateTime localTime)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("# QuadVitals 硬件检测报告");
            text.AppendLine();
            text.AppendLine("- 生成时间：" + localTime.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture));
            text.AppendLine("- 软件版本：" + ProductVersion.SemVer);
            text.AppendLine("- 报告内容为只读采集，序列号与 MAC 已掩码");
            text.AppendLine();

            foreach (HwSection section in Sections)
            {
                if (section.Blocks.Count == 0) continue;
                text.AppendLine("## " + section.Title);
                text.AppendLine();
                foreach (HwBlock block in section.Blocks)
                {
                    if (!string.IsNullOrEmpty(block.Title))
                    {
                        text.AppendLine("### " + block.Title);
                        text.AppendLine();
                    }
                    text.AppendLine("| 字段 | 值 |");
                    text.AppendLine("| --- | --- |");
                    foreach (HwField field in block.Fields)
                    {
                        text.AppendLine("| " + EscapePipe(field.Label) + " | " + EscapePipe(field.Value) + " |");
                    }
                    text.AppendLine();
                }
            }
            return text.ToString();
        }

        private static string EscapePipe(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
        }

        // ---- 段落 -------------------------------------------------------------------

        private static HwSection BuildOverview(MetricsSnapshot snapshot)
        {
            HwSection section = new HwSection("overview", "概览");
            HwBlock block = section.AddBlock(null);

            string model = WmiValue("Win32_ComputerSystemProduct", "Name");
            block.Add("电脑型号", Text(model));

            string caption = WmiValue("Win32_OperatingSystem", "Caption");
            string arch = WmiValue("Win32_OperatingSystem", "OSArchitecture");
            block.Add("操作系统", Join(" · ", caption, arch));

            string cpuName = RegistryProcessorName();
            int cores = ToInt(WmiValue("Win32_Processor", "NumberOfCores"));
            int threads = ToInt(WmiValue("Win32_Processor", "NumberOfLogicalProcessors"));
            block.Add("CPU", HardwareNaming.CpuSignature(cpuName, cores, threads));

            block.Add("CPU 温度", Temperature(snapshot));

            string boardProduct = WmiValue("Win32_BaseBoard", "Product");
            string boardVendor = HardwareBrands.Display(WmiValue("Win32_BaseBoard", "Manufacturer"));
            block.Add("主板", Join(" ", boardVendor, Text(boardProduct)));

            block.Add("内存", OverviewMemory());
            block.Add("主硬盘", OverviewSystemDisk());
            block.Add("显卡", OverviewGpu());
            block.Add("显示器", OverviewDisplay());
            block.Add("声卡", FirstDeviceName("Win32_SoundDevice"));
            block.Add("网卡", OverviewNetwork(snapshot));

            return section;
        }

        private static HwSection BuildCpu(MetricsSnapshot snapshot)
        {
            HwSection section = new HwSection("cpu", "CPU");
            HwBlock block = section.AddBlock(null);

            block.Add("名称", Text(RegistryProcessorName()));
            block.Add("温度", Temperature(snapshot));
            block.Add("厂商", CpuVendor(RegistryProcessorVendor()));
            block.Add("核心", CoreLabel());
            block.Add("默认频率", Clock(WmiValue("Win32_Processor", "MaxClockSpeed")));
            block.Add("外频", Clock(WmiValue("Win32_Processor", "ExtClock")));
            block.Add("当前频率", Clock(WmiValue("Win32_Processor", "CurrentClockSpeed")));
            block.Add("二级缓存", HardwareNaming.CacheMb(ToInt(WmiValue("Win32_Processor", "L2CacheSize"))));
            block.Add("三级缓存", HardwareNaming.CacheMb(ToInt(WmiValue("Win32_Processor", "L3CacheSize"))));
            block.Add("电压", HardwareNaming.Voltage(ToInt(WmiValue("Win32_Processor", "CurrentVoltage"))));
            block.Add("处理器标识", Text(WmiValue("Win32_Processor", "ProcessorId")));
            block.Add("扩展版本", RegistryProcessorIdentifier());

            return section;
        }

        private static HwSection BuildBoard()
        {
            HwSection section = new HwSection("board", "主板");
            HwBlock block = section.AddBlock(null);

            string boardProduct = WmiValue("Win32_BaseBoard", "Product");
            string boardVendorRaw = WmiValue("Win32_BaseBoard", "Manufacturer");
            block.Add("主板名称", Join(" ", HardwareBrands.Display(boardVendorRaw), Text(boardProduct)));
            block.Add("主板厂商", Text(HardwareBrands.Display(boardVendorRaw)));
            block.Add("主板序列号", HardwareNaming.MaskSerial(WmiValue("Win32_BaseBoard", "SerialNumber")));

            string biosVendor = WmiValue("Win32_BIOS", "Manufacturer");
            block.Add("BIOS 厂商", Text(biosVendor));
            block.Add("BIOS 版本", Text(WmiValue("Win32_BIOS", "SMBIOSBIOSVersion")));
            block.Add("BIOS 发布日期", HardwareNaming.DateOnly(WmiDate("Win32_BIOS", "ReleaseDate")));

            string systemVendorRaw = WmiValue("Win32_ComputerSystemProduct", "Vendor");
            block.Add("电脑型号", Text(WmiValue("Win32_ComputerSystemProduct", "Name")));
            block.Add("电脑厂商", Text(HardwareBrands.Display(systemVendorRaw)));
            block.Add("系统安装日期", HardwareNaming.DateOnly(WmiDate("Win32_OperatingSystem", "InstallDate")));
            block.Add("最近启动时间", HardwareNaming.DateAndTime(WmiDate("Win32_OperatingSystem", "LastBootUpTime")));

            return section;
        }

        private static HwSection BuildMemory()
        {
            HwSection section = new HwSection("memory", "内存");
            List<Dictionary<string, object>> modules = WmiObjects(
                "SELECT Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, " +
                "DataWidth, SMBIOSMemoryType FROM Win32_PhysicalMemory");

            int index = 0;
            foreach (Dictionary<string, object> module in modules)
            {
                index++;
                string slot = Str(module, "DeviceLocator");
                HwBlock block = section.AddBlock(string.IsNullOrEmpty(slot)
                    ? "内存 " + index.ToString(CultureInfo.InvariantCulture)
                    : "内存 " + index.ToString(CultureInfo.InvariantCulture) + " · " + slot);

                string part = Str(module, "PartNumber");
                block.Add("名称", Text(part));
                block.Add("厂商", Text(HardwareBrands.Display(Str(module, "Manufacturer"))));
                block.Add("大小", HardwareNaming.CapacityGiB(ToLong(module, "Capacity")));
                block.Add("频率", HardwareNaming.MemoryClock(ToInt(module, "ConfiguredClockSpeed"), ToInt(module, "Speed")));
                block.Add("插槽", Text(slot));
                block.Add("数据宽度", BitWidth(ToInt(module, "DataWidth")));
                block.Add("类型", Text(HardwareNaming.MemoryTypeName(ToInt(module, "SMBIOSMemoryType"))));
            }

            if (section.Blocks.Count == 0) section.AddBlock(null);
            return section;
        }

        private static HwSection BuildGpu()
        {
            HwSection section = new HwSection("gpu", "显卡");
            List<Dictionary<string, object>> adapters = WmiObjects(
                "SELECT Name, DriverVersion, PNPDeviceID, VideoProcessor FROM Win32_VideoController");

            Dictionary<string, string> nvml = NvmlDevices();

            foreach (Dictionary<string, object> adapter in adapters)
            {
                string name = Str(adapter, "Name");
                string pnp = Str(adapter, "PNPDeviceID");
                if (IsVirtualDisplay(name, pnp)) continue;

                HwBlock block = section.AddBlock(string.IsNullOrEmpty(name) ? "显卡" : name);
                block.Add("显卡名称", Text(name));

                string vendor = HardwareNaming.PciVendorName(pnp);
                if (vendor.Length == 0) vendor = HardwareBrands.Display(PnpVendorHint(pnp));
                block.Add("厂商", Text(vendor));
                block.Add("大小", NvmlMemoryOf(nvml, name));
                block.Add("驱动版本", Text(Str(adapter, "DriverVersion")));
            }

            if (section.Blocks.Count == 0) section.AddBlock(null);

            HwBlock note = section.AddBlock("说明");
            note.Add("显存口径", "NVML 读取；WMI 的 AdapterRAM 是 32 位字段，超过 4 GB 会被截断，因此不使用。");
            return section;
        }

        private static HwSection BuildDisks()
        {
            HwSection section = new HwSection("disk", "硬盘");
            List<Dictionary<string, object>> disks = WmiObjects(
                "SELECT DeviceID, Model, SerialNumber, Size, Index, InterfaceType FROM Win32_DiskDrive");

            // 盘符顺序就是列表顺序：C → D → E。一块盘上挂多个分区时合成一个条目，
            // 标题里把字母都列出来，而块的位置由它最小的盘符（第一个分区）决定。
            List<KeyValuePair<string, int>> plainLetters = new List<KeyValuePair<string, int>>();
            foreach (DriveLetter letter in DriveLetters())
            {
                plainLetters.Add(new KeyValuePair<string, int>(letter.Name, letter.DiskIndex));
            }

            Dictionary<int, Dictionary<string, object>> byIndex = new Dictionary<int, Dictionary<string, object>>();
            foreach (Dictionary<string, object> disk in disks) byIndex[ToInt(disk, "Index")] = disk;

            // 有盘符的按字母排；没有盘符的（未格式化、恢复分区所在的整盘）排在最后，不静默丢掉。
            foreach (DiskOrdering.Group group in DiskOrdering.Order(plainLetters))
            {
                Dictionary<string, object> disk;
                if (!byIndex.TryGetValue(group.DiskIndex, out disk)) continue;
                AddDiskBlock(section, disk, string.Join("、", new List<string>(group.Letters).ToArray()));
                byIndex.Remove(group.DiskIndex);
            }

            List<int> remaining = new List<int>();
            foreach (int diskIndex in byIndex.Keys) remaining.Add(diskIndex);
            remaining.Sort();
            foreach (int diskIndex in remaining)
            {
                AddDiskBlock(section, byIndex[diskIndex], "无盘符");
            }

            if (section.Blocks.Count == 0) section.AddBlock(null);
            return section;
        }

        private static void AddDiskBlock(HwSection section, Dictionary<string, object> disk, string letters)
        {
            int position = section.Blocks.Count + 1;
            string model = Str(disk, "Model");
            HwBlock block = section.AddBlock("硬盘 " + position.ToString(CultureInfo.InvariantCulture) +
                                             " · " + letters +
                                             (string.IsNullOrEmpty(model) ? string.Empty : "  " + model));
            block.Add("盘符", letters);
            block.Add("名称", Text(model));
            block.Add("厂商", Text(HardwareBrands.DriveVendor(model)));
            block.Add("大小", HardwareNaming.CapacityDecimalGb(ToLong(disk, "Size")));
            block.Add("主硬盘", ToInt(disk, "Index") == SystemDiskIndex() ? "是" : "否");
            block.Add("序列号", HardwareNaming.MaskSerial(TrimSerial(Str(disk, "SerialNumber"))));

            // 介质与总线只有 MSFT_PhysicalDisk 给得出（Win32_DiskDrive 的 InterfaceType 对 NVMe
            // 也报 SCSI，没有信息量）。
            Dictionary<string, object> physical = PhysicalDiskFor(model, TrimSerial(Str(disk, "SerialNumber")));
            if (physical != null)
            {
                block.Add("介质", MediaName(ToInt(physical, "MediaType")));
                block.Add("总线", BusName(ToInt(physical, "BusType")));
            }
        }

        private static HwSection BuildMonitors()
        {
            HwSection section = new HwSection("monitor", "显示器");
            List<Dictionary<string, object>> ids = WmiObjects(
                "SELECT InstanceName, ManufacturerName, UserFriendlyName, YearOfManufacture, WeekOfManufacture " +
                "FROM WmiMonitorID", "root\\wmi");
            List<Dictionary<string, object>> sizes = WmiObjects(
                "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams",
                "root\\wmi");

            Dictionary<string, int[]> sizeByInstance = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> size in sizes)
            {
                sizeByInstance[Str(size, "InstanceName")] = new[]
                {
                    ToInt(size, "MaxHorizontalImageSize"), ToInt(size, "MaxVerticalImageSize")
                };
            }

            int index = 0;
            foreach (Dictionary<string, object> monitor in ids)
            {
                index++;
                string instance = Str(monitor, "InstanceName");
                string code = HardwareNaming.MonitorCode(instance);
                string name = WideString(monitor, "UserFriendlyName");
                string vendorCode = WideString(monitor, "ManufacturerName");

                HwBlock block = section.AddBlock("显示器 " + index.ToString(CultureInfo.InvariantCulture) +
                                                 (string.IsNullOrEmpty(name) ? string.Empty : " · " + name));
                block.Add("名称", Text(name));
                block.Add("厂商", Text(MonitorVendor(vendorCode)));
                block.Add("代号", Text(code));

                int[] size = null;
                if (instance != null) sizeByInstance.TryGetValue(instance, out size);
                block.Add("尺寸", size == null
                    ? MetricFormat.Invalid
                    : HardwareNaming.ScreenSize(size[0], size[1]));

                int maxWidth;
                int maxHeight;
                int edidWeek;
                int edidYear;
                string edidVendor;
                TryReadEdid(code, out maxWidth, out maxHeight, out edidWeek, out edidYear, out edidVendor);

                int week = ToInt(monitor, "WeekOfManufacture");
                if (week <= 0) week = edidWeek;
                int year = ToInt(monitor, "YearOfManufacture");
                if (year <= 0) year = edidYear;

                int modeWidth;
                int modeHeight;
                int bits;
                int hertz;
                TryReadDisplayMode(out modeWidth, out modeHeight, out bits, out hertz);

                if (modeWidth > 0 && modeHeight > 0)
                {
                    block.Add("屏幕比例", HardwareNaming.AspectRatio(modeWidth, modeHeight));
                }
                else
                {
                    block.Add("屏幕比例", HardwareNaming.AspectRatio(maxWidth, maxHeight));
                }
                block.Add("颜色位数", bits > 0 ? bits.ToString(CultureInfo.InvariantCulture) + " 位" : MetricFormat.Invalid);
                block.Add("刷新频率", hertz > 0 ? hertz.ToString(CultureInfo.InvariantCulture) + " Hz" : MetricFormat.Invalid);
                block.Add("最大分辨率", maxWidth > 0 && maxHeight > 0
                    ? maxWidth.ToString(CultureInfo.InvariantCulture) + " × " + maxHeight.ToString(CultureInfo.InvariantCulture)
                    : MetricFormat.Invalid);
                block.Add("当前分辨率", modeWidth > 0 && modeHeight > 0
                    ? modeWidth.ToString(CultureInfo.InvariantCulture) + " × " + modeHeight.ToString(CultureInfo.InvariantCulture)
                    : MetricFormat.Invalid);
                block.Add("制造日期", ManufactureDate(week, year));
            }

            if (section.Blocks.Count == 0) section.AddBlock(null);
            return section;
        }

        private static HwSection BuildNetwork(MetricsSnapshot snapshot)
        {
            HwSection section = new HwSection("network", "网络");
            List<Dictionary<string, object>> adapters = WmiObjects(
                "SELECT Name, Manufacturer, MACAddress, Speed, PNPDeviceID, NetConnectionID FROM Win32_NetworkAdapter " +
                "WHERE PhysicalAdapter = TRUE");

            string measuring = snapshot != null ? snapshot.AdapterName : null;

            foreach (Dictionary<string, object> adapter in adapters)
            {
                string name = Str(adapter, "Name");
                string mac = Str(adapter, "MACAddress");
                if (string.IsNullOrEmpty(mac)) continue;
                if (IsVirtualNetwork(name)) continue;

                HwBlock block = section.AddBlock(string.IsNullOrEmpty(name) ? "网卡" : name);
                block.Add("名称", Text(name));
                block.Add("厂商", Text(HardwareBrands.Display(Str(adapter, "Manufacturer"))));
                block.Add("MAC", HardwareNaming.MaskMac(mac));
                block.Add("速率", LinkSpeed(ToLong(adapter, "Speed")));
                // 快照里的 AdapterName 是 NetworkInterface.Name（中文系统上叫“以太网 2”），
                // 对应 WMI 的 NetConnectionID；拿它和 Name 比永远对不上。
                if (!string.IsNullOrEmpty(measuring) && IsMeasuring(adapter, measuring))
                {
                    block.Add("当前计量", "是");
                }
            }

            if (section.Blocks.Count == 0) section.AddBlock(null);
            return section;
        }

        private static HwSection BuildAudio()
        {
            HwSection section = new HwSection("audio", "音频");
            List<Dictionary<string, object>> devices = WmiObjects("SELECT Name FROM Win32_SoundDevice");

            foreach (Dictionary<string, object> device in devices)
            {
                string name = Str(device, "Name");
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                HwBlock block = section.AddBlock(name);
                block.Add("名称", name);
            }

            if (section.Blocks.Count == 0) section.AddBlock(null);
            return section;
        }

    }

    internal sealed class HwSection
    {
        public HwSection(string key, string title)
        {
            Key = key;
            Title = title;
            Blocks = new List<HwBlock>();
        }

        public string Key { get; private set; }
        public string Title { get; private set; }
        public IList<HwBlock> Blocks { get; private set; }

        public HwBlock AddBlock(string title)
        {
            HwBlock block = new HwBlock(title);
            Blocks.Add(block);
            return block;
        }
    }

    internal sealed class HwBlock
    {
        public HwBlock(string title)
        {
            Title = title;
            Fields = new List<HwField>();
        }

        public string Title { get; private set; }
        public IList<HwField> Fields { get; private set; }

        public void Add(string label, string value)
        {
            Fields.Add(new HwField(label, value));
        }
    }

    internal sealed class HwField
    {
        public HwField(string label, string value)
        {
            Label = label;
            Value = string.IsNullOrEmpty(value) ? MetricFormat.Invalid : value;
        }

        public string Label { get; private set; }
        public string Value { get; private set; }
        public bool HasValue { get { return Value != MetricFormat.Invalid; } }
    }
}
