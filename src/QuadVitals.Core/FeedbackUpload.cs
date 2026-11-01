using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace QuadVitals.Core
{
    /// <summary>
    /// One upload attempt. The local zip stays the source of truth; this only carries
    /// the same content to the receiver. No retries and no queue: a failure is shown to the user,
    /// who can press "重新上传" — Settings is a short-lived process, so a background queue would
    /// need a resident owner.
    /// </summary>
    public sealed class FeedbackUploadRequest
    {
        public string Kind = "other";
        public string Message = string.Empty;
        public string MetadataJson = "{}";
        public List<string> AttachmentPaths = new List<string>();
    }

    /// <summary>Transport boundary so assertions never touch the network.</summary>
    public interface IFeedbackTransport
    {
        FeedbackTransportResult Send(string url, string bodyFile, string contentType,
                                     long contentLength, TimeSpan timeout);
    }

    public sealed class FeedbackTransportResult
    {
        public int StatusCode;
        public string Body;
        /// <summary>Set when no HTTP response was received at all (DNS/TLS/timeouts).</summary>
        public string Failure;
    }

    public sealed class FeedbackUploadResult
    {
        public bool Ok;
        public string FeedbackId;
        public string Error;
    }

    /// <summary>Shapes the multipart body and interprets the receiver's answer.</summary>
    public static class FeedbackUploader
    {
        public const string Endpoint = "https://quadvitals.enosawa.uno:8088/api/quadvitals/feedback";
        public const int MaxAttachments = 8;
        public const long MaxAttachmentBytes = 50L * 1024 * 1024;
        /// <summary>Request budget: NPM rejects anything over 410 MiB, so leave 1 MiB headroom.</summary>
        public const long MaxRequestBytes = 409L * 1024 * 1024;

        public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly Regex FeedbackIdPattern = new Regex("^QV_[0-9a-f]{8}$", RegexOptions.CultureInvariant);
        private static readonly Regex ErrorCodePattern = new Regex("^[a-z][a-z0-9_]{0,40}$", RegexOptions.CultureInvariant);

        public static FeedbackUploadResult Upload(FeedbackUploadRequest request, IFeedbackTransport transport,
                                                  string tempDirectory = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (transport == null) throw new ArgumentNullException(nameof(transport));

            string error;
            if (!Validate(request, out error))
            {
                return new FeedbackUploadResult { Error = error };
            }

            string directory = string.IsNullOrEmpty(tempDirectory) ? Path.GetTempPath() : tempDirectory;
            string contentType;
            long contentLength;
            string bodyFile = BuildBody(request, directory, out contentType, out contentLength);
            try
            {
                FeedbackTransportResult response = transport.Send(Endpoint, bodyFile, contentType, contentLength, Timeout);
                return Interpret(response);
            }
            finally
            {
                try { if (File.Exists(bodyFile)) File.Delete(bodyFile); }
                catch (Exception) { }
            }
        }

        /// <summary>Cheap pre-flight so a rejected package is not uploaded at all.</summary>
        public static bool Validate(FeedbackUploadRequest request, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(request.Message) && request.AttachmentPaths.Count == 0)
            {
                error = "反馈内容不能为空（也可以只附截图或日志）。";
                return false;
            }
            if (request.AttachmentPaths.Count > MaxAttachments)
            {
                error = "最多只能添加 " + MaxAttachments + " 个附件。";
                return false;
            }

            FeedbackOptions options = new FeedbackOptions();
            long total = 0;
            foreach (string path in request.AttachmentPaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "附件不存在：" + (path ?? string.Empty);
                    return false;
                }
                if (!options.IsExtensionAllowed(path))
                {
                    error = "不支持的附件格式：" + Path.GetExtension(path);
                    return false;
                }
                long size = new FileInfo(path).Length;
                if (size <= 0)
                {
                    error = "附件为空：" + Path.GetFileName(path);
                    return false;
                }
                if (size > MaxAttachmentBytes)
                {
                    error = "单个附件不能超过 " + MetricFormat.Bytes(MaxAttachmentBytes) + "。";
                    return false;
                }
                total += size;
                if (total > MaxRequestBytes)
                {
                    error = "附件总大小不能超过 " + MetricFormat.Bytes(MaxRequestBytes) + "。";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Serialises the body once into a temp file: the receiver needs Content-Length, and
        /// 8 x 50 MB attachments must not be assembled in memory.
        /// </summary>
        public static string BuildBody(FeedbackUploadRequest request, string directory,
                                       out string contentType, out long contentLength)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string boundary = "----QuadVitals" + Guid.NewGuid().ToString("N");
            contentType = "multipart/form-data; boundary=" + boundary;
            if (string.IsNullOrEmpty(directory)) directory = Path.GetTempPath();
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, "feedback-upload-" + Guid.NewGuid().ToString("N") + ".body");
            long written = 0;

            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                written += WriteText(stream, "--" + boundary + "\r\n");
                written += WriteText(stream, "Content-Disposition: form-data; name=\"message\"\r\n");
                written += WriteText(stream, "Content-Type: text/plain; charset=utf-8\r\n\r\n");
                written += WriteBytes(stream, Utf8.GetBytes(request.Message ?? string.Empty));
                written += WriteText(stream, "\r\n");

                written += WriteText(stream, "--" + boundary + "\r\n");
                written += WriteText(stream, "Content-Disposition: form-data; name=\"metadata\"\r\n");
                written += WriteText(stream, "Content-Type: application/json; charset=utf-8\r\n\r\n");
                written += WriteBytes(stream, Utf8.GetBytes(request.MetadataJson ?? "{}"));
                written += WriteText(stream, "\r\n");

                foreach (string attachment in request.AttachmentPaths)
                {
                    string name = FeedbackStore.SanitizeFileName(Path.GetFileName(attachment));
                    written += WriteText(stream, "--" + boundary + "\r\n");
                    written += WriteText(stream, "Content-Disposition: form-data; name=\"attachments\"; filename=\""
                                                 + AsciiFallback(name) + "\"; filename*=UTF-8''" + PercentEncode(name) + "\r\n");
                    written += WriteText(stream, "Content-Type: application/octet-stream\r\n\r\n");
                    using (FileStream source = File.OpenRead(attachment))
                    {
                        byte[] buffer = new byte[81920];
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            stream.Write(buffer, 0, read);
                            written += read;
                        }
                    }
                    written += WriteText(stream, "\r\n");
                }

                written += WriteText(stream, "--" + boundary + "--\r\n");
            }

            contentLength = written;
            return path;
        }

        /// <summary>Builds the metadata JSON the receiver stores next to the package.</summary>
        public static string BuildMetadata(FeedbackContext context, string kind, IList<string> attachmentPaths)
        {
            UploadMetadata metadata = new UploadMetadata
            {
                Kind = string.IsNullOrEmpty(kind) ? "other" : kind,
                AppVersion = context == null ? "unknown" : context.AppVersion,
                OsVersion = context == null ? "unknown" : context.OsVersion,
                Arch = context == null ? "unknown" : context.Architecture
            };
            if (context != null)
            {
                metadata.IncludeLogs = !string.IsNullOrEmpty(context.AgentSummary);
                metadata.Cpu = string.IsNullOrEmpty(context.CpuModel) ? null : context.CpuModel;
                metadata.MemoryTotalBytes = context.MemoryTotalBytes;
                metadata.Agent = string.IsNullOrEmpty(context.AgentSummary) ? null : context.AgentSummary;
                metadata.Settings = string.IsNullOrEmpty(context.SettingsSummary) ? null : context.SettingsSummary;
            }
            if (attachmentPaths != null && attachmentPaths.Count > 0)
            {
                metadata.Attachments = new List<UploadAttachmentRecord>(attachmentPaths.Count);
                foreach (string path in attachmentPaths)
                {
                    metadata.Attachments.Add(new UploadAttachmentRecord
                    {
                        FileName = FeedbackStore.SanitizeFileName(Path.GetFileName(path)),
                        Bytes = File.Exists(path) ? new FileInfo(path).Length : 0
                    });
                }
            }
            return metadata.ToJson();
        }

        /// <summary>
        /// Maps the answer to a user-facing sentence. Only the short error code is ever shown or
        /// logged — the body may contain receiver internals.
        /// </summary>
        public static FeedbackUploadResult Interpret(FeedbackTransportResult response)
        {
            if (response == null)
            {
                return new FeedbackUploadResult { Error = "反馈接收服务没有响应，请稍后重试。" };
            }
            if (!string.IsNullOrEmpty(response.Failure))
            {
                return new FeedbackUploadResult { Error = "无法连接反馈接收服务，请检查网络后重试。" };
            }

            UploadResponse payload = ParseBody(response.Body);

            if (response.StatusCode == 201)
            {
                if (payload != null && payload.Ok && !string.IsNullOrEmpty(payload.FeedbackId) &&
                    FeedbackIdPattern.IsMatch(payload.FeedbackId))
                {
                    return new FeedbackUploadResult { Ok = true, FeedbackId = payload.FeedbackId };
                }
                return new FeedbackUploadResult { Error = "反馈接收服务返回异常，请稍后重试。" };
            }

            if (response.StatusCode == 429)
            {
                return new FeedbackUploadResult { Error = "反馈提交过于频繁，请稍后再试。" };
            }
            if (response.StatusCode == 413)
            {
                return new FeedbackUploadResult { Error = "反馈附件总大小过大，请减少附件后重试。" };
            }
            if (response.StatusCode >= 400 && response.StatusCode < 500)
            {
                string code = payload == null ? null : payload.Error;
                return new FeedbackUploadResult { Error = DescribeCode(code) };
            }
            return new FeedbackUploadResult { Error = "反馈接收服务暂时不可用，请稍后重试。" };
        }

        private static string DescribeCode(string code)
        {
            if (string.IsNullOrEmpty(code) || !ErrorCodePattern.IsMatch(code)) return "反馈内容未通过校验，请检查后重试。";
            switch (code)
            {
                case "empty_feedback": return "反馈内容不能为空，请写点内容后再提交。";
                case "rate_limited": return "反馈提交过于频繁，请稍后再试。";
                case "too_many_attachments": return "附件数量超过上限，请减少后重试。";
                case "attachment_too_large": return "有附件超过单张大小上限，请压缩后重试。";
                case "unsupported_attachment": return "有附件格式不被接收端支持，请换一种格式。";
                case "invalid_metadata": return "诊断信息超出上限或格式不对，请关闭“附带诊断日志”后重试。";
                case "request_too_large": return "反馈整包超过接收端上限，请减少附件后重试。";
            }
            return "反馈内容未通过校验，请检查后重试（错误码：" + code + "）。";
        }

        private static UploadResponse ParseBody(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 4096) return null;
            try
            {
                using (MemoryStream stream = new MemoryStream(Utf8.GetBytes(body)))
                {
                    DataContractJsonSerializer serializer =
                        new DataContractJsonSerializer(typeof(UploadResponse));
                    return (UploadResponse)serializer.ReadObject(stream);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long WriteText(Stream stream, string text)
        {
            return WriteBytes(stream, Utf8.GetBytes(text));
        }

        private static long WriteBytes(Stream stream, byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
            return bytes.Length;
        }

        /// <summary>Non-ASCII filenames go through filename*; keep the plain one ASCII-safe.</summary>
        private static string AsciiFallback(string name)
        {
            StringBuilder builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool ascii = c >= 32 && c < 127 && c != '"' && c != '\\';
                builder.Append(ascii ? c : '_');
            }
            return builder.Length == 0 ? "attachment" : builder.ToString();
        }

        private static string PercentEncode(string value)
        {
            StringBuilder builder = new StringBuilder();
            foreach (byte b in Utf8.GetBytes(value))
            {
                bool safe = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                            || b == '-' || b == '_' || b == '.' || b == '~';
                builder.Append(safe ? ((char)b).ToString() : "%" + b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        [DataContract]
        internal sealed class UploadResponse
        {
            [DataMember(Name = "ok")]
            public bool Ok;

            [DataMember(Name = "feedbackId")]
            public string FeedbackId;

            [DataMember(Name = "error")]
            public string Error;
        }

        [DataContract]
        internal sealed class UploadMetadata
        {
            [DataMember(Name = "kind")]
            public string Kind;

            [DataMember(Name = "appVersion")]
            public string AppVersion;

            [DataMember(Name = "osVersion")]
            public string OsVersion;

            [DataMember(Name = "arch")]
            public string Arch;

            [DataMember(Name = "includeLogs", EmitDefaultValue = false)]
            public bool IncludeLogs;

            [DataMember(Name = "cpu", EmitDefaultValue = false)]
            public string Cpu;

            [DataMember(Name = "memoryTotalBytes", EmitDefaultValue = false)]
            public long MemoryTotalBytes;

            [DataMember(Name = "agent", EmitDefaultValue = false)]
            public string Agent;

            [DataMember(Name = "settings", EmitDefaultValue = false)]
            public string Settings;

            [DataMember(Name = "attachments", EmitDefaultValue = false)]
            public List<UploadAttachmentRecord> Attachments;

            public string ToJson()
            {
                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(typeof(UploadMetadata));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, this);
                    return Utf8.GetString(stream.ToArray());
                }
            }
        }

        [DataContract]
        internal sealed class UploadAttachmentRecord
        {
            [DataMember(Name = "fileName")]
            public string FileName;

            [DataMember(Name = "bytes")]
            public long Bytes;
        }
    }

    /// <summary>Real transport: streams the already-built body file to the receiver.</summary>
    public sealed class HttpFeedbackTransport : IFeedbackTransport
    {
        public FeedbackTransportResult Send(string url, string bodyFile, string contentType,
                                            long contentLength, TimeSpan timeout)
        {
            try
            {
                // .NET Framework 4.8 默认可能仍协商到 TLS 1.0；接收端只接受现代协议。
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
                // 老系统上设置失败不应阻断上传尝试
            }

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = contentType;
                request.ContentLength = contentLength;
                request.Timeout = (int)timeout.TotalMilliseconds;
                request.ReadWriteTimeout = (int)timeout.TotalMilliseconds;
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;
                request.UserAgent = "QuadVitals-Feedback/1";

                using (Stream requestStream = request.GetRequestStream())
                using (FileStream body = File.OpenRead(bodyFile))
                {
                    body.CopyTo(requestStream, 81920);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return new FeedbackTransportResult
                    {
                        StatusCode = (int)response.StatusCode,
                        Body = ReadBody(response)
                    };
                }
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    {
                        return new FeedbackTransportResult
                        {
                            StatusCode = (int)response.StatusCode,
                            Body = ReadBody(response)
                        };
                    }
                }
                return new FeedbackTransportResult { Failure = exception.Status.ToString() };
            }
            catch (Exception exception)
            {
                return new FeedbackTransportResult { Failure = exception.GetType().Name };
            }
        }

        /// <summary>Only the first 4 KiB is needed: the receiver answers with a short code.</summary>
        private static string ReadBody(HttpWebResponse response)
        {
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null) return string.Empty;
                    byte[] buffer = new byte[4096];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    return read <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
