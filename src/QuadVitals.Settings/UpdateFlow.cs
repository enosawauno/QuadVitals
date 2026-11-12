using System;
using System.Threading;
using System.Threading.Tasks;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    internal enum UpdatePhase
    {
        Checking,
        Current,
        Failed,
        Available,
        Downloading,
        Verifying,
        Launching,
        Cancelled
    }

    /// <summary>One immutable step of the update flow, handed to whichever surface is showing it.</summary>
    internal sealed class UpdateFlowState
    {
        public UpdatePhase Phase;
        public string Message = string.Empty;
        public bool IsError;
        public UpdateManifest Manifest;
        public UpdateProgress Progress;

        public UpdateFlowState(UpdatePhase phase, string message, bool isError = false)
        {
            Phase = phase;
            Message = message;
            IsError = isError;
        }
    }

    /// <summary>
    /// Check → download → verify → launch, with no UI of its own.
    ///
    /// Two surfaces show the same flow: the About card (which keeps its one-click behaviour)
    /// and the update dialog. Keeping the orchestration here means the strings, the cancel
    /// semantics and the "never launch an unverified package" rule cannot drift between them.
    ///
    /// Reports arrive on a worker thread; every consumer marshals to the dispatcher itself.
    /// </summary>
    internal sealed class UpdateFlow
    {
        private readonly Action<UpdateFlowState> _report;
        private readonly Func<bool> _cancelled;
        private int _running;
        private volatile bool _cancelRequested;

        public UpdateFlow(Action<UpdateFlowState> report, Func<bool> cancelled)
        {
            _report = report;
            _cancelled = cancelled;
        }

        public bool IsRunning { get { return Volatile.Read(ref _running) != 0; } }

        /// <summary>Starts with the check only. A caller that wants one-click install chains on Available.</summary>
        public void StartCheck()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            _cancelRequested = false;
            Task.Run((Action)CheckLoop);
        }

        /// <summary>Starts from an already-fetched manifest, e.g. the dialog's 我要升级 button.</summary>
        public void StartInstall(UpdateManifest manifest)
        {
            if (manifest == null) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            _cancelRequested = false;
            Task.Run(() => InstallLoop(manifest));
        }

        public void Cancel()
        {
            _cancelRequested = true;
        }

        private void CheckLoop()
        {
            try
            {
                Report(new UpdateFlowState(UpdatePhase.Checking, "正在检查更新…"));

                UpdateCheckOutcome outcome = UpdateChecker.Check(
                    new HttpUpdateTransport(), UpdateFeed.BaseUrl(), ProductVersion.SemVer);

                if (outcome.Status == UpdateCheckStatus.Failed)
                {
                    Report(new UpdateFlowState(UpdatePhase.Failed,
                        "无法检查更新：" + DescribeFailure(outcome.Error), true));
                    return;
                }

                if (outcome.Status == UpdateCheckStatus.Current)
                {
                    Report(new UpdateFlowState(UpdatePhase.Current,
                        "已是最新版本（" + ProductVersion.SemVer + "）"));
                    return;
                }

                UpdateFlowState available = new UpdateFlowState(UpdatePhase.Available,
                    "发现新版本 " + outcome.LatestVersion + "，正在下载…") { Manifest = outcome.Manifest };
                Report(available);
            }
            catch (Exception)
            {
                Report(new UpdateFlowState(UpdatePhase.Failed, "暂时无法检查更新", true));
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private void InstallLoop(UpdateManifest manifest)
        {
            try
            {
                string destination = UpdateInstaller.InstallerPath(manifest);
                string url;
                try
                {
                    url = UpdateFeed.InstallerUrl(UpdateFeed.BaseUrl(), manifest.SetupName);
                }
                catch (ArgumentException)
                {
                    Report(new UpdateFlowState(UpdatePhase.Failed, "更新清单里的安装包名不可用。", true));
                    return;
                }

                Report(new UpdateFlowState(UpdatePhase.Downloading,
                    "正在下载 " + manifest.Version + "…") { Manifest = manifest });

                UpdateDownloadResult download = UpdateDownloader.Download(
                    new HttpUpdateTransport(), url, destination,
                    progress => Report(new UpdateFlowState(UpdatePhase.Downloading, string.Empty)
                    {
                        Manifest = manifest,
                        Progress = progress
                    }),
                    () => _cancelRequested || (_cancelled != null && _cancelled()),
                    null);

                if (download.Cancelled)
                {
                    Report(new UpdateFlowState(UpdatePhase.Cancelled, "已取消更新"));
                    return;
                }
                if (!download.Ok)
                {
                    Report(new UpdateFlowState(UpdatePhase.Failed,
                        "下载失败：" + DescribeDownloadFailure(download.Error), true));
                    return;
                }

                Report(new UpdateFlowState(UpdatePhase.Verifying, "正在校验安装包…"));

                string actualHash;
                string verifyError;
                if (!UpdateInstaller.Verify(manifest, destination, out actualHash, out verifyError))
                {
                    DeleteQuietly(destination);
                    Report(new UpdateFlowState(UpdatePhase.Failed,
                        "安装包校验未通过（" + DescribeVerifyFailure(verifyError) + "），文件已删除。", true));
                    return;
                }

                string launchError;
                if (!UpdateInstaller.Launch(destination, out launchError))
                {
                    Report(new UpdateFlowState(UpdatePhase.Failed,
                        "无法启动安装程序：" + DescribeFailure(launchError), true));
                    return;
                }

                Report(new UpdateFlowState(UpdatePhase.Launching,
                    "安装包校验通过（" + ShortHash(actualHash) + "），正在启动安装程序…"));
            }
            catch (Exception)
            {
                Report(new UpdateFlowState(UpdatePhase.Failed, "安装包处理失败，请重试。", true));
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private void Report(UpdateFlowState state)
        {
            Action<UpdateFlowState> handler = _report;
            if (handler == null) return;
            try
            {
                handler(state);
            }
            catch (Exception)
            {
                // A closed window must not turn a finished transfer into a crash.
            }
        }

        internal static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "—";
            string text = hash.ToLowerInvariant();
            return text.Length <= 12 ? text : text.Substring(0, 12) + "…";
        }

        /// <summary>Turns transport/parse codes into one readable sentence. The raw code is never shown.</summary>
        internal static string DescribeFailure(string error)
        {
            if (string.IsNullOrEmpty(error)) return "暂时无法检查更新";
            switch (error)
            {
                case "Timeout": return "连接超时";
                case "NameResolutionFailure": return "无法解析更新源域名";
                case "ConnectFailure": return "无法连接更新源";
                case "TrustFailure":
                case "SecureChannelFailure": return "更新源的证书校验失败";
                case "ProtocolError": return "更新源返回错误（可能尚未发布正式版本）";
                case "manifest-invalid-json":
                case "manifest-empty":
                case "manifest-bad-version":
                case "manifest-no-setup":
                case "manifest-bad-name":
                case "manifest-bad-size":
                case "manifest-bad-hash": return "更新清单格式不正确";
                case "manifest-too-large": return "更新清单体积异常";
                case "installer-missing": return "安装程序不存在";
                default: return "暂时无法检查更新";
            }
        }

        internal static string DescribeDownloadFailure(string error)
        {
            if (string.IsNullOrEmpty(error)) return "未知原因";
            if (error.StartsWith("download-failed", StringComparison.Ordinal)) return "连接中断";
            switch (error)
            {
                case "too-large": return "安装包体积异常";
                case "short-read": return "下载不完整";
                default: return DescribeFailure(error);
            }
        }

        internal static string DescribeVerifyFailure(string error)
        {
            switch (error)
            {
                case "size-mismatch": return "大小与清单不符";
                case "hash-mismatch": return "SHA-256 与清单不符";
                case "installer-missing": return "安装包不存在";
                default: return "无法读取安装包";
            }
        }

        internal static void DeleteQuietly(string path)
        {
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (Exception)
            {
                // A rejected package that stays on disk is never launched: the name is only ever
                // reused after a fresh download overwrites it.
            }
        }
    }
}
