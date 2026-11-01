using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using QuadVitals.Core;

namespace QuadVitals.Core.Tests
{
    internal static partial class Program
    {
        // ---- in-app update -----------------------------------------------------------

        private const string GoodManifest =
            "{\"version\":\"0.9.1\",\"publishedAt\":\"2026-11-02T09:00:00Z\"," +
            "\"notes\":[\"修复刻度抖动\",\"\"]," +
            "\"setup\":{\"name\":\"QuadVitals-Setup-0.9.1.exe\",\"size\":1436672," +
            "\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}}";

        private static void UpdateManifestBehaviour()
        {
            UpdateManifest manifest;
            string error;

            True(UpdateManifestReader.TryParse(GoodManifest, out manifest, out error), "合法清单被接受");
            Equal("0.9.1", manifest.Version, "版本原样保留");
            Equal("QuadVitals-Setup-0.9.1.exe", manifest.SetupName, "安装包文件名");
            Equal(1436672L, manifest.SetupSize, "安装包大小");
            Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", manifest.SetupSha256,
                "哈希统一小写");
            Equal(1, manifest.Notes.Length, "空说明被丢掉");
            Equal(new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc), manifest.PublishedAtUtc, "发布时间解析为 UTC");

            True(!UpdateManifestReader.TryParse(null, out manifest, out error), "空清单被拒绝");
            Equal("manifest-empty", error, "空清单原因");
            True(!UpdateManifestReader.TryParse("not json", out manifest, out error), "坏 JSON 被拒绝");
            Equal("manifest-invalid-json", error, "坏 JSON 原因");

            string missingSetup = "{\"version\":\"0.9.1\"}";
            True(!UpdateManifestReader.TryParse(missingSetup, out manifest, out error), "缺 setup 被拒绝");
            Equal("manifest-no-setup", error, "缺 setup 原因");

            string badVersion = GoodManifest.Replace("\"0.9.1\"", "\"0.9\"");
            True(!UpdateManifestReader.TryParse(badVersion, out manifest, out error), "两段版本号被拒绝");
            Equal("manifest-bad-version", error, "两段版本号原因");

            string traversal = GoodManifest.Replace("QuadVitals-Setup-0.9.1.exe", "../evil.exe");
            True(!UpdateManifestReader.TryParse(traversal, out manifest, out error), "带路径的安装包名被拒绝");
            Equal("manifest-bad-name", error, "带路径的原因");

            string windowsTraversal = GoodManifest.Replace("QuadVitals-Setup-0.9.1.exe", "..\\\\evil.exe");
            True(!UpdateManifestReader.TryParse(windowsTraversal, out manifest, out error), "Windows 路径分隔符也被拒绝");

            string notExe = GoodManifest.Replace("QuadVitals-Setup-0.9.1.exe", "QuadVitals-Setup-0.9.1.zip");
            True(!UpdateManifestReader.TryParse(notExe, out manifest, out error), "非 exe 安装包被拒绝");

            string badHash = GoodManifest.Replace(
                "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "zzzz");
            True(!UpdateManifestReader.TryParse(badHash, out manifest, out error), "坏哈希被拒绝");
            Equal("manifest-bad-hash", error, "坏哈希原因");

            string zeroSize = GoodManifest.Replace("\"size\":1436672", "\"size\":0");
            True(!UpdateManifestReader.TryParse(zeroSize, out manifest, out error), "零大小被拒绝");
            Equal("manifest-bad-size", error, "零大小原因");

            string hugeSize = GoodManifest.Replace("\"size\":1436672", "\"size\":" + (UpdateFeed.MaxInstallerBytes + 1));
            True(!UpdateManifestReader.TryParse(hugeSize, out manifest, out error), "超过上限的大小被拒绝");

            string noDate = GoodManifest.Replace("\"publishedAt\":\"2026-11-02T09:00:00Z\",", string.Empty);
            True(UpdateManifestReader.TryParse(noDate, out manifest, out error), "缺发布时间仍可用");
            Equal(DateTime.MinValue, manifest.PublishedAtUtc, "缺发布时间记为空");
            Equal("0.9.1", manifest.DisplayVersion, "缺发布时间时显示版本号");

            string longNotes = "{\"version\":\"0.9.1\",\"setup\":{\"name\":\"a.exe\",\"size\":1," +
                "\"sha256\":\"" + new string('a', 64) + "\"},\"notes\":[" +
                string.Join(",", System.Linq.Enumerable.Repeat("\"x\"", 30)) + "]}";
            True(UpdateManifestReader.TryParse(longNotes, out manifest, out error), "超量说明仍可解析");
            Equal(UpdateManifestReader.MaxNotes, manifest.Notes.Length, "说明条数被封顶");

            // The release script also records the zip asset. A client must ignore members it does
            // not need instead of rejecting the whole manifest over them.
            string withZip = GoodManifest.Replace("\"setup\":",
                "\"zip\":{\"name\":\"QuadVitals-0.9.1.zip\",\"size\":5,\"sha256\":\"" + new string('b', 64) +
                "\"},\"setup\":");
            True(UpdateManifestReader.TryParse(withZip, out manifest, out error), "额外的 zip 资产被忽略");
            Equal("QuadVitals-Setup-0.9.1.exe", manifest.SetupName, "仍取 setup 里的安装包");
        }

