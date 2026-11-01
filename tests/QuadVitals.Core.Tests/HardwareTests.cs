using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using QuadVitals.Core;

namespace QuadVitals.Core.Tests
{
    /// <summary>
    /// Core 断言拆成几个 partial 文件：单文件超过 1200 行会触发门禁警告、1800 行直接拦提交。
    /// 按主题分：本文件 = 入口 + 快照/格式化；Hardware = 硬件信息格式化与命名；Settings = 设置模型；
    /// Storage = 共享内存与反馈链路；Misc = 版本、指标目录、曲线与配色。
    /// </summary>
    internal static partial class Program
    {
        private static void HardwareNamingBehaviour()
        {
            const double gb = 1024.0 * 1024.0 * 1024.0;
            long used = (long)Math.Round(31.2 * gb);
            long total = (long)Math.Round(63.7 * gb);

            // 注册表里的原始串带 (R)/(TM) 和市场名，页面上只要型号本身。
            Equal("Intel Core i9-13900K", HardwareNaming.CpuName("13th Gen Intel(R) Core(TM) i9-13900K"),
                "CPU 名去掉代际与 (R)/(TM)");
            Equal("Intel Core i5-12400", HardwareNaming.CpuName("Intel(R) Core(TM) i5-12400 @ 2.50GHz"),
                "CPU 名去掉频率后缀");
            Equal("AMD Ryzen 9 7950X", HardwareNaming.CpuName("AMD Ryzen 9 7950X 16-Core Processor"),
                "CPU 名去掉核心数后缀");
            Equal(string.Empty, HardwareNaming.CpuName(null), "空 CPU 名不抛异常");

            Equal("Intel Core i9-13900K · 16核 · 32线程",
                HardwareNaming.CpuSignature("13th Gen Intel(R) Core(TM) i9-13900K", 16, 32),
                "CPU 签名 = 型号 + 核心在前线程在后");
            Equal("16核 · 32线程", HardwareNaming.CoreCountLabel(16, 32), "核心/线程标签");
            Equal("32线程", HardwareNaming.CoreCountLabel(0, 32), "只有线程数时只显示线程");
            Equal("16核", HardwareNaming.CoreCountLabel(16, 0), "只有核心数时只显示核心");
            Equal(string.Empty, HardwareNaming.CoreCountLabel(0, 0), "都不知道时为空");
            Equal("16核 · 32线程",
                HardwareNaming.CpuSignature(string.Empty, 16, 32), "型号未知时只显示核心/线程");

            Equal("DDR5", HardwareNaming.MemoryTypeName(0x22), "SMBIOS 34 = DDR5");
            Equal("DDR4", HardwareNaming.MemoryTypeName(0x1A), "SMBIOS 26 = DDR4");
            Equal(string.Empty, HardwareNaming.MemoryTypeName(0), "未知 SMBIOS 类型不编名字");

            Equal("G-Skill DDR5 4200", HardwareNaming.MemoryLabel("G-Skill", "F5-6000J3038F16G", 0x22, 4200),
                "内存标签 = 品牌 + 代际 + 频率");
            Equal("F5-6000J3038F16G", HardwareNaming.MemoryLabel("Unknown", "F5-6000J3038F16G", 0, 0),
                "没有厂商/类型时回退到型号");
            Equal(string.Empty, HardwareNaming.MemoryLabel("", "", 0, 0), "什么都没有就是空串");
            Equal("G.Skill DDR5 4200",
                HardwareNaming.MemoryLabel("G Skill Intl", "F5-6000J3038F16G", 0x22, 4200), "厂商后缀被裁掉");
            Equal("Kingston DDR5 4800",
                HardwareNaming.MemoryLabel("Kingston Technology", "KF548C38", 0x22, 4800), "Technology 后缀被裁掉");

            Equal("31 / 64 GB", HardwareNaming.MemoryPairWhole(used, total), "容量取整到整 GB");
            Equal("--", HardwareNaming.MemoryPairWhole(0, 0), "无容量数据时显示 --");

            Equal("G-Skill DDR5 4200 · 31 / 64 GB",
                HardwareNaming.MemorySignature("G-Skill", "F5", 0x22, 4200, used, total), "内存签名完整形态");
            Equal("31 / 64 GB", HardwareNaming.MemorySignature("", "", 0, 0, used, total),
                "无模块信息时只显示容量");
        }

