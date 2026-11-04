using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QuadVitals.Core
{
    public enum FeedbackKind
    {
        Bug = 0,
        Feature = 1,
        Other = 2
    }

    public static class FeedbackKinds
    {
        public static string Key(FeedbackKind kind)
        {
            switch (kind)
            {
                case FeedbackKind.Bug: return "bug";
                case FeedbackKind.Feature: return "feature";
                default: return "other";
            }
        }

        public static string DisplayName(FeedbackKind kind)
        {
            switch (kind)
            {
                case FeedbackKind.Bug: return "问题反馈";
                case FeedbackKind.Feature: return "功能建议";
                default: return "其他";
            }
        }

        public static bool TryParse(string key, out FeedbackKind kind)
        {
            kind = FeedbackKind.Other;
            if (string.Equals(key, "bug", StringComparison.OrdinalIgnoreCase)) { kind = FeedbackKind.Bug; return true; }
            if (string.Equals(key, "feature", StringComparison.OrdinalIgnoreCase)) { kind = FeedbackKind.Feature; return true; }
            if (string.Equals(key, "other", StringComparison.OrdinalIgnoreCase)) { kind = FeedbackKind.Other; return true; }
            return false;
        }
    }

    /// <summary>
    /// Limits for one feedback package. Defaults:
    /// 8 attachments, 50 MB each; we additionally cap the total so a package stays portable.
    /// </summary>
    public sealed class FeedbackOptions
    {
        public int MaxAttachments = 8;
        public long MaxAttachmentBytes = 50L * 1024 * 1024;
        public long MaxTotalAttachmentBytes = 409L * 1024 * 1024;
        public long MaxLogBytes = 256L * 1024;
        public int MaxLogFiles = 5;
        public int MaxTextLength = 20000;

        public string[] AllowedExtensions =
        {
            "png", "jpg", "jpeg", "webp", "gif", "bmp",
            "txt", "log", "md", "json", "csv",
            "zip", "7z", "rar", "pdf"
        };

        public bool IsExtensionAllowed(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty).TrimStart('.').ToLowerInvariant();
            if (extension.Length == 0) return false;
            for (int i = 0; i < AllowedExtensions.Length; i++)
            {
                if (string.Equals(AllowedExtensions[i], extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public string AllowedExtensionList
        {
            get { return string.Join(", ", AllowedExtensions); }
        }
    }

    public sealed class FeedbackSubmission
    {
        public FeedbackKind Kind = FeedbackKind.Bug;
        public string Text = string.Empty;
        public bool IncludeLogs = true;
        public List<string> AttachmentPaths = new List<string>();
    }

    /// <summary>
    /// Plain-text snapshot of the machine taken at submission time. Kept free of WPF and
    /// WMI so it can live in Core and be reused by tests.
    /// </summary>
    public sealed class FeedbackContext
    {
        public string AppVersion = "unknown";
        public string OsVersion = "unknown";
        public string Architecture = "unknown";
        public string CpuModel = string.Empty;
        public long MemoryTotalBytes;
        public string AgentSummary = string.Empty;
        public string SettingsSummary = string.Empty;
        public string DataDirectory = string.Empty;
        public string LogDirectory = string.Empty;

        public static FeedbackContext Capture(string appVersion, Settings settings, bool includeLogs = true)
        {
            string dataDir = Path.GetDirectoryName(Settings.DefaultPath);
            FeedbackContext context = new FeedbackContext
            {
                AppVersion = string.IsNullOrEmpty(appVersion) ? "unknown" : appVersion,
                OsVersion = FeedbackSystemInfo.OsVersion(),
                Architecture = Environment.Is64BitProcess ? "x64" : "x86",
                DataDirectory = dataDir ?? string.Empty,
                LogDirectory = dataDir ?? string.Empty
            };

            // design：未勾选“附带诊断日志”时，连 CPU/内存/Agent/设置摘要都不采集。
            if (!includeLogs) return context;

            context.CpuModel = FeedbackSystemInfo.CpuModel();
            context.MemoryTotalBytes = FeedbackSystemInfo.TotalMemoryBytes();
            context.SettingsSummary = SummarizeSettings(settings);

            try
            {
                using (MetricsChannel.Reader reader = MetricsChannel.Reader.TryOpen())
                {
                    if (reader == null)
                    {
                        context.AgentSummary = "Agent 未运行（共享内存通道不存在）";
                    }
                    else
                    {
                        MetricsSnapshot snapshot = reader.TryRead();
                        context.AgentSummary = snapshot == null
                            ? "Agent 通道存在，但读不到一致快照"
                            : DescribeAgent(snapshot);
                    }
                }
            }
            catch (Exception ex)
            {
                context.AgentSummary = "读取 Agent 状态失败：" + ex.GetType().Name;
            }

            return context;
        }

        private static string SummarizeSettings(Settings settings)
        {
            if (settings == null) return "设置不可用";
            return string.Format(CultureInfo.InvariantCulture,
                "items={0}, enabled={1}, refresh={2}ms, network={3}",
                settings.Items == null ? 0 : settings.Items.Count,
                settings.EnabledItemCount,
                settings.RefreshMs,
                string.IsNullOrEmpty(settings.NetworkAdapter) ? "auto" : settings.NetworkAdapter);
        }

        private static string DescribeAgent(MetricsSnapshot snapshot)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            DateTime now = DateTime.UtcNow;
            bool alive = snapshot.IsAlive(now, MetricsSnapshot.AliveTimeout);
            StringBuilder text = new StringBuilder(alive ? "在线" : "离线/数据陈旧");
            text.AppendFormat(inv, ", age={0:0}ms", snapshot.Age(now).TotalMilliseconds);
            if (snapshot.Has(MetricFlags.CpuValid))
                text.AppendFormat(inv, ", cpu={0:0.0}%", snapshot.CpuPercent);
            if (snapshot.Has(MetricFlags.MemoryValid))
                text.AppendFormat(inv, ", mem={0:0.0}%", snapshot.MemoryPercent);
            if (snapshot.Has(MetricFlags.TemperatureValid))
                text.AppendFormat(inv, ", temp={0:0.0}C", snapshot.TemperatureC);
            if (snapshot.Has(MetricFlags.NetworkValid))
                text.AppendFormat(inv, ", up={0:0}/s, down={1:0}/s",
                    snapshot.NetUpBytesPerSec, snapshot.NetDownBytesPerSec);
            if (!string.IsNullOrEmpty(snapshot.AdapterName))
                text.Append(", adapter=").Append(snapshot.AdapterName);
            return text.ToString();
        }
    }

    public sealed class FeedbackResult
    {
        public bool Ok;
        public string Error;
        public string Warning;
        public string Id;
        public string PackagePath;
        public DateTime CreatedAtUtc;
        public int AttachmentCount;
        public bool IncludedLogs;
    }

    public sealed class FeedbackPreview
    {
        public string MetadataText;
        public string LogsText;
        public string LogFileName;
    }

    public sealed class FeedbackLogFile
    {
        public string Name;
        public string Text;
    }

    [DataContract]
    public sealed class FeedbackDraftAttachment
    {
        [DataMember(Name = "fileName")]
        public string FileName;

        [DataMember(Name = "originalName")]
        public string OriginalName;

        [DataMember(Name = "bytes")]
        public long Bytes;
    }

    [DataContract]
    public sealed class FeedbackDraft
    {
        [DataMember(Name = "kind")]
        public FeedbackKind Kind;

        [DataMember(Name = "text")]
        public string Text;

        [DataMember(Name = "includeLogs")]
        public bool IncludeLogs;

        [DataMember(Name = "attachments")]
        public List<FeedbackDraftAttachment> Attachments;

        [DataMember(Name = "updatedAtUtc")]
        public DateTime UpdatedAtUtc;

        [OnDeserializing]
        private void BeforeDeserialize(StreamingContext context)
        {
            // DataContractSerializer skips field initialisers; default to "include logs" the
            // same way the dialog does when a draft has no explicit value yet.
            IncludeLogs = true;
            Text = string.Empty;
            Attachments = new List<FeedbackDraftAttachment>();
        }
    }

    [DataContract]
    internal sealed class FeedbackRecord
    {
        [DataMember(Name = "id")]
        public string Id;

        [DataMember(Name = "kind")]
        public string Kind;

        [DataMember(Name = "text")]
        public string Text;

        [DataMember(Name = "createdAtUtc")]
        public string CreatedAtUtc;

        [DataMember(Name = "appVersion")]
        public string AppVersion;

        [DataMember(Name = "os")]
        public string Os;

        [DataMember(Name = "arch")]
        public string Arch;

        [DataMember(Name = "includeLogs")]
        public bool IncludeLogs;

        [DataMember(Name = "cpu", EmitDefaultValue = false)]
        public string Cpu;

        [DataMember(Name = "memoryTotalBytes", EmitDefaultValue = false)]
        public long MemoryTotalBytes;

        [DataMember(Name = "agent", EmitDefaultValue = false)]
        public string Agent;

        /// <summary>
        /// 诊断摘要原文（已脱敏）。以前单独写一个 diagnostics.txt，内容与这里的字段重复，
        /// 现在并进记录本身，一个包不再多一份副本。
        /// </summary>
        [DataMember(Name = "diagnostics", EmitDefaultValue = false)]
        public string Diagnostics;

        [DataMember(Name = "settings", EmitDefaultValue = false)]
        public string Settings;

        [DataMember(Name = "attachments", EmitDefaultValue = false)]
        public List<FeedbackAttachmentRecord> Attachments;

        [DataMember(Name = "logs", EmitDefaultValue = false)]
        public List<string> Logs;

        public string ToCompactJson()
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FeedbackRecord));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, this);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public string ToPrettyJson()
        {
            return JsonPretty.Print(ToCompactJson());
        }
    }

    [DataContract]
    internal sealed class FeedbackAttachmentRecord
    {
        [DataMember(Name = "fileName")]
        public string FileName;

        [DataMember(Name = "originalName")]
        public string OriginalName;

        [DataMember(Name = "bytes")]
        public long Bytes;
    }

    [DataContract]
    internal sealed class LedgerEntry
    {
        [DataMember(Name = "type")]
        public string Type;

        [DataMember(Name = "id")]
        public string Id;

        [DataMember(Name = "kind", EmitDefaultValue = false)]
        public string Kind;

        [DataMember(Name = "package", EmitDefaultValue = false)]
        public string Package;

        [DataMember(Name = "bytes", EmitDefaultValue = false)]
        public long Bytes;

        [DataMember(Name = "atUtc")]
        public string AtUtc;

        [DataMember(Name = "remoteId", EmitDefaultValue = false)]
        public string RemoteId;

        [DataMember(Name = "error", EmitDefaultValue = false)]
        public string Error;
    }

    /// <summary>
    /// Local-first feedback store: validate, copy attachments, optionally collect
    /// diagnostics/log tails, write a zip plus a one-line ledger entry, and keep a draft so a
    /// closed dialog does not lose text. No network access.
    /// </summary>
    public static class FeedbackStore
    {
        public const string RootDirectoryName = "feedback";
        public const string DraftDirectoryName = "feedback-draft";
        public const string LedgerFileName = "feedback.jsonl";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object LedgerGate = new object();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string FeedbackRoot(string dataDirectory)
        {
            return Path.Combine(dataDirectory, RootDirectoryName);
        }

        public static string DraftRoot(string dataDirectory)
        {
            return Path.Combine(dataDirectory, DraftDirectoryName);
        }

        public static string DraftAttachmentsDirectory(string dataDirectory)
        {
            return Path.Combine(DraftRoot(dataDirectory), "attachments");
        }

        public static string LedgerPath(string dataDirectory)
        {
            return Path.Combine(FeedbackRoot(dataDirectory), LedgerFileName);
        }

        public static string CreateId(DateTime now)
        {
            return now.ToString("yyyyMMdd-HHmmss", Inv) + "-" +
                   Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public static bool Validate(FeedbackSubmission submission, FeedbackOptions options, out string error)
        {
            error = null;
            options = options ?? new FeedbackOptions();
            if (submission == null) { error = "反馈内容无效。"; return false; }
            if (submission.Kind != FeedbackKind.Bug &&
                submission.Kind != FeedbackKind.Feature &&
                submission.Kind != FeedbackKind.Other)
            {
                error = "反馈类型无效。";
                return false;
            }

            string text = (submission.Text ?? string.Empty).Trim();
            if (text.Length > options.MaxTextLength)
            {
                error = "反馈内容不能超过 " + options.MaxTextLength + " 个字符。";
                return false;
            }

            List<string> paths = submission.AttachmentPaths ?? new List<string>();
            if (text.Length == 0 && paths.Count == 0)
            {
                error = "反馈内容不能为空（也可以只附截图或日志）。";
                return false;
            }

            if (paths.Count > options.MaxAttachments)
            {
                error = "最多只能添加 " + options.MaxAttachments + " 个附件。";
                return false;
            }

            long total = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    error = "附件不存在：" + (path ?? string.Empty);
                    return false;
                }
                if (!options.IsExtensionAllowed(path))
                {
                    error = "不支持的附件格式：" + Path.GetExtension(path) + "（允许：" +
                            options.AllowedExtensionList + "）";
                    return false;
                }
                long size = new FileInfo(path).Length;
                if (size <= 0)
                {
                    error = "附件为空：" + Path.GetFileName(path);
                    return false;
                }
                if (size > options.MaxAttachmentBytes)
                {
                    error = "单个附件不能超过 " + MetricFormat.Bytes(options.MaxAttachmentBytes) + "：" +
                            Path.GetFileName(path);
                    return false;
                }
                total += size;
                if (total > options.MaxTotalAttachmentBytes)
                {
                    error = "附件总大小不能超过 " + MetricFormat.Bytes(options.MaxTotalAttachmentBytes) + "。";
                    return false;
                }
            }

            return true;
        }

        public static FeedbackResult Submit(FeedbackSubmission submission, FeedbackContext context,
                                            FeedbackOptions options = null)
        {
            options = options ?? new FeedbackOptions();
            context = context ?? new FeedbackContext();
            FeedbackResult result = new FeedbackResult { CreatedAtUtc = DateTime.UtcNow };

            string error;
            if (!Validate(submission, options, out error))
            {
                result.Error = error;
                return result;
            }

            string dataDirectory = context.DataDirectory;
            if (string.IsNullOrEmpty(dataDirectory))
            {
                result.Error = "找不到反馈保存目录。";
                return result;
            }

            DateTime now = DateTime.Now;
            string id = CreateId(now);
            string kindKey = FeedbackKinds.Key(submission.Kind);
            string root = FeedbackRoot(dataDirectory);
            string temporary = Path.Combine(root, ".tmp-" + id);
            string package = Path.Combine(root, id + "_" + kindKey + ".zip");
            string temporaryZip = package + ".tmp";

            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(temporary);
                Directory.CreateDirectory(Path.Combine(temporary, "attachments"));

                List<FeedbackAttachmentRecord> attachmentRecords = CopyAttachments(
                    submission.AttachmentPaths, Path.Combine(temporary, "attachments"));

                List<FeedbackLogFile> logs = submission.IncludeLogs
                    ? CollectLogs(context, options)
                    : new List<FeedbackLogFile>();
                List<string> logNames = new List<string>();
                if (logs.Count > 0)
                {
                    string logDirectory = Path.Combine(temporary, "logs");
                    Directory.CreateDirectory(logDirectory);
                    foreach (FeedbackLogFile log in logs)
                    {
                        string name = UniqueName(logDirectory, SanitizeFileName(log.Name));
                        File.WriteAllText(Path.Combine(logDirectory, name),
                            RedactText(log.Text, context), Utf8NoBom);
                        logNames.Add(name);
                    }
                }

                // 诊断摘要不再写成单独的文件：它与记录里的字段是同一份内容，合并进 feedback.json。
                string diagnostics = submission.IncludeLogs
                    ? RedactText(BuildDiagnosticsText(context), context)
                    : null;

                FeedbackRecord record = new FeedbackRecord
                {
                    Id = id,
                    Kind = kindKey,
                    Text = (submission.Text ?? string.Empty).Trim(),
                    CreatedAtUtc = result.CreatedAtUtc.ToString("o", Inv),
                    AppVersion = context.AppVersion,
                    Os = context.OsVersion,
                    Arch = context.Architecture,
                    IncludeLogs = submission.IncludeLogs,
                    Diagnostics = diagnostics,
                    Cpu = submission.IncludeLogs ? context.CpuModel : null,
                    MemoryTotalBytes = submission.IncludeLogs ? context.MemoryTotalBytes : 0,
                    Agent = submission.IncludeLogs ? context.AgentSummary : null,
                    Settings = submission.IncludeLogs ? context.SettingsSummary : null,
                    Attachments = attachmentRecords.Count > 0 ? attachmentRecords : null,
                    Logs = logNames.Count > 0 ? logNames : null
                };

                File.WriteAllText(Path.Combine(temporary, "feedback.json"),
                    record.ToPrettyJson(), Utf8NoBom);
                File.WriteAllText(Path.Combine(temporary, "feedback.md"),
                    BuildMarkdown(record, diagnostics, context), Utf8NoBom);

                ZipDirectory(temporary, temporaryZip);
                if (File.Exists(package)) File.Delete(package);
                File.Move(temporaryZip, package);

                try
                {
                    AppendLedger(root, new LedgerEntry
                    {
                        Type = "saved",
                        Id = id,
                        Kind = kindKey,
                        Package = Path.GetFileName(package),
                        Bytes = new FileInfo(package).Length,
                        AtUtc = result.CreatedAtUtc.ToString("o", Inv)
                    });
                }
                catch (Exception ex)
                {
                    result.Warning = "反馈已保存，但索引写入失败：" + ex.GetType().Name;
                }

                result.Ok = true;
                result.Id = id;
                result.PackagePath = package;
                result.AttachmentCount = attachmentRecords.Count;
                result.IncludedLogs = submission.IncludeLogs && (diagnostics != null || logNames.Count > 0);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "反馈保存失败：" + ex.Message;
                return result;
            }
            finally
            {
                TryDeleteDirectory(temporary);
                try { if (File.Exists(temporaryZip)) File.Delete(temporaryZip); }
                catch (Exception) { }
            }
        }

        public static FeedbackPreview BuildPreview(FeedbackContext context, bool includeLogs,
                                                   FeedbackOptions options = null)
        {
            context = context ?? new FeedbackContext();
            options = options ?? new FeedbackOptions();

            List<string> metadata = new List<string>
            {
                "程序：" + context.AppVersion,
                "系统：" + context.OsVersion,
                "架构：" + context.Architecture
            };
            if (includeLogs)
            {
                metadata.Add("CPU：" + (string.IsNullOrEmpty(context.CpuModel) ? "未知" : context.CpuModel));
                metadata.Add("内存：" + MetricFormat.Bytes(context.MemoryTotalBytes));
                metadata.Add("Agent：" + (string.IsNullOrEmpty(context.AgentSummary) ? "未知" : context.AgentSummary));
                metadata.Add("设置：" + (string.IsNullOrEmpty(context.SettingsSummary) ? "未知" : context.SettingsSummary));
            }

            List<FeedbackLogFile> logs = includeLogs
                ? CollectLogs(context, options)
                : new List<FeedbackLogFile>();
            string logsText;
            if (!includeLogs)
            {
                logsText = "未勾选“附带诊断日志”，不会读取或附带日志。";
            }
            else if (logs.Count == 0)
            {
                logsText = "没有找到日志文件。";
            }
            else
            {
                StringBuilder text = new StringBuilder();
                for (int i = 0; i < logs.Count; i++)
                {
                    if (i > 0) text.AppendLine();
                    text.Append("--- ").Append(logs[i].Name).AppendLine(" ---");
                    text.AppendLine(TailLines(logs[i].Text, 40));
                }
                logsText = text.ToString();
            }

            return new FeedbackPreview
            {
                MetadataText = RedactText(string.Join("\n", metadata.ToArray()), context),
                LogsText = RedactText(logsText, context),
                LogFileName = string.Join(", ", logs.Select(log => log.Name).ToArray())
            };
        }

        public static List<FeedbackLogFile> CollectLogs(FeedbackContext context, FeedbackOptions options)
        {
            options = options ?? new FeedbackOptions();
            List<FeedbackLogFile> result = new List<FeedbackLogFile>();
            string directory = string.IsNullOrEmpty(context.LogDirectory)
                ? context.DataDirectory
                : context.LogDirectory;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return result;

            List<FileInfo> files = new List<FileInfo>();
            try
            {
                foreach (string path in Directory.GetFiles(directory, "*.log"))
                {
                    try
                    {
                        FileInfo info = new FileInfo(path);
                        if (info.Length > 0) files.Add(info);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            files.Sort((left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
            int take = Math.Min(options.MaxLogFiles, files.Count);
            for (int i = 0; i < take; i++)
            {
                try
                {
                    string text = ReadTailText(files[i].FullName, options.MaxLogBytes);
                    if (!string.IsNullOrEmpty(text))
                    {
                        result.Add(new FeedbackLogFile
                        {
                            Name = SanitizeFileName(files[i].Name),
                            Text = text
                        });
                    }
                }
                catch (Exception) { }
            }
            return result;
        }

        public static string RedactText(string text, FeedbackContext context = null)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string machine = Environment.MachineName;

            if (!string.IsNullOrEmpty(appData))
                text = ReplaceIgnoreCase(text, appData, "%APPDATA%");
            if (!string.IsNullOrEmpty(profile))
                text = ReplaceIgnoreCase(text, profile, "%USERPROFILE%");
            if (!string.IsNullOrEmpty(machine))
                text = Regex.Replace(text, Regex.Escape(machine), "%COMPUTERNAME%",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (context != null && !string.IsNullOrEmpty(context.DataDirectory))
                text = ReplaceIgnoreCase(text, context.DataDirectory, "%APPDATA%\\QuadVitals");
            return text;
        }

        public static string SanitizeFileName(string name)
        {
            // Do not call Path.GetFileName here: it throws on raw input containing characters
            // that are illegal in a path (e.g. "a<b>:c.txt"), and this method is the sanitizer.
            string raw = name ?? string.Empty;
            int slash = Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/'));
            string fileName = slash >= 0 ? raw.Substring(slash + 1) : raw;
            if (string.IsNullOrEmpty(fileName)) return "attachment";

            StringBuilder cleaned = new StringBuilder(fileName.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in fileName)
            {
                bool bad = c < 32;
                if (!bad)
                {
                    for (int i = 0; i < invalid.Length; i++)
                    {
                        if (c == invalid[i]) { bad = true; break; }
                    }
                }
                cleaned.Append(bad ? '_' : c);
            }

            string result = cleaned.ToString().Trim().Trim('.');
            if (result.Length == 0) result = "attachment";
            if (result.Length > 80)
            {
                string extension = Path.GetExtension(result);
                string stem = Path.GetFileNameWithoutExtension(result);
                int keep = Math.Max(1, 80 - extension.Length);
                result = stem.Substring(0, Math.Min(stem.Length, keep)) + extension;
            }
            return result;
        }

        // ---- draft -----------------------------------------------------------------

        public static FeedbackDraft LoadDraft(string dataDirectory, out List<string> warnings)
        {
            warnings = new List<string>();
            string path = Path.Combine(DraftRoot(dataDirectory), "draft.json");
            if (!File.Exists(path)) return null;

            FeedbackDraft draft;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FeedbackDraft));
                    draft = (FeedbackDraft)serializer.ReadObject(stream);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("反馈草稿读取失败（" + ex.GetType().Name + "），已忽略。");
                return null;
            }

            if (draft == null) return null;
            draft.Text = draft.Text ?? string.Empty;
            if (draft.Attachments == null) draft.Attachments = new List<FeedbackDraftAttachment>();

            List<FeedbackDraftAttachment> kept = new List<FeedbackDraftAttachment>();
            foreach (FeedbackDraftAttachment attachment in draft.Attachments)
            {
                if (attachment == null || string.IsNullOrEmpty(attachment.FileName)) continue;
                string file = Path.Combine(DraftAttachmentsDirectory(dataDirectory), attachment.FileName);
                if (File.Exists(file)) kept.Add(attachment);
                else warnings.Add("草稿附件已丢失：" + attachment.OriginalName);
            }
            draft.Attachments = kept;
            return draft;
        }

        public static void SaveDraft(string dataDirectory, FeedbackDraft draft)
        {
            if (draft == null) return;
            string root = DraftRoot(dataDirectory);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(DraftAttachmentsDirectory(dataDirectory));
            draft.UpdatedAtUtc = DateTime.UtcNow;
            draft.Text = draft.Text ?? string.Empty;
            if (draft.Attachments == null) draft.Attachments = new List<FeedbackDraftAttachment>();

            string path = Path.Combine(root, "draft.json");
            string temporary = path + ".tmp";
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FeedbackDraft));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, draft);
                string json = JsonPretty.Print(Encoding.UTF8.GetString(stream.ToArray()));
                File.WriteAllText(temporary, json, Utf8NoBom);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        public static void ClearDraft(string dataDirectory)
        {
            TryDeleteDirectory(DraftRoot(dataDirectory));
        }

        public static FeedbackDraftAttachment ImportDraftAttachment(string dataDirectory, string sourcePath,
                                                                   FeedbackOptions options, out string error)
        {
            error = null;
            options = options ?? new FeedbackOptions();
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "附件不存在。";
                return null;
            }
            if (!options.IsExtensionAllowed(sourcePath))
            {
                error = "不支持的附件格式：" + Path.GetExtension(sourcePath) + "（允许：" +
                        options.AllowedExtensionList + "）";
                return null;
            }

            long size = new FileInfo(sourcePath).Length;
            if (size <= 0)
            {
                error = "附件为空。";
                return null;
            }
            if (size > options.MaxAttachmentBytes)
            {
                error = "单个附件不能超过 " + MetricFormat.Bytes(options.MaxAttachmentBytes) + "。";
                return null;
            }

            try
            {
                string directory = DraftAttachmentsDirectory(dataDirectory);
                Directory.CreateDirectory(directory);
                string name = UniqueName(directory, SanitizeFileName(Path.GetFileName(sourcePath)));
                File.Copy(sourcePath, Path.Combine(directory, name), false);
                return new FeedbackDraftAttachment
                {
                    FileName = name,
                    OriginalName = Path.GetFileName(sourcePath),
                    Bytes = size
                };
            }
            catch (Exception ex)
            {
                error = "复制附件失败：" + ex.Message;
                return null;
            }
        }

        public static void RemoveDraftAttachment(string dataDirectory, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;
            try
            {
                string path = Path.Combine(DraftAttachmentsDirectory(dataDirectory), SanitizeFileName(fileName));
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { }
        }

        // ---- helpers ----------------------------------------------------------------

        private static List<FeedbackAttachmentRecord> CopyAttachments(List<string> paths, string destination)
        {
            List<FeedbackAttachmentRecord> records = new List<FeedbackAttachmentRecord>();
            if (paths == null) return records;

            for (int i = 0; i < paths.Count; i++)
            {
                string source = paths[i];
                string name = UniqueName(destination, SanitizeFileName(Path.GetFileName(source)));
                string target = Path.Combine(destination, name);
                File.Copy(source, target, false);
                records.Add(new FeedbackAttachmentRecord
                {
                    FileName = name,
                    OriginalName = Path.GetFileName(source),
                    Bytes = new FileInfo(target).Length
                });
            }
            return records;
        }

        private static string BuildDiagnosticsText(FeedbackContext context)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("QuadVitals 反馈诊断");
            text.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            text.AppendLine("程序版本: " + context.AppVersion);
            text.AppendLine("系统: " + context.OsVersion);
            text.AppendLine("架构: " + context.Architecture);
            if (!string.IsNullOrEmpty(context.CpuModel)) text.AppendLine("CPU: " + context.CpuModel);
            if (context.MemoryTotalBytes > 0)
                text.AppendLine("内存: " + MetricFormat.Bytes(context.MemoryTotalBytes));
            if (!string.IsNullOrEmpty(context.AgentSummary)) text.AppendLine("Agent: " + context.AgentSummary);
            if (!string.IsNullOrEmpty(context.SettingsSummary)) text.AppendLine("设置: " + context.SettingsSummary);
            return text.ToString();
        }

        private static string BuildMarkdown(FeedbackRecord record, string diagnostics, FeedbackContext context)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("# QuadVitals 反馈 " + record.Id);
            text.AppendLine();
            text.AppendLine("- 类型: " + FeedbackKinds.DisplayName(ParseKind(record.Kind)));
            text.AppendLine("- 时间: " + record.CreatedAtUtc);
            text.AppendLine("- 程序: " + record.AppVersion);
            text.AppendLine("- 系统: " + record.Os);
            text.AppendLine("- 架构: " + record.Arch);

            if (record.Attachments != null && record.Attachments.Count > 0)
            {
                text.AppendLine("- 附件: " + string.Join(", ",
                    record.Attachments.Select(a => a.FileName + " (" + MetricFormat.Bytes(a.Bytes) + ")").ToArray()));
            }
            else
            {
                text.AppendLine("- 附件: 无");
            }
            text.AppendLine("- 日志: " + (record.Logs != null && record.Logs.Count > 0
                ? string.Join(", ", record.Logs.ToArray())
                : "未附带"));
            text.AppendLine();
            text.AppendLine("## 反馈内容");
            text.AppendLine();
            text.AppendLine(string.IsNullOrEmpty(record.Text) ? "（未填写，仅附件）" : record.Text);
            text.AppendLine();
            text.AppendLine("## 诊断信息");
            text.AppendLine();
            text.AppendLine(diagnostics == null
                ? "未勾选“附带诊断日志”，不附带诊断信息。"
                : RedactText(diagnostics, context));
            return text.ToString();
        }

        private static FeedbackKind ParseKind(string key)
        {
            FeedbackKind kind;
            return FeedbackKinds.TryParse(key, out kind) ? kind : FeedbackKind.Other;
        }

        private static void AppendLedger(string root, LedgerEntry entry)
        {
            lock (LedgerGate)
            {
                Directory.CreateDirectory(root);
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(LedgerEntry));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, entry);
                    string json = Utf8NoBom.GetString(stream.ToArray());
                    File.AppendAllText(Path.Combine(root, LedgerFileName), json + "\n", Utf8NoBom);
                }
            }
        }

        /// <summary>Upload succeeded: the local zip is deleted, the ledger keeps the proof.</summary>
        public static void AppendUploaded(string dataDirectory, string id, string remoteId)
        {
            AppendLedger(FeedbackRoot(dataDirectory), new LedgerEntry
            {
                Type = "uploaded",
                Id = id,
                RemoteId = remoteId,
                AtUtc = DateTime.UtcNow.ToString("o", Inv)
            });
        }

        /// <summary>Upload failed: the local zip stays, the reason is recorded for the next launch.</summary>
        public static void AppendUploadFailed(string dataDirectory, string id, string error)
        {
            AppendLedger(FeedbackRoot(dataDirectory), new LedgerEntry
            {
                Type = "upload-failed",
                Id = id,
                Error = error,
                AtUtc = DateTime.UtcNow.ToString("o", Inv)
            });
        }

        /// <summary>Ids whose newest ledger event is "uploaded" (append-only: last event wins).</summary>
        public static HashSet<string> ReadUploadedIds(string dataDirectory)
        {
            HashSet<string> uploaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = LedgerPath(dataDirectory);
            if (!File.Exists(path)) return uploaded;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception) { return uploaded; }

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                LedgerEntry entry;
                try
                {
                    using (MemoryStream stream = new MemoryStream(Utf8NoBom.GetBytes(line)))
                    {
                        entry = (LedgerEntry)new DataContractJsonSerializer(typeof(LedgerEntry)).ReadObject(stream);
                    }
                }
                catch (Exception)
                {
                    continue;
                }
                if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                if (string.Equals(entry.Type, "uploaded", StringComparison.OrdinalIgnoreCase))
                {
                    uploaded.Add(entry.Id);
                }
                else if (string.Equals(entry.Type, "upload-failed", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(entry.Type, "saved", StringComparison.OrdinalIgnoreCase))
                {
                    uploaded.Remove(entry.Id);
                }
            }
            return uploaded;
        }

        private static void ZipDirectory(string directory, string zipPath)
        {
            using (FileStream file = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string relative = path.Substring(directory.Length + 1).Replace('\\', '/');
                    ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    using (Stream entryStream = entry.Open())
                    using (FileStream source = File.OpenRead(path))
                    {
                        source.CopyTo(entryStream);
                    }
                }
            }
        }

        private static string ReadTailText(string path, long maxBytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bool truncated = stream.Length > maxBytes;
                long start = truncated ? stream.Length - maxBytes : 0;
                stream.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[(int)(stream.Length - start)];
                int read = 0;
                while (read < buffer.Length)
                {
                    int chunk = stream.Read(buffer, read, buffer.Length - read);
                    if (chunk <= 0) break;
                    read += chunk;
                }
                string text = new UTF8Encoding(false, false).GetString(buffer, 0, read);
                if (truncated) text = "...（仅保留最后 " + MetricFormat.Bytes(maxBytes) + "）...\n" + text;
                return text;
            }
        }

        private static string TailLines(string text, int count)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int start = Math.Max(0, lines.Length - count);
            return string.Join("\n", lines.Skip(start).ToArray());
        }

        private static string UniqueName(string directory, string name)
        {
            string candidate = name;
            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            int suffix = 2;
            while (File.Exists(Path.Combine(directory, candidate)))
            {
                candidate = stem + "-" + suffix + extension;
                suffix++;
            }
            return candidate;
        }

        private static string ReplaceIgnoreCase(string text, string needle, string replacement)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return text;
            return Regex.Replace(text, Regex.Escape(needle), replacement.Replace("$", "$$"),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception) { }
        }
    }

    internal static class FeedbackSystemInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        public static string OsVersion()
        {
            try
            {
                object product = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", null);
                object display = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null);
                object build = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", null);
                string text = product == null ? Environment.OSVersion.VersionString : product.ToString();
                if (display != null) text += " " + display;
                if (build != null) text += " (build " + build + ")";
                return text;
            }
            catch (Exception)
            {
                return Environment.OSVersion.VersionString;
            }
        }

        public static string CpuModel()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null);
                return value == null ? string.Empty : value.ToString().Trim();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static long TotalMemoryBytes()
        {
            try
            {
                MEMORYSTATUSEX status = new MEMORYSTATUSEX();
                status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref status)) return (long)status.ullTotalPhys;
            }
            catch (Exception) { }
            return 0;
        }
    }
}
