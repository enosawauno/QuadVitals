using System;
using System.Collections.Generic;
using System.Globalization;

namespace QuadVitals.Core
{
    /// <summary>
    /// 硬件厂商归一化：把 SMBIOS / WMI / 型号串里的法律后缀与拼写差异收成家用的名字，
    /// 并在能认出来的时候给一个官网地址。
    ///
    /// 这是纯字符串表，没有任何网络访问：官网地址只用于「硬件信息」页里的链接行，
    /// 点它才会用默认浏览器打开。表里的关键字全部大写比较，**长关键字优先**，
    /// 所以 “GIGA-BYTE” 不会被 “GIGA” 抢先命中。
    /// </summary>
    public static class HardwareBrands
    {
        /// <summary>型号里认不出厂商时显示这个，而不是把型号当厂商（型号不是厂商）。</summary>
        public const string Unrecognized = "未识别";

        public sealed class Entry
        {
            public Entry(string[] keywords, string display, string url)
            {
                Keywords = keywords;
                Display = display;
                Url = url;
            }

            public string[] Keywords { get; private set; }
            public string Display { get; private set; }
            public string Url { get; private set; }
        }

        // 顺序不重要（匹配时按关键字长度降序），但为了让表好读，按用途分组列出。
        private static readonly Entry[] Table =
        {
            // 主板 / 整机
            new Entry(new[] { "ASUSTEK", "ASUS", "ASUSTeK" }, "华硕", "https://cn.asus.com"),
            new Entry(new[] { "MICRO-STAR", "MSI" }, "微星", "https://www.msi.com"),
            new Entry(new[] { "GIGA-BYTE", "GIGABYTE" }, "技嘉", "https://www.gigabyte.com"),
            new Entry(new[] { "ASROCK" }, "华擎", "https://www.asrock.com"),
            new Entry(new[] { "BIOSTAR" }, "映泰", "https://www.biostar.com.cn"),
            new Entry(new[] { "FOXCONN" }, "富士康", "https://www.foxconn.com"),
            new Entry(new[] { "DELL" }, "戴尔", "https://www.dell.com.cn"),
            new Entry(new[] { "LENOVO" }, "联想", "https://www.lenovo.com.cn"),
            new Entry(new[] { "HEWLETT-PACKARD", "HP" }, "惠普", "https://www.hp.com.cn"),
            new Entry(new[] { "ACER" }, "宏碁", "https://www.acer.com"),
            new Entry(new[] { "COLORFUL" }, "七彩虹", "https://www.colorful.cn"),
            new Entry(new[] { "MAXSUN" }, "铭瑄", "https://www.maxsun.com.cn"),
            new Entry(new[] { "ONDA" }, "昂达", "https://www.onda.cn"),
            new Entry(new[] { "ZOTAC" }, "索泰", "https://china.zotac.com"),
            new Entry(new[] { "HASEE" }, "神舟", "https://www.hasee.com"),

            // 芯片 / 板卡
            new Entry(new[] { "NVIDIA" }, "英伟达", "https://www.nvidia.cn"),
            new Entry(new[] { "ADVANCED MICRO DEVICES", "AMD" }, "AMD", "https://www.amd.com"),
            new Entry(new[] { "INTEL" }, "英特尔", "https://www.intel.cn"),
            new Entry(new[] { "REALTEK" }, "瑞昱", "https://www.realtek.com"),

            // 内存
            new Entry(new[] { "G SKILL", "GSKILL" }, "芝奇", "https://www.gskill.com"),
            new Entry(new[] { "KINGSTON" }, "金士顿", "https://www.kingston.com.cn"),
            new Entry(new[] { "CORSAIR" }, "海盗船", "https://www.corsair.com"),
            new Entry(new[] { "MICRON", "CRUCIAL" }, "美光", "https://www.crucial.cn"),
            new Entry(new[] { "SK HYNIX", "HYNIX" }, "SK 海力士", "https://www.skhynix.com"),
            new Entry(new[] { "SAMSUNG" }, "三星", "https://www.samsung.com.cn"),
            new Entry(new[] { "ADATA" }, "威刚", "https://www.adata.com"),
            new Entry(new[] { "TEAMGROUP", "TEAM GROUP" }, "十铨", "https://www.teamgroupinc.com"),

            // 存储
            new Entry(new[] { "WESTERN DIGITAL", "WDC", "WD_" }, "西部数据", "https://www.westerndigital.com"),
            new Entry(new[] { "SEAGATE" }, "希捷", "https://www.seagate.com"),
            new Entry(new[] { "TOSHIBA" }, "东芝", "https://www.toshiba.com.cn"),
            new Entry(new[] { "KIOXIA" }, "铠侠", "https://www.kioxia.com"),
            new Entry(new[] { "SANDISK" }, "闪迪", "https://www.sandisk.cn"),
            new Entry(new[] { "HS-SSD", "HIKVISION" }, "海康存储", "https://www.hikvision.com"),
            new Entry(new[] { "PLEXTOR" }, "浦科特", "https://www.plextor.com"),
            new Entry(new[] { "LITEON" }, "建兴", "https://www.liteon.com")
        };

