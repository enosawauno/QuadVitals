using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using QuadVitals.Core;

namespace QuadVitals.Core.Tests
{
    /// <summary>
    /// 处理器规格表：品牌串归一化、远端表解析与校验、缓存与刷新。
    /// 规格值是厂商定的常数，表只是“没有 MSR 时”的兜底，所以这些用例关注的是
    /// “认不出来就别猜”和“文件坏了就别用”，而不是某个型号具体多少瓦。
    /// </summary>
    internal static partial class Program
    {
        private static void CpuSpecKeyBehaviour()
        {
            Equal("I9-13900K", CpuSku.Key("13th Gen Intel(R) Core(TM) i9-13900K"), "13 代品牌串");
            Equal("I9-13900K", CpuSku.Key("Intel(R) Core(TM) i9-13900K CPU @ 3.00GHz"), "带频率后缀");
            Equal("I7-1360P", CpuSku.Key("12th Gen Intel(R) Core(TM) i7-1360P"), "移动 P 系列");
            Equal("7950X", CpuSku.Key("AMD Ryzen 9 7950X 16-Core Processor"), "锐龙 9");
            Equal("5800X3D", CpuSku.Key("AMD Ryzen 7 5800X3D 8-Core Processor"), "带 3D 后缀");
            Equal("5600G", CpuSku.Key("AMD Ryzen 5 5600G with Radeon Graphics"), "带核显后缀");
            Equal("285K", CpuSku.Key("Intel(R) Core(TM) Ultra 9 285K"), "Core Ultra 三位 SKU");
            Equal("6430", CpuSku.Key("Intel(R) Xeon(R) Gold 6430"), "至强");
            Equal("W-2295", CpuSku.Key("Intel(R) Xeon(R) W-2295 CPU @ 3.00GHz"), "至强 W 带连字符");
            Equal("I7-0000", CpuSku.Key("Intel(R) Core(TM) i7-0000"), "工程样品的键能归一化，但表里查不到");
            Equal<string>(null, CpuSku.Key("13th Gen Intel(R) Core(TM)"), "没有 SKU 时返回 null");
            Equal<string>(null, CpuSku.Key(""), "空串返回 null");
            Equal<string>(null, CpuSku.Key(null), "null 返回 null");
            Equal(true, CpuSku.IsAmd("AMD Ryzen 9 7950X"), "AMD 品牌串");
            Equal(false, CpuSku.IsAmd("13th Gen Intel(R) Core(TM) i9-13900K"), "Intel 品牌串");
            Equal(true, CpuSku.IsValidKey("I9-13900K"), "合法键");
            Equal(false, CpuSku.IsValidKey("i9 13900k"), "小写与空格不是合法键");
            Equal(false, CpuSku.IsValidKey("A"), "太短的键不算合法");
        }