        private static void UpdateVersionBehaviour()
        {
            True(UpdateVersions.IsNewer("0.9.1", "0.9.0"), "补丁号更大算新");
            True(UpdateVersions.IsNewer("0.10.0", "0.9.9"), "次版本按数字而不是字典序比较");
            True(UpdateVersions.IsNewer("1.0.0", "0.99.99"), "主版本更大算新");
            True(!UpdateVersions.IsNewer("0.9.0", "0.9.0"), "相同版本不算新");
            True(!UpdateVersions.IsNewer("0.8.9", "0.9.0"), "更旧的版本不算新");
            True(!UpdateVersions.IsNewer("0.9", "0.9.0"), "两段版本不算新");
            True(!UpdateVersions.IsNewer("0.9.x", "0.9.0"), "非数字不算新");
            True(!UpdateVersions.IsNewer(null, "0.9.0"), "空版本不算新");
            True(!UpdateVersions.IsNewer("+0.9.1", "0.9.0"), "带符号不算新");
            Equal(0, UpdateVersions.Compare("0.9.0", "0.9.0"), "相等返回 0");
            Equal(-1, UpdateVersions.Compare("0.9.0", "0.9.1"), "更小返回 -1");
            Equal(1, UpdateVersions.Compare("0.9.10", "0.9.9"), "更大返回 1");
        }

        private static void UpdateFeedBehaviour()
        {
            string original = Environment.GetEnvironmentVariable(UpdateFeed.FeedEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(UpdateFeed.FeedEnvironmentVariable, null);
                Equal(UpdateFeed.DefaultBaseUrl, UpdateFeed.BaseUrl(), "默认走 GitHub Releases");
                True(UpdateFeed.DefaultBaseUrl.EndsWith("/"), "默认基址带斜杠");

                Environment.SetEnvironmentVariable(UpdateFeed.FeedEnvironmentVariable, "http://127.0.0.1:8099");
                Equal("http://127.0.0.1:8099/", UpdateFeed.BaseUrl(), "自定义基址自动补斜杠");

                Environment.SetEnvironmentVariable(UpdateFeed.FeedEnvironmentVariable, "file:///C:/tmp/feed/");
                Equal(UpdateFeed.DefaultBaseUrl, UpdateFeed.BaseUrl(), "file:// 被拒绝并回退默认源");

                Environment.SetEnvironmentVariable(UpdateFeed.FeedEnvironmentVariable, "../../etc");
                Equal(UpdateFeed.DefaultBaseUrl, UpdateFeed.BaseUrl(), "相对路径被拒绝并回退默认源");
            }
            finally
            {
                Environment.SetEnvironmentVariable(UpdateFeed.FeedEnvironmentVariable, original);
            }

            Equal("https://example.com/feed/latest.json",
                UpdateFeed.ManifestUrl("https://example.com/feed"), "清单地址拼接");
            Equal("https://example.com/feed/QuadVitals-Setup-0.9.1.exe",
                UpdateFeed.InstallerUrl("https://example.com/feed/", "QuadVitals-Setup-0.9.1.exe"), "安装包地址拼接");
            Throws<ArgumentException>(() => UpdateFeed.InstallerUrl("https://example.com/feed/", "../evil.exe"),
                "拼接时再次拒绝带路径的名字");
        }

