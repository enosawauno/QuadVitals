using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace QuadVitals.Core
{
    /// <summary>
    /// 概览四格各自显示哪个指标。界面上是设置弹窗里的四个下拉（CPU / 内存 / 显卡 / 硬盘），
    /// 每个硬件列出它能显示的指标；不在列表里的值一律回退到占用 —— “占用”是四个硬件都有的那一项，
    /// 所以旧配置、手改坏的配置和将来删掉的选项都不会把格子变成空白。
    /// </summary>
    [DataContract]
    public sealed class DeviceMetricChoices
    {
        public const string Usage = "usage";
        public const string Temperature = "temperature";
        public const string Frequency = "frequency";
        public const string Power = "power";
        public const string Used = "used";
        public const string Free = "free";
        public const string Memory = "memory";
        public const string Io = "io";
        public const string Unavailable = "unavailable";

        [DataMember(Name = "cpu")] public string Cpu { get; set; }
        [DataMember(Name = "memory")] public string MemoryUsed { get; set; }
        [DataMember(Name = "gpu")] public string Gpu { get; set; }
        [DataMember(Name = "disk")] public string Disk { get; set; }

        public DeviceMetricChoices()
        {
            Cpu = Usage;
            MemoryUsed = Usage;
            Gpu = Usage;
            Disk = Usage;
        }

        /// <summary>每个硬件能选的指标，顺序即下拉里的顺序。</summary>
        public static readonly string[] CpuOptions = { Usage, Temperature, Frequency, Power };
        public static readonly string[] MemoryOptions = { Usage, Used };
        public static readonly string[] GpuOptions = { Usage, Memory, Temperature, Power, Frequency };
        public static readonly string[] DiskOptions = { Usage, Free, Io };

        public void Normalize()
        {
            Cpu = Keep(Cpu, CpuOptions);
            MemoryUsed = Keep(MemoryUsed, MemoryOptions);
            Gpu = Keep(Gpu, GpuOptions);
            Disk = Keep(Disk, DiskOptions);
        }

        /// <summary>把一项拉回合法集合；空值、未知值、大小写不一致都归到占用。</summary>
        public static string Keep(string value, string[] options)
        {
            if (string.IsNullOrEmpty(value)) return Usage;
            foreach (string option in options)
            {
                if (string.Equals(option, value, StringComparison.OrdinalIgnoreCase)) return option;
            }
            return Usage;
        }

        /// <summary>下拉里用的中文名；未知 key 返回占用。</summary>
        public static string DisplayName(string metric)
        {
            switch (metric)
            {
                case Temperature: return "温度";
                case Frequency: return "频率";
                case Power: return "功耗";
                case Used: return "已用";
                case Free: return "可用";
                case Memory: return "显存";
                case Io: return "读写";
                default: return "占用";
            }
        }

        public static IList<string> OptionsFor(string hardware)
        {
            switch (hardware)
            {
                case "cpu": return CpuOptions;
                case "memory": return MemoryOptions;
                case "gpu": return GpuOptions;
                case "disk": return DiskOptions;
                default: return CpuOptions;
            }
        }
    }
}