        private static void CpuSpecTableParseBehaviour()
        {
            CpuSpecTable table;
            List<string> warnings;
            string error;

            string good = "{ \"schema\": 1, \"rows\": [ " +
                          "{ \"vendor\": \"intel\", \"sku\": \"i9-13900K\", \"pbp\": 125, \"mtp\": 253 }, " +
                          "{ \"vendor\": \"amd\", \"sku\": \"5800X\", \"tdp\": 105, \"ppt\": 142 } ] }";
            True(CpuSpecTable.TryParse(good, out table, out warnings, out error), "合法表能解析");
            Equal(2, table.Count, "两行都进来了");

            CpuSpecEntry entry;
            True(table.TryLookup("13th Gen Intel(R) Core(TM) i9-13900K", out entry), "Intel 能查到");
            Equal(125.0, entry.BaseWatts, "基础功耗");
            Equal(253.0, entry.MaxWatts, "最大加速功耗");
            True(table.TryLookup("AMD Ryzen 7 5800X 8-Core Processor", out entry), "AMD 能查到");
            Equal(142.0, entry.MaxWatts, "AMD 的上限取 ppt");
            Equal(false, table.TryLookup("Intel(R) Core(TM) i5-12400", out entry), "没登记的型号查不到");

            // 同一张表里，厂商判断错了也要能查回来：品牌串被 OEM 改写过。
            True(table.TryLookup("AMD Ryzen 9 7950X", out entry) == false, "未知型号不命中");

            string future = "{ \"schema\": 2, \"rows\": [ { \"vendor\": \"intel\", \"sku\": \"i9-13900K\", \"pbp\": 125, \"mtp\": 253 } ] }";
            Equal(false, CpuSpecTable.TryParse(future, out table, out warnings, out error), "未来 schema 不接受");
            Equal("schema:2", error, "报 schema 错误");

            Equal(false, CpuSpecTable.TryParse("not json", out table, out warnings, out error), "非 JSON 拒绝");
            Equal(false, CpuSpecTable.TryParse("", out table, out warnings, out error), "空文件拒绝");
            Equal(false, CpuSpecTable.TryParse("{ \"schema\": 1 }", out table, out warnings, out error), "没有行拒绝");
            Equal("no-rows", error, "报 no-rows");

            string dirty = "{ \"schema\": 1, \"rows\": [ " +
                           "{ \"vendor\": \"intel\", \"sku\": \"i9-13900K\", \"pbp\": 125, \"mtp\": 253 }, " +
                           "{ \"vendor\": \"intel\", \"sku\": \"i7-1360P\", \"pbp\": 64, \"mtp\": 28 }, " +
                           "{ \"vendor\": \"intel\", \"sku\": \"i5-12400\", \"pbp\": 0, \"mtp\": 117 }, " +
                           "{ \"vendor\": \"intel\", \"sku\": \"bad key!\", \"pbp\": 65, \"mtp\": 88 } ] }";
            True(CpuSpecTable.TryParse(dirty, out table, out warnings, out error), "脏表只丢坏行");
            Equal(1, table.Count, "只保留合法行");
            True(warnings.Count > 0, "坏行有 warning");
            True(table.TryLookup("i9-13900K", out entry), "小写品牌串也能查到");
            Equal(253.0, entry.MaxWatts, "脏表里保留的那行是对的");
        }

