using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace QuadVitals.Core
{
    /// <summary>
    /// Startup sweep. Settings is a short-lived process, so instead of a background
    /// queue each launch retries the packages that never reached the receiver, and removes
    /// leftovers whose ledger already says "uploaded".
    ///
    /// Policy:
    ///   upload ok   -> delete the local zip, ledger keeps the proof (append-only)
    ///   upload fail -> keep the zip, record the reason, retry on the next launch
    /// </summary>
    public static class FeedbackRecovery
    {
        public sealed class Outcome
        {
            public int Uploaded;
            public int Failed;
            public int Removed;
            public int Skipped;
        }

        private static readonly Regex PackageNamePattern =
            new Regex("^(\\d{8}-\\d{6}-[0-9a-fA-F]+)_[a-z]+\\.zip$", RegexOptions.CultureInvariant);

        public static Outcome Flush(string dataDirectory, IFeedbackTransport transport)
        {
            Outcome outcome = new Outcome();
            if (string.IsNullOrEmpty(dataDirectory) || transport == null) return outcome;

            string inbox = FeedbackStore.FeedbackRoot(dataDirectory);
            if (!Directory.Exists(inbox)) return outcome;

            CleanStaleTempDirectories();

            HashSet<string> uploaded;
            try { uploaded = FeedbackStore.ReadUploadedIds(dataDirectory); }
            catch (Exception) { uploaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

            string[] packages;
            try { packages = Directory.GetFiles(inbox, "*.zip"); }
            catch (Exception) { return outcome; }

            Array.Sort(packages, StringComparer.OrdinalIgnoreCase);
            foreach (string package in packages)
            {
                try
                {
                    Sweep(package, uploaded, dataDirectory, transport, outcome);
                }
                catch (Exception)
                {
                    // 一个坏包不能挡住其余包的重试。
                    outcome.Failed++;
                }
            }
            return outcome;
        }

        private static void Sweep(string package, HashSet<string> uploaded, string dataDirectory,
                                  IFeedbackTransport transport, Outcome outcome)
        {
            string knownId = IdFromPackageName(Path.GetFileName(package));
            if (knownId != null && uploaded.Contains(knownId))
            {
                if (TryDelete(package)) outcome.Removed++;
                return;
            }

            FeedbackUploadRequest request;
            string tempDirectory;
            string id;
            if (!TryBuildRequest(package, out request, out tempDirectory, out id))
            {
                outcome.Skipped++;
                return;
            }

            try
            {
                FeedbackUploadResult result = FeedbackUploader.Upload(request, transport);
                if (result.Ok)
                {
                    // 先记台账再删文件：删失败的话，下次启动按 "uploaded" 只删不重传。
                    FeedbackStore.AppendUploaded(dataDirectory, id, result.FeedbackId);
                    if (TryDelete(package)) outcome.Removed++;
                    outcome.Uploaded++;
                }
                else
                {
                    FeedbackStore.AppendUploadFailed(dataDirectory, id, result.Error);
                    outcome.Failed++;
                }
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }

        /// <summary>Rebuilds an upload request from the local zip (self-describing, no draft needed).</summary>
        public static bool TryBuildRequest(string packagePath, out FeedbackUploadRequest request,
                                           out string tempDirectory, out string id)
        {
            request = null;
            tempDirectory = null;
            id = null;
            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath)) return false;

            FeedbackRecord record;
            bool hasAttachments = false;
            string diagnosticsText = null;
            List<FeedbackLogPart> logParts = new List<FeedbackLogPart>();
            List<string> attachmentPaths = new List<string>();
            string temporary = Path.Combine(Path.GetTempPath(), "qv-recover-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (FileStream file = File.OpenRead(packagePath))
                using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry recordEntry = archive.GetEntry("feedback.json");
                    if (recordEntry == null) return false;
                    using (Stream stream = recordEntry.Open())
                    {
                        record = (FeedbackRecord)new DataContractJsonSerializer(typeof(FeedbackRecord)).ReadObject(stream);
                    }
                    if (record == null || string.IsNullOrEmpty(record.Id)) return false;

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase) &&
                            entry.Name.Length > 0)
                        {
                            hasAttachments = true;
                            break;
                        }
                    }

                    if (hasAttachments)
                    {
                        Directory.CreateDirectory(temporary);
                        HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (!entry.FullName.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase) ||
                                entry.Name.Length == 0)
                            {
                                continue;
                            }
                            string name = entry.Name;
                            if (!taken.Add(name))
                            {
                                name = Guid.NewGuid().ToString("N").Substring(0, 6) + "-" + name;
                                taken.Add(name);
                            }
                            string target = Path.Combine(temporary, name);
                            using (Stream source = entry.Open())
                            using (FileStream sink = File.Create(target))
                            {
                                source.CopyTo(sink, 81920);
                            }
                            attachmentPaths.Add(target);
                        }
                    }

                    // 诊断日志也是本地包里的一部分，上传必须原样带上：本地包在成功后就被删了。
                    ZipArchiveEntry diagnosticsEntry = archive.GetEntry("diagnostics.txt");
                    if (diagnosticsEntry != null) diagnosticsText = ReadEntryText(diagnosticsEntry);

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (!entry.FullName.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
                            entry.Name.Length == 0)
                        {
                            continue;
                        }
                        string text = ReadEntryText(entry);
                        if (string.IsNullOrEmpty(text)) continue;
                        logParts.Add(new FeedbackLogPart { Name = entry.Name, Text = text });
                    }
                }

                id = record.Id;
                FeedbackContext context = new FeedbackContext
                {
                    AppVersion = string.IsNullOrEmpty(record.AppVersion) ? "unknown" : record.AppVersion,
                    OsVersion = string.IsNullOrEmpty(record.Os) ? "unknown" : record.Os,
                    Architecture = string.IsNullOrEmpty(record.Arch) ? "unknown" : record.Arch,
                    CpuModel = record.Cpu ?? string.Empty,
                    MemoryTotalBytes = record.MemoryTotalBytes,
                    AgentSummary = record.Agent ?? string.Empty,
                    SettingsSummary = record.Settings ?? string.Empty
                };

                request = new FeedbackUploadRequest
                {
                    Kind = string.IsNullOrEmpty(record.Kind) ? "other" : record.Kind,
                    Message = (record.Text ?? string.Empty).Trim(),
                    MetadataJson = FeedbackUploader.BuildMetadata(context, record.Kind, attachmentPaths,
                        record.IncludeLogs),
                    DiagnosticsText = diagnosticsText,
                    Logs = logParts,
                    AttachmentPaths = attachmentPaths
                };
                tempDirectory = hasAttachments ? temporary : null;
                return true;
            }
            catch (Exception)
            {
                request = null;
                id = null;
                TryDeleteDirectory(temporary);
                tempDirectory = null;
                return false;
            }
        }

        /// <summary>`20261030-143853-a1b2c3_bug.zip` -> `20261030-143853-a1b2c3`.</summary>
        public static string IdFromPackageName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            Match match = PackageNamePattern.Match(fileName);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>Leftover extraction dirs from a crashed session; only old ones are touched.</summary>
        private static void CleanStaleTempDirectories()
        {
            try
            {
                string temp = Path.GetTempPath();
                foreach (string directory in Directory.GetDirectories(temp, "qv-recover-*"))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromHours(1))
                        {
                            Directory.Delete(directory, true);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Reads one zip entry as UTF-8 text (logs and diagnostics are written that way).</summary>
        private static string ReadEntryText(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception)
            {
            }
        }
    }
}
