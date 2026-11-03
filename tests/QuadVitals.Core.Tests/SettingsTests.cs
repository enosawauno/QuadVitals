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

        private static void FanZoneThresholds()
        {
            Equal(2000, FanZones.NominalMaxRpm, "满量程 2000 RPM");
            Equal(MetricTone.Normal, FanZones.Tone(0), "停转算正常（空通道不告警）");
            Equal(MetricTone.Normal, FanZones.Tone(1399), "1400 以下绿");
            Equal(MetricTone.Warning, FanZones.Tone(1400), "1400 起黄");
            Equal(MetricTone.Warning, FanZones.Tone(1599), "1600 前仍是黄");
            Equal(MetricTone.Danger, FanZones.Tone(1600), "1600 起红");
            Equal(MetricTone.Danger, FanZones.Tone(3358), "高转速红（水泵）");
            Equal(0.0, FanZones.Fraction(0), "0 RPM = 0");
            Equal(0.7, FanZones.Fraction(1400), "1400 = 70%");
            Equal(1.0, FanZones.Fraction(9000), "超过满量程钳到 1");
        }

        private static void FanToneLatchBehaviour()
        {
            FanToneLatch latch = new FanToneLatch();
            Equal(MetricTone.Normal, latch.Update(1300), "1300 绿");
            Equal(MetricTone.Normal, latch.Update(1399), "1399 仍绿");
            Equal(MetricTone.Normal, latch.Update(1401), "1401 仍绿（临界抖动不换档）");
            Equal(MetricTone.Normal, latch.Update(1459), "1459 仍绿");
            Equal(MetricTone.Warning, latch.Update(1460), "1460 越过边界才黄");
            Equal(MetricTone.Warning, latch.Update(1401), "回落 1401 仍是黄");
            Equal(MetricTone.Warning, latch.Update(1340), "1340 仍是黄");
            Equal(MetricTone.Normal, latch.Update(1339), "1339 才回绿");
            Equal(MetricTone.Danger, latch.Update(1660), "1660 越过红界");
            Equal(MetricTone.Danger, latch.Update(1550), "1550 仍是红");
            Equal(MetricTone.Danger, latch.Update(1540), "1540 仍在红界内");
            Equal(MetricTone.Warning, latch.Update(1539), "1539 才回黄");
        }

        private static void FanLabelBehaviour()
        {
            True(FanKinds.IsPump(FanKinds.AioPump), "AIO_PUMP 是水泵");
            True(FanKinds.IsPump(FanKinds.WaterPump), "W_PUMP+ 是水泵");
            True(!FanKinds.IsPump(FanKinds.CpuFan), "CPU_FAN 不是水泵");
            True(!FanKinds.IsPump(FanKinds.Unknown), "未知通道不按水泵处理");

            Equal("CPU_FAN", FanLabels.For(FanKinds.CpuFan, 2), "CPU 头部标签");
            Equal("CPU_OPT", FanLabels.For(FanKinds.CpuOpt, 1), "CPU_OPT 标签");
            Equal("CHA_FAN3", FanLabels.For(FanKinds.ChassisFan3, 5), "机箱风扇标签");
            Equal("AIO_PUMP", FanLabels.For(FanKinds.AioPump, 7), "水泵标签");
            Equal("通道 4", FanLabels.For(FanKinds.Unknown, 4), "未知板型退回通道号");

            const string board = "ROG STRIX Z690-E GAMING WIFI";
            Equal(FanKinds.CpuOpt, FanLabels.KindForBoard(board, 1), "通道 1 = CPU_OPT");
            Equal(FanKinds.CpuFan, FanLabels.KindForBoard(board, 2), "通道 2 = CPU_FAN");
            Equal(FanKinds.ChassisFan1, FanLabels.KindForBoard(board, 3), "通道 3 = CHA_FAN1");
            Equal(FanKinds.ChassisFan4, FanLabels.KindForBoard(board, 6), "通道 6 = CHA_FAN4");
            Equal(FanKinds.AioPump, FanLabels.KindForBoard(board, 7), "通道 7 = AIO_PUMP");
            Equal(FanKinds.Unknown, FanLabels.KindForBoard(board, 9), "越界通道不编名字");
            Equal(FanKinds.Unknown, FanLabels.KindForBoard("B660M", 2), "未登记板型不编名字");
            Equal(FanKinds.Unknown, FanLabels.KindForBoard(null, 2), "空板型不编名字");
        }

        private static void AnimationModeBehaviour()
        {
            Settings fresh = Settings.CreateDefault();
            Equal(Settings.AnimationHover, fresh.AnimationMode, "默认静止时不动");
            True(Settings.IsKnownAnimationMode(Settings.AnimationHover), "hover 合法");
            True(Settings.IsKnownAnimationMode("ALWAYS"), "大小写不敏感");
            True(!Settings.IsKnownAnimationMode("sometimes"), "乱写的值不合法");
            True(!Settings.IsKnownAnimationMode(null), "空值不合法");

            Settings odd = Settings.CreateDefault();
            odd.AnimationMode = "sometimes";
            odd.Normalize();
            Equal(Settings.AnimationHover, odd.AnimationMode, "非法值回退为悬停才动");

            Settings kept = Settings.CreateDefault();
            kept.AnimationMode = Settings.AnimationAlways;
            kept.Normalize();
            Equal(Settings.AnimationAlways, kept.AnimationMode, "常驻模式不会被 Normalize 改掉");
        }

        private static void FanToneLatchReset()
        {
            FanToneLatch latch = new FanToneLatch();
            Equal(MetricTone.Danger, latch.Update(1700), "1700 进红档");
            Equal(MetricTone.Danger, latch.Update(1610), "回差内仍是红");
            // 空态、停转、换通道之后要重新从绿色开始判，否则复工的第一帧还是上一路的红。
            latch.Reset();
            Equal(MetricTone.Normal, latch.Tone, "复位回绿");
            Equal(MetricTone.Normal, latch.Update(1300), "复位后 1300 是绿");
        }

        private static void FanDiagnosisBehaviour()
        {
            // 没有风扇通道 = 主板本来就没有风扇头，日志不该编一个“被占用”的理由。
            Equal(false, FanDiagnosis.HasChannels(0), "没有通道");
            True(FanDiagnosis.HasChannels(7), "有 7 路通道");
            Equal(null, FanDiagnosis.ExplainAllZero(0, 0), "没有通道不解释");
            Equal(null, FanDiagnosis.ExplainAllZero(7, 2), "有通道在转就不解释");
            string explained = FanDiagnosis.ExplainAllZero(7, 0);
            True(explained != null && explained.Contains("7"), "全 0 时带通道数: " + explained);
            True(explained.Contains("占用"), "全 0 时点出 SuperIO 被占用这种可能");
            True(explained.Contains("停转"), "全 0 时也说“可能就是停转”");

            True(FanDiagnosis.AnySpinning(7, 1), "有一路在转");
            True(!FanDiagnosis.AnySpinning(7, 0), "全 0 不算在转");
            True(!FanDiagnosis.AnySpinning(0, 0), "没通道更不算");

            True(FanDiagnosis.IsPlausible(1028), "正常转速可发布");
            True(FanDiagnosis.IsPlausible(1), "1 RPM 也可发布");
            True(FanDiagnosis.IsPlausible(0), "0 RPM 要发布（停转可见）");
            True(!FanDiagnosis.IsPlausible(double.NaN), "NaN 不发布");
            True(!FanDiagnosis.IsPlausible(-5), "负值不发布");
            True(!FanDiagnosis.IsPlausible(25000), "超出合理上限不发布");
            True(!FanDiagnosis.IsPlausible(double.PositiveInfinity), "无穷不发布");
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

        private static void FanChannelSetting()
        {
            Settings settings = Settings.CreateDefault();
            settings.FanChannel = 7;
            settings.Normalize();
            Equal(7, settings.FanChannel, "合法通道保留");

            settings.FanChannel = 0;
            settings.Normalize();
            Equal(-1, settings.FanChannel, "0 视为自动");

            settings.FanChannel = 99;
            settings.Normalize();
            Equal(-1, settings.FanChannel, "越界恢复自动");

            settings.FanChannel = -5;
            settings.Normalize();
            Equal(-1, settings.FanChannel, "负数恢复自动");

            Settings copy = Settings.CreateDefault();
            copy.FanChannel = 3;
            Equal(3, copy.Clone().FanChannel, "Clone 保留通道");
        }

        private static void DefaultSettings()
        {
            Settings settings = Settings.CreateDefault();
            Equal(Settings.CurrentVersion, settings.Version, "默认 version=3");
            Equal(-1, settings.FanChannel, "风扇默认自动选第一路");
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
            original.AnimationMode = Settings.AnimationAlways;
            original.Items[1].Lines[0].Color = "#FF00CC6A";
            original.Save(path);

            string json = File.ReadAllText(path);
            True(json.Contains("\"items\""), "JSON 含 items");
            True(json.Contains("\"type\": \"bar\""), "JSON 是可读的（有缩进）");
            True(json.Contains("netUp"), "JSON 含指标 key");
            True(json.Contains("\"curveStyle\""), "JSON 含 curveStyle");
            True(json.Contains("\"indicatorStyle\""), "JSON 含 indicatorStyle");
            True(json.Contains("\"animationMode\""), "JSON 含 animationMode");
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
            Equal(Settings.AnimationAlways, loaded.AnimationMode, "动效模式保持");
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
    }
}
