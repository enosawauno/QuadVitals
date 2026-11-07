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

        // ---- metric sanity and the data watchdog --------------------------------------

        private static MetricsSnapshot SanitySnapshot()
        {
            return new MetricsSnapshot
            {
                Flags = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.TemperatureValid |
                        MetricFlags.NetworkValid | MetricFlags.AgentAlive | MetricFlags.GpuValid |
                        MetricFlags.GpuTemperatureValid | MetricFlags.DiskValid | MetricFlags.DiskIoValid |
                        MetricFlags.CpuPowerValid | MetricFlags.GpuPowerValid | MetricFlags.GpuClockValid,
                CpuPercent = 12.5f,
                MemoryPercent = 34.0f,
                TemperatureC = 55.0f,
                NetUpBytesPerSec = 1024.0,
                NetDownBytesPerSec = 2048.0,
                GpuLoadPercent = 45.0f,
                GpuTemperatureC = 61.0f,
                DiskPercent = 72.0f
            };
        }

        private static void MetricSanityBehaviour()
        {
            MetricsSnapshot good = SanitySnapshot();
            Equal(good.Flags, MetricSanity.EffectiveFlags(good), "合理数值保留全部标志");

            MetricsSnapshot cpu = SanitySnapshot();
            cpu.CpuPercent = float.NaN;
            True((MetricSanity.EffectiveFlags(cpu) & MetricFlags.CpuValid) == 0, "CPU NaN 视为没有数据");

            MetricsSnapshot memory = SanitySnapshot();
            memory.MemoryPercent = 101.0f;
            True((MetricSanity.EffectiveFlags(memory) & MetricFlags.MemoryValid) == 0, "内存 101% 视为没有数据");

            MetricsSnapshot gpu = SanitySnapshot();
            gpu.GpuLoadPercent = -1.0f;
            True((MetricSanity.EffectiveFlags(gpu) & MetricFlags.GpuValid) == 0, "显卡负载为负视为没有数据");

            MetricsSnapshot disk = SanitySnapshot();
            disk.DiskPercent = float.NaN;
            True((MetricSanity.EffectiveFlags(disk) & MetricFlags.DiskValid) == 0, "磁盘占比 NaN 视为没有数据");

            MetricsSnapshot temperature = SanitySnapshot();
            temperature.TemperatureC = 200.0f;
            True((MetricSanity.EffectiveFlags(temperature) & MetricFlags.TemperatureValid) == 0, "温度 200 视为异常");

            MetricsSnapshot network = SanitySnapshot();
            network.NetDownBytesPerSec = -5.0;
            True((MetricSanity.EffectiveFlags(network) & MetricFlags.NetworkValid) == 0, "负速率视为没有数据");

            Equal(MetricFlags.None, MetricSanity.EffectiveFlags(null), "空快照没有任何可信标志");
        }

        private static void MetricWatchdogBehaviour()
        {
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            MetricFlags healthy = MetricFlags.CpuValid | MetricFlags.MemoryValid |
                                  MetricFlags.DiskValid | MetricFlags.AgentAlive;
            MetricFlags noCpu = MetricFlags.MemoryValid | MetricFlags.DiskValid | MetricFlags.AgentAlive;

            MetricWatchdog watch = new MetricWatchdog();
            MetricFlags missing;
            True(!watch.Observe(noCpu, true, false, t0, out missing), "CPU 刚缺失时不立即重启");
            True((missing & MetricFlags.CpuValid) != 0, "缺失集合里包含 CPU");
            True(!watch.Observe(noCpu, true, false, t0.AddSeconds(9), out missing), "持续 9 秒仍不重启");
            True(watch.Observe(noCpu, true, false, t0.AddSeconds(10), out missing), "持续 10 秒后触发重启");
            Equal(1, watch.Attempts, "第一次重启记录了一次尝试");
            True(!watch.Observe(noCpu, true, false, t0.AddSeconds(12), out missing), "退避 3 秒内不重复重启");
            True(watch.Observe(noCpu, true, false, t0.AddSeconds(13), out missing), "退避到期后允许第二次");

            MetricWatchdog capped = new MetricWatchdog();
            int attempts = 0;
            for (int i = 0; i < 6; i++)
            {
                if (capped.Observe(noCpu, true, false, t0.AddSeconds(i * 100), out missing)) attempts++;
            }
            Equal(5, attempts, "连续失败最多重启五次");
            Equal(MetricWatchdog.MaxAttempts, capped.Attempts, "尝试次数停在五次");

            MetricWatchdog jitter = new MetricWatchdog();
            jitter.Observe(healthy, true, false, t0, out missing);
            jitter.Observe(noCpu, true, false, t0.AddSeconds(1), out missing);
            jitter.Observe(healthy, true, false, t0.AddSeconds(5), out missing);
            jitter.Observe(noCpu, true, false, t0.AddSeconds(6), out missing);
            True(!jitter.Observe(noCpu, true, false, t0.AddSeconds(15), out missing), "抖动后重新计宽限，9 秒不触发");
            True(jitter.Observe(noCpu, true, false, t0.AddSeconds(16), out missing), "抖动后持续 10 秒才触发");

            MetricWatchdog recovered = new MetricWatchdog();
            recovered.Observe(noCpu, true, false, t0, out missing);
            recovered.Observe(noCpu, true, false, t0.AddSeconds(10), out missing);
            recovered.Observe(healthy, true, false, t0.AddSeconds(20), out missing);
            recovered.Observe(healthy, true, false, t0.AddSeconds(81), out missing);
            Equal(0, recovered.Attempts, "连续健康 60 秒后尝试预算恢复");

            MetricWatchdog temperature = new MetricWatchdog();
            True(!temperature.Observe(healthy, true, false, t0.AddSeconds(120), out missing), "从未有效的温度不触发重启");

            MetricFlags noGpu = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.DiskValid | MetricFlags.AgentAlive;
            MetricWatchdog nvidia = new MetricWatchdog();
            nvidia.Observe(noGpu, true, true, t0, out missing);
            True(nvidia.Observe(noGpu, true, true, t0.AddSeconds(10), out missing), "NVIDIA 显卡缺失即使从未有效也重启");
            Equal("显卡", MetricWatchdog.Describe(missing), "缺失原因指向显卡");

            MetricWatchdog notNvidia = new MetricWatchdog();
            True(!notNvidia.Observe(noGpu, true, false, t0.AddSeconds(120), out missing), "非 NVIDIA 机器不追显卡数据");

            MetricFlags gpuThenGone = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.GpuValid |
                                      MetricFlags.DiskValid | MetricFlags.AgentAlive;
            MetricWatchdog seen = new MetricWatchdog();
            seen.Observe(gpuThenGone, true, false, t0, out missing);
            seen.Observe(noGpu, true, false, t0.AddSeconds(1), out missing);
            True(seen.Observe(noGpu, true, false, t0.AddSeconds(11), out missing), "曾经有效的显卡失效后重启");

            // 风扇：见过一次之后才预期，风扇板/无风扇头的主板不会被重启。
            MetricFlags fansThenGone = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.DiskValid |
                                       MetricFlags.FanValid | MetricFlags.AgentAlive;
            MetricFlags noFans = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.DiskValid |
                                 MetricFlags.AgentAlive;
            MetricWatchdog fans = new MetricWatchdog();
            fans.Observe(fansThenGone, true, false, t0, out missing);
            fans.Observe(noFans, true, false, t0.AddSeconds(1), out missing);
            True(fans.Observe(noFans, true, false, t0.AddSeconds(11), out missing), "曾经有效的风扇失效后重启");
            Equal("风扇", MetricWatchdog.Describe(missing), "缺失原因指向风扇");

            MetricWatchdog fanless = new MetricWatchdog();
            True(!fanless.Observe(noFans, true, false, t0.AddSeconds(120), out missing), "从未有效的风扇不触发重启");

            Equal(3, MetricWatchdog.RetryDelaysSeconds[0], "退避从 3 秒起");
            Equal(30, MetricWatchdog.RetryDelaysSeconds[MetricWatchdog.RetryDelaysSeconds.Length - 1], "最长退避 30 秒");

            MetricWatchdog offline = new MetricWatchdog();
            offline.Observe(noCpu, true, false, t0, out missing);
            offline.Observe(noCpu, true, false, t0.AddSeconds(10), out missing);
            True(!offline.Observe(MetricFlags.None, false, false, t0.AddSeconds(20), out missing), "离线时不按指标重启");
            Equal(MetricFlags.None, missing, "离线时缺失集合清空");
            Equal(1, offline.Attempts, "离线保留已经用掉的尝试次数");
            True(!offline.Observe(noCpu, true, false, t0.AddSeconds(31), out missing), "重新上线先重新计宽限");
            True(offline.Observe(noCpu, true, false, t0.AddSeconds(41), out missing), "重新上线后按剩余退避继续");

            MetricWatchdog reset = new MetricWatchdog();
            reset.Observe(noCpu, true, false, t0, out missing);
            reset.Observe(noCpu, true, false, t0.AddSeconds(10), out missing);
            reset.Reset();
            Equal(0, reset.Attempts, "手动重启清空尝试预算");

            // 见到过一次就永久期待，是“风扇曾经有效、之后换回不带风扇的版本”时每轮重启五次的根因。
            MetricWatchdog cycle = new MetricWatchdog();
            cycle.Observe(fansThenGone, true, false, t0, out missing);
            cycle.Reset();
            True(!cycle.Observe(noFans, true, false, t0.AddSeconds(120), out missing), "手动重启后重新证明，不再追风扇");
            Equal(MetricFlags.None, missing, "重置之后缺失集合为空");

            // Agent 换进程 = 换一套传感器能力，之前的“曾经有效”不再作数。
            MetricWatchdog newProcess = new MetricWatchdog();
            newProcess.Observe(fansThenGone, true, false, t0, out missing);
            newProcess.Observe(noFans, true, false, t0.AddSeconds(1), out missing);
            newProcess.Observe(noFans, true, false, t0.AddSeconds(11), out missing);
            Equal(1, newProcess.Attempts, "换进程前已经重试过一次");
            newProcess.AgentRestarted();
            True(!newProcess.Observe(noFans, true, false, t0.AddSeconds(30), out missing), "换进程后不再按旧能力追风扇");
            Equal(1, newProcess.Attempts, "换进程保留已用掉的尝试预算");

            // 连续五次都救不回来的指标：本会话熔断，不再反复重启。
            MetricWatchdog breaker = new MetricWatchdog();
            breaker.Observe(fansThenGone, true, false, t0, out missing);
            for (int i = 0; i < 6; i++)
            {
                breaker.Observe(noFans, true, false, t0.AddSeconds(20 + i * 40), out missing);
            }
            Equal(MetricWatchdog.MaxAttempts, breaker.Attempts, "熔断前用满五次");
            // 用满配额的那一次仍然如实报告“缺”，但不再重启；下一次起这一项被移出期待集合。
            True(!breaker.Observe(noFans, true, false, t0.AddSeconds(400), out missing), "用满五次后不再重启");
            Equal(MetricFlags.FanValid, missing, "那一次仍报告缺失");
            True(!breaker.Observe(noFans, true, false, t0.AddSeconds(500), out missing), "熔断后不再重启");
            Equal(MetricFlags.None, missing, "熔断的指标不再出现在缺失集合里");
        }

        // ---- agent diagnostics box ------------------------------------------------------

        private static void AgentDiagnosticsBehaviour()
        {
            MetricsSnapshot snap = new MetricsSnapshot();
            snap.Flags = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.AgentAlive;
            snap.NetUpBytesPerSec = 10;
            snap.NetDownBytesPerSec = 10;

            // 未采样的网速不能打印数字：字节计数器保留着上一帧，看起来像网络是活的。
            Equal("--", AgentDiagnostics.NetworkLine(snap), "网络无效时不打印速率");
            snap.Flags |= MetricFlags.NetworkValid;
            True(AgentDiagnostics.NetworkLine(snap).Contains("/s"), "网络有效时打印速率");
            Equal("--", AgentDiagnostics.NetworkLine(null), "没有快照时给 --");

            string missing = AgentDiagnostics.MissingLine(snap);
            True(missing.Contains("温度"), "缺失行点名温度: " + missing);
            True(missing.Contains("网络") == false, "网络有效时不列网络");
            True(!missing.Contains("CPU"), "CPU 有效时不列 CPU");

            snap.Flags = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.TemperatureValid |
                         MetricFlags.NetworkValid | MetricFlags.GpuValid | MetricFlags.GpuTemperatureValid |
                         MetricFlags.DiskValid | MetricFlags.DiskIoValid | MetricFlags.AgentAlive;
            Equal("全部有效", AgentDiagnostics.MissingLine(snap), "全有效时说全部有效");

            // 刚重启的头几帧：温度还没到 5 秒节拍、网络还没有窗口，说明一句而不是让人以为坏了。
            MetricsSnapshot cold = new MetricsSnapshot();
            cold.Flags = MetricFlags.CpuValid | MetricFlags.MemoryValid | MetricFlags.AgentAlive;
            True(AgentDiagnostics.WarmupLine(2, cold, 1000) != null, "第 2 帧缺温度网络时给一句说明");
            Equal(null, AgentDiagnostics.WarmupLine(9, cold, 1000), "稳定之后不再提示");
            Equal(null, AgentDiagnostics.WarmupLine(2, snap, 1000), "什么都不缺时不必说明");
            Equal(null, AgentDiagnostics.WarmupLine(2, null, 1000), "没有快照时不提示");

            // 提权状态：新 Agent 明确报告，旧 Agent 什么都不说，不能把旧版当成“未提权”。
            MetricsSnapshot unelevated = new MetricsSnapshot { Flags = MetricFlags.AgentAlive | MetricFlags.ElevationKnown };
            MetricsSnapshot elevated = new MetricsSnapshot { Flags = MetricFlags.AgentAlive | MetricFlags.ElevationKnown | MetricFlags.Elevated };
            MetricsSnapshot oldAgent = new MetricsSnapshot { Flags = MetricFlags.AgentAlive };
            True(AgentDiagnostics.IsKnownUnelevated(unelevated), "已知未提权");
            True(!AgentDiagnostics.IsKnownUnelevated(elevated), "已提权不算未提权");
            True(!AgentDiagnostics.IsKnownUnelevated(oldAgent), "旧 Agent 不声称未提权");
            Equal("未提权，读不到温度", AgentDiagnostics.TemperatureUnavailableLine(unelevated), "未提权温度文案");
            Equal("温度读取失败", AgentDiagnostics.TemperatureUnavailableLine(elevated), "已提权但温度缺失是会话失效");
            Equal("温度读取失败", AgentDiagnostics.TemperatureUnavailableLine(oldAgent), "旧 Agent 不下断言");
            // 格子说明行不许换行：两段文案都必须短到一行放得下。
            True(AgentDiagnostics.TemperatureUnavailableLine(unelevated).Length <= 10, "未提权文案一行内");
            True(AgentDiagnostics.TemperatureUnavailableLine(elevated).Length <= 10, "失效文案一行内");
            Equal("传感器未提权，风扇不可用", AgentDiagnostics.FanUnavailableLine(unelevated), "未提权风扇文案");
            Equal("风扇数据读取失败，请重启传感器", AgentDiagnostics.FanUnavailableLine(elevated), "已提权且温度也缺时是会话失效");
            elevated.Flags |= MetricFlags.TemperatureValid;
            Equal("未读取到主板风扇", AgentDiagnostics.FanUnavailableLine(elevated), "温度正常只是没有风扇通道");
            Equal("未读取到主板风扇", AgentDiagnostics.FanUnavailableLine(oldAgent), "旧 Agent 风扇文案不变");
        }

        // ---- agent launch path ---------------------------------------------------------

        private static void AgentLaunchPlanBehaviour()
        {
            // 任务优先：任务能跑就不该弹 UAC，也不该管 exe 路径（任务自己记着）。
            Equal(AgentLaunchMode.LogonTask, AgentLaunchPlan.Decide(true, null), "任务可用时不走提权路径");
            Equal(AgentLaunchMode.LogonTask, AgentLaunchPlan.Decide(true, @"C:\x\QuadVitals.Agent.exe"), "任务可用时不看 exe");
            // 任务不可用必须走 runas：普通 Process.Start 继承非提权令牌，温度与风扇永远读不到。
            Equal(AgentLaunchMode.ElevatedShell, AgentLaunchPlan.Decide(false, @"C:\x\QuadVitals.Agent.exe"), "任务不可用改走提权启动");
            True(AgentLaunchPlan.PromptsForElevation(AgentLaunchMode.ElevatedShell), "提权路径会弹 UAC");
            True(!AgentLaunchPlan.PromptsForElevation(AgentLaunchMode.LogonTask), "计划任务路径不弹 UAC");
            Equal(AgentLaunchMode.MissingExe, AgentLaunchPlan.Decide(false, null), "没有 exe 就报缺文件");
            Equal(AgentLaunchMode.MissingExe, AgentLaunchPlan.Decide(false, string.Empty), "空路径按缺文件处理");
            True(AgentLaunchPlan.IsElevationCancelled(1223), "UAC 取消码被识别");
            True(!AgentLaunchPlan.IsElevationCancelled(5), "拒绝访问不等于用户取消");

            // 面板「权限」行：三种状态分开，不能把旧 Agent 说成未提权。
            MetricFlags known = MetricFlags.ElevationKnown;
            Equal("普通权限，无法获取CPU温度和风扇转速数据", AgentDiagnostics.ElevationLine(known), "已知未提权");
            Equal("管理员：CPU 温度与风扇可读", AgentDiagnostics.ElevationLine(known | MetricFlags.Elevated), "已提权");
            Equal("未上报（旧版 Agent）", AgentDiagnostics.ElevationLine(MetricFlags.None), "旧 Agent 不下断言");
            True(AgentDiagnostics.IsKnownUnelevated(known), "flags 与快照两个入口同判");
            True(!AgentDiagnostics.IsKnownUnelevated(known | MetricFlags.Elevated), "提权不算未提权");

            // 重启回来却没提权时必须说出来并给出下一步，不能报成功。
            True(AgentDiagnostics.NotElevatedAfterRestart.Contains("重启传感器"), "未提权重启结果指向重启入口");
            True(AgentDiagnostics.ElevationCancelled.Contains("CPU 温度"), "取消 UAC 时说明温度读不到");
            Equal("重建登录计划任务失败：schtasks 超时", AgentDiagnostics.TaskRepairFailed("schtasks 超时"), "重建失败文案");
        }

        // ---- agent control events ------------------------------------------------------

        private static void AgentControlBehaviour()
        {
            True(AgentControl.QuitEventName.StartsWith(@"Local\"), "退出事件限定在当前会话");
            True(AgentControl.ActivateEventName.StartsWith(@"Local\"), "激活事件限定在当前会话");
            True(AgentControl.QuitEventName != AgentControl.ActivateEventName, "退出与激活是两个事件");
            True(AgentControl.QuitEventName != MetricsChannel.MapName, "控制事件与指标通道分开");
            True(AgentControl.RestartEventName.StartsWith(@"Local\"), "重启事件限定在当前会话");
            True(AgentControl.RestartEventName != AgentControl.QuitEventName, "重启与退出是两个事件");
            True(AgentControl.RestartEventName != AgentControl.ActivateEventName, "重启与激活是两个事件");
            True(!AgentControl.RequestRestart() || true, "无 Agent 时重启请求不会抛异常");

            // Use a throwaway name: the probe must never be able to quit the agent that is running.
            string probe = @"Local\QuadVitals.Agent.Control.test." + Guid.NewGuid().ToString("N");
            using (EventWaitHandle expected = new EventWaitHandle(false, EventResetMode.AutoReset, probe))
            {
                True(AgentControl.Signal(probe), "控制事件可被同会话置位");
                True(expected.WaitOne(1000), "置位后等待方能立刻醒来");
            }
            True(!AgentControl.Signal(probe), "事件不存在时报告未送达");
        }
    }
}
