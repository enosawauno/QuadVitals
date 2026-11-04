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
            Suite("MetricsSnapshot 风扇通道", SnapshotFans);
            Suite("MetricsSnapshot 逐盘表", SnapshotVolumes);
            Suite("MetricFormat 速率", FormatRate);
            Suite("MetricFormat 百分比/温度/字节", FormatOthers);
            Suite("HardwareNaming 硬件签名", HardwareNamingBehaviour);
            Suite("EmaFilter", EmaBehaviour);
            Suite("MetricCatalog", Catalog);
            Suite("MetricTone 阈值", ToneThresholds);
            Suite("MetricTone 温度分档", TemperatureZoneThresholds);
            Suite("FanZones 风扇分档", FanZoneThresholds);
            Suite("FanLabels 风扇命名", FanLabelBehaviour);
            Suite("FanDiagnosis 风扇空态归因", FanDiagnosisBehaviour);
            Suite("FanToneLatch 复位", FanToneLatchReset);
            Suite("animationMode 动效模式", AnimationModeBehaviour);
            Suite("FanToneLatch 档位防抖", FanToneLatchBehaviour);
            Suite("Settings 柱状几何写死", BarGeometryIsFixed);
            Suite("列数跟随 CPU 核心/线程", ColumnsFollowCpu);
            Suite("Settings 默认值", DefaultSettings);
            Suite("Settings 风扇通道", FanChannelSetting);
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
            Suite("MetricSanity 数据合理性", MetricSanityBehaviour);
            Suite("MetricWatchdog 指标兜底", MetricWatchdogBehaviour);
            Suite("AgentDiagnostics 诊断框", AgentDiagnosticsBehaviour);
            Suite("AgentControl 控制事件", AgentControlBehaviour);
            Suite("AgentLaunchPlan 启动路径", AgentLaunchPlanBehaviour);
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
            Equal(MetricsSnapshot.SizeBytes, bytes.Length, "快照固定 404 字节（v4 布局）");
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
            Equal(MetricsSnapshot.SizeBytes, powerBytes.Length, "带功耗的快照仍是 404 字节");
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
            Equal(MetricsSnapshot.SizeBytes, bytes.Length, "加显卡/磁盘/磁盘 IO/逐盘表/GPU 功耗后仍是 404 字节");
            Equal((ushort)4, MetricsSnapshot.VersionOf(bytes), "version 升到 4");
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

        private static void SnapshotFans()
        {
            MetricsSnapshot source = new MetricsSnapshot
            {
                TimestampUtc = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc),
                Flags = MetricFlags.AgentAlive | MetricFlags.FanValid,
                Fans = new[]
                {
                    new MetricsSnapshot.FanReading { Kind = FanKinds.CpuFan, Channel = 2, Rpm = 1028, Valid = true },
                    new MetricsSnapshot.FanReading { Kind = FanKinds.AioPump, Channel = 7, Rpm = 3358, Valid = true }
                }
            };

            byte[] bytes = source.ToBytes();
            Equal(404, MetricsSnapshot.SizeBytes, "v4 快照 404 字节");
            Equal(368, MetricsSnapshot.GpuTailSizeBytes, "v3 尾长 368");
            Equal(368, MetricsSnapshot.FanCountOffset, "风扇数量偏移 368");
            Equal(372, MetricsSnapshot.FanTableOffset, "风扇表起始 372");
            Equal(4, MetricsSnapshot.FanSlotBytes, "每路 4 字节");
            Equal((ushort)2, MetricsSnapshot.ReadUInt16(bytes, 368), "写入两路风扇");

            MetricsSnapshot parsed;
            SnapshotError error;
            True(MetricsSnapshot.TryParse(bytes, out parsed, out error), "带风扇的快照可解析");
            Equal(2, parsed.Fans.Length, "两路风扇往返");
            Equal(2, parsed.Fans[0].Channel, "通道号往返");
            Equal(FanKinds.CpuFan, parsed.Fans[0].Kind, "头部种类往返");
            Equal(FanKinds.AioPump, parsed.Fans[1].Kind, "水泵种类往返");
            Equal(1028, parsed.Fans[0].Rpm, "转速往返");
            Equal(3358, parsed.Fans[1].Rpm, "第二路转速往返");
            True(parsed.Has(MetricFlags.FanValid), "风扇有效位往返");

            // A v3 agent writes 368 bytes with version 3: no fans, but the GPU tail still reads.
            byte[] v3 = new byte[MetricsSnapshot.GpuTailSizeBytes];
            Array.Copy(bytes, v3, v3.Length);
            MetricsSnapshot.WriteUInt16(v3, 4, 3);
            MetricsSnapshot.WriteUInt16(v3, 6, (ushort)MetricFlags.AgentAlive);
            MetricsSnapshot.WriteUInt16(v3, 364, 1234);
            True(MetricsSnapshot.TryParse(v3, out parsed, out error), "v3 帧仍可解析");
            Equal(0, parsed.Fans.Length, "v3 帧没有风扇");
            True(!parsed.Has(MetricFlags.FanValid), "v3 帧风扇无效");
            Equal(123.4f, parsed.GpuPowerWatts, "v3 帧尾部 GPU 功耗仍读得到");

            List<MetricsSnapshot.FanReading> many = new List<MetricsSnapshot.FanReading>();
            for (int i = 0; i < 12; i++)
            {
                many.Add(new MetricsSnapshot.FanReading { Kind = FanKinds.Unknown, Channel = i, Rpm = 1000 + i, Valid = true });
            }
            MetricsSnapshot overflow = new MetricsSnapshot { Fans = many.ToArray() };
            True(MetricsSnapshot.TryParse(overflow.ToBytes(), out parsed, out error), "超量风扇可解析");
            Equal(MetricsSnapshot.MaxFans, parsed.Fans.Length, "超过 8 路只保留前 8 路");

            byte[] v2 = new byte[MetricsSnapshot.GpuTailSizeBytes];
            Array.Copy(bytes, v2, v2.Length);
            MetricsSnapshot.WriteUInt16(v2, 4, 2);
            True(!MetricsSnapshot.TryParse(v2, out parsed, out error), "v2 帧被拒绝");
            Equal(SnapshotError.VersionMismatch, error, "v2 错误码");
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
