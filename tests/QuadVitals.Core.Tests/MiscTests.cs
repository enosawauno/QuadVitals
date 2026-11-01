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
    }
}
