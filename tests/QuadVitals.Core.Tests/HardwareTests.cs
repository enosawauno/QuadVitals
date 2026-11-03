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
    /// 按主题分：本文件 = 入口 + 快照/格式化；Hardware = 硬件命名；Settings = 设置模型；
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
        }

    }
}