        private static void CpuSpecRefreshBehaviour()
        {
            string directory = Path.Combine(Path.GetTempPath(), "qv-specs-" + Guid.NewGuid().ToString("N"));
            string cache = Path.Combine(directory, "cpu-specs.json");
            string json = "{ \"schema\": 1, \"rows\": [ { \"vendor\": \"intel\", \"sku\": \"i9-13900K\", \"pbp\": 125, \"mtp\": 199 } ] }";
            string error;

            try
            {
                CpuSpecs.Apply(null);
                True(CpuSpecs.Refresh(new FakeSpecTransport(json), "http://example/specs.json", cache,
                    DateTime.UtcNow, CpuSpecs.MaxCacheAge, TimeSpan.FromSeconds(1), out error), "拉到表");
                True(CpuSpecs.Current != null, "表装进内存");
                True(File.Exists(cache), "写进缓存");

                CpuSpecEntry entry;
                True(CpuSpecs.Current.TryLookup("i9-13900K", out entry), "刷新后能查到");
                Equal(199.0, entry.MaxWatts, "用的是远端值");

                CpuSpecs.Apply(null);
                True(CpuSpecs.TryLoadCache(cache, out error), "缓存能读回来");
                True(CpuSpecs.Current.TryLookup("i9-13900K", out entry), "缓存里也能查到");

                // 远端坏掉时不能把已有的表弄没。
                CpuSpecTable keep = CpuSpecs.Current;
                Equal(false, CpuSpecs.Refresh(new FakeSpecTransport("{ \"schema\": 9 }"), "http://example/specs.json",
                    cache, DateTime.UtcNow, CpuSpecs.MaxCacheAge, TimeSpan.FromSeconds(1), out error), "坏表不应用");
                True(ReferenceEquals(keep, CpuSpecs.Current), "当前表保持不变");

                Equal(false, CpuSpecs.Refresh(new FakeSpecTransport(null, "offline"), "http://example/specs.json",
                    cache, DateTime.UtcNow, CpuSpecs.MaxCacheAge, TimeSpan.FromSeconds(1), out error), "断网不抛异常");
                True(error == "offline", "错误码原样带出");

                True(CpuSpecs.CacheIsFresh(cache, DateTime.UtcNow, CpuSpecs.MaxCacheAge), "刚写的缓存算新鲜");
                Equal(false, CpuSpecs.CacheIsFresh(cache, DateTime.UtcNow.AddDays(30), CpuSpecs.MaxCacheAge), "过期缓存不算新鲜");

                File.WriteAllText(cache, "{ broken", Encoding.UTF8);
                CpuSpecs.Apply(null);
                Equal(false, CpuSpecs.TryLoadCache(cache, out error), "坏缓存读不进来");
                True(CpuSpecs.Current == null, "坏缓存不会装进内存");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 仓库里那张表自身也要能过：一行写错就是那台机器永远查不到自己的 SKU。
        /// 表格是人手改的，所以这里用真实文件跑一遍，而不是只验格式。
        /// </summary>
        private static void CpuSpecAssetBehaviour()
        {
            string path = FindRepoFile("assets\\cpu-specs.json");
            True(path != null, "仓库里带着规格表文件");
            if (path == null) return;

            string json = File.ReadAllText(path, Encoding.UTF8);
            CpuSpecTable table;
            List<string> warnings;
            string error;
            True(CpuSpecTable.TryParse(json, out table, out warnings, out error), "表文件能解析 " + error);
            Equal(0, warnings.Count, "没有行被丢弃");
            True(table.Count >= 100, "覆盖至少 100 行（实际 " + table.Count + "）");

            CpuSpecEntry entry;
            True(table.TryLookup("13th Gen Intel(R) Core(TM) i9-13900K", out entry), "13900K 在表里");
            Equal(125.0, entry.BaseWatts, "13900K 基础功耗");
            Equal(253.0, entry.MaxWatts, "13900K 最大睿频功耗");
            True(table.TryLookup("12th Gen Intel(R) Core(TM) i7-12700H", out entry), "12700H 在表里");
            Equal(115.0, entry.MaxWatts, "12700H 最大睿频功耗");
            True(table.TryLookup("Intel(R) Core(TM) Ultra 7 155H", out entry), "Core Ultra 155H 在表里");
            Equal(28.0, entry.BaseWatts, "155H 基础功耗");
            True(table.TryLookup("AMD Ryzen 7 7800X3D 8-Core Processor", out entry), "7800X3D 在表里");
            Equal(162.0, entry.MaxWatts, "7800X3D 的 PPT");
            True(table.TryLookup("AMD Ryzen 5 5700X3D 8-Core Processor", out entry), "5700X3D 在表里");
            Equal(false, table.TryLookup("Intel(R) Xeon(R) Gold 6430", out entry), "未登记的型号不命中");
        }

        /// <summary>从测试程序的目录往上找仓库根，找不到返回 null。</summary>
        private static string FindRepoFile(string relative)
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(directory); i++)
            {
                string candidate = Path.Combine(directory, relative);
                if (File.Exists(candidate)) return candidate;
                DirectoryInfo parent = Directory.GetParent(directory);
                directory = parent == null ? null : parent.FullName;
            }
            return null;
        }

        private sealed class FakeSpecTransport : IUpdateTransport
        {
            private readonly string _text;
            private readonly string _failure;

            public FakeSpecTransport(string text, string failure = null)
            {
                _text = text;
                _failure = failure;
            }

            public UpdateResponse Open(string url, TimeSpan timeout)
            {
                if (_failure != null) return new UpdateResponse { Failure = _failure };
                byte[] bytes = Encoding.UTF8.GetBytes(_text ?? string.Empty);
                return new UpdateResponse { Body = new MemoryStream(bytes, false), ContentLength = bytes.Length };
            }
        }
    }
}