        private static void UpdateHashingBehaviour()
        {
            // "abc" — the canonical SHA-256 test vector.
            const string abcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

            string directory = NewTempDirectory();
            try
            {
                string file = Path.Combine(directory, "abc.bin");
                File.WriteAllBytes(file, new byte[] { 0x61, 0x62, 0x63 });
                Equal(abcHash, UpdateHashing.ComputeFileSha256(file), "文件哈希与已知向量一致");

                True(UpdateHashing.IsSha256Hex(abcHash), "64 位十六进制被接受");
                True(UpdateHashing.IsSha256Hex(abcHash.ToUpperInvariant()), "大写也被接受");
                True(!UpdateHashing.IsSha256Hex(abcHash.Substring(1)), "长度不足被拒绝");
                True(!UpdateHashing.IsSha256Hex(new string('g', 64)), "非十六进制字符被拒绝");
                True(UpdateHashing.Matches(abcHash, abcHash.ToUpperInvariant()), "大小写不影响比对");
                True(!UpdateHashing.Matches(abcHash, new string('0', 64)), "不同哈希不相等");
                True(!UpdateHashing.Matches("", abcHash), "空期望值不相等");
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static void UpdateDownloadBehaviour()
        {
            byte[] payload = Encoding.ASCII.GetBytes("installer-bytes-for-the-downloader");
            string url = "https://example.com/feed/QuadVitals-Setup-0.9.1.exe";
            string directory = NewTempDirectory();
            try
            {
                string destination = Path.Combine(directory, "update.exe");
                FakeUpdateTransport transport = new FakeUpdateTransport(payload);
                List<UpdateProgress> events = new List<UpdateProgress>();
                UpdateDownloadResult ok = UpdateDownloader.Download(transport, url, destination,
                    progress => events.Add(progress), null, null);

                True(ok.Ok, "下载成功");
                Equal((long)payload.Length, ok.Bytes, "字节数正确");
                True(File.Exists(destination), "安装包落盘");
                True(!File.Exists(destination + ".part"), "没有残留半成品");
                Equal(url, transport.LastUrl, "请求的是清单给出的地址");
                True(events.Count > 0, "进度回调被触发");
                Equal(1.0, events[events.Count - 1].Fraction, "最后一次进度是 100%");
                True(events[events.Count - 1].Received == payload.Length, "最后一条进度字节数正确");

                // A cancelled transfer must leave nothing behind and must not look like a failure
                // the caller can retry as if the file were usable.
                string cancelledDest = Path.Combine(directory, "cancelled.exe");
                UpdateDownloadResult cancelled = UpdateDownloader.Download(transport, url, cancelledDest, null,
                    () => true, null);
                True(cancelled.Cancelled, "取消被标记");
                True(!cancelled.Ok, "取消不算成功");
                True(!File.Exists(cancelledDest), "取消后不留文件");

                string shortDest = Path.Combine(directory, "short.exe");
                UpdateDownloadResult shortRead = UpdateDownloader.Download(
                    new FakeUpdateTransport(payload, payload.Length + 100), url, shortDest, null, null, null);
                True(!shortRead.Ok, "长度不足的传输失败");
                Equal("short-read", shortRead.Error, "长度不足的错误码");
                True(!File.Exists(shortDest), "短读后不留文件");

                string failedDest = Path.Combine(directory, "failed.exe");
                UpdateDownloadResult failed = UpdateDownloader.Download(
                    new FakeUpdateTransport(payload, -2, "Timeout"), url, failedDest, null, null, null);
                True(!failed.Ok, "传输失败不算成功");
                Equal("Timeout", failed.Error, "失败原因原样带出");
                True(!File.Exists(failedDest), "失败后不留文件");

                string hugeDest = Path.Combine(directory, "huge.exe");
                UpdateDownloadResult huge = UpdateDownloader.Download(
                    new FakeUpdateTransport(payload, UpdateFeed.MaxInstallerBytes + 1), url, hugeDest, null, null, null);
                True(!huge.Ok, "超过上限的包被拒绝");
                Equal("too-large", huge.Error, "超过上限的错误码");

                string emptyDest = Path.Combine(directory, "empty.exe");
                UpdateDownloadResult empty = UpdateDownloader.Download(
                    new FakeUpdateTransport(new byte[0]), url, emptyDest, null, null, null);
                True(!empty.Ok, "空响应不算成功");
                True(!File.Exists(emptyDest), "空响应后不留文件");
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static void UpdateInstallerBehaviour()
        {
            True(UpdateInstaller.IsSafeInstallerName("QuadVitals-Setup-0.9.1.exe"), "正常文件名被接受");
            True(UpdateInstaller.IsSafeInstallerName("settup.EXE"), "扩展名大小写不敏感");
            True(!UpdateInstaller.IsSafeInstallerName("setup.exe:stream"), "备用数据流被拒绝");
            True(!UpdateInstaller.IsSafeInstallerName("sub\\setup.exe"), "反斜杠被拒绝");
            True(!UpdateInstaller.IsSafeInstallerName("sub/setup.exe"), "斜杠被拒绝");
            True(!UpdateInstaller.IsSafeInstallerName("setup.zip"), "非 exe 被拒绝");
            True(!UpdateInstaller.IsSafeInstallerName("\\\u0001.exe".Trim()), "控制字符被拒绝");
            True(!UpdateInstaller.IsSafeInstallerName(".."), "纯点号被拒绝");
            True(!UpdateInstaller.IsSafeInstallerName(null), "空名字被拒绝");

            UpdateManifest manifest = new UpdateManifest
            {
                Version = "0.9.1",
                SetupName = "QuadVitals-Setup-0.9.1.exe",
                SetupSize = 3,
                SetupSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            };
            Equal(Path.Combine(UpdateInstaller.UpdatesDirectory(), manifest.SetupName),
                UpdateInstaller.InstallerPath(manifest), "安装包路径 = 更新目录 + 清单文件名");
            True(UpdateInstaller.UpdatesDirectory().EndsWith("updates"), "更新包落在本地 updates 目录");

            string directory = NewTempDirectory();
            try
            {
                string file = Path.Combine(directory, "QuadVitals-Setup-0.9.1.exe");
                File.WriteAllBytes(file, new byte[] { 0x61, 0x62, 0x63 });

                string actual;
                string error;
                True(UpdateInstaller.Verify(manifest, file, out actual, out error), "大小与哈希都一致时通过");
                Equal(manifest.SetupSha256, actual, "带出实际哈希供界面显示");

                File.WriteAllBytes(file, new byte[] { 0x61, 0x62, 0x64 });
                True(!UpdateInstaller.Verify(manifest, file, out actual, out error), "内容被改过则不通过");
                Equal("hash-mismatch", error, "内容被改的错误码");

                File.WriteAllBytes(file, new byte[] { 0x61, 0x62 });
                True(!UpdateInstaller.Verify(manifest, file, out actual, out error), "大小不符则不通过");
                Equal("size-mismatch", error, "大小不符的错误码");

                True(!UpdateInstaller.Verify(manifest, Path.Combine(directory, "missing.exe"), out actual, out error),
                    "文件不存在则不通过");
                Equal("installer-missing", error, "文件不存在的错误码");

                True(!UpdateInstaller.Verify(null, file, out actual, out error), "没有清单则不通过");

                True(!UpdateInstaller.Launch(Path.Combine(directory, "missing.exe"), out error), "启动不存在的安装包失败");
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private sealed class FakeUpdateTransport : IUpdateTransport
        {
            private readonly byte[] _payload;
            private readonly long _declaredLength;
            private readonly string _failure;

            public string LastUrl;

            public FakeUpdateTransport(byte[] payload, long declaredLength = -2, string failure = null)
            {
                _payload = payload;
                _declaredLength = declaredLength;
                _failure = failure;
            }

            public UpdateResponse Open(string url, TimeSpan timeout)
            {
                LastUrl = url;
                if (_failure != null) return new UpdateResponse { Failure = _failure };
                return new UpdateResponse
                {
                    Body = new MemoryStream(_payload, false),
                    ContentLength = _declaredLength == -2 ? _payload.Length : _declaredLength
                };
            }
        }

        private static string NewTempDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "qv-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void DeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // Leaving a temp folder behind must not fail the suite.
            }
        }
    }
}
