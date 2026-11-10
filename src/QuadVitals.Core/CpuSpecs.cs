using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace QuadVitals.Core
{
    /// <summary>
    /// 一行处理器规格：基础功耗（Intel 的 PBP / AMD 的 TDP）与最大加速功耗（Intel 的 MTP /
    /// AMD 的 PPT）。两个数都是**厂商给这个 SKU 定的固定值**，与这台机器怎么配 BIOS 无关；
    /// 机器实际能跑多少由实时采样回答，不由这张表回答。
    /// </summary>
    public sealed class CpuSpecEntry
    {
        public double BaseWatts;
        public double MaxWatts;
        public string Note;
    }

    /// <summary>
    /// 处理器规格表。数据放在我们自己发布的一份 JSON 里（`assets/cpu-specs.json`），
    /// 设置窗口启动时拉一次、缓存到 `%APPDATA%\QuadVitals`，失败或离线时沿用上一次的结果，
    /// 连缓存也没有就退回代码里的小表。表能改而不必发版，是因为厂商不给规格 API：
    /// 抓官网既不稳也有条款风险，所以自己维护一份。
    /// </summary>
    public sealed class CpuSpecTable
    {
        /// <summary>一份规格表不应该超过这个体积；超了说明不是我们的文件。</summary>
        public const int MaxBytes = 256 * 1024;

        public const int CurrentSchema = 1;

        private readonly Dictionary<string, CpuSpecEntry> _intel = new Dictionary<string, CpuSpecEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CpuSpecEntry> _amd = new Dictionary<string, CpuSpecEntry>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _intel.Count + _amd.Count; } }

        public bool TryLookup(string brand, out CpuSpecEntry entry)
        {
            entry = null;
            string key = CpuSku.Key(brand);
            if (key == null) return false;

            // 品牌串里没有可靠的厂商字段（OEM 会改写），所以先查判断出来的那一侧，再查另一侧。
            if (CpuSku.IsAmd(brand))
            {
                if (_amd.TryGetValue(key, out entry)) return true;
                return _intel.TryGetValue(key, out entry);
            }
            if (_intel.TryGetValue(key, out entry)) return true;
            return _amd.TryGetValue(key, out entry);
        }

        /// <summary>把一份已经解析好的表合并进来（同 key 以后者为准）。</summary>
        public void Add(string vendor, string key, double baseWatts, double maxWatts, string note)
        {
            if (key == null) return;
            CpuSpecEntry entry = new CpuSpecEntry
            {
                BaseWatts = baseWatts,
                MaxWatts = maxWatts,
                Note = note
            };
            if (string.Equals(vendor, "amd", StringComparison.OrdinalIgnoreCase)) _amd[key] = entry;
            else _intel[key] = entry;
        }

        /// <summary>
        /// 解析远端表。任何一行不合法都只丢那一行并记一条 warning，
        /// 不因为一个错字把整张表作废（远端文件是人手改的）。
        /// </summary>
        public static bool TryParse(string json, out CpuSpecTable table, out List<string> warnings, out string error)
        {
            table = null;
            warnings = new List<string>();
            error = string.Empty;

            if (string.IsNullOrEmpty(json))
            {
                error = "empty";
                return false;
            }
            if (json.Length > MaxBytes)
            {
                error = "too-large";
                return false;
            }

            SpecFileDto dto;
            try
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SpecFileDto));
                    dto = serializer.ReadObject(stream) as SpecFileDto;
                }
            }
            catch (Exception ex)
            {
                error = "json:" + ex.GetType().Name;
                return false;
            }

            if (dto == null)
            {
                error = "json:null";
                return false;
            }
            if (dto.Schema != CurrentSchema)
            {
                // 未来格式可能是别的字段含义，宁可不用，也别把错的数显示成规格。
                error = "schema:" + dto.Schema.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            CpuSpecTable parsed = new CpuSpecTable();
            int dropped = 0;
            if (dto.Rows != null)
            {
                for (int i = 0; i < dto.Rows.Count; i++)
                {
                    SpecRowDto row = dto.Rows[i];
                    if (row == null) { dropped++; continue; }

                    string key = CpuSku.Key(row.Sku);
                    double baseWatts = row.Pbp > 0 ? row.Pbp : row.Tdp;
                    double maxWatts = row.Mtp > 0 ? row.Mtp : row.Ppt;
                    string vendor = string.Equals(row.Vendor, "amd", StringComparison.OrdinalIgnoreCase) ? "amd" : "intel";

                    if (key == null || !CpuSku.IsValidKey(key) || baseWatts <= 0 || maxWatts <= 0 ||
                        baseWatts > 999 || maxWatts > 1000 || maxWatts < baseWatts)
                    {
                        dropped++;
                        warnings.Add(vendor + ":" + (row.Sku ?? "?") + " 数值或键不合法，已丢弃");
                        continue;
                    }
                    parsed.Add(vendor, key, baseWatts, maxWatts, row.Note);
                }
            }

            if (parsed.Count == 0)
            {
                error = "no-rows";
                return false;
            }
            if (dropped > 0) warnings.Add("丢弃了 " + dropped.ToString(CultureInfo.InvariantCulture) + " 行不合法数据");

            table = parsed;
            return true;
        }

        [DataContract]
        public sealed class SpecFileDto
        {
            [DataMember(Name = "schema")]
            public int Schema { get; set; }

            [DataMember(Name = "rows")]
            public List<SpecRowDto> Rows { get; set; }
        }

        [DataContract]
        public sealed class SpecRowDto
        {
            [DataMember(Name = "vendor")]
            public string Vendor { get; set; }

            [DataMember(Name = "sku")]
            public string Sku { get; set; }

            [DataMember(Name = "pbp")]
            public double Pbp { get; set; }

            [DataMember(Name = "mtp")]
            public double Mtp { get; set; }

            [DataMember(Name = "tdp")]
            public double Tdp { get; set; }

            [DataMember(Name = "ppt")]
            public double Ppt { get; set; }

            [DataMember(Name = "note")]
            public string Note { get; set; }
        }
    }

    /// <summary>
    /// 把注册表里的品牌串归一化成表里的 key。品牌串被 OEM 改写过很多次
    /// （`13th Gen Intel(R) Core(TM) i9-13900K`、`AMD Ryzen 9 7950X 16-Core Processor`），
    /// 所以规则只有一条：取最后一个带 4 位以上数字（或 3 位数字 + 字母后缀）的词。
    /// </summary>
    public static class CpuSku
    {
        public static bool IsAmd(string brand)
        {
            return brand != null && brand.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>归一化后的 SKU 标记；认不出来返回 null（界面据此标 `*`，不猜）。</summary>
        public static string Key(string brand)
        {
            if (string.IsNullOrEmpty(brand)) return null;

            string text = brand.ToUpperInvariant()
                .Replace("(R)", " ")
                .Replace("(TM)", " ")
                .Replace("CPU", " ")
                .Replace("PROCESSOR", " ");

            string[] tokens = text.Split(new char[] { ' ', '\t', '/', ',', ';', '@', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            string best = null;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (LooksLikeSku(tokens[i])) best = tokens[i];
            }
            if (best == null) return null;

            StringBuilder builder = new StringBuilder(best.Length);
            for (int i = 0; i < best.Length; i++)
            {
                char c = best[i];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-') builder.Append(c);
            }
            string key = builder.ToString().Trim('-');
            return key.Length == 0 ? null : key;
        }

        public static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 2 || key.Length > 24) return false;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        private static bool LooksLikeSku(string token)
        {
            int digits = 0;
            int run = 0;
            int bestRun = 0;
            bool hasLetterAfterDigits = false;
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (c >= '0' && c <= '9')
                {
                    digits++;
                    run++;
                    if (run > bestRun) bestRun = run;
                }
                else
                {
                    if (run >= 3 && c >= 'A' && c <= 'Z') hasLetterAfterDigits = true;
                    run = 0;
                }
            }
            if (bestRun >= 4) return true;
            // Core Ultra 这类三位数字 SKU（285K）：三位数字后面必须跟字母，`13TH` 这类不会被当成 SKU。
            return bestRun == 3 && hasLetterAfterDigits;
        }
    }

    /// <summary>
    /// 规格表的运行期持有者：内存里只有一份，后台线程换掉、界面线程读，所以整体替换而不是就地改。
    /// 缓存与远端都失败时保持当前这张表（可能是上一次启动拉到的）。
    /// </summary>
    public static class CpuSpecs
    {
        public const string DefaultFeedUrl = "https://raw.githubusercontent.com/enosawauno/QuadVitals/main/assets/cpu-specs.json";
        public const string FeedUrlVariable = "QUADVITALS_SPEC_FEED";

        /// <summary>编进 Core.dll 的基线表（构建时用 csc /resource 嵌入）。</summary>
        public const string EmbeddedResourceName = "QuadVitals.Core.cpu-specs.json";

        /// <summary>缓存多久后重新拉一次。规格是固定值，追新一代 CPU 不需要天天看。</summary>
        public static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(7);

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

        private static readonly object Gate = new object();
        private static CpuSpecTable _current;

        public static CpuSpecTable Current
        {
            get { lock (Gate) { return _current; } }
        }

        public static void Apply(CpuSpecTable table)
        {
            lock (Gate) { _current = table; }
        }

        /// <summary>远端地址；测试用环境变量指向本地源。</summary>
        public static string FeedUrl()
        {
            string candidate = Environment.GetEnvironmentVariable(FeedUrlVariable);
            if (string.IsNullOrEmpty(candidate)) return DefaultFeedUrl;
            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return candidate;
            return DefaultFeedUrl;
        }

        /// <summary>
        /// 读编进 DLL 的那份表：离线新装也能拿到完整列表，远端只负责“更新”。
        /// 解析失败或资源缺失只返回 false，不抛异常。
        /// </summary>
        public static bool TryLoadEmbedded(out string error)
        {
            error = string.Empty;
            try
            {
                Stream stream = typeof(CpuSpecs).Assembly.GetManifestResourceStream(EmbeddedResourceName);
                if (stream == null)
                {
                    error = "missing-resource";
                    return false;
                }

                string json;
                using (stream)
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }

                CpuSpecTable table;
                List<string> warnings;
                if (!CpuSpecTable.TryParse(json, out table, out warnings, out error)) return false;

                Apply(table);
                return true;
            }
            catch (Exception ex)
            {
                error = "embedded:" + ex.GetType().Name;
                return false;
            }
        }

        public static string CachePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(Path.Combine(appData, "QuadVitals"), "cpu-specs.json");
        }

        /// <summary>读缓存；文件不存在或内容不合法都只返回 false，并把调用方留在现有表上。</summary>
        public static bool TryLoadCache(string path, out string error)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "missing";
                    return false;
                }
                FileInfo info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > CpuSpecTable.MaxBytes)
                {
                    error = "size";
                    return false;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                CpuSpecTable table;
                List<string> warnings;
                if (!CpuSpecTable.TryParse(json, out table, out warnings, out error)) return false;

                Apply(table);
                return true;
            }
            catch (Exception ex)
            {
                error = "cache:" + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>缓存是否还够新；用写入时间判断，不往文件里塞日期（数据文件里不出现日期）。</summary>
        public static bool CacheIsFresh(string path, DateTime nowUtc, TimeSpan maxAge)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                return nowUtc - File.GetLastWriteTimeUtc(path) <= maxAge;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 拉一次远端表：通过 <see cref="IUpdateTransport"/> 走同一套超时/代理处理，
        /// 断言用内存流即可，不必开 socket。任何失败都返回 false，不动当前表。
        /// </summary>
        public static bool Refresh(IUpdateTransport transport, string url, string cachePath, DateTime nowUtc,
            TimeSpan maxAge, TimeSpan timeout, out string error)
        {
            error = string.Empty;
            if (transport == null)
            {
                error = "no-transport";
                return false;
            }
            if (string.IsNullOrEmpty(url))
            {
                error = "no-url";
                return false;
            }

            try
            {
                using (UpdateResponse response = transport.Open(url, timeout))
                {
                    if (response == null)
                    {
                        error = "no-response";
                        return false;
                    }
                    if (!string.IsNullOrEmpty(response.Failure))
                    {
                        error = response.Failure;
                        return false;
                    }
                    if (response.Body == null)
                    {
                        error = "empty-body";
                        return false;
                    }

                    string json = ReadText(response.Body, CpuSpecTable.MaxBytes);
                    CpuSpecTable table;
                    List<string> warnings;
                    if (!CpuSpecTable.TryParse(json, out table, out warnings, out error)) return false;

                    Apply(table);
                    TryWriteCache(cachePath, json);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "refresh:" + ex.GetType().Name;
                return false;
            }
        }

        private static void TryWriteCache(string path, string json)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                // 先写临时文件再替换：中途被杀掉不会留下半个 JSON 当缓存。
                string temp = path + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception)
            {
                // 缓存写不进去只影响下次启动要不要重新拉，不影响这次的显示。
            }
        }

        private static string ReadText(Stream body, int maxBytes)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int total = 0;
                while (true)
                {
                    int read = body.Read(chunk, 0, chunk.Length);
                    if (read <= 0) break;
                    total += read;
                    if (total > maxBytes) throw new InvalidOperationException("spec-table-too-large");
                    buffer.Write(chunk, 0, read);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }
    }
}