        private static void HardwareInfoTextBehaviour()
        {
            // 占位值：固件写不进真值时给的是这几串固定字符串。
            True(HardwareNaming.IsPlaceholder("To Be Filled By O.E.M."), "占位符 To Be Filled 被认出来");
            True(HardwareNaming.IsPlaceholder("System Product Name"), "占位符 System Product Name 被认出来");
            True(HardwareNaming.IsPlaceholder("  system version  "), "占位符忽略首尾空白");
            True(HardwareNaming.IsPlaceholder("0x00000000"), "全 0 十六进制算未提供");
            True(HardwareNaming.IsPlaceholder("00000000-0000-0000-0000-000000000000"), "全 0 GUID 算未提供");
            True(HardwareNaming.IsPlaceholder(string.Empty), "空串算未提供");
            True(!HardwareNaming.IsPlaceholder("211093167500401"), "真实序列号不被误杀");
            True(!HardwareNaming.IsPlaceholder("ASUSTeK COMPUTER INC."), "真实厂商串不被误杀");

            Equal("64 GB", HardwareNaming.CapacityGiB(64L * 1024 * 1024 * 1024), "内存容量按 1024 进制");
            Equal("--", HardwareNaming.CapacityGiB(0), "无容量时显示 --");
            Equal("1000 GB", HardwareNaming.CapacityDecimalGb(1000202273280), "硬盘容量按厂商标称 10^9");

            Equal("4200 MHz", HardwareNaming.MemoryClock(4200, 4200), "频率相同时只写一个");
            Equal("4200 MHz（标称 6000 MHz）", HardwareNaming.MemoryClock(4200, 6000), "超频后两个都写");
            Equal("4800 MHz", HardwareNaming.MemoryClock(0, 4800), "只有标称值时用标称值");
            Equal("--", HardwareNaming.MemoryClock(0, 0), "无频率时显示 --");

            Equal("16 MB", HardwareNaming.CacheMb(16384), "二级缓存 KB 转 MB");
            Equal("36 MB", HardwareNaming.CacheMb(36864), "三级缓存 KB 转 MB");
            Equal("512 KB", HardwareNaming.CacheMb(512), "小于 1 MB 仍按 KB");
            Equal("--", HardwareNaming.CacheMb(0), "无缓存数据时显示 --");

            Equal("1.40 V", HardwareNaming.Voltage(14), "电压按 0.1 V 为单位换算");
            Equal("0.50 V", HardwareNaming.Voltage(5), "电压保留两位小数");
            Equal("--", HardwareNaming.Voltage(0), "CurrentVoltage=0 表示未知");

            Equal("34.1 英寸（80 × 33 厘米）", HardwareNaming.ScreenSize(80, 33), "对角线由厘米尺寸算出");
            Equal("--", HardwareNaming.ScreenSize(0, 33), "缺一个尺寸就不编数字");

            Equal("43:18（2.39:1）", HardwareNaming.AspectRatio(3440, 1440), "超宽屏比例按最简整数比");
            Equal("16:9（1.78:1）", HardwareNaming.AspectRatio(1920, 1080), "常见比例不分错");
            Equal("--", HardwareNaming.AspectRatio(0, 0), "无分辨率时显示 --");

            Equal("2026/06/17", HardwareNaming.DateOnly(new DateTime(2026, 6, 17)), "日期只留年月日");
            Equal("--", HardwareNaming.DateOnly(null), "无日期时显示 --");
            Equal("2026/06/17 17:05", HardwareNaming.DateAndTime(new DateTime(2026, 6, 17, 17, 5, 0)),
                "启动时间带时分");

            Equal("2110****0401", HardwareNaming.MaskSerial("211093167500401"), "长序列号首尾各留 4 位");
            Equal("****", HardwareNaming.MaskSerial("AB"), "过短的序列号只留掩码");
            Equal("--", HardwareNaming.MaskSerial("System Serial Number"), "占位序列号不显示");

            Equal("50:EB:F6:**:**:67", HardwareNaming.MaskMac("50:EB:F6:3E:E9:67"), "MAC 只留前 3 段与后 1 段");
            Equal("--", HardwareNaming.MaskMac(string.Empty), "无 MAC 时显示 --");

            Equal("NVIDIA", HardwareNaming.PciVendorName("PCI\\VEN_10DE&DEV_2704&SUBSYS_167A10DE"),
                "PCI 厂商 ID 10DE = NVIDIA");
            Equal("AMD", HardwareNaming.PciVendorName("PCI\\VEN_1002&DEV_73BF"), "1002 = AMD");
            Equal("Intel", HardwareNaming.PciVendorName("PCI\\VEN_8086&DEV_46A0"), "8086 = Intel");
            Equal(string.Empty, HardwareNaming.PciVendorName("PCI\\VEN_ABCD&DEV_0001"), "未知厂商 ID 不回猜");
            Equal(string.Empty, HardwareNaming.PciVendorName(null), "无 PNP ID 不抛异常");

            Equal("DELA1A3", HardwareNaming.MonitorCode("DISPLAY\\DELA1A3\\5&37a025e6&0&UID4352_0"),
                "显示器代号取实例名第二段");
            Equal(string.Empty, HardwareNaming.MonitorCode("USB\\VID_1234"), "非显示器实例名不误取");
        }