        private static readonly Entry[] ByLength = BuildByLength();

        private static Entry[] BuildByLength()
        {
            List<Entry> flat = new List<Entry>();
            foreach (Entry entry in Table)
            {
                foreach (string keyword in entry.Keywords)
                {
                    flat.Add(new Entry(new[] { keyword }, entry.Display, entry.Url));
                }
            }
            // 长关键字先试：“GIGA-BYTE” 要先于 “GIGA” 命中，否则展示名会对但网址可能对不上。
            flat.Sort((a, b) => b.Keywords[0].Length.CompareTo(a.Keywords[0].Length));
            return flat.ToArray();
        }

        /// <summary>原始厂商串 → 展示名；认不出来时原样返回（去掉首尾空白）。</summary>
        public static string Display(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            Entry match = Match(raw);
            return match != null ? match.Display : raw.Trim();
        }

        /// <summary>原始厂商串 → 官网；认不出来返回空串（页面就不画链接）。</summary>
        public static string UrlFor(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            Entry match = Match(raw);
            return match != null ? match.Url : string.Empty;
        }

        /// <summary>
        /// 硬盘型号 → 厂商。只认型号前缀，不把型号本身当厂商名：认不出时返回「未识别」，
        /// 让页面显示“未识别”而不是把 “WUS4BB076D7P3E3” 冒充成厂商。
        /// </summary>
        public static string DriveVendor(string model)
        {
            if (string.IsNullOrEmpty(model)) return Unrecognized;
            string name = model.Trim().ToUpperInvariant();

            if (name.StartsWith("SAMSUNG", StringComparison.Ordinal)) return "三星";
            if (name.StartsWith("WDC ", StringComparison.Ordinal) ||
                name.StartsWith("WD ", StringComparison.Ordinal) ||
                name.StartsWith("WUS", StringComparison.Ordinal) ||
                name.StartsWith("WDS", StringComparison.Ordinal)) return "西部数据";
            if (name.StartsWith("ST", StringComparison.Ordinal) && name.Length > 4) return "希捷";
            if (name.StartsWith("TOSHIBA", StringComparison.Ordinal) ||
                name.StartsWith("THNS", StringComparison.Ordinal)) return "东芝";
            if (name.StartsWith("KIOXIA", StringComparison.Ordinal)) return "铠侠";
            if (name.StartsWith("SANDISK", StringComparison.Ordinal) ||
                name.StartsWith("SDSSD", StringComparison.Ordinal)) return "闪迪";
            if (name.StartsWith("CRUCIAL", StringComparison.Ordinal) ||
                name.StartsWith("CT", StringComparison.Ordinal)) return "美光";
            if (name.StartsWith("KINGSTON", StringComparison.Ordinal) ||
                name.StartsWith("SA400", StringComparison.Ordinal)) return "金士顿";
            if (name.StartsWith("HS-SSD", StringComparison.Ordinal)) return "海康存储";
            if (name.StartsWith("INTEL", StringComparison.Ordinal)) return "英特尔";
            if (name.StartsWith("PLEXTOR", StringComparison.Ordinal) ||
                name.StartsWith("PX-", StringComparison.Ordinal)) return "浦科特";
            return Unrecognized;
        }

        private static Entry Match(string raw)
        {
            string text = raw.ToUpperInvariant();
            foreach (Entry entry in ByLength)
            {
                if (text.IndexOf(entry.Keywords[0], StringComparison.Ordinal) >= 0) return entry;
            }
            return null;
        }
    }
}
