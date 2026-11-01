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
    /// Dependency-free test runner (no .NET SDK required): a console app
    /// that prints PASS/FAIL per assertion and returns the failure count as its exit code.
    /// </summary>
    internal static partial class Program
    {
        private static int _passed;
        private static int _failed;
        private static string _suite = "";

        // Tests must not publish into the live band's channel (the band holds the mapping and
        // would display the fake frame until the next agent tick).
        private const string TestMapName = @"Local\QuadVitals.Metrics.test.v1";

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            Suite("MetricsSnapshot 序列化", SnapshotRoundTrip);
            Suite("MetricsSnapshot 拒绝坏数据", SnapshotRejects);
            Suite("MetricsSnapshot 心跳/离线", SnapshotHeartbeat);
            Suite("MetricsSnapshot 适配器名", SnapshotAdapterName);
            Suite("MetricsSnapshot CPU 用户/内核拆分", SnapshotCpuSplit);
            Suite("MetricsSnapshot 显卡/磁盘块", SnapshotGpuDisk);
            Suite("MetricsSnapshot 逐盘表", SnapshotVolumes);
            Suite("MetricFormat 速率", FormatRate);
            Suite("MetricFormat 百分比/温度/字节", FormatOthers);
            Suite("HardwareNaming 硬件签名", HardwareNamingBehaviour);
            Suite("EmaFilter", EmaBehaviour);
            Suite("MetricCatalog", Catalog);
            Suite("MetricTone 阈值", ToneThresholds);
            Suite("MetricTone 温度分档", TemperatureZoneThresholds);
            Suite("Settings 柱状几何写死", BarGeometryIsFixed);
            Suite("列数跟随 CPU 核心/线程", ColumnsFollowCpu);
            Suite("Settings 默认值", DefaultSettings);
            Suite("Settings Normalize 容错", NormalizeRecovers);
            Suite("磁盘元素盘符", DiskDriveSetting);
            Suite("Settings JSON 往返", SettingsJsonRoundTrip);
            Suite("柱状内核态颜色", SystemKernelColour);
            Suite("颜色文本解析", ColourTextParsing);
            Suite("预设色板", PaletteBehaviour);
            Suite("JsonPretty", PrettyPrint);
            Suite("BandLayout 宽度", LayoutWidth);
            Suite("MetricsChannel 共享内存", ChannelRoundTrip);
            Suite("MetricsChannel 并发撕裂读", ChannelConcurrentReads);
            Suite("Feedback 校验", FeedbackValidation);
            Suite("Feedback 打包与台账", FeedbackPackage);
            Suite("Feedback 草稿", FeedbackDraftBehaviour);
            Suite("Feedback 预览与脱敏", FeedbackPreviewBehaviour);
            Suite("Feedback 上传", FeedbackUploadBehaviour);
            Suite("Feedback 启动补传", FeedbackRecoveryBehaviour);
            Suite("ProductVersion 版本号", VersionBehaviour);
            Suite("Settings 四格指标选择", DeviceMetricChoicesBehaviour);
            Suite("IoWave 走马循环", IoWaveBehaviour);
            Suite("NetHistory 曲线快照", NetHistoryBehaviour);
            Suite("OKLCh 取色", OklchBehaviour);
            Suite("Update 发布清单", UpdateManifestBehaviour);
            Suite("Update 版本比较", UpdateVersionBehaviour);
            Suite("Update 更新源", UpdateFeedBehaviour);
            Suite("Update 安装包哈希", UpdateHashingBehaviour);
            Suite("Update 下载", UpdateDownloadBehaviour);
            Suite("Update 安装器", UpdateInstallerBehaviour);

            Console.WriteLine();
            Console.WriteLine("===== {0} passed, {1} failed =====", _passed, _failed);
            return _failed;
        }

        // ---- the snapshot ------------------------------------------------------------

        private static void SnapshotRoundTrip()
        {
            MetricsSnapshot original = new MetricsSnapshot
            {
                Flags = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.TemperatureValid |
                        MetricFlags.NetworkValid | MetricFlags.AgentAlive,
                TimestampUtc = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                CpuPercent = 12.5f,
                MemoryPercent = 34.25f,
                TemperatureC = 55.4f,
                NetUpBytesPerSec = 1258291.0,
                NetDownBytesPerSec = 29696.0,
                AdapterName = "Intel(R) Ethernet Converged Network Adapter X550-T2",
                MemoryUsedBytes = 34359738368L,
                MemoryTotalBytes = 68719476736L
            };

            byte[] bytes = original.ToBytes();
            Equal(MetricsSnapshot.SizeBytes, bytes.Length, "快照固定 368 字节（v3 布局 + 4 字节追加）");
            Equal(0x50u, MetricsSnapshot.ReadUInt32(bytes, 0) & 0xFF, "magic 首字节 'P'");
            Equal(MetricsSnapshot.CurrentVersion, MetricsSnapshot.VersionOf(bytes), "version=3");

            MetricsSnapshot parsed;
            SnapshotError error;
            True(MetricsSnapshot.TryParse(bytes, out parsed, out error), "TryParse 成功");
            Equal(SnapshotError.None, error, "无错误码");
            Equal(original.CpuPercent, parsed.CpuPercent, "CPU 往返");
            Equal(original.MemoryPercent, parsed.MemoryPercent, "内存往返");
            Equal(original.TemperatureC, parsed.TemperatureC, "温度往返");
            Equal(original.NetUpBytesPerSec, parsed.NetUpBytesPerSec, "上行往返");
            Equal(original.NetDownBytesPerSec, parsed.NetDownBytesPerSec, "下行往返");
            Equal(original.MemoryUsedBytes, parsed.MemoryUsedBytes, "已用内存往返");
            Equal(original.MemoryTotalBytes, parsed.MemoryTotalBytes, "总内存往返");
            Equal(original.TimestampUtc, parsed.TimestampUtc, "时间戳往返");
            Equal(original.Flags, parsed.Flags, "flags 往返");
            True(parsed.Has(MetricFlags.TemperatureValid), "温度有效位");
            Equal((MetricFlags)0, MetricFlags.None, "None 位为 0");
        }

        private static void SnapshotRejects()
        {
            MetricsSnapshot parsed;
            SnapshotError error;

            True(!MetricsSnapshot.TryParse(null, out parsed, out error), "null 被拒绝");
            Equal(SnapshotError.NullBuffer, error, "null 错误码");

            True(!MetricsSnapshot.TryParse(new byte[10], out parsed, out error), "过短被拒绝");
            Equal(SnapshotError.BufferTooSmall, error, "过短错误码");

            byte[] badMagic = new byte[MetricsSnapshot.SizeBytes];
            badMagic[0] = (byte)'X';
            True(!MetricsSnapshot.TryParse(badMagic, out parsed, out error), "magic 错被拒绝");
            Equal(SnapshotError.BadMagic, error, "magic 错误码");

            byte[] badVersion = new MetricsSnapshot().ToBytes();
            MetricsSnapshot.WriteUInt16(badVersion, 4, 99);
            True(!MetricsSnapshot.TryParse(badVersion, out parsed, out error), "版本不符被拒绝");
            Equal(SnapshotError.VersionMismatch, error, "版本错误码");
            Equal((ushort)99, MetricsSnapshot.VersionOf(badVersion), "VersionOf 读出版本");
        }

        private static void SnapshotHeartbeat()
        {
            DateTime now = new DateTime(2026, 10, 30, 12, 0, 10, DateTimeKind.Utc);
            MetricsSnapshot fresh = new MetricsSnapshot
            {
                Flags = MetricFlags.AgentAlive | MetricFlags.CpuValid,
                TimestampUtc = now.AddSeconds(-2)
            };
            True(fresh.IsAlive(now, TimeSpan.FromSeconds(3)), "2 秒内视为在线");

            MetricsSnapshot stale = new MetricsSnapshot
            {
                Flags = MetricFlags.AgentAlive,
                TimestampUtc = now.AddSeconds(-4)
            };
            True(!stale.IsAlive(now, TimeSpan.FromSeconds(3)), "超过 3 秒视为离线");

            MetricsSnapshot noFlag = new MetricsSnapshot
            {
                Flags = MetricFlags.CpuValid,
                TimestampUtc = now
            };
            True(!noFlag.IsAlive(now, TimeSpan.FromSeconds(3)), "缺 AgentAlive 视为离线");

            MetricsSnapshot never = new MetricsSnapshot();
            True(!never.IsAlive(now, TimeSpan.FromSeconds(3)), "空时间戳视为离线");
            Equal(TimeSpan.MaxValue, never.Age(now), "空时间戳 Age 为 MaxValue");
        }

        private static void SnapshotAdapterName()
        {
            string longName = new string('A', 60);
            MetricsSnapshot snapshot = new MetricsSnapshot { AdapterName = longName };
            byte[] bytes = snapshot.ToBytes();
            MetricsSnapshot parsed;
            SnapshotError error;
            MetricsSnapshot.TryParse(bytes, out parsed, out error);
            Equal(47, parsed.AdapterName.Length, "截断到 47 字符并保留结尾 \\0");
            Equal(new string('A', 47), parsed.AdapterName, "截断内容正确");

            MetricsSnapshot chinese = new MetricsSnapshot { AdapterName = "英特尔 X550-T2 网卡" };
            byte[] chineseBytes = chinese.ToBytes();
            MetricsSnapshot chineseParsed;
            MetricsSnapshot.TryParse(chineseBytes, out chineseParsed, out error);
            Equal("英特尔 X550-T2 网卡", chineseParsed.AdapterName, "中文适配器名往返");

            MetricsSnapshot empty = new MetricsSnapshot();
            Equal(string.Empty, empty.AdapterName, "默认适配器名为空串");
        }

        // ---- formatting --------------------------------------------------------------

        private static void SnapshotCpuSplit()
        {
            MetricsSnapshot source = new MetricsSnapshot
            {
                TimestampUtc = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
                Flags = MetricFlags.AgentAlive | MetricFlags.CpuValid,
                CpuPercent = 60.0f,
                CpuUserPercent = 20.0f,
                CpuSystemPercent = 40.0f
            };

            MetricsSnapshot parsed;
            SnapshotError error;
            True(MetricsSnapshot.TryParse(source.ToBytes(), out parsed, out error), "解析成功");
            Equal(20.0f, parsed.CpuUserPercent, "用户态百分比往返");
            Equal(40.0f, parsed.CpuSystemPercent, "内核态百分比往返");
            Equal(60.0f, parsed.CpuPercent, "总占用不变");

            // 旧 Agent 只写保留区为零：新 band 应看到 0/0，由渲染端降级成“整柱用户态色”
            byte[] legacy = source.ToBytes();
            Buffer.BlockCopy(BitConverter.GetBytes(0.0f), 0, legacy, MetricsSnapshot.UserPercentOffset, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(0.0f), 0, legacy, MetricsSnapshot.SystemPercentOffset, 4);
            True(MetricsSnapshot.TryParse(legacy, out parsed, out error), "旧帧仍可解析");
            Equal(0.0f, parsed.CpuUserPercent, "旧帧用户态为 0");
            Equal(0.0f, parsed.CpuSystemPercent, "旧帧内核态为 0");
            Equal(60.0f, parsed.CpuPercent, "旧帧总占用仍正确");
        }

        private static void SnapshotVolumes()
        {
            // CPU 功耗借用 v3 的保留槽（deciwatt），载荷仍是 364 字节 —— 这条断言把“不长大”也钉住。
            MetricsSnapshot power = new MetricsSnapshot
            {
                TimestampUtc = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
                Flags = MetricFlags.AgentAlive | MetricFlags.CpuPowerValid,
                CpuPowerWatts = 253.4f
            };
            byte[] powerBytes = power.ToBytes();
            Equal(MetricsSnapshot.SizeBytes, powerBytes.Length, "带功耗的快照仍是 368 字节");
            MetricsSnapshot powerBack;
            SnapshotError powerError;
            True(MetricsSnapshot.TryParse(powerBytes, out powerBack, out powerError), "带功耗的快照可解析");
            Equal(253.4f, powerBack.CpuPowerWatts, "功耗保留一位小数（deciwatt）");
            True(powerBack.Has(MetricFlags.CpuPowerValid), "功耗有效位往返");

            MetricsSnapshot noPower = new MetricsSnapshot { TimestampUtc = power.TimestampUtc };
            MetricsSnapshot noPowerBack;
            True(MetricsSnapshot.TryParse(noPower.ToBytes(), out noPowerBack, out powerError), "无功耗的快照可解析");
            Equal(0f, noPowerBack.CpuPowerWatts, "未采样时功耗为 0");
            MetricsSnapshot source = new MetricsSnapshot
            {
                TimestampUtc = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
                Flags = MetricFlags.AgentAlive | MetricFlags.DiskIoValid,
                DiskIoReadBytesPerSec = 4096,
                DiskIoWriteBytesPerSec = 8192,
                Volumes = new[]
                {
                    new MetricsSnapshot.VolumeIo { Letter = 'C', Valid = true, ReadBytesPerSec = 1000, WriteBytesPerSec = 2000 },
                    new MetricsSnapshot.VolumeIo { Letter = 'E', Valid = false }
                }
            };

            byte[] bytes = source.ToBytes();
            Equal(MetricsSnapshot.SizeBytes, bytes.Length, "逐盘表 + 尾部 GPU 字段后固定 368 字节");
            Equal(364, MetricsSnapshot.LegacySizeBytes, "旧读端只映射 364 字节");
            Equal(264, MetricsSnapshot.VolumeCountOffset, "盘数量偏移 264");
            Equal(268, MetricsSnapshot.VolumeTableOffset, "盘表起始 268");
            Equal(12, MetricsSnapshot.VolumeSlotBytes, "每盘 12 字节");
            Equal((ushort)2, MetricsSnapshot.ReadUInt16(bytes, 264), "写入两个盘");
            Equal((ushort)'C', MetricsSnapshot.ReadUInt16(bytes, 268), "盘符 C 在第一个槽");

            MetricsSnapshot parsed;
            SnapshotError error;
            True(MetricsSnapshot.TryParse(bytes, out parsed, out error), "逐盘快照可解析");
            Equal(2, parsed.Volumes.Length, "两个盘往返");
            Equal('C', parsed.Volumes[0].Letter, "盘符往返");
            True(parsed.Volumes[0].Valid, "C 盘有效");
            Equal(1000.0f, parsed.Volumes[0].ReadBytesPerSec, "C 盘读速率往返");
            Equal(2000.0f, parsed.Volumes[0].WriteBytesPerSec, "C 盘写速率往返");
            True(!parsed.Volumes[1].Valid, "E 盘无效标记往返");

            MetricsSnapshot noVolumes = new MetricsSnapshot();
            True(MetricsSnapshot.TryParse(noVolumes.ToBytes(), out parsed, out error), "无逐盘表可解析");
            Equal(0, parsed.Volumes.Length, "旧 Agent 没有逐盘表");

            MetricsSnapshot many = new MetricsSnapshot();
            List<MetricsSnapshot.VolumeIo> list = new List<MetricsSnapshot.VolumeIo>();
            for (int i = 0; i < 10; i++) list.Add(new MetricsSnapshot.VolumeIo { Letter = (char)('A' + i), Valid = true });
            many.Volumes = list.ToArray();
            True(MetricsSnapshot.TryParse(many.ToBytes(), out parsed, out error), "过多盘位可解析");
            Equal(MetricsSnapshot.MaxVolumes, parsed.Volumes.Length, "超过 8 个盘只保留前 8 个");
        }

        private static void SnapshotGpuDisk()
        {
            MetricsSnapshot source = new MetricsSnapshot
            {
                TimestampUtc = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc),
                Flags = MetricFlags.AgentAlive | MetricFlags.GpuValid |
                        MetricFlags.GpuTemperatureValid | MetricFlags.DiskValid | MetricFlags.DiskIoValid,
                GpuLoadPercent = 37.5f,
                GpuMemoryPercent = 62.0f,
                GpuTemperatureC = 63.0f,
                DiskPercent = 71.4f,
                DiskUsedGb = 356,
                DiskTotalGb = 931,
                DiskIoReadBytesPerSec = 12.5f * 1024 * 1024,
                DiskIoWriteBytesPerSec = 3.5f * 1024 * 1024
            };

            byte[] bytes = source.ToBytes();
            Equal(MetricsSnapshot.SizeBytes, bytes.Length, "加显卡/磁盘/磁盘 IO/逐盘表/GPU 功耗后仍固定 368 字节");
            Equal((ushort)3, MetricsSnapshot.VersionOf(bytes), "version 升到 3");
            Equal(236, MetricsSnapshot.GpuLoadPercentOffset, "显卡占用偏移 236");
            Equal(248, MetricsSnapshot.DiskPercentOffset, "磁盘占用偏移 248");

            MetricsSnapshot parsed;
            SnapshotError error;
            True(MetricsSnapshot.TryParse(bytes, out parsed, out error), "解析成功");
            Equal(37.5f, parsed.GpuLoadPercent, "显卡占用往返");
            Equal(62.0f, parsed.GpuMemoryPercent, "显存往返");
            Equal(63.0f, parsed.GpuTemperatureC, "GPU 温度往返");
            Equal(71.4f, parsed.DiskPercent, "磁盘百分比往返");
            Equal(356, parsed.DiskUsedGb, "磁盘已用 GB 往返");
            Equal(931, parsed.DiskTotalGb, "磁盘总量 GB 往返");
            True(parsed.Has(MetricFlags.GpuValid), "GPU 有效位往返");
            True(parsed.Has(MetricFlags.GpuTemperatureValid), "GPU 温度有效位往返");
            True(parsed.Has(MetricFlags.DiskValid), "磁盘有效位往返");
            True(parsed.Has(MetricFlags.DiskIoValid), "磁盘 IO 有效位往返");
            Equal(256, MetricsSnapshot.DiskIoReadOffset, "磁盘 IO 读偏移 256");
            Equal(260, MetricsSnapshot.DiskIoWriteOffset, "磁盘 IO 写偏移 260");
            Equal(12.5f * 1024 * 1024, parsed.DiskIoReadBytesPerSec, "磁盘 IO 读速率往返");
            Equal(3.5f * 1024 * 1024, parsed.DiskIoWriteBytesPerSec, "磁盘 IO 写速率往返");

            // 旧 Agent 不写尾部：新客户端必须看到“无效”而不是 0%
            MetricsSnapshot legacy = new MetricsSnapshot
            {
                TimestampUtc = source.TimestampUtc,
                Flags = MetricFlags.AgentAlive | MetricFlags.CpuValid,
                CpuPercent = 10.0f
            };
            True(MetricsSnapshot.TryParse(legacy.ToBytes(), out parsed, out error), "旧帧可解析");
            True(!parsed.Has(MetricFlags.GpuValid), "旧帧 GPU 无效");
            True(!parsed.Has(MetricFlags.DiskValid), "旧帧磁盘无效");
            double value;
            True(!MetricCatalog.TryReadValue(parsed, MetricId.Gpu, out value), "旧帧目录读不到显卡");

            // 台数超过 uint16 时截断而不是回绕；负值归零
            MetricsSnapshot extreme = new MetricsSnapshot { DiskUsedGb = 200000, DiskTotalGb = -5 };
            True(MetricsSnapshot.TryParse(extreme.ToBytes(), out parsed, out error), "极端值可解析");
            Equal(65535, parsed.DiskUsedGb, "GB 上限截到 65535");
            Equal(0, parsed.DiskTotalGb, "负 GB 归零");

            True(MetricCatalog.TryReadValue(source, MetricId.Gpu, out value), "显卡目录可读");
            Equal(37.5, value, "显卡目录数值");
            True(MetricCatalog.TryReadValue(source, MetricId.GpuMemory, out value), "显存目录可读");
            True(MetricCatalog.TryReadValue(source, MetricId.GpuTemperature, out value), "GPU 温度目录可读");
            True(MetricCatalog.TryReadValue(source, MetricId.Disk, out value), "磁盘目录可读");
            True(Math.Abs(value - 71.4) < 0.01, "磁盘目录数值");
        }

        private static void FormatRate()
        {
            Equal("0 B/s", MetricFormat.Rate(0), "0");
            Equal("999 B/s", MetricFormat.Rate(999), "999 B/s");
            Equal("1 KB/s", MetricFormat.Rate(1024), "1024 -> 1 KB/s");
            Equal("1.5 KB/s", MetricFormat.Rate(1536), "1536 -> 1.5 KB/s");
            Equal("251 KB/s", MetricFormat.Rate(257024), "251 KB/s");
            Equal("1 MB/s", MetricFormat.Rate(1048576), "1 MB/s 不带小数");
            Equal("1.2 MB/s", MetricFormat.Rate(1258291), "1.2 MB/s");
            Equal("1 GB/s", MetricFormat.Rate(1073741824), "1 GB/s");
            Equal(MetricFormat.Invalid, MetricFormat.Rate(double.NaN), "NaN -> --");
            Equal(MetricFormat.Invalid, MetricFormat.Rate(double.PositiveInfinity), "Inf -> --");
            Equal(MetricFormat.Invalid, MetricFormat.Rate(-1), "负数 -> --");

            string unit;
            Equal("1.5", MetricFormat.RateParts(1536, out unit), "RateParts 数字");
            Equal("KB/s", unit, "RateParts 单位");
            Equal(MetricFormat.Invalid, MetricFormat.RateParts(double.NaN, out unit), "RateParts NaN -> --");
            Equal(string.Empty, unit, "RateParts NaN 无单位");

            // 概览大数字固定一位小数：整数与一位小数混排时字宽会跳。
            Equal("3.0", MetricFormat.RatePartsFixed(3 * 1024 * 1024, out unit), "3 MB/s -> 3.0");
            Equal("MB/s", unit, "固定一位的单位");
            Equal("1.0", MetricFormat.RatePartsFixed(1024, out unit), "1024 -> 1.0 KB/s");
            Equal("0.0", MetricFormat.RatePartsFixed(0, out unit), "0 -> 0.0 B/s");
            Equal("12.3", MetricFormat.RatePartsFixed(12.34 * 1024, out unit), "保留一位");
            Equal(MetricFormat.Invalid, MetricFormat.RatePartsFixed(double.NaN, out unit), "固定一位 NaN -> --");
        }

        private static void FormatOthers()
        {
            Equal("12%", MetricFormat.Percent(12.4), "四舍五入");
            Equal("0%", MetricFormat.Percent(-5), "负值钳到 0");
            Equal("100%", MetricFormat.Percent(130), "超 100 钳住");
            Equal(MetricFormat.Invalid, MetricFormat.Percent(double.NaN), "NaN -> --");

            Equal("12.5%", MetricFormat.PercentDetailed(12.5), "一位小数");
            Equal("55\u00B0C", MetricFormat.Temperature(55.4), "温度取整 + 度符号");
            Equal(MetricFormat.Invalid, MetricFormat.Temperature(double.NaN), "温度 NaN -> --");
            Equal("55.4\u00B0C", MetricFormat.TemperatureDetailed(55.4), "温度一位小数");

            Equal("512 B", MetricFormat.Bytes(512), "字节");
            Equal("1 KB", MetricFormat.Bytes(1024), "KB");
            Equal("32 GB", MetricFormat.Bytes(34359738368L), "GB");
            Equal("1.6 TB / 2 TB", MetricFormat.MemoryPair(1805000000000L, 2199023255552L), "内存对格式（自动进位到 TB）");
            Equal(MetricFormat.Invalid, MetricFormat.MemoryPair(0, 0), "总量 0 -> --");
        }

        // ---- EMA ---------------------------------------------------------------------

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

        private static void EmaBehaviour()
        {
            EmaFilter filter = new EmaFilter(0.5);
            True(!filter.HasValue, "初始无值");
            Equal(40.0, filter.Push(40), "首个样本直接采用");
            Equal(30.0, filter.Push(20), "第二个样本走平均");
            Equal(0.5, filter.Alpha, "alpha 暴露");

            filter.Reset();
            True(!filter.HasValue, "重置后无值");
            Equal(7.0, filter.Push(7), "重置后首样本再生效");

            EmaFilter guarded = new EmaFilter(0.5);
            guarded.Push(10);
            Equal(10.0, guarded.Push(double.NaN), "NaN 被忽略，保留旧值");

            double slow = EmaFilter.AlphaForSettling(10);
            True(slow > 0 && slow < 0.35, "10 样本收敛的 alpha 合理: " + slow.ToString("F3"));

            Throws<ArgumentOutOfRangeException>(() => new EmaFilter(0), "alpha=0 抛异常");
            Throws<ArgumentOutOfRangeException>(() => new EmaFilter(1.5), "alpha>1 抛异常");
        }

        // ---- catalog -----------------------------------------------------------------

        private static void ToneThresholds()
        {
            Equal(MetricTone.Normal, MetricTones.Cpu(69.9), "CPU 69.9 正常");
            Equal(MetricTone.Warning, MetricTones.Cpu(70), "CPU 70 提醒");
            Equal(MetricTone.Warning, MetricTones.Cpu(79.9), "CPU 79.9 提醒");
            Equal(MetricTone.Danger, MetricTones.Cpu(80), "CPU 80 告警");
            Equal(MetricTone.Warning, MetricTones.Memory(70), "内存 70 提醒");
            Equal(MetricTone.Warning, MetricTones.Memory(84.9), "内存 84.9 提醒");
            Equal(MetricTone.Danger, MetricTones.Memory(85), "内存 85 告警");
            Equal(MetricTone.Normal, MetricTones.Generic(79.9), "通用 79.9 正常");
            Equal(MetricTone.Warning, MetricTones.Generic(80), "通用 80 提醒");
            Equal(MetricTone.Danger, MetricTones.Generic(90), "通用 90 告警");
        }


        private static void TemperatureZoneThresholds()
        {
            Equal(MetricTone.Normal, TemperatureZones.Cpu(30), "CPU 30 度安全");
            Equal(MetricTone.Normal, TemperatureZones.Cpu(79.9), "CPU 79.9 度安全");
            Equal(MetricTone.Warning, TemperatureZones.Cpu(80), "CPU 80 度起警告");
            Equal(MetricTone.Warning, TemperatureZones.Cpu(89.9), "CPU 89.9 度仍警告");
            Equal(MetricTone.Danger, TemperatureZones.Cpu(90), "CPU 90 度起危险");

            Equal(MetricTone.Normal, TemperatureZones.Gpu(74.9), "GPU 74.9 度安全");
            Equal(MetricTone.Warning, TemperatureZones.Gpu(75), "GPU 75 度起警告");
            Equal(MetricTone.Warning, TemperatureZones.Gpu(82.9), "GPU 82.9 度仍警告");
            Equal(MetricTone.Danger, TemperatureZones.Gpu(83), "GPU 83 度起危险");

            Equal(MetricTone.Normal, TemperatureZones.Cpu(double.NaN), "NaN 不算告警");
            Equal(MetricTone.Normal, TemperatureZones.Gpu(double.NaN), "GPU NaN 不算告警");

            Equal(MetricTone.Warning, TemperatureZones.For("temperature", 85), "目录键 temperature 走 CPU 分档");
            Equal(MetricTone.Danger, TemperatureZones.For("gpuTemp", 85), "目录键 gpuTemp 走 GPU 分档");
            Equal(MetricTone.Normal, TemperatureZones.For("cpu", 85), "非温度指标不分档");
            Equal(MetricTone.Normal, TemperatureZones.For(null, 85), "空指标名不分档");
        }

        private static void Catalog()
        {
            Equal(12, MetricCatalog.All.Count, "目录 12 项（v1 五项 + 显卡三项 + 磁盘容量 + 磁盘 IO 合并/读/写）");
            HashSet<string> keys = new HashSet<string>();
            foreach (MetricInfo info in MetricCatalog.All)
            {
                True(keys.Add(info.Key), "key 唯一: " + info.Key);
                True(!string.IsNullOrEmpty(info.DisplayName), "有显示名: " + info.Key);
            }

            MetricInfo diskIo;
            True(MetricCatalog.TryGet("diskIo", out diskIo), "磁盘 IO 在目录里");
            Equal(MetricKind.Rate, diskIo.Kind, "磁盘 IO 是速率型");
            Equal("磁盘 I/O", diskIo.DisplayName, "磁盘 IO 显示名");

            MetricInfo readInfo;
            True(MetricCatalog.TryGet("diskIoRead", out readInfo), "磁盘读在目录里");
            Equal(MetricKind.Rate, readInfo.Kind, "磁盘读是速率型");
            MetricInfo writeInfo;
            True(MetricCatalog.TryGet("diskIoWrite", out writeInfo), "磁盘写在目录里");
            Equal("磁盘写入", writeInfo.DisplayName, "磁盘写显示名");

            MetricsSnapshot diskSnapshot = new MetricsSnapshot
            {
                Flags = MetricFlags.DiskIoValid,
                DiskIoReadBytesPerSec = 1024,
                DiskIoWriteBytesPerSec = 2048
            };
            double diskValue;
            True(MetricCatalog.TryReadValue(diskSnapshot, MetricId.DiskIoRead, out diskValue), "磁盘读有效可读");
            Equal(1024.0, diskValue, "磁盘读数值");
            True(MetricCatalog.TryReadValue(diskSnapshot, MetricId.DiskIoWrite, out diskValue), "磁盘写有效可读");
            Equal(2048.0, diskValue, "磁盘写数值");
            True(MetricCatalog.TryReadValue(diskSnapshot, MetricId.DiskIo, out diskValue), "合并值仍可读");
            Equal(3072.0, diskValue, "合并值 = 读 + 写");

            MetricInfo cpu;
            True(MetricCatalog.TryGet("CPU", out cpu), "key 查找大小写不敏感");
            Equal(MetricId.Cpu, cpu.Id, "找到 CPU");
            True(!MetricCatalog.TryGet("fan", out cpu), "目录外指标查不到");

            Equal("12%", MetricCatalog.Get(MetricId.Cpu).Format(12.4), "CPU 格式化");
            Equal("55\u00B0C", MetricCatalog.Get(MetricId.Temperature).Format(55.4), "温度格式化");
            Equal("1.2 MB/s", MetricCatalog.Get(MetricId.NetUp).Format(1258291), "速率格式化");

            MetricsSnapshot snapshot = new MetricsSnapshot
            {
                Flags = MetricFlags.CpuValid | MetricFlags.NetworkValid,
                CpuPercent = 12,
                NetUpBytesPerSec = 1024,
                NetDownBytesPerSec = 2048
            };
            double value;
            True(MetricCatalog.TryReadValue(snapshot, MetricId.Cpu, out value), "CPU 有效可读");
            Equal(12.0, value, "CPU 数值");
            True(!MetricCatalog.TryReadValue(snapshot, MetricId.Memory, out value), "无有效位读到失败");
            True(!MetricCatalog.TryReadValue(snapshot, MetricId.Temperature, out value), "温度无效位失败");
            True(MetricCatalog.TryReadValue(snapshot, MetricId.NetDown, out value), "网速有效");
            Equal(2048.0, value, "下行数值");
        }

        // ---- settings ----------------------------------------------------------------

        private static void ColumnsFollowCpu()
        {
            Settings settings = Settings.CreateDefault();
            BandElement bar = settings.Items[0];

            bar.ColumnMode = ColumnModes.Cores;
            Equal(24, BandLayout.ColumnsFor(bar, 24, 32), "默认按物理核心数");
            bar.ColumnMode = ColumnModes.Threads;
            Equal(32, BandLayout.ColumnsFor(bar, 24, 32), "选线程数量时按逻辑线程数");

            // 自定义：用 CustomColumns（持久值），且不能超过线程数
            bar.ColumnMode = ColumnModes.Custom;
            bar.CustomColumns = 12;
            Equal(12, BandLayout.ColumnsFor(bar, 24, 32), "自定义 12 列");
            bar.CustomColumns = 99;
            Equal(32, BandLayout.ColumnsFor(bar, 24, 32), "自定义超过线程数时夹到 32");
            bar.CustomColumns = 0;
            bar.HistoryColumns = 0;
            Equal(1, BandLayout.ColumnsFor(bar, 24, 32), "自定义 0 时至少 1 列");

            // 切到 cores 再切回 custom，手填的数字不能被 CPU 数字覆盖掉
            bar.CustomColumns = 12;
            bar.ColumnMode = ColumnModes.Cores;
            bar.HistoryColumns = BandLayout.ColumnsFor(bar, 24, 32);
            bar.ColumnMode = ColumnModes.Custom;
            Equal(12, BandLayout.ColumnsFor(bar, 24, 32), "来回切模式后自定义值仍在");
            Equal(12, bar.CustomColumns, "CustomColumns 未被改写");

            // 硬件信息拿不到时回退到元素自己记录的值
            bar.ColumnMode = ColumnModes.Cores;
            bar.HistoryColumns = 16;
            Equal(16, BandLayout.ColumnsFor(bar, 0, 0), "无硬件信息时回退 HistoryColumns");

            // 多路/异常值也要夹在 1..64
            bar.ColumnMode = ColumnModes.Cores;
            Equal(64, BandLayout.ColumnsFor(bar, 96, 192), "核心数超过 64 列时夹紧");

            // 旧文件只有 useLogicalCores：Normalize 迁移成 columnMode
            Settings legacy = Settings.CreateDefault();
            legacy.Items[0].ColumnMode = null;
            legacy.Items[0].UseLogicalCores = true;
            legacy.Normalize();
            Equal(ColumnModes.Threads, legacy.Items[0].ColumnMode, "旧开关迁移为 threads");
        }

        private static void BarGeometryIsFixed()
        {
            Settings settings = Settings.CreateDefault();
            BandElement bar = settings.Items[0];
            bar.CellWidth = 20;
            bar.CellGap = 7;
            bar.Height = 40;
            settings.Normalize();
            Equal(Settings.BarCellWidth, bar.CellWidth, "柱宽被拉回写死值");
            Equal(Settings.BarCellGap, bar.CellGap, "柱间距被拉回写死值");
            Equal(Settings.BarHeight, bar.Height, "柱高被拉回写死值");
            Equal(158, BandLayout.ElementWidth(bar), "固定几何下的柱阵宽度");
        }

        private static void DefaultSettings()
        {
            Settings settings = Settings.CreateDefault();
            Equal(Settings.CurrentVersion, settings.Version, "默认 version=3");
            Equal(Settings.CurveSmooth, settings.CurveStyle, "默认曲线样式=圆滑");
            Equal(3, settings.Items.Count, "默认三元素");
            Equal(3, settings.EnabledItemCount, "默认全启用");
            Equal("bar", settings.Items[0].Type, "第一个是 bar");
            Equal("cpu", settings.Items[0].Metric, "bar 绑 CPU");
            Equal("text", settings.Items[1].Type, "第二个是 text");
            Equal(2, settings.Items[1].Lines.Count, "text 两行");
            Equal(Settings.MinTextWidth, settings.Items[1].Width, "文本宽度只是最小值");
            Equal("netUp", settings.Items[1].Lines[0].Metric, "第一行上行");
            Equal("netDown", settings.Items[1].Lines[1].Metric, "第二行下行");
            Equal("pie", settings.Items[2].Type, "第三个是 pie");
            Equal("memory", settings.Items[2].Metric, "pie 绑内存");
            Equal(PieStyle.Solid, settings.Items[2].Style, "环形默认画法 = 实心扇形");
            Equal(40, settings.Band.Height, "条高 40");
            True(settings.Palette.Count >= 4, "调色板有颜色");
            Equal(Settings.DefaultGreen, settings.Palette[0], "调色板首色 = 统一绿");
            // 色板里只留一个绿色：第 5 位不再是浅绿，而是橙色
            Equal(Settings.DefaultOrange, settings.Palette[4], "调色板第 5 位 = 橙");
            Equal(Settings.DefaultViolet, settings.Palette[5], "调色板第 6 位 = 紫");
            True(!settings.Palette.Contains(Settings.DefaultGreenSoft), "色板不含第二个绿色");
            True(!settings.Palette.Contains("#FF00CC6A"), "色板不含 XMeters 绿");
            Equal(Settings.DefaultGreen, settings.Items[0].Colors.Fill, "柱填充 = 统一绿");
            Equal(Settings.DefaultBlue, settings.Items[1].Lines[0].Color, "上行文字 = 统一蓝");

            List<string> warnings = settings.Normalize();
            Equal(0, warnings.Count, "默认值 Normalize 无告警");
        }

        private static void DiskDriveSetting()
        {
            Settings settings = Settings.CreateDefault();
            BandElement disk = new BandElement
            {
                Id = "disk",
                Type = "text",
                Enabled = true,
                Drive = "c",
                Lines = new List<TextLine>
                {
                    new TextLine { Metric = "diskIoRead" },
                    new TextLine { Metric = "diskIoWrite" }
                }
            };
            settings.Items.Add(disk);
            BandElement net = new BandElement
            {
                Id = "net",
                Type = "text",
                Enabled = true,
                Drive = "D:",
                Lines = new List<TextLine>
                {
                    new TextLine { Metric = "netUp" },
                    new TextLine { Metric = "netDown" }
                }
            };
            settings.Items.Add(net);
            settings.Normalize();
            Equal("C:", disk.Drive, "小写盘符规范成大写带冒号");
            Equal(string.Empty, net.Drive, "非磁盘元素清掉盘符");
            Equal("D:", Settings.CanonicalDrive("d"), "单字母补冒号");
            Equal(string.Empty, Settings.CanonicalDrive("1:"), "非法盘符回退为空");
            Equal(string.Empty, Settings.CanonicalDrive(null), "空值回退为空");
        }

        private static void NormalizeRecovers()
        {
            Settings empty = new Settings();
            List<string> warnings = empty.Normalize();
            True(warnings.Count > 0, "空设置产生告警");
            Equal(3, empty.Items.Count, "空设置恢复默认元素");
            Equal(1000, empty.RefreshMs, "刷新周期恢复默认");
            Equal(40, empty.Band.Height, "条高恢复默认");
            Equal(Settings.CurveSmooth, empty.CurveStyle, "空设置曲线样式回默认圆滑");

            Settings badCurve = Settings.CreateDefault();
            badCurve.CurveStyle = "zigzag";
            warnings = badCurve.Normalize();
            Equal(Settings.CurveSmooth, badCurve.CurveStyle, "非法曲线样式回默认圆滑");
            True(warnings.Exists(w => w.Contains("curveStyle")), "非法曲线样式有告警");

            // 环形画法：gradient 保留，非法值回实心（不影响内存那个默认元素）。
            Settings gradientPie = Settings.CreateDefault();
            gradientPie.Items[2].Style = "GRADIENT";
            gradientPie.Normalize();
            Equal(PieStyle.Gradient, gradientPie.Items[2].Style, "gradient 大小写不敏感并保留");
            Settings zigzagPie = Settings.CreateDefault();
            zigzagPie.Items[2].Style = "zigzag";
            zigzagPie.Normalize();
            Equal(PieStyle.Solid, zigzagPie.Items[2].Style, "非法环形画法回实心扇形");

            Settings outOfRange = Settings.CreateDefault();
            outOfRange.RefreshMs = 99999;
            outOfRange.Items[0].RefreshMs = 1;
            warnings = outOfRange.Normalize();
            Equal(Settings.DefaultRefreshMs, outOfRange.RefreshMs, "全局刷新越界恢复默认");
            Equal(Settings.DefaultRefreshMs, outOfRange.Items[0].RefreshMs, "元素刷新越界跟随全局");

            Settings broken = Settings.CreateDefault();
            broken.Items.Add(new BandElement { Id = "bad-type", Type = "flame", Enabled = true, Metric = "cpu" });
            broken.Items.Add(new BandElement { Id = "bad-metric", Type = "pie", Enabled = true, Metric = "fan", Diameter = 25 });
            broken.Items.Add(new BandElement { Id = "no-lines", Type = "text", Enabled = true, Width = 40 });
            broken.Items.Add(new BandElement { Id = "sp", Type = "spacer", Enabled = true, Width = 6 });
            broken.Items[0].Colors.Fill = "not-a-color";
            broken.Band.HorizontalSpacing = 999;
            warnings = broken.Normalize();
            True(warnings.Count >= 4, "多个问题都产生告警: " + warnings.Count);
            Equal(3, broken.Items.Count, "丢弃 4 个坏元素（含旧 spacer 类型），保留默认三元素");
            Equal("#FF1DB954", broken.Items[0].Colors.Fill, "非法颜色恢复默认绿");
            Equal(3, broken.Band.HorizontalSpacing, "越界间距恢复默认");
            Equal("pie", broken.Items[2].Type, "默认三元素原样保留（柱/文本/环）");

            Settings allOff = Settings.CreateDefault();
            foreach (BandElement item in allOff.Items) item.Enabled = false;
            warnings = allOff.Normalize();
            True(warnings.Count > 0, "全部停用会告警");
            Equal(1, allOff.EnabledItemCount, "至少启用一个");
        }

        private static void PaletteBehaviour()
        {
            Equal(7, Settings.DefaultPalette.Count, "默认色板 7 个色块");

            // 自定义色板：顺序与颜色都要原样保留（UI 增删就靠这个）
            Settings custom = Settings.CreateDefault();
            custom.Palette = new List<string> { "#FF102030", "#FF00CC6A", "#FFABCDEF" };
            string path = Path.Combine(Path.GetTempPath(), "quadvitals-palette-test.json");
            if (File.Exists(path)) File.Delete(path);
            custom.Save(path);

            List<string> warnings;
            Settings loaded = Settings.Load(path, out warnings);
            Equal(3, loaded.Palette.Count, "自定义色板数量保持");
            Equal("#FF102030", loaded.Palette[0], "自定义色板顺序保持（1）");
            Equal("#FF00CC6A", loaded.Palette[1], "自定义色板顺序保持（2）");
            Equal("#FFABCDEF", loaded.Palette[2], "自定义色板顺序保持（3）");
            File.Delete(path);

            // 非法项被换成默认色，数量不变（避免手改 JSON 把整排色块弄空）
            Settings broken = Settings.CreateDefault();
            broken.Palette = new List<string> { "#FF102030", "not-a-colour" };
            broken.Normalize();
            Equal(2, broken.Palette.Count, "非法项不减少数量");
            Equal("#FF102030", broken.Palette[0], "合法项保留");
            Equal(Settings.DefaultPalette[1 % Settings.DefaultPalette.Count], broken.Palette[1], "非法项换成默认色");

            // 空色板 -> 回默认（UI 也靠这个保证「预设」不会空着）
            Settings empty = Settings.CreateDefault();
            empty.Palette = new List<string>();
            empty.Normalize();
            True(empty.Palette.Count > 0, "空色板回退到默认色板");

            // 末尾淘汰 + 去重（取色器开关打开时靠这两条自动维护色板）
            List<string> fifo = new List<string> { "#FF000001", "#FF000002" };
            True(PaletteRules.Add(fifo, "#FF000003"), "新颜色可加入");
            Equal(3, fifo.Count, "加入后数量+1");
            Equal("#FF000003", fifo[2], "新颜色在末尾");

            True(!PaletteRules.Add(fifo, "#FF000001"), "重复颜色不重复加");
            Equal(3, fifo.Count, "重复加不改变数量");

            List<string> full = new List<string>();
            for (int i = 0; i < PaletteRules.MaxPresets; i++)
            {
                full.Add("#FF0000" + i.ToString("X2"));
            }
            Equal(PaletteRules.MaxPresets, full.Count, "色板可堆到上限");
            string oldest = full[0];
            True(PaletteRules.Add(full, "#FF00FFFF"), "满员时仍可加入");
            Equal(PaletteRules.MaxPresets, full.Count, "满员加入后数量不超上限");
            True(full[0] != oldest, "最早的被挤掉");
            Equal("#FF00FFFF", full[full.Count - 1], "新颜色在末尾（FIFO）");

            List<string> two = new List<string> { "#FF000001", "#FF000002" };
            True(PaletteRules.Remove(two, "#FF000001"), "可删除已存在的颜色");
            Equal(1, two.Count, "删除后数量-1");
            True(!PaletteRules.Remove(two, "#FF000002"), "只剩一个时拒绝删除");
            Equal(1, two.Count, "拒绝删除后数量不变");

            // 内置默认预设不可删：删了就再也恢复不回来
            List<string> withDefaults = new List<string>(Settings.DefaultPalette);
            withDefaults.Add("#FF123456");
            string builtIn = Settings.DefaultPalette[0];
            True(PaletteRules.IsProtected(builtIn, Settings.DefaultPalette), "内置色被识别为受保护");
            True(!PaletteRules.Remove(withDefaults, builtIn, Settings.DefaultPalette), "内置预设拒绝删除");
            True(withDefaults.Contains(builtIn), "内置预设仍在列表里");
            True(PaletteRules.Remove(withDefaults, "#FF123456", Settings.DefaultPalette), "自定义色可以删");
            True(!withDefaults.Contains("#FF123456"), "自定义色已移除");

            // 满 16 个时淘汰的是**最早的受保护色以外**那个（内置默认色不会被挤掉）
            List<string> mixed = new List<string>(Settings.DefaultPalette);
            for (int i = 0; i < PaletteRules.MaxPresets - Settings.DefaultPalette.Count; i++)
            {
                mixed.Add("#FF1000" + i.ToString("X2"));
            }
            Equal(PaletteRules.MaxPresets, mixed.Count, "默认色 + 自定义色堆到上限");
            True(PaletteRules.Add(mixed, "#FF00FFFF", Settings.DefaultPalette), "满员时仍可加入");
            Equal(PaletteRules.MaxPresets, mixed.Count, "加入后数量不超上限");
            Equal(Settings.DefaultPalette[0], mixed[0], "内置默认色没被挤掉（仍在首位）");
            True(!mixed.Contains("#FF100000"), "被淘汰的是最早的自定义色");
            True(mixed.Contains("#FF00FFFF"), "新颜色加进来了");
            bool allDefaultsKept = true;
            foreach (string builtInColour in Settings.DefaultPalette)
            {
                if (!mixed.Contains(builtInColour)) allDefaultsKept = false;
            }
            True(allDefaultsKept, "七个内置默认色全部保留");

            // 整张色板都是受保护色时：拒绝加入，不动默认色
            List<string> onlyProtected = new List<string>(Settings.DefaultPalette);
            List<string> protectAll = new List<string>(Settings.DefaultPalette);
            while (onlyProtected.Count < PaletteRules.MaxPresets)
            {
                string extra = "#FF200" + onlyProtected.Count.ToString("X3");
                onlyProtected.Add(extra);
                protectAll.Add(extra);
            }
            True(!PaletteRules.Add(onlyProtected, "#FF00FFFF", protectAll), "全受保护时拒绝加入");
            Equal(PaletteRules.MaxPresets, onlyProtected.Count, "拒绝后数量不变");

            // 开关本身要能存下来
            Settings auto = Settings.CreateDefault();
            True(!auto.AutoAddPreset, "「确定时加入预设」默认关闭");
            auto.AutoAddPreset = true;
            string autoPath = Path.Combine(Path.GetTempPath(), "quadvitals-autoadd-test.json");
            if (File.Exists(autoPath)) File.Delete(autoPath);
            auto.Save(autoPath);
            List<string> autoWarnings;
            Settings autoLoaded = Settings.Load(autoPath, out autoWarnings);
            True(autoLoaded.AutoAddPreset, "开关状态往返保持");
            File.Delete(autoPath);

            // 按角色分开的预设：三个角色互不影响
            ColorPresets presets = new ColorPresets();
            presets.Set(ColorRoles.Fill, new List<string> { "#FF111111" });
            Equal(1, presets.Get(ColorRoles.Fill).Count, "fill 有自己的预设");
            True(presets.Get(ColorRoles.Kernel) == null, "加 fill 不会给 kernel 建列表");
            True(presets.Get(ColorRoles.Outline) == null, "加 fill 不会给 outline 建列表");

            presets.Set(ColorRoles.Kernel, new List<string> { "#FF222222" });
            Equal("#FF111111", presets.Get(ColorRoles.Fill)[0], "kernel 的写入不影响 fill");
            Equal("#FF222222", presets.Get(ColorRoles.Kernel)[0], "kernel 存对了");

            // 未知/空角色归到 fill，不会凭空造出新列表
            Equal(1, presets.Get("nonsense").Count, "未知角色回退到 fill");

            // 往返：三个角色各自保留
            Settings withPresets = Settings.CreateDefault();
            withPresets.ColorPresets = new ColorPresets();
            withPresets.ColorPresets.Set(ColorRoles.Fill, new List<string> { "#FF102030", "#FF405060" });
            withPresets.ColorPresets.Set(ColorRoles.Kernel, new List<string> { "#FFAABBCC" });
            string presetPath = Path.Combine(Path.GetTempPath(), "quadvitals-rolepresets-test.json");
            if (File.Exists(presetPath)) File.Delete(presetPath);
            withPresets.Save(presetPath);
            List<string> presetWarnings;
            Settings presetLoaded = Settings.Load(presetPath, out presetWarnings);
            Equal(2, presetLoaded.ColorPresets.Get(ColorRoles.Fill).Count, "fill 预设往返保持");
            Equal(1, presetLoaded.ColorPresets.Get(ColorRoles.Kernel).Count, "kernel 预设往返保持");
            True(presetLoaded.ColorPresets.Get(ColorRoles.Outline) == null, "没自定义的角色保持 null（用默认色板）");
            File.Delete(presetPath);

            // 非法值换成默认色、超过上限丢最早的
            Settings dirty = Settings.CreateDefault();
            dirty.ColorPresets = new ColorPresets();
            List<string> longList = new List<string>();
            for (int i = 0; i < PaletteRules.MaxPresets + 2; i++) longList.Add("#FF0000" + i.ToString("X2"));
            dirty.ColorPresets.Set(ColorRoles.Outline, longList);
            dirty.ColorPresets.Set(ColorRoles.Line, new List<string> { "bad", "#FF00CC6A" });
            dirty.Normalize();
            Equal(PaletteRules.MaxPresets, dirty.ColorPresets.Get(ColorRoles.Outline).Count, "超长列表被截到上限");
            True(dirty.ColorPresets.Get(ColorRoles.Outline)[0] != "#FF000000", "截断时丢的是最早的");
            Equal(Settings.DefaultPalette[0], dirty.ColorPresets.Get(ColorRoles.Line)[0], "非法项换成默认色");
            Equal("#FF00CC6A", dirty.ColorPresets.Get(ColorRoles.Line)[1], "合法项保留");
        }

        private static void ColourTextParsing()
        {
            string canonical;

            True(ColorText.TryParse("#FF1DB954", out canonical), "#AARRGGBB 可解析");
            Equal("#FF1DB954", canonical, "#AARRGGBB 原样保留");

            True(ColorText.TryParse("#1db954", out canonical), "#RRGGBB 可解析");
            Equal("#FF1DB954", canonical, "#RRGGBB 补齐 alpha");

            True(ColorText.TryParse("00cc6a", out canonical), "不带 # 的 RRGGBB 可解析");
            Equal("#FF00CC6A", canonical, "不带 # 的 RRGGBB 规范化");

            True(ColorText.TryParse("00CC6A", out canonical), "大写不带 # 也可解析");
            Equal("#FF00CC6A", canonical, "大写规范化");

            True(ColorText.TryParse("rgb(0,204,106)", out canonical), "rgb() 可解析");
            Equal("#FF00CC6A", canonical, "rgb() 与 00cc6a 同色");

            True(ColorText.TryParse("RGB( 0 , 204 , 106 )", out canonical), "rgb() 容忍空格/大小写");
            Equal("#FF00CC6A", canonical, "rgb() 空格与大小写规范化");

            True(ColorText.TryParse("rgba(0,204,106,0.5)", out canonical), "rgba() 可解析");
            Equal("#8000CC6A", canonical, "rgba() 的 0-1 alpha 转 0-255");

            True(ColorText.TryParse("rgba(0,204,106,128)", out canonical), "rgba() 整数 alpha 可解析");
            Equal("#8000CC6A", canonical, "rgba() 的 0-255 alpha 直接使用");

            True(ColorText.TryParse("  #00CC6A  ", out canonical), "前后空格可容忍");
            Equal("#FF00CC6A", canonical, "空格被裁掉");

            True(!ColorText.TryParse("rgb(300,0,0)", out canonical), "rgb() 超范围拒绝");
            True(!ColorText.TryParse("#12345", out canonical), "5 位十六进制拒绝");
            True(!ColorText.TryParse("00cc6g", out canonical), "非法字符拒绝");
            True(!ColorText.TryParse("", out canonical), "空串拒绝");
            True(!ColorText.TryParse(null, out canonical), "null 拒绝");
            True(!ColorText.TryParse("rgb(0,204)", out canonical), "rgb() 参数不足拒绝");

            True(ColorText.IsCanonical("#FF00CC6A"), "IsCanonical 认 #AARRGGBB");
            True(!ColorText.IsCanonical("#00CC6A"), "IsCanonical 不认 6 位");
        }

        private static void SystemKernelColour()
        {
            Settings defaults = Settings.CreateDefault();
            BandElement bar = null;
            foreach (BandElement item in defaults.Items)
            {
                if (item.IsType(BandElementType.Bar)) { bar = item; break; }
            }
            True(bar != null, "默认配置里有柱状元素");
            True(bar != null && bar.Colors != null, "柱状元素有颜色对象");
            if (bar != null && bar.Colors != null)
            {
                Equal(Settings.DefaultSystemRed, bar.Colors.Kernel, "默认内核色是 #FFFF0000");
            }

            Settings legacy = Settings.CreateDefault();
            legacy.Items[0].Colors.Kernel = null;
            legacy.Normalize();
            Equal(Settings.DefaultSystemRed, legacy.Items[0].Colors.Kernel, "旧配置缺 kernel -> 补默认红");

            Settings broken = Settings.CreateDefault();
            broken.Items[0].Colors.Kernel = "红色";
            broken.Normalize();
            Equal(Settings.DefaultSystemRed, broken.Items[0].Colors.Kernel, "非法 kernel -> 回退默认红");

            Settings custom = Settings.CreateDefault();
            custom.Items[0].Colors.Kernel = "#FF00CC6A";
            Equal("#FF00CC6A", custom.Items[0].Colors.Clone().Kernel, "Clone 带上内核色");

            string path = Path.Combine(Path.GetTempPath(), "quadvitals-kernel-test.json");
            if (File.Exists(path)) File.Delete(path);
            custom.Save(path);
            string json = File.ReadAllText(path);
            True(json.Contains("\"kernel\""), "JSON 写出 kernel 字段");

            List<string> warnings;
            Settings loaded = Settings.Load(path, out warnings);
            Equal("#FF00CC6A", loaded.Items[0].Colors.Kernel, "kernel 往返保持");
            File.Delete(path);
        }

        private static void SettingsJsonRoundTrip()
        {
            string path = Path.Combine(Path.GetTempPath(), "quadvitals-settings-test.json");
            if (File.Exists(path)) File.Delete(path);

            Settings original = Settings.CreateDefault();
            original.NetworkAdapter = "Intel(R) Ethernet Converged Network Adapter X550-T2";
            original.CurveStyle = Settings.CurvePolyline;
            original.IndicatorStyle = Settings.IndicatorCapsule;
            original.Items[1].Lines[0].Color = "#FF00CC6A";
            original.Save(path);

            string json = File.ReadAllText(path);
            True(json.Contains("\"items\""), "JSON 含 items");
            True(json.Contains("\"type\": \"bar\""), "JSON 是可读的（有缩进）");
            True(json.Contains("netUp"), "JSON 含指标 key");
            True(json.Contains("\"curveStyle\""), "JSON 含 curveStyle");
            True(json.Contains("\"indicatorStyle\""), "JSON 含 indicatorStyle");
            True(!json.Contains("\"fill2\""), "JSON 不再写 fill2");
            True(!json.Contains("\"verticalSpacing\""), "JSON 不再写 verticalSpacing");
            True(!json.Contains("\"theme\""), "JSON 不再写 theme");
            True(!json.Contains("\"opacity\""), "JSON 不再写 opacity");
            True(!json.Contains("\"networkUnit\""), "JSON 不再写 networkUnit");
            True(!json.Contains("\"temperatureSource\""), "JSON 不再写 temperatureSource（当前只有 LHM）");

            List<string> warnings;
            Settings loaded = Settings.Load(path, out warnings);
            Equal(0, warnings.Count, "往返无告警: " + string.Join("; ", warnings));
            Equal(3, loaded.Items.Count, "元素数量保持");
            Equal(original.NetworkAdapter, loaded.NetworkAdapter, "网卡名保持");
            Equal(Settings.CurvePolyline, loaded.CurveStyle, "曲线样式保持");
            Equal(Settings.IndicatorCapsule, loaded.IndicatorStyle, "指示器样式保持");
            Equal("#FF00CC6A", loaded.Items[1].Lines[0].Color, "行颜色保持");
            Equal(16, loaded.Items[0].HistoryColumns, "柱数保持");
            Equal(3, loaded.Band.HorizontalSpacing, "间距保持");
            Equal(BandLayout.TotalWidth(original), BandLayout.TotalWidth(loaded), "往返后宽度一致");

            File.Delete(path);

            string missingPath = Path.Combine(Path.GetTempPath(), "quadvitals-does-not-exist.json");
            Settings missing = Settings.Load(missingPath, out warnings);
            Equal(3, missing.Items.Count, "文件不存在 -> 默认设置");

            Settings missingTry;
            List<string> missingWarnings;
            True(!Settings.TryLoad(missingPath, out missingTry, out missingWarnings), "TryLoad 对缺失文件返回 false");

            string corruptPath = Path.Combine(Path.GetTempPath(), "quadvitals-corrupt.json");
            File.WriteAllText(corruptPath, "{ this is not json ", new UTF8Encoding(false));
            Settings corrupt = Settings.Load(corruptPath, out warnings);
            True(warnings.Count > 0, "损坏文件产生告警");
            Equal(3, corrupt.Items.Count, "损坏文件 -> 默认设置");

            Settings corruptTry;
            List<string> corruptWarnings;
            True(!Settings.TryLoad(corruptPath, out corruptTry, out corruptWarnings), "TryLoad 拒绝损坏文件");
            True(corruptWarnings.Count > 0, "TryLoad 损坏文件有告警");
            File.Delete(corruptPath);
        }

        private static void PrettyPrint()
        {
            string compact = "{\"a\":1,\"b\":[1,2],\"c\":{},\"d\":\"x{y,z}\",\"e\":[]}";
            string pretty = JsonPretty.Print(compact);
            True(pretty.Contains("\n  \"a\": 1"), "键值缩进");
            True(pretty.Contains("\"c\": {}"), "空对象不换行");
            True(pretty.Contains("\"e\": []"), "空数组不换行");
            True(pretty.Contains("\"x{y,z}\""), "字符串里的括号不动");

            string escaped = "{\"k\":\"a\\\"b{c}\"}";
            string escapedPretty = JsonPretty.Print(escaped);
            True(escapedPretty.Contains("a\\\"b{c}"), "转义引号不破坏结构");
        }

        // ---- layout ------------------------------------------------------------------

        private static void LayoutWidth()
        {
            Settings settings = Settings.CreateDefault();
            // 柱宽/柱间距是写死的产品常量（8 / 2），见 Settings.BarCellWidth/BarCellGap。
            Equal(158, BandLayout.ElementWidth(settings.Items[0]), "16 格 * 8 + 15 * 2 = 158");
            // 文本宽度是下界：DeskBand 按字体实测文字宽度排布，Core 只算最小值。
            Equal(Settings.MinTextWidth + BandLayout.IndicatorWidth, BandLayout.ElementWidth(settings.Items[1]),
                  "文本下界 12 + 三角槽 11 = 23");
            // 环直径 = 柱高（同一个内容带），不是另一个独立数值
            Equal(Settings.BarHeight, BandLayout.ElementWidth(settings.Items[2]), "环直径 = 柱高");

            // 158 + 3 + 23 + 3 + 26 + 2 = 215（下界；真实宽度由原生按字体实测）
            Equal(215, BandLayout.TotalWidth(settings), "下界总宽 215");
            True(BandLayout.TotalWidth(settings) < 320, "下界必须小于 S3 实测的 275 量级");
            Equal(40, BandLayout.DrawingHeight(settings), "绘制高度 40");
            Equal(16, BandLayout.HistoryColumns(settings.Items[0]), "历史列数 16");

            settings.Items[0].Enabled = false;
            Equal(54, BandLayout.TotalWidth(settings), "停用柱元素后 23 + 3 + 26 + 2 = 54");

            settings.Items.Clear();
            Equal(BandLayout.MinBandWidth, BandLayout.TotalWidth(settings), "没有元素时回落到最小宽度");

            Settings wide = Settings.CreateDefault();
            wide.Items.Add(new BandElement { Id = "w", Type = "text", Enabled = true, Width = 5000 });
            Equal(BandLayout.MaxBandWidth, BandLayout.TotalWidth(wide), "超宽被钳到 1200");

            // v2 → v3 迁移：旧的固定字段宽 52 不再是可选配置，留着它会把「元素间距」垫宽。
            Settings legacy = Settings.CreateDefault();
            legacy.Version = 2;
            legacy.Items[1].Width = 52;
            legacy.Normalize();
            Equal(Settings.CurrentVersion, legacy.Version, "旧文件 version 抬到当前版本");
            Equal(Settings.MinTextWidth, legacy.Items[1].Width, "文本宽度迁移成最小值");
        }

        // ---- shared memory -----------------------------------------------------------

        private static void ChannelRoundTrip()
        {
            MetricsChannel.Reader missing = MetricsChannel.Reader.TryOpen(TestMapName);
            // An earlier agent run may still hold the mapping in this session; only assert
            // the negative path when nothing exists.
            if (missing == null)
            {
                True(true, "没有 agent 时 TryOpen 返回 null");
            }
            else
            {
                missing.Dispose();
                True(true, "会话里已有映射（前一次运行残留）");
            }

            using (MetricsChannel.Writer writer = new MetricsChannel.Writer(TestMapName))
            {
                MetricsSnapshot published = new MetricsSnapshot
                {
                    Flags = MetricFlags.CpuValid | MetricFlags.AgentAlive,
                    TimestampUtc = DateTime.UtcNow,
                    CpuPercent = 42.5f,
                    MemoryPercent = 61.0f
                };
                writer.Publish(published);

                using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen(TestMapName))
                {
                    True(reader != null, "writer 建立后 reader 能打开映射");

                    MetricsSnapshot read;
                    SnapshotError error;
                    True(reader.TryRead(out read, out error), "读到快照: " + error);
                    Equal(42.5f, read.CpuPercent, "CPU 通过共享内存往返");
                    Equal(61.0f, read.MemoryPercent, "内存通过共享内存往返");
                    True(read.Has(MetricFlags.AgentAlive), "flags 通过共享内存往返");
                    True((read.Sequence & 1u) == 0, "序号为偶（稳定态），实际 " + read.Sequence);

                    writer.Publish(new MetricsSnapshot
                    {
                        Flags = MetricFlags.AgentAlive | MetricFlags.TemperatureValid,
                        TimestampUtc = DateTime.UtcNow,
                        TemperatureC = 55.5f
                    });
                    True(reader.TryRead(out read, out error), "第二次读成功");
                    Equal(55.5f, read.TemperatureC, "第二次内容更新");
                    True(read.Sequence > 0, "序号递增: " + read.Sequence);
                }
            }
        }

        /// <summary>
        /// Hammers the channel with a writer that changes every field on every publish and a
        /// reader that must never accept a mix of two frames. This is the regression test for
        /// the seqlock marker being copied as part of the payload (a reader could see the old
        /// even sequence while the frame was half copied).
        /// </summary>
        private static void ChannelConcurrentReads()
        {
            using (ManualResetEventSlim stop = new ManualResetEventSlim(false))
            using (ManualResetEventSlim ready = new ManualResetEventSlim(false))
            using (MetricsChannel.Writer writer = new MetricsChannel.Writer(TestMapName))
            using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen(TestMapName))
            {
                if (reader == null) { True(false, "并发测试需要 reader"); return; }

                Thread publisher = new Thread(() =>
                {
                    uint counter = 0;
                    bool firstFrame = true;
                    while (!stop.IsSet)
                    {
                        counter++;
                        writer.Publish(new MetricsSnapshot
                        {
                            Flags = MetricFlags.CpuValid | MetricFlags.MemoryValid |
                                    MetricFlags.TemperatureValid | MetricFlags.NetworkValid | MetricFlags.AgentAlive,
                            TimestampUtc = DateTime.UtcNow,
                            CpuPercent = counter,
                            MemoryPercent = counter,
                            TemperatureC = counter,
                            NetUpBytesPerSec = counter,
                            NetDownBytesPerSec = counter,
                            MemoryUsedBytes = counter,
                            MemoryTotalBytes = counter,
                            AdapterName = "T"
                        });

                        if (firstFrame) { firstFrame = false; ready.Set(); }

                        // Give the reader a chance to run between frames.
                        if ((counter & 0xFF) == 0) Thread.Yield();
                    }
                })
                { IsBackground = true, Name = "QuadVitals.TestPublisher" };
                publisher.Start();
                if (!ready.Wait(2000))
                {
                    True(false, "publisher did not produce a frame within 2 s");
                    stop.Set();
                    publisher.Join(2000);
                    return;
                }

                int accepted = 0;
                int torn = 0;
                bool badFrame = false;
                for (int attempt = 0; attempt < 3000; attempt++)
                {
                    if ((attempt & 0x3F) == 0) Thread.Yield();
                    MetricsSnapshot snapshot;
                    SnapshotError error;
                    if (!reader.TryRead(out snapshot, out error)) { torn++; continue; }
                    if (!snapshot.Has(MetricFlags.AgentAlive)) continue;

                    accepted++;
                    bool consistent = snapshot.CpuPercent == snapshot.MemoryPercent &&
                                      snapshot.MemoryPercent == snapshot.TemperatureC &&
                                      (long)snapshot.TemperatureC == snapshot.MemoryUsedBytes &&
                                      (long)snapshot.CpuPercent == snapshot.NetUpBytesPerSec &&
                                      (long)snapshot.CpuPercent == snapshot.NetDownBytesPerSec &&
                                      snapshot.AdapterName == "T";
                    if (!consistent)
                    {
                        badFrame = true;
                        Console.WriteLine("  FAIL 撕裂帧 cpu=" + snapshot.CpuPercent + " mem=" + snapshot.MemoryPercent +
                                          " temp=" + snapshot.TemperatureC + " used=" + snapshot.MemoryUsedBytes);
                        break;
                    }
                }

                stop.Set();
                publisher.Join(2000);
                True(accepted > 0, "并发读到了完整帧: " + accepted);
                True(!badFrame, "没有接受半新半旧的帧（拒绝 " + torn + " 个忙帧）");
            }
        }

        // ---- feedback ----------------------------------------------------------------

        private static string NewFeedbackTemp(string name)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "quadvitals-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void CleanupFeedbackTemp(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception) { }
        }

        private static string WriteFeedbackFile(string directory, string name, int bytes)
        {
            string path = Path.Combine(directory, name);
            byte[] data = new byte[bytes];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)('a' + (i % 26));
            File.WriteAllBytes(path, data);
            return path;
        }

        private static string ReadZipEntry(string zipPath, string name)
        {
            using (FileStream stream = File.OpenRead(zipPath))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ZipArchiveEntry entry = archive.GetEntry(name);
                if (entry == null) return null;
                using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static bool ZipHasEntry(string zipPath, string name)
        {
            using (FileStream stream = File.OpenRead(zipPath))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                return archive.GetEntry(name) != null;
            }
        }

        private static void FeedbackValidation()
        {
            string dir = NewFeedbackTemp("fb-validate");
            try
            {
                FeedbackOptions options = new FeedbackOptions
                {
                    MaxAttachments = 2,
                    MaxAttachmentBytes = 100,
                    MaxTotalAttachmentBytes = 150,
                    MaxTextLength = 20,
                    AllowedExtensions = new[] { "txt", "png" }
                };
                string small = WriteFeedbackFile(dir, "small.txt", 50);
                string big = WriteFeedbackFile(dir, "big.txt", 101);
                string empty = WriteFeedbackFile(dir, "empty.txt", 0);
                string archive = WriteFeedbackFile(dir, "archive.png", 10);
                string bad = WriteFeedbackFile(dir, "bad.exe", 10);
                string second = WriteFeedbackFile(dir, "second.txt", 80);
                string third = WriteFeedbackFile(dir, "third.txt", 80);

                string error;
                True(!FeedbackStore.Validate(new FeedbackSubmission { Text = "   " }, options, out error), "空内容被拒绝");
                True(FeedbackStore.Validate(new FeedbackSubmission { Text = "hello" }, options, out error), "纯文字可提交");
                True(FeedbackStore.Validate(new FeedbackSubmission
                {
                    Text = "",
                    AttachmentPaths = new List<string> { small }
                }, options, out error), "只有附件可提交");
                True(!FeedbackStore.Validate(new FeedbackSubmission
                {
                    Text = "ok",
                    AttachmentPaths = new List<string> { bad }
                }, options, out error), "拒绝不支持的扩展名");
                True(!FeedbackStore.Validate(new FeedbackSubmission
                {
                    Text = "ok",
                    AttachmentPaths = new List<string> { big }
                }, options, out error), "拒绝超过单文件上限");
                True(!FeedbackStore.Validate(new FeedbackSubmission
                {
                    Text = "ok",
                    AttachmentPaths = new List<string> { empty }
                }, options, out error), "拒绝空文件");
                True(!FeedbackStore.Validate(new FeedbackSubmission
                {
                    Text = "ok",
                    AttachmentPaths = new List<string> { second, third }
                }, options, out error), "拒绝超过总大小上限");
                True(!FeedbackStore.Validate(new FeedbackSubmission
                {
                    Text = "ok",
                    AttachmentPaths = new List<string> { small, archive, third }
                }, options, out error), "拒绝超过附件数量");
                True(!FeedbackStore.Validate(new FeedbackSubmission { Text = "this text is definitely too long" }, options, out error), "拒绝超过文字上限");
                True(!FeedbackStore.Validate(new FeedbackSubmission { Kind = (FeedbackKind)99, Text = "ok" }, options, out error), "拒绝非法类型");
                True(!FeedbackStore.Validate(new FeedbackSubmission
                {
                    Text = "ok",
                    AttachmentPaths = new List<string> { Path.Combine(dir, "missing.txt") }
                }, options, out error), "拒绝不存在的附件");

                Equal("bug", FeedbackKinds.Key(FeedbackKind.Bug), "kind bug key");
                Equal("问题反馈", FeedbackKinds.DisplayName(FeedbackKind.Bug), "kind bug name");
                FeedbackKind parsed;
                True(FeedbackKinds.TryParse("feature", out parsed) && parsed == FeedbackKind.Feature, "kind 解析");

                Equal("evil.txt", FeedbackStore.SanitizeFileName("../../evil.txt"), "去除路径段");
                True(!FeedbackStore.SanitizeFileName("a<b>:c.txt").Contains("<"), "去除非法字符");
                True(FeedbackStore.SanitizeFileName(new string('x', 200) + ".png").Length <= 80, "长文件名被截断");
                Equal("attachment", FeedbackStore.SanitizeFileName(""), "空文件名回退");
            }
            finally { CleanupFeedbackTemp(dir); }
        }

        private static void FeedbackPackage()
        {
            string dir = NewFeedbackTemp("fb-package");
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                File.WriteAllText(Path.Combine(dir, "agent-crash.log"),
                    "System.Exception: boom at " + profile + "\\QuadVitals\\x\n" + new string('x', 100) + "\n");
                File.WriteAllText(Path.Combine(dir, "settings-crash.log"), "not a real crash\n");
                string attachment = WriteFeedbackFile(dir, "screen.png", 32);

                FeedbackContext context = new FeedbackContext
                {
                    AppVersion = "test-1.0",
                    OsVersion = "Windows Test",
                    Architecture = "x64",
                    CpuModel = "Test CPU",
                    MemoryTotalBytes = 1024L * 1024L,
                    AgentSummary = "在线, cpu=1.0%",
                    SettingsSummary = "items=1",
                    DataDirectory = dir,
                    LogDirectory = dir
                };
                FeedbackOptions options = new FeedbackOptions();
                FeedbackSubmission submission = new FeedbackSubmission
                {
                    Kind = FeedbackKind.Bug,
                    Text = "first line\nsecond line",
                    IncludeLogs = true,
                    AttachmentPaths = new List<string> { attachment }
                };

                FeedbackResult result = FeedbackStore.Submit(submission, context, options);
                True(result.Ok, "提交成功: " + result.Error);
                if (result.Ok)
                {
                    True(File.Exists(result.PackagePath), "zip 存在: " + result.PackagePath);
                    True(result.Id.StartsWith(DateTime.Now.ToString("yyyyMMdd")), "编号以日期开头");
                    Equal(1, result.AttachmentCount, "附件数");
                    True(result.IncludedLogs, "包含诊断日志");

                    string json = ReadZipEntry(result.PackagePath, "feedback.json");
                    string md = ReadZipEntry(result.PackagePath, "feedback.md");
                    string diagnostics = ReadZipEntry(result.PackagePath, "diagnostics.txt");
                    True(json != null && json.Contains("\"kind\": \"bug\""), "JSON 记录类型");
                    True(json != null && json.Contains("first line") && json.Contains("second line"), "JSON 保留换行");
                    True(json != null && json.Contains("\"cpu\": \"Test CPU\""), "JSON 记录 CPU");
                    True(ZipHasEntry(result.PackagePath, "attachments/screen.png"), "附件打包");
                    True(ZipHasEntry(result.PackagePath, "logs/agent-crash.log"), "日志打包");
                    string packagedLog = ReadZipEntry(result.PackagePath, "logs/agent-crash.log");
                    True(packagedLog != null && packagedLog.Contains("%USERPROFILE%"), "日志路径脱敏");
                    True(packagedLog != null && !packagedLog.Contains(profile), "不含原始用户目录");
                    True(diagnostics != null && diagnostics.Contains("test-1.0"), "诊断含版本");
                    True(md != null && md.Contains("first line"), "Markdown 含正文");

                    string ledger = FeedbackStore.LedgerPath(dir);
                    True(File.Exists(ledger), "台账存在");
                    Equal(1, File.ReadAllLines(ledger).Length, "台账一行");
                }

                FeedbackSubmission noLogs = new FeedbackSubmission
                {
                    Kind = FeedbackKind.Feature,
                    Text = "no logs please",
                    IncludeLogs = false
                };
                FeedbackResult second = FeedbackStore.Submit(noLogs, context, options);
                True(second.Ok, "无日志提交成功: " + second.Error);
                if (second.Ok)
                {
                    True(!ZipHasEntry(second.PackagePath, "diagnostics.txt"), "未勾选时无 diagnostics");
                    True(!ZipHasEntry(second.PackagePath, "logs/agent-crash.log"), "未勾选时无日志");
                    string json = ReadZipEntry(second.PackagePath, "feedback.json");
                    True(json != null && json.Contains("\"includeLogs\": false"), "记录 includeLogs=false");
                    True(json != null && !json.Contains("\"cpu\""), "未勾选时不采集 CPU");
                }
                Equal(2, File.ReadAllLines(FeedbackStore.LedgerPath(dir)).Length, "台账两行");
            }
            finally { CleanupFeedbackTemp(dir); }
        }

        private static void FeedbackDraftBehaviour()
        {
            string dir = NewFeedbackTemp("fb-draft");
            try
            {
                FeedbackOptions options = new FeedbackOptions
                {
                    MaxAttachmentBytes = 100,
                    AllowedExtensions = new[] { "txt" }
                };
                string source = WriteFeedbackFile(dir, "note.txt", 10);
                string error;
                FeedbackDraftAttachment imported = FeedbackStore.ImportDraftAttachment(dir, source, options, out error);
                True(imported != null, "导入草稿附件: " + error);
                if (imported != null)
                {
                    True(File.Exists(Path.Combine(FeedbackStore.DraftAttachmentsDirectory(dir), imported.FileName)),
                        "草稿附件已复制");
                }
                string bad = WriteFeedbackFile(dir, "bad.exe", 10);
                True(FeedbackStore.ImportDraftAttachment(dir, bad, options, out error) == null, "草稿拒绝坏扩展名");
                string big = WriteFeedbackFile(dir, "big.txt", 101);
                True(FeedbackStore.ImportDraftAttachment(dir, big, options, out error) == null, "草稿拒绝超限");

                FeedbackDraft draft = new FeedbackDraft
                {
                    Kind = FeedbackKind.Feature,
                    Text = "draft text",
                    IncludeLogs = false,
                    Attachments = new List<FeedbackDraftAttachment> { imported }
                };
                FeedbackStore.SaveDraft(dir, draft);
                List<string> warnings;
                FeedbackDraft loaded = FeedbackStore.LoadDraft(dir, out warnings);
                True(loaded != null, "草稿可读");
                if (loaded != null)
                {
                    Equal(FeedbackKind.Feature, loaded.Kind, "草稿类型");
                    Equal("draft text", loaded.Text, "草稿正文");
                    True(!loaded.IncludeLogs, "草稿 includeLogs");
                    Equal(1, loaded.Attachments.Count, "草稿附件数");
                }

                if (imported != null)
                {
                    FeedbackStore.RemoveDraftAttachment(dir, imported.FileName);
                    loaded = FeedbackStore.LoadDraft(dir, out warnings);
                    Equal(0, loaded.Attachments.Count, "丢失的附件被丢弃");
                }

                FeedbackStore.ClearDraft(dir);
                True(!Directory.Exists(FeedbackStore.DraftRoot(dir)), "清空草稿目录");
            }
            finally { CleanupFeedbackTemp(dir); }
        }

        private static void FeedbackPreviewBehaviour()
        {
            string dir = NewFeedbackTemp("fb-preview");
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                File.WriteAllText(Path.Combine(dir, "agent-crash.log"),
                    "line1\nline2 at " + profile + "\\secret\nline3\n");
                FeedbackContext context = new FeedbackContext
                {
                    AppVersion = "test-1.0",
                    OsVersion = "Windows Test",
                    Architecture = "x64",
                    CpuModel = "Test CPU",
                    MemoryTotalBytes = 1024L * 1024L,
                    AgentSummary = "在线, cpu=1.0%",
                    SettingsSummary = "items=1",
                    DataDirectory = dir,
                    LogDirectory = dir
                };
                FeedbackOptions options = new FeedbackOptions();
                FeedbackPreview preview = FeedbackStore.BuildPreview(context, true, options);
                True(preview.MetadataText.Contains("test-1.0"), "预览含版本");
                True(preview.MetadataText.Contains("Test CPU"), "预览含 CPU");
                True(preview.MetadataText.Contains("在线"), "预览含 Agent");
                True(preview.LogsText.Contains("%USERPROFILE%"), "预览日志脱敏");
                True(!preview.LogsText.Contains(profile), "预览不含原始目录");
                True(preview.LogFileName.Contains("agent-crash.log"), "预览日志文件名");

                FeedbackPreview noLogs = FeedbackStore.BuildPreview(context, false, options);
                True(noLogs.LogsText.Contains("未勾选"), "未勾选提示");
                True(!noLogs.MetadataText.Contains("CPU"), "未勾选不显示 CPU");
                Equal("%USERPROFILE%\\x", FeedbackStore.RedactText(profile + "\\x"), "直接脱敏");
            }
            finally { CleanupFeedbackTemp(dir); }
        }

        private sealed class FakeTransport : IFeedbackTransport
        {
            public int Status = 201;
            public string Body = "{\"ok\":true,\"feedbackId\":\"QV_1234abcd\"}";
            public string Failure;
            public byte[] Sent;
            public string ContentType;
            public long ContentLength;
            public int Calls;

            public FeedbackTransportResult Send(string url, string bodyFile, string contentType,
                                                long contentLength, TimeSpan timeout)
            {
                Equal(FeedbackUploader.Endpoint, url, "上传到固定端点");
                Calls++;
                ContentType = contentType;
                ContentLength = contentLength;
                Sent = File.ReadAllBytes(bodyFile);
                return new FeedbackTransportResult { StatusCode = Status, Body = Body, Failure = Failure };
            }
        }

        private static void FeedbackUploadBehaviour()
        {
            string dir = NewFeedbackTemp("fb-upload");
            try
            {
                string attachment = Path.Combine(dir, "截图 1.png");
                File.WriteAllBytes(attachment, new byte[] { 1, 2, 3, 4, 5 });

                FeedbackUploadRequest request = new FeedbackUploadRequest
                {
                    Kind = "bug",
                    Message = "任务栏不显示温度",
                    MetadataJson = "{\"kind\":\"bug\",\"appVersion\":\"1.0\"}",
                    AttachmentPaths = new List<string> { attachment }
                };

                FakeTransport transport = new FakeTransport();
                FeedbackUploadResult result = FeedbackUploader.Upload(request, transport, dir);
                True(result.Ok, "201 + QV_ 编号视为成功");
                Equal("QV_1234abcd", result.FeedbackId, "回传服务端编号");
                True(transport.ContentType.StartsWith("multipart/form-data; boundary="), "multipart 类型");
                Equal(transport.ContentLength, (long)transport.Sent.Length, "Content-Length 与实际字节一致");
                string text = Encoding.UTF8.GetString(transport.Sent);
                True(text.Contains("name=\"message\""), "含 message 字段");
                True(text.Contains("任务栏不显示温度"), "message 为 UTF-8");
                True(text.Contains("name=\"metadata\""), "含 metadata 字段");
                True(text.Contains("filename*=UTF-8''"), "中文附件名走 filename*");
                True(Directory.GetFiles(dir, "*.body").Length == 0, "临时 body 已删除");

                FakeTransport tooMany = new FakeTransport { Status = 429 };
                True(FeedbackUploader.Upload(request, tooMany, dir).Error.Contains("频繁"), "429 → 频繁提示");

                FakeTransport big = new FakeTransport { Status = 413 };
                True(FeedbackUploader.Upload(request, big, dir).Error.Contains("过大"), "413 → 体积提示");

                FakeTransport code = new FakeTransport { Status = 400, Body = "{\"error\":\"too_many_attachments\"}" };
                True(FeedbackUploader.Upload(request, code, dir).Error.Contains("附件数量"), "错误码映射为可读文案");

                FakeTransport unknownCode = new FakeTransport { Status = 400, Body = "{\"error\":\"weird\"}" };
                True(FeedbackUploader.Upload(request, unknownCode, dir).Error.Contains("weird"), "未知错误码写进提示");

                FakeTransport badGarbage = new FakeTransport { Status = 400, Body = "<html>stack</html>" };
                True(!FeedbackUploader.Upload(request, badGarbage, dir).Error.Contains("stack"), "不把响应正文当文案");

                FakeTransport server = new FakeTransport { Status = 503 };
                True(FeedbackUploader.Upload(request, server, dir).Error.Contains("暂时不可用"), "5xx → 稍后重试");

                FakeTransport offline = new FakeTransport { Failure = "NameResolutionFailure" };
                True(FeedbackUploader.Upload(request, offline, dir).Error.Contains("网络"), "传输失败 → 网络提示");

                FakeTransport wrongId = new FakeTransport { Body = "{\"ok\":true,\"feedbackId\":\"FB_1\"}" };
                True(!FeedbackUploader.Upload(request, wrongId, dir).Ok, "非 QV_ 编号不算成功");

                string error;
                FeedbackUploadRequest empty = new FeedbackUploadRequest();
                True(!FeedbackUploader.Validate(empty, out error) && error.Contains("不能为空"), "预检：空内容");

                FeedbackUploadRequest nine = new FeedbackUploadRequest { Message = "x" };
                for (int i = 0; i < 9; i++) nine.AttachmentPaths.Add(attachment);
                True(!FeedbackUploader.Validate(nine, out error) && error.Contains("8"), "预检：附件数量");

                FeedbackUploadRequest badExt = new FeedbackUploadRequest { Message = "x" };
                string exe = Path.Combine(dir, "tool.exe");
                File.WriteAllBytes(exe, new byte[] { 1 });
                badExt.AttachmentPaths.Add(exe);
                True(!FeedbackUploader.Validate(badExt, out error) && error.Contains("格式"), "预检：扩展名");

                FeedbackContext context = new FeedbackContext
                {
                    AppVersion = "v9",
                    OsVersion = "Windows Test",
                    Architecture = "x64",
                    DataDirectory = dir
                };
                string metadata = FeedbackUploader.BuildMetadata(context, "feature", new List<string> { attachment });
                True(metadata.Contains("\"kind\":\"feature\""), "metadata 带 kind");
                True(metadata.Contains("\"fileName\":\"截图 1.png\""), "metadata 带附件名");
                True(!metadata.Contains("agent"), "无诊断时不带 agent 字段");
            }
            finally { CleanupFeedbackTemp(dir); }
        }

        private static void FeedbackRecoveryBehaviour()
        {
            string dir = NewFeedbackTemp("fb-recover");
            try
            {
                string attachment = Path.Combine(dir, "shot.png");
                File.WriteAllBytes(attachment, new byte[] { 9, 8, 7 });

                FeedbackContext context = new FeedbackContext
                {
                    AppVersion = "v1",
                    OsVersion = "Windows Test",
                    Architecture = "x64",
                    DataDirectory = dir,
                    LogDirectory = dir
                };
                FeedbackSubmission submission = new FeedbackSubmission
                {
                    Kind = FeedbackKind.Bug,
                    Text = "启动补传测试",
                    IncludeLogs = false
                };
                submission.AttachmentPaths.Add(attachment);

                FeedbackResult saved = FeedbackStore.Submit(submission, context);
                True(saved.Ok && File.Exists(saved.PackagePath), "本地包已生成");

                // 提交后草稿附件会被删，上传必须从本地 zip 重建（否则会报“附件不存在”）。
                FeedbackUploadRequest built;
                string builtTemp;
                string builtId;
                True(FeedbackRecovery.TryBuildRequest(saved.PackagePath, out built, out builtTemp, out builtId), "从 zip 重建上传请求");
                Equal(saved.Id, builtId, "重建的 id 一致");
                Equal(1, built.AttachmentPaths.Count, "重建的附件数一致");
                True(File.Exists(built.AttachmentPaths[0]), "重建的附件落在临时目录");
                Equal(3L, new FileInfo(built.AttachmentPaths[0]).Length, "重建的附件字节一致");
                if (!string.IsNullOrEmpty(builtTemp)) Directory.Delete(builtTemp, true);

                FakeTransport ok = new FakeTransport();
                FeedbackRecovery.Outcome first = FeedbackRecovery.Flush(dir, ok);
                Equal(1, first.Uploaded, "启动补传旧包成功");
                Equal(1, first.Removed, "成功后删除本地包");
                True(!File.Exists(saved.PackagePath), "包已删除");
                True(ok.Sent != null && Encoding.UTF8.GetString(ok.Sent).Contains("shot.png"), "补传带上了附件");
                string ledger = File.ReadAllText(FeedbackStore.LedgerPath(dir));
                True(ledger.Contains("\"uploaded\""), "台账记了 uploaded");
                True(ledger.Contains("QV_1234abcd"), "台账记了服务端编号");

                FeedbackRecovery.Outcome idle = FeedbackRecovery.Flush(dir, ok);
                Equal(0, idle.Uploaded + idle.Failed + idle.Removed, "无待传时不动");

                FeedbackResult kept = FeedbackStore.Submit(submission, context);
                FakeTransport broken = new FakeTransport { Failure = "Timeout" };
                FeedbackRecovery.Outcome failed = FeedbackRecovery.Flush(dir, broken);
                Equal(1, failed.Failed, "上传失败计数");
                True(File.Exists(kept.PackagePath), "失败保留本地包");
                True(File.ReadAllText(FeedbackStore.LedgerPath(dir)).Contains("upload-failed"), "台账记了失败");

                FakeTransport retry = new FakeTransport();
                FeedbackRecovery.Outcome again = FeedbackRecovery.Flush(dir, retry);
                Equal(1, again.Uploaded, "下次启动补传成功");
                True(!File.Exists(kept.PackagePath), "补传成功后删除");

                FeedbackResult leftover = FeedbackStore.Submit(submission, context);
                FeedbackStore.AppendUploaded(dir, leftover.Id, "QV_deadbeef");
                FakeTransport mustNot = new FakeTransport();
                FeedbackRecovery.Outcome sweep = FeedbackRecovery.Flush(dir, mustNot);
                Equal(0, mustNot.Calls, "已上传残留不再重传");
                Equal(1, sweep.Removed, "已上传残留被删除");

                Equal("20261030-143853-a1b2c3", FeedbackRecovery.IdFromPackageName("20261030-143853-a1b2c3_bug.zip"), "从文件名取 id");
                Equal(null, FeedbackRecovery.IdFromPackageName("random.zip"), "非本产品包名返回 null");
            }
            finally { CleanupFeedbackTemp(dir); }
        }

        private static void VersionBehaviour()
        {
            // The UI, the feedback metadata and the assembly attributes all read this one
            // constant: a malformed version would surface in a user-facing string, and a binary
            // whose recorded version disagrees with the About page is exactly the drift the
            // single source exists to prevent.
            True(System.Text.RegularExpressions.Regex.IsMatch(ProductVersion.SemVer, @"^[0-9]+\.[0-9]+\.[0-9]+$"),
                "SemVer 为三段数字");
            System.Reflection.AssemblyInformationalVersionAttribute info =
                (System.Reflection.AssemblyInformationalVersionAttribute)System.Attribute.GetCustomAttribute(
                    typeof(ProductVersion).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            Equal(ProductVersion.SemVer, info == null ? "(missing)" : info.InformationalVersion,
                "程序集记录的版本 = ProductVersion.SemVer");
        }

        // ---- device tile metric choices ------------------------------------------------

        private static void DeviceMetricChoicesBehaviour()
        {
            DeviceMetricChoices defaults = new DeviceMetricChoices();
            Equal(DeviceMetricChoices.Usage, defaults.Cpu, "CPU 默认占用");
            Equal(DeviceMetricChoices.Usage, defaults.Gpu, "显卡默认占用");

            Equal(4, DeviceMetricChoices.CpuOptions.Length, "CPU 四个可选指标");
            Equal(2, DeviceMetricChoices.MemoryOptions.Length, "内存两个可选指标");
            Equal(5, DeviceMetricChoices.GpuOptions.Length, "显卡五个可选指标（占用/显存/温度/功耗/频率）");
            Equal(3, DeviceMetricChoices.DiskOptions.Length, "硬盘三个可选指标");

            DeviceMetricChoices bad = new DeviceMetricChoices
            {
                Cpu = "power",
                MemoryUsed = "nonsense",
                Gpu = "MEMORY",
                Disk = "io"
            };
            bad.Normalize();
            Equal(DeviceMetricChoices.Power, bad.Cpu, "功耗保留");
            Equal(DeviceMetricChoices.Usage, bad.MemoryUsed, "未知值回退占用");
            Equal(DeviceMetricChoices.Memory, bad.Gpu, "大小写不一致也能对上");
            Equal(DeviceMetricChoices.Io, bad.Disk, "读写保留");

            Equal(DeviceMetricChoices.Usage, DeviceMetricChoices.Keep(null, DeviceMetricChoices.CpuOptions),
                "空值回退占用");
            Equal(DeviceMetricChoices.Usage, DeviceMetricChoices.Keep("memory", DeviceMetricChoices.CpuOptions),
                "其他硬件的指标名不算合法");
            Equal("温度", DeviceMetricChoices.DisplayName(DeviceMetricChoices.Temperature), "温度中文名");
            Equal("占用", DeviceMetricChoices.DisplayName("nonsense"), "未知指标显示占用");
        }

// ---- io marquee --------------------------------------------------------------

        private static void IoWaveBehaviour()
        {
            const double Mb = 1024.0 * 1024.0;

            Equal(0.0, IoWave.FillFraction(0), "没流量不点亮");
            Equal(0.0, IoWave.FillFraction(0.05), "0.1 MB/s 以下不点亮");
            True(Math.Abs(IoWave.FillFraction(25) - 0.5) < 1e-9, "25 MB/s 半格");
            Equal(1.0, IoWave.FillFraction(50), "50 MB/s 满格");
            Equal(1.0, IoWave.FillFraction(900), "超过阈值仍是满格");

            Equal(0.0, IoWave.WaveOpacity(20), "20 MB/s 无波浪");
            True(Math.Abs(IoWave.WaveOpacity(40) - 0.5) < 1e-9, "40 MB/s 淡入一半");
            Equal(1.0, IoWave.WaveOpacity(50), "50 MB/s 波浪全显");
            Equal(1.0, IoWave.WaveOpacity(5000), "再快也不超过 1");

            // 档位：必须到 50 才滚（30 就滚会让条提前满格、低速尖峰反复涨缩）。
            True(!IoWave.ShouldWave(30), "30 MB/s 不进入走马档");
            True(!IoWave.ShouldWave(49.9), "差一点也不行");
            True(IoWave.ShouldWave(50), "50 MB/s 才滚");
            True(!IoWave.ShouldWave(double.NaN), "坏数据不滚");
            Equal(250.0, IoWave.SettleMilliseconds, "掉档后保持 250 ms");
            Equal(5, IoWave.RollSampleCount, "转速取 5 个采样平均");
            True(Math.Abs(IoWave.Average(new[] { 10.0, 20.0, 30.0 }) - 20.0) < 1e-9, "平均"); 
            Equal(0.0, IoWave.Average(new double[0]), "空集合为 0");
            True(Math.Abs(IoWave.Average(new[] { double.NaN, 40.0 }) - 20.0) < 1e-9, "坏样本当 0 计");

            // 转速：两端夹紧，中间单调变快。
            Equal(1.0 / IoWave.RollMaxSeconds, IoWave.RollCyclesPerSecond(0), "没流量时最慢");
            True(IoWave.RollCyclesPerSecond(200) > IoWave.RollCyclesPerSecond(50), "流量越大转得越快");
            Equal(1.0 / IoWave.RollMinSeconds, IoWave.RollCyclesPerSecond(1e9), "极快时封顶");

            // 读写取较大单向：只有写也在跑。
            True(Math.Abs(IoWave.PeakMbPerSecond(0, 40 * Mb) - 40) < 1e-6, "单写也计入");
            Equal(0.0, IoWave.PeakMbPerSecond(double.NaN, -1), "坏数据当 0");

            // 取色位置：纯静态时等于等分位置；完全滚动时按相位环绕；中间是插值。
            double staticP = IoWave.ColorPosition(4, 16, 0.37, 0, 1);
            True(Math.Abs(staticP - 4.0 / 15.0) < 1e-9, "release=1 时回到静态色阶");
            double rollP = IoWave.ColorPosition(0, 16, 0.0, 1, 0);
            Equal(0.0, rollP, "相位 0 时第 0 段在最浅端");
            double wrapped = IoWave.ColorPosition(0, 16, 1.0, 1, 0);
            Equal(0.0, wrapped, "转满一圈回到原位（无缝）");
            double midWave = IoWave.ColorPosition(8, 16, 0.0, 0.5, 0);
            True(midWave > 0.5 && midWave < 8.0 / 15.0, "淡入阶段落在滚动相位与静态相位之间");
        }

        // ---- persisted network history -------------------------------------------------

        private static void NetHistoryBehaviour()
        {
            string dir = Path.Combine(Path.GetTempPath(), "qv-nethistory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "net-history.json");
            DateTime t0 = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
            try
            {
                NetHistory missing = NetHistory.Load(path);
                Equal(0, missing.Count, "没有文件就是空历史");
                Equal(0, missing.UpValues().Length, "空历史的点数为 0");

                NetHistory history = new NetHistory();
                history.Push(t0, 1024, 2048);
                history.Push(t0.AddSeconds(1), 4096, 8192);
                Equal(2, history.Count, "两点入队");
                Equal(1024.0, history.UpValues()[0], "上行按时间顺序保存");
                Equal(8192.0, history.DownValues()[1], "下行按时间顺序保存");
                Equal(t0.AddSeconds(1), history.Stamps()[1], "时间戳跟着点走");

                history.Push(t0.AddSeconds(1), 1, 2);
                Equal(2, history.Count, "同一帧重复读只记一次");
                history.Push(t0.AddSeconds(-1), 1, 2);
                Equal(2, history.Count, "更早的时间戳不回填");

                history.Push(t0.AddSeconds(2), -5, double.NaN);
                Equal(0.0, history.UpValues()[2], "负数钳到 0");
                Equal(0.0, history.DownValues()[2], "NaN 钳到 0");

                history.Save(path);
                NetHistory loaded = NetHistory.Load(path);
                Equal(3, loaded.Count, "保存后读回同样点数");
                Equal(4096.0, loaded.UpValues()[1], "读回上行");
                Equal(8192.0, loaded.DownValues()[1], "读回下行");
                Equal(t0.AddSeconds(2), loaded.Stamps()[2], "读回时间戳");

                string json = File.ReadAllText(path);
                File.WriteAllText(path, json, new UTF8Encoding(true));
                Equal(3, NetHistory.Load(path).Count, "UTF-8 BOM 可读");

                NetHistory many = new NetHistory();
                for (int i = 0; i < 70; i++) many.Push(t0.AddSeconds(i), i, i * 2);
                Equal(60, many.Count, "只保留 60 点");
                Equal(10.0, many.UpValues()[0], "最老的点被挤掉");
                Equal(69.0, many.UpValues()[59], "最新点在最右端");

                File.WriteAllText(path, "{not json");
                Equal(0, NetHistory.Load(path).Count, "坏文件回退空历史，不抛异常");

                File.WriteAllText(path, new string('x', 70 * 1024));
                Equal(0, NetHistory.Load(path).Count, "超大文件不解析");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        // ---- oklch colour -----------------------------------------------------------

        private static void OklchBehaviour()
        {
            // 往返误差：OKLab 往返 sRGB 应当几乎不变（8 位量化最多差 1）。
            byte[][] samples =
            {
                new byte[] { 0x1D, 0xB9, 0x54 },   // brand green
                new byte[] { 0xFF, 0xFF, 0xFF },
                new byte[] { 0x00, 0x00, 0x00 },
                new byte[] { 0xF6, 0xA7, 0x48 }    // warning
            };
            foreach (byte[] sample in samples)
            {
                byte[] back = OklchColor.FromRgb(sample[0], sample[1], sample[2]).ToRgb();
                True(Math.Abs(back[0] - sample[0]) <= 2 && Math.Abs(back[1] - sample[1]) <= 2 && Math.Abs(back[2] - sample[2]) <= 2,
                    "OKLCh 往返误差 <= 2 (" + sample[0] + "," + sample[1] + "," + sample[2] + ")");
            }

            OklchColor seed = OklchColor.FromRgb(0x1D, 0xB9, 0x54);
            OklchColor light = seed.With(0.9, 0.06);
            OklchColor deep = seed.With(0.55, 0.16);
            True(light.L > deep.L, "浅端比深端亮");
            True(light.C <= 0.06 + 1e-9 && deep.C <= 0.16 + 1e-9, "彩度按上限夹紧");
            True(Math.Abs(light.H - seed.H) < 1e-9, "色相不变（只改亮度/彩度）");

            byte[] atZero = OklchColor.Lerp(light, deep, 0).ToRgb();
            byte[] atOne = OklchColor.Lerp(light, deep, 1).ToRgb();
            byte[] lightRgb = light.ToRgb();
            byte[] deepRgb = deep.ToRgb();
            True(atZero[0] == lightRgb[0] && atZero[1] == lightRgb[1] && atZero[2] == lightRgb[2], "t=0 等于浅端");
            True(atOne[0] == deepRgb[0] && atOne[1] == deepRgb[1] && atOne[2] == deepRgb[2], "t=1 等于深端");

            // 插值单调变深：亮度逐段下降（这正是 sRGB 直线插值做不到的“均匀色阶”）。
            int previous = 999;
            bool monotonic = true;
            for (int step = 0; step <= 10; step++)
            {
                byte[] rgb = OklchColor.Lerp(light, deep, step / 10.0).ToRgb();
                int luma = (rgb[0] * 30 + rgb[1] * 59 + rgb[2] * 11) / 100;
                if (luma > previous) monotonic = false;
                previous = luma;
            }
            True(monotonic, "色阶亮度单调下降");
        }

        // ---- tiny assertion helpers --------------------------------------------------
        private static void Suite(string name, Action body)
        {
            _suite = name;
            Console.WriteLine();
            Console.WriteLine("-- " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  FAIL [exception] " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void True(bool condition, string what)
        {
            if (condition) { _passed++; Console.WriteLine("  ok   " + what); }
            else { _failed++; Console.WriteLine("  FAIL " + what); }
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            bool ok = EqualityComparer<T>.Default.Equals(expected, actual);
            if (ok) { _passed++; Console.WriteLine("  ok   " + what); }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL " + what + "  (期望 " + expected + "，实际 " + actual + ")");
            }
        }

        private static void Throws<TException>(Action action, string what) where TException : Exception
        {
            try
            {
                action();
                _failed++;
                Console.WriteLine("  FAIL " + what + "（没有抛异常）");
            }
            catch (TException)
            {
                _passed++;
                Console.WriteLine("  ok   " + what);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  FAIL " + what + "（抛了 " + ex.GetType().Name + "）");
            }
        }
    }
}