        private static void ReportNamingBehaviour()
        {
            DateTime day = new DateTime(2026, 11, 2, 3, 0, 0);
            Equal("20261102-硬件检测报告_QuadVitals", ReportNaming.BaseName(day), "默认名 = 日期 + 报告名");
            Equal(".md", ReportNaming.NormalizeExtension("MD"), "扩展名转成小写带点");
            Equal(".md", ReportNaming.NormalizeExtension(".Md"), "已经带点的不重复加");
            Equal(string.Empty, ReportNaming.NormalizeExtension(null), "空扩展名返回空串");

            string folder = Path.Combine(Path.GetTempPath(), "qv-report-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string first = ReportNaming.NextFileName(folder, day, ".md");
                Equal("20261102-硬件检测报告_QuadVitals.md", Path.GetFileName(first), "第一份不带序号");

                File.WriteAllText(first, "x");
                string second = ReportNaming.NextFileName(folder, day, ".md");
                Equal("20261102-硬件检测报告_QuadVitals-2.md", Path.GetFileName(second), "第二份编号 -2");

                File.WriteAllText(second, "x");
                Equal("20261102-硬件检测报告_QuadVitals-3.md",
                    Path.GetFileName(ReportNaming.NextFileName(folder, day, ".md")), "第三份编号 -3");

                // 删掉 -2：下一份补这个空位，而不是一路涨到 -4。
                File.Delete(second);
                Equal("20261102-硬件检测报告_QuadVitals-2.md",
                    Path.GetFileName(ReportNaming.NextFileName(folder, day, ".md")), "空位被补上");

                Equal("20261103-硬件检测报告_QuadVitals.md",
                    Path.GetFileName(ReportNaming.NextFileName(folder, day.AddDays(1), "md")),
                    "换一天重新从无序号开始（扩展名不带点也可以）");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
        }

        private static void DiskOrderingBehaviour()
        {
            // 乱序输入：块序 = 各自最小盘符的字母序，与输入顺序、物理盘号无关。
            List<KeyValuePair<string, int>> letters = new List<KeyValuePair<string, int>>();
            letters.Add(new KeyValuePair<string, int>("E:", 2));
            letters.Add(new KeyValuePair<string, int>("C:", 0));
            letters.Add(new KeyValuePair<string, int>("D:", 1));
            IList<DiskOrdering.Group> ordered = DiskOrdering.Order(letters);
            Equal(3, ordered.Count, "三块盘各成一组");
            Equal("C:", ordered[0].FirstLetter, "第一块按 C:");
            Equal("E:", ordered[2].FirstLetter, "最后一块按 E:");
            Equal(0, ordered[0].DiskIndex, "组里保留物理盘号");

            // 一块盘两个分区（C: 与 F:），另一块只有 D:：多分区那块按 C: 排到前面，不被 F: 影响。
            List<KeyValuePair<string, int>> mixed = new List<KeyValuePair<string, int>>();
            mixed.Add(new KeyValuePair<string, int>("F:", 7));
            mixed.Add(new KeyValuePair<string, int>("D:", 9));
            mixed.Add(new KeyValuePair<string, int>("C:", 7));
            IList<DiskOrdering.Group> byFirst = DiskOrdering.Order(mixed);
            Equal(2, byFirst.Count, "两块盘只出两组");
            Equal(7, byFirst[0].DiskIndex, "多分区那块按 C: 排第一");
            Equal("C:、F:", string.Join("、", new string[] { byFirst[0].Letters[0], byFirst[0].Letters[1] }),
                "组内字母升序、不按输入顺序");
            Equal("D:", byFirst[1].FirstLetter, "另一块按 D: 排第二");

            // 小写与缺冒号都归一化；重复项不重复计数。
            List<KeyValuePair<string, int>> messy = new List<KeyValuePair<string, int>>();
            messy.Add(new KeyValuePair<string, int>("c", 3));
            messy.Add(new KeyValuePair<string, int>("C:", 3));
            IList<DiskOrdering.Group> normalized = DiskOrdering.Order(messy);
            Equal(1, normalized.Count, "同一块盘的同一位盘符只算一次");
            Equal("C:", normalized[0].FirstLetter, "小写与缺冒号都收成 C:");

            Equal(0, DiskOrdering.Order(null).Count, "空输入返回空列表");
        }

        private static void HardwareBrandsBehaviour()
        {
            Equal("华硕", HardwareBrands.Display("ASUSTeK COMPUTER INC."), "SMBIOS 的法律后缀被收掉");
            Equal("技嘉", HardwareBrands.Display("GIGA-BYTE TECHNOLOGY CO., LTD."), "长关键字先命中");
            Equal("微星", HardwareBrands.Display("MICRO-STAR INTERNATIONAL"), "MSI 的 SMBIOS 名");
            Equal("芝奇", HardwareBrands.Display("G Skill Intl"), "内存厂商标识");
            Equal("Mystery Vendor", HardwareBrands.Display("Mystery Vendor"), "认不出时原样返回");
            Equal(string.Empty, HardwareBrands.UrlFor("Mystery Vendor"), "认不出时不给官网");
            True(HardwareBrands.UrlFor("ASUSTeK COMPUTER INC.").StartsWith("https://"), "认得时给官网");

            Equal("三星", HardwareBrands.DriveVendor("Samsung SSD 980 PRO 1TB"), "硬盘型号前缀 → 三星");
            Equal("西部数据", HardwareBrands.DriveVendor("WUS4BB076D7P3E3"), "西数的型号前缀");
            Equal("海康存储", HardwareBrands.DriveVendor("HS-SSD-C2000ECO 2048G"), "海康的型号前缀");
            Equal(HardwareBrands.Unrecognized, HardwareBrands.DriveVendor("Mystery Disk"),
                "认不出时显示未识别，不把型号当厂商");
        }
    }
}
