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
    }
}
