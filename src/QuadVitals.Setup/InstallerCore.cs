using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace QuadVitals.Setup
{
    /// <summary>
    /// Everything an install/uninstall actually does, kept out of the window so the GUI only
    /// describes state. The work itself is the same <c>scripts\install.ps1</c> /
    /// <c>scripts\uninstall.ps1</c> the repo tests, run in a hidden process: the tested path stays
    /// the only path, and the user never sees a console window.
    /// </summary>
    internal sealed class InstallerCore
    {
        private const string InstallScript = "install.ps1";
        private const string UninstallScript = "uninstall.ps1";
        private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QuadVitals";
        private const int ScriptTimeoutMs = 300000;

        private readonly string _version;
        private readonly StringBuilder _log = new StringBuilder();

        public InstallerCore(string version)
        {
            _version = version ?? "0.0.0";
        }

        /// <summary>Everything the scripts printed, for the failure page.</summary>
        public string LogText
        {
            get { lock (_log) { return _log.ToString(); } }
        }

        /// <summary>
        /// Processes <c>scripts\stop-quadvitals.ps1</c> could not end, e.g. "QuadVitals.Agent (pid 4212)".
        /// The script runs elevated, so a survivor here really is something the user has to deal with
        /// (process protection, a security product holding it) - the failure page names it and offers
        /// Task Manager.
        /// </summary>
        public System.Collections.Generic.List<string> StopFailedProcesses
        {
            get
            {
                System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string>();
                string log = LogText;
                if (string.IsNullOrEmpty(log)) return names;

                const string marker = "QV-STOP-FAILED:";
                foreach (string raw in log.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith(marker, StringComparison.Ordinal)) continue;
                    string rest = line.Substring(marker.Length).Trim();
                    if (rest.Length > 0) names.Add(rest);
                }
                return names;
            }
        }

        /// <summary>True when the stop step reported survivors, so the page can offer Task Manager.</summary>
        public bool StopFailed { get { return StopFailedProcesses.Count > 0; } }

        /// <summary>Stage text for the UI (install.ps1 prints one "== step" line per phase).</summary>
        public Action<string> OnStatus { get; set; }

        /// <summary>Raw output for the log pane. Raised from the worker thread.</summary>
        public Action<string> OnLog { get; set; }

        public static string DefaultInstallDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "QuadVitals");
            }
        }

        public string SetupExeName { get { return "QuadVitals-Uninstall.exe"; } }

        /// <summary>
        /// Canonicalises a folder the user can type in. The value is spliced into an elevated command
        /// line and deleted recursively on uninstall, so a drive root, a system folder, or a folder
        /// that contains one is refused rather than escaped.
        /// </summary>
        public static string ValidateInstallDir(string path, out string error)
        {
            error = null;
            string trimmed = (path ?? string.Empty).Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                error = "安装目录不能为空";
                return null;
            }
            if (trimmed.IndexOfAny(new[] { '"', (char)13, (char)10, (char)9 }) >= 0)
            {
                error = "安装目录不能包含引号或换行";
                return null;
            }

            string full;
            try
            {
                full = Path.GetFullPath(trimmed);
            }
            catch (Exception)
            {
                error = "安装目录不是有效路径";
                return null;
            }

            full = TrimSeparators(full);
            if (full.Length <= 2 || Path.GetDirectoryName(full) == null)
            {
                error = "不能装在磁盘根目录";
                return null;
            }

            foreach (string system in SystemFolders())
            {
                if (system.Length == 0) continue;
                if (IsSameOrInside(full, system))
                {
                    error = "不能装在系统目录：" + system;
                    return null;
                }
                if (IsSameOrInside(system, full))
                {
                    error = "这个目录里包含系统目录：" + system;
                    return null;
                }
            }

            foreach (string single in UserFolders())
            {
                if (single.Length == 0) continue;
                if (string.Equals(full, single, StringComparison.OrdinalIgnoreCase))
                {
                    error = "不能用这个目录本身：" + single;
                    return null;
                }
                if (IsSameOrInside(single, full))
                {
                    error = "这个目录里包含 " + single;
                    return null;
                }
            }

            return full;
        }

        /// <summary>
        /// Uninstall deletes the folder recursively, so a valid path is not enough: it has to still
        /// look like this product's install folder. Anything else is left for the user to remove.
        /// </summary>
        public static bool IsInstallFolder(string installDir)
        {
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return false;
            string[] markers = { "QuadVitals.Agent.exe", "QuadVitals.UI.dll", "QuadVitals-Uninstall.exe" };
            foreach (string marker in markers)
            {
                if (File.Exists(Path.Combine(installDir, marker))) return true;
            }
            return false;
        }

        private static string TrimSeparators(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string[] SystemFolders()
        {
            return new[]
            {
                TrimSeparators(Folder(Environment.SpecialFolder.Windows)),
                TrimSeparators(Folder(Environment.SpecialFolder.ProgramFiles)),
                TrimSeparators(Folder(Environment.SpecialFolder.ProgramFilesX86)),
                TrimSeparators(Folder(Environment.SpecialFolder.CommonApplicationData))
            };
        }

        /// <summary>
        /// Folders whose contents belong to the user or the OS: refusing them by identity keeps the
        /// default install path (a child of LocalApplicationData) valid.
        /// </summary>
        private static string[] UserFolders()
        {
            return new[]
            {
                TrimSeparators(Folder(Environment.SpecialFolder.UserProfile)),
                TrimSeparators(Folder(Environment.SpecialFolder.Desktop)),
                TrimSeparators(Folder(Environment.SpecialFolder.MyDocuments)),
                TrimSeparators(Folder(Environment.SpecialFolder.LocalApplicationData)),
                TrimSeparators(Folder(Environment.SpecialFolder.ApplicationData)),
                TrimSeparators(Path.GetTempPath())
            };
        }

        private static string Folder(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsSameOrInside(string candidate, string parent)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(parent)) return false;
            if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)) return true;
            return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // ---- payload ------------------------------------------------------------------

        /// <summary>
        /// Unpacks the embedded zip to a fresh temp folder and returns the package root (the
        /// versioned folder inside the zip). Entry names are normalised by hand because PowerShell's
        /// Compress-Archive writes backslashes, and every target is checked against the root so a
        /// crafted archive cannot write outside the temp folder.
        /// </summary>
        public string ExtractPayload(string tempRoot = null)
        {
            string root = string.IsNullOrEmpty(tempRoot)
                ? Path.Combine(Path.GetTempPath(),
                    "QuadVitals-Setup-" + _version + "-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                : tempRoot;

            string fullRoot = Path.GetFullPath(root);
            Directory.CreateDirectory(fullRoot);

            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuadVitals.Payload.zip"))
            {
                if (stream == null) throw new InvalidOperationException("内嵌安装载荷缺失");

                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string target = Path.GetFullPath(Path.Combine(fullRoot, relative));

                        bool inside = string.Equals(target, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                                      target.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                                                        StringComparison.OrdinalIgnoreCase);
                        if (!inside) throw new InvalidDataException("安装载荷条目越界：" + entry.FullName);

                        if (entry.Name.Length == 0)
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }

                        string folder = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                        entry.ExtractToFile(target, true);
                    }
                }
            }

            if (File.Exists(Path.Combine(fullRoot, "scripts", InstallScript))) return fullRoot;

            string[] children = Directory.GetDirectories(fullRoot);
            if (children.Length == 1 && File.Exists(Path.Combine(children[0], "scripts", InstallScript)))
            {
                return children[0];
            }
            return fullRoot;
        }

        // ---- install / uninstall ------------------------------------------------------

        public InstallResult Install(string payloadRoot, string installDir, bool createTask, bool startAgent,
                                     bool desktopShortcut)
        {
            if (!Directory.Exists(payloadRoot)) return InstallResult.Failure("安装载荷没有解包成功");

            string installError;
            installDir = ValidateInstallDir(installDir, out installError);
            if (installDir == null) return InstallResult.Failure(installError);

            string arguments = " -InstallDir \"" + installDir + "\"" +
                               (createTask ? " -Task" : " -NoTask") +
                               (startAgent ? string.Empty : " -NoStart");

            // Elevation comes first, before any stage text or log line: the UAC prompt belongs to the
            // start of the install, not to step five of the progress list. Only the logon task needs
            // admin, so a per-user install without autostart stays prompt-free.
            int exitCode;
            if (createTask)
            {
                Action<string> status = OnStatus;
                if (status != null) status("正在请求管理员权限…");
                try
                {
                    exitCode = RunScriptElevated(payloadRoot, InstallScript, arguments);
                }
                catch (Exception ex)
                {
                    if (IsUacCancelled(ex)) return UacDeclined(false);
                    return InstallResult.Failure("无法启动安装程序：" + ex.GetType().Name);
                }
            }
            else
            {
                exitCode = RunScript(payloadRoot, InstallScript, arguments);
            }

            if (exitCode != 0) return InstallResult.Failure(DescribeFailure(exitCode, false));

            // Safety net: if the task still is not there (elevation granted but schtasks failed), the
            // install is rolled back so "failed" is the truth and a retry starts clean.
            if (createTask && !LogonTaskExists())
            {
                Log("登录计划任务不存在，回滚本次安装");
                RunScript(payloadRoot, UninstallScript, " -InstallDir \"" + installDir + "\" -SkipTask");
                RemoveUninstallEntry();
                RemoveDesktopShortcut();
                RemoveStartMenuEntries();
                TryDeleteLeftovers(installDir);
                return UacDeclined(false);
            }

            if (desktopShortcut) CreateDesktopShortcut(installDir);

            // Bringing the shell back, and swapping the band module it still has mapped, runs here rather
            // than in the elevated child: an elevated process must never restart Explorer.
            RestoreShellIfStopped();
            bool shellRestarted;
            bool bandRestored;
            ActivateBandModule(out shellRestarted, out bandRestored);

            // 应用和功能 needs a copy of this exe that outlives the download, otherwise the uninstall
            // command breaks the moment the user deletes the file they downloaded.
            string copied = CopySelfInto(installDir);
            RegisterUninstallEntry(installDir, copied);
            CreateStartMenuEntries(installDir, copied);

            InstallResult success = InstallResult.Success(copied);
            success.ShellRestarted = shellRestarted;
            success.BandRestored = bandRestored;
            return success;
        }

        // ---- shell ---------------------------------------------------------------------

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        /// <summary>
        /// Puts the shell back if the install script had to kill it to replace a locked file - and nothing
        /// else. Asking the shell to show the band is what makes Explorer raise its own
        /// 「是否要在任务栏上显示 QuadVitals 工具栏?」 and 「工具栏与此版本的 Windows 不兼容」 dialogs,
        /// and answering those is a race against the shell (three attempts lost it that way). Swapping the
        /// loaded band module is done by <see cref="ActivateBandModule"/>, which only restarts Explorer.
        /// </summary>
        private void RestoreShellIfStopped()
        {
            // Starting Explorer has to happen here, in the unelevated process: an elevated Explorer would
            // hand the desktop an admin token.
            EnsureShellRunning();
        }

        /// <summary>
        /// Starts Explorer again if it is not running, and reports whether it did. The elevated install
        /// script may have killed the shell to replace a file it had mapped; restarting it from here
        /// (unelevated) is what keeps the taskbar a normal, unprivileged shell.
        /// </summary>
        private bool EnsureShellRunning()
        {
            if (Process.GetProcessesByName("explorer").Length > 0) return false;

            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                Process.Start(Path.Combine(windows, "explorer.exe"));
                Log("资源管理器已重新启动");

                for (int i = 0; i < 30; i++)
                {
                    if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) break;
                    System.Threading.Thread.Sleep(300);
                }
                System.Threading.Thread.Sleep(800);
                return true;
            }
            catch (Exception ex)
            {
                Log("重启资源管理器失败：" + ex.GetType().Name);
                return false;
            }
        }

        // The band is a COM object Explorer loads once and keeps until the shell restarts. A new build
        // writes a new file and points the registration at it, but the module already in memory keeps
        // drawing the old code - which is why an upgrade used to show last version's taskbar bar until
        // the next logon. So the shell is restarted once, here and unelevated, and nothing else: asking
        // the shell to show the band is what raises Explorer's own toolbar dialogs.
        private void ActivateBandModule(out bool shellRestarted, out bool bandRestored)
        {
            shellRestarted = false;
            bandRestored = false;
            try
            {
                if (Process.GetProcessesByName("explorer").Length == 0)
                {
                    // No shell to swap. Starting one here would hand the desktop an admin token, so the
                    // finish page falls back to the manual instruction.
                    Log("没有正在运行的任务栏，任务栏条需要手动勾选");
                    return;
                }

                Log("重启资源管理器以加载新的任务栏组件");
                foreach (Process shell in Process.GetProcessesByName("explorer"))
                {
                    try { shell.Kill(); } catch (Exception) { }
                }
                for (int i = 0; i < 40 && Process.GetProcessesByName("explorer").Length > 0; i++)
                {
                    System.Threading.Thread.Sleep(150);
                }
                shellRestarted = true;
                EnsureShellRunning();

                // Explorer rebuilds the taskbar before it rebuilds the bands: poll instead of guessing a
                // fixed delay, and treat "not back yet" as a manual step rather than a failure.
                for (int i = 0; i < 45; i++)
                {
                    if (BandWindowPresent())
                    {
                        bandRestored = true;
                        break;
                    }
                    System.Threading.Thread.Sleep(200);
                }
                Log(bandRestored ? "任务栏条已随资源管理器重新加载" : "任务栏条没有自动回来，需要手动勾选");
            }
            catch (Exception ex)
            {
                Log("重启资源管理器失败：" + ex.GetType().Name);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

        /// <summary>True when the band's own window sits anywhere under the taskbar.</summary>
        internal static bool BandWindowPresent()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            return ContainsChildClass(tray, "QuadVitalsBandWindow", 5);
        }

        private static bool ContainsChildClass(IntPtr parent, string className, int depth)
        {
            IntPtr child = IntPtr.Zero;
            while (true)
            {
                child = FindWindowEx(parent, child, null, null);
                if (child == IntPtr.Zero) return false;
                StringBuilder name = new StringBuilder(64);
                if (GetClassName(child, name, name.Capacity) > 0 &&
                    string.Equals(name.ToString(), className, StringComparison.Ordinal)) return true;
                if (depth > 1 && ContainsChildClass(child, className, depth - 1)) return true;
            }
        }

        // ---- logon task ----------------------------------------------------------------

        private const string LogonTaskName = "QuadVitals Agent";

        /// <summary>
        /// Turns a script exit code plus its output into one sentence a person can act on. Every text
        /// here is written to fit two lines at the page's measure: a third line pushes the log pane
        /// down and makes the page top-heavy. The raw output stays in the log pane.
        /// </summary>
        internal string DescribeFailure(int exitCode, bool uninstall)
        {
            string log = LogText ?? string.Empty;
            string action = uninstall ? "卸载" : "安装";

            // The stop step comes first: if it failed, every later failure is a consequence of it.
            if (StopFailed)
            {
                string names = string.Join("、", StopFailedProcesses.ToArray());
                return "无法关闭 " + names + "\r\n请用管理员权限的任务管理器结束它后重新" + action;
            }

            if (Contains(log, "cannot write"))
            {
                // Same lock, different verb: an install writes over the file, an uninstall deletes it.
                return uninstall
                    ? "无法删除安装目录中的文件：文件正被占用\r\n请退出 QuadVitals 后重试"
                    : "无法写入安装目录：文件正被占用\r\n请退出 QuadVitals 后重试";
            }
            if (Contains(log, "access is denied") || Contains(log, "拒绝访问"))
            {
                return uninstall
                    ? "没有足够的权限：文件受保护或被安全软件拦截\r\n关闭安全软件后重新" + action
                    : "没有足够的权限：文件受保护或被安全软件拦截\r\n换一个安装位置后重试";
            }
            if (Contains(log, "not enough space") || Contains(log, "磁盘空间"))
            {
                return "磁盘空间不足，" + action + "没有完成\r\n清理空间后重新" + action;
            }
            if (Contains(log, "cannot find the path") || Contains(log, "找不到路径"))
            {
                return "安装位置不可用：路径不存在或无访问权限\r\n" +
                       (uninstall ? "检查该目录是否还在" : "换一个安装位置后重试");
            }
            if (Contains(log, "missing "))
            {
                return "安装包不完整：缺少必要的文件\r\n请重新下载后重试";
            }

            return action + "没有完成（步骤返回 " + exitCode + "）\r\n下方是详细记录，可据此反馈";
        }

        private static bool Contains(string text, string needle)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when the logon task is registered (query only, no elevation).</summary>
        public bool LogonTaskExists()
        {
            return RunExe("schtasks.exe", "/Query /TN \"" + LogonTaskName + "\"") == 0;
        }

        /// <summary>
        /// Runs a tool (schtasks) for its exit code only. Its output is deliberately not fed into the log:
        /// schtasks writes in the console OEM code page, so decoding it as UTF-8 turns every Chinese label
        /// into replacement glyphs — and the caller only ever looks at the exit code anyway.
        /// </summary>
        private int RunExe(string fileName, string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(0),   // console ANSI page: correct for a dump
                StandardErrorEncoding = Encoding.GetEncoding(0)
            };

            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = info;
                    process.Start();

                    // Drain both pipes so a tool that writes a lot cannot block on a full buffer; the text is
                    // discarded, not logged.
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return -1;
                    }
                    return process.ExitCode;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Uninstall mirrors the install: the script does the work, then the entry is removed and
        /// whatever is left (this exe is locked while it runs) is cleaned up or scheduled away.
        /// </summary>
        private static InstallResult UacDeclined(bool uninstall)
        {
            string action = uninstall ? "卸载" : "安装";
            InstallResult declined = InstallResult.Failure(
                "系统授权(UAC)失败\r\n请点击「重新" + action + "」并完成UAC提权后即可继续");
            declined.ShowLog = false;
            return declined;
        }

        /// <summary>ERROR_CANCELLED (1223): the user pressed No on the UAC prompt.</summary>
        private static bool IsUacCancelled(Exception ex)
        {
            System.ComponentModel.Win32Exception win = ex as System.ComponentModel.Win32Exception;
            return win != null && win.NativeErrorCode == 1223;
        }

        public InstallResult Uninstall(string payloadRoot, string installDir, bool keepSettings,
                                       bool skipTask = false)
        {
            if (!Directory.Exists(payloadRoot)) return InstallResult.Failure("卸载载荷没有解包成功");

            string uninstallError;
            string checkedDir = ValidateInstallDir(installDir, out uninstallError);
            if (checkedDir == null) return InstallResult.Failure(uninstallError);
            if (!IsInstallFolder(checkedDir)) return InstallResult.Failure("这个目录不是 QuadVitals 安装目录：" + checkedDir);
            installDir = checkedDir;

            string arguments = " -InstallDir \"" + installDir + "\"" +
                               (keepSettings ? string.Empty : " -RemoveSettings") +
                               (skipTask ? " -SkipTask" : string.Empty);

            int exitCode;
            if (skipTask)
            {
                exitCode = RunScript(payloadRoot, UninstallScript, arguments);
            }
            else
            {
                Action<string> status = OnStatus;
                if (status != null) status("正在请求管理员权限…");
                try
                {
                    exitCode = RunScriptElevated(payloadRoot, UninstallScript, arguments);
                }
                catch (Exception ex)
                {
                    if (IsUacCancelled(ex)) return UacDeclined(true);
                    return InstallResult.Failure("无法启动卸载程序：" + ex.GetType().Name);
                }
            }
            RemoveUninstallEntry();
            RemoveDesktopShortcut();
            RemoveStartMenuEntries();
            TryDeleteLeftovers(installDir);

            return exitCode == 0
                ? InstallResult.Success(null)
                : InstallResult.Failure(DescribeFailure(exitCode, true));
        }

        private int RunScript(string payloadRoot, string scriptName, string arguments)
        {
            string script = Path.Combine(payloadRoot, "scripts", scriptName);
            if (!File.Exists(script))
            {
                Log("缺少脚本：" + script);
                return 2;
            }

            ProcessStartInfo info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"" + arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,          // a GUI installer must never flash a console
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = payloadRoot
            };

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.OutputDataReceived += (sender, e) => Pump(e.Data);
                process.ErrorDataReceived += (sender, e) => Pump(e.Data);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(ScriptTimeoutMs))
                {
                    try { process.Kill(); } catch (Exception) { }
                    Log("脚本超时：" + scriptName);
                    return 3;
                }
                process.WaitForExit();   // second call flushes the async output handlers
                return process.ExitCode;
            }
        }

        /// <summary>
        /// Runs the script **elevated**, with the UAC prompt raised before any work starts.
        ///
        /// Redirecting a child that ShellExecute starts is not possible, so the script writes to a log
        /// file and this side tails it - the progress page still shows every stage, just one poll
        /// behind. Elevating here (through cmd.exe, which is Microsoft-signed) means the UAC prompt
        /// appears first, once, instead of popping up halfway through the progress list, and the
        /// publisher line never says "unknown publisher" because cmd.exe owns the prompt.
        /// </summary>
        private int RunScriptElevated(string payloadRoot, string scriptName, string arguments)
        {
            string script = Path.Combine(payloadRoot, "scripts", scriptName);
            if (!File.Exists(script))
            {
                Log("缺少脚本：" + script);
                return 2;
            }

            string logFile = Path.Combine(Path.GetTempPath(),
                "QuadVitals-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");

            string inner = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"" + arguments +
                           " > \"" + logFile + "\" 2>&1";

            ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                                                        "/c " + inner)
            {
                UseShellExecute = true,          // required for Verb=runas
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = payloadRoot
            };

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.Start();

                DateTime started = DateTime.UtcNow;
                long offset = 0;
                while (!process.HasExited)
                {
                    offset = PumpFile(logFile, offset);
                    if ((DateTime.UtcNow - started).TotalMilliseconds > ScriptTimeoutMs)
                    {
                        try { process.Kill(); } catch (Exception) { }
                        Log("脚本超时：" + scriptName);
                        return 3;
                    }
                    System.Threading.Thread.Sleep(120);
                }
                PumpFile(logFile, offset);
                process.WaitForExit();

                try { File.Delete(logFile); } catch (Exception) { }
                return process.ExitCode;
            }
        }

        /// <summary>Feeds every complete line added to the log since the last read into Pump().</summary>
        private long PumpFile(string path, long offset)
        {
            try
            {
                if (!File.Exists(path)) return offset;

                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                          FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= offset) return offset;

                    stream.Seek(offset, SeekOrigin.Begin);
                    using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
                    {
                        string chunk = reader.ReadToEnd();
                        offset = stream.Position;

                        int end = chunk.LastIndexOf('\n');
                        if (end < 0)
                        {
                            // No complete line yet: rewind so the partial line is read again later.
                            return offset - Encoding.UTF8.GetByteCount(chunk);
                        }

                        int consumed = Encoding.UTF8.GetByteCount(chunk.Substring(0, end + 1));
                        foreach (string line in chunk.Substring(0, end).Split('\n'))
                        {
                            Pump(line.TrimEnd('\r'));
                        }
                        return offset - Encoding.UTF8.GetByteCount(chunk) + consumed;
                    }
                }
            }
            catch (Exception)
            {
                return offset;
            }
        }

        private void Pump(string line)
        {
            if (line == null) return;

            string trimmed = line.Trim();
            if (trimmed.Length == 0) return;

            string logLine = line;
            if (trimmed.StartsWith("==", StringComparison.Ordinal))
            {
                string stage = trimmed.TrimStart('=').Trim();
                if (stage.Length > 0)
                {
                    logLine = "== " + LocalizeStage(stage);
                    Action<string> status = OnStatus;
                    if (status != null) status(LocalizeStage(stage));
                }
            }

            lock (_log) { _log.AppendLine(logLine); }
            Action<string> handler = OnLog;
            if (handler != null) handler(logLine);
        }

        /// <summary>
        /// The scripts print ASCII stage names (PS 5.1 reads BOM-less files as GBK, so Chinese in them
        /// would need a BOM). The progress page is Chinese, so the mapping lives here instead: the
        /// heading says 正在安装 and the line under it must not answer in English.
        /// </summary>
        private static string LocalizeStage(string stage)
        {
            string label;
            if (StageLabels.TryGetValue(stage, out label)) return label;

            // Parameters are appended by some steps ("remove <path>").
            foreach (KeyValuePair<string, string> pair in StageLabels)
            {
                if (stage.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value + stage.Substring(pair.Key.Length);
                }
            }
            return stage;
        }

        private static readonly Dictionary<string, string> StageLabels = new Dictionary<string, string>
        {
            { "check build outputs", "检查安装文件" },
            { "stop running QuadVitals processes", "停止正在运行的 QuadVitals" },
            { "release the taskbar band", "释放任务栏组件" },
            { "copy files", "复制文件" },
            { "register DeskBand (per-user)", "注册任务栏组件" },
            { "refresh the taskbar", "正在刷新任务栏配置" },
            { "install logon task (one UAC prompt)", "创建开机自启任务" },
            { "remove logon task (one UAC prompt)", "删除开机自启任务" },
            { "unregister DeskBand", "注销任务栏组件" },
            { "start agent", "启动 Agent" },
            { "keep install files (-KeepFiles)", "保留安装文件" },
            { "remove user data", "删除用户数据" },
            { "remove ", "删除" }
        };

        private void Log(string line)
        {
            lock (_log) { _log.AppendLine(line); }
            Action<string> handler = OnLog;
            if (handler != null) handler(line);
        }

        // ---- desktop shortcut ----------------------------------------------------------

        /// <summary>
        /// A per-user shortcut to the dashboard. Built through WScript.Shell because a .lnk is a
        /// COM object, and creating it here keeps the installer's options in one place instead of
        /// leaving a half-configured shortcut to the install script.
        /// </summary>
        private void CreateDesktopShortcut(string installDir)
        {
            string target = Path.Combine(installDir, "QuadVitals.Settings.exe");
            if (!File.Exists(target))
            {
                Log("跳过桌面快捷方式：找不到 " + target);
                return;
            }

            if (CreateShortcut(DesktopShortcutPath, target, installDir, "QuadVitals 面板"))
            {
                Log("桌面快捷方式：" + DesktopShortcutPath);
            }
        }

        /// <summary>Creates one .lnk through WScript.Shell. Returns false instead of throwing.</summary>
        private bool CreateShortcut(string shortcutPath, string target, string workingDir, string description)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;

                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                                         new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();

                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                                          new object[] { workingDir });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                                          new object[] { target + ",0" });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                                          new object[] { description });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return true;
            }
            catch (Exception ex)
            {
                Log("创建快捷方式失败（" + Path.GetFileName(shortcutPath) + "）：" + ex.GetType().Name);
                return false;
            }
        }

        private void RemoveDesktopShortcut()
        {
            try
            {
                string path = DesktopShortcutPath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A leftover .lnk at worst opens nothing; never fail an uninstall over it.
            }
        }

        private static string DesktopShortcutPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                    "QuadVitals.lnk");
            }
        }

        // ---- uninstall registration ---------------------------------------------------

        /// <summary>
        /// Per-user uninstall entry (HKCU): the key Windows' 应用和功能 reads. The install is
        /// per-user, so this needs no admin and only shows up for the current account.
        /// </summary>
        private void RegisterUninstallEntry(string installDir, string copiedSetupPath)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
                {
                    if (key == null) return;

                    // The copy in the install directory is named so users recognise it as the uninstaller;
                    // that exe switches to uninstall mode by itself when started under that name.
                    string uninstall = "\"" + copiedSetupPath + "\" --uninstall";
                    key.SetValue("DisplayName", "QuadVitals", Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("DisplayVersion", _version, Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("Publisher", "enosawa", Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("InstallLocation", installDir, Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("DisplayIcon", Path.Combine(installDir, "QuadVitals.Settings.exe"),
                                 Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("UninstallString", uninstall, Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("QuietUninstallString", uninstall + " /S",
                                 Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                // A missing shortcut in 应用和功能 is cosmetic; the install itself is still good.
                Log("写入卸载项失败：" + ex.GetType().Name);
            }
        }

        private void RemoveUninstallEntry()
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
            }
            catch (Exception)
            {
                // Nothing to remove, or the key is in use: not worth failing an uninstall over.
            }
        }

        private string CopySelfInto(string installDir)
        {
            string self = SelfPath();
            try
            {
                string target = Path.Combine(installDir, SetupExeName);
                if (!string.Equals(self, target, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(installDir);
                    File.Copy(self, target, true);
                }
                return target;
            }
            catch (Exception ex)
            {
                Log("复制安装器失败：" + ex.GetType().Name);
                return self;
            }
        }

        private const string StartMenuFolder = @"Microsoft\Windows\Start Menu\Programs\QuadVitals";

        /// <summary>
        /// Start-menu group with the dashboard and a real uninstaller entry. 应用和功能 alone is not
        /// discoverable enough - users look for "卸载" in the folder or the start menu first.
        /// </summary>
        private void CreateStartMenuEntries(string installDir, string uninstallerPath)
        {
            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string folder = Path.Combine(programs, "QuadVitals");
                Directory.CreateDirectory(folder);

                string dashboard = Path.Combine(installDir, "QuadVitals.Settings.exe");
                CreateShortcut(Path.Combine(folder, "QuadVitals.lnk"), dashboard, installDir, "QuadVitals 面板");
                CreateShortcut(Path.Combine(folder, "卸载 QuadVitals.lnk"), uninstallerPath, installDir,
                               "卸载 QuadVitals");
            }
            catch (Exception ex)
            {
                Log("创建开始菜单项失败：" + ex.GetType().Name);
            }
        }

        private void RemoveStartMenuEntries()
        {
            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string folder = Path.Combine(programs, "QuadVitals");
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // A leftover folder in the start menu is cosmetic.
            }
        }

        /// <summary>
        /// Deleting a folder while this exe runs from inside it always leaves something behind, so
        /// the rest is removed here and the locked file is scheduled away at the next reboot.
        /// </summary>
        private void TryDeleteLeftovers(string installDir)
        {
            string self = SelfPath();
            try
            {
                if (!Directory.Exists(installDir)) return;
                if (!IsInstallFolder(installDir)) return;

                foreach (string file in Directory.GetFiles(installDir))
                {
                    if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); } catch (Exception) { }
                }

                bool runningFromInstall = string.Equals(Path.GetDirectoryName(self), installDir,
                                                        StringComparison.OrdinalIgnoreCase);
                if (runningFromInstall)
                {
                    MoveFileEx(self, null, MoveFileDelayUntilReboot);
                }
                else
                {
                    try { Directory.Delete(installDir, true); } catch (Exception) { }
                }
            }
            catch (Exception)
            {
                // Leftovers are already reported by the scripts; never fail an uninstall over them.
            }
        }

        /// <summary>Adds one line to the log the progress and failure pages show.</summary>
        public void Note(string line)
        {
            Pump(line);
        }

        /// <summary>Preview hook: runs one script line through the same pipeline a real install uses.</summary>
        internal void FeedPreviewLine(string line)
        {
            Note(line);
        }

        public void TryDeletePayload(string payloadRoot)
        {
            try
            {
                if (!string.IsNullOrEmpty(payloadRoot) && Directory.Exists(payloadRoot))
                {
                    Directory.Delete(payloadRoot, true);
                }
            }
            catch (Exception)
            {
                // A temp folder that outlives the install is harmless.
            }
        }

        public static string SelfPath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch (Exception) { return Assembly.GetExecutingAssembly().Location; }
        }

        // ---- Win32 ---------------------------------------------------------------------

        private const int MoveFileDelayUntilReboot = 0x4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }

    internal sealed class InstallResult
    {
        public bool Succeeded { get; private set; }
        public string Error { get; private set; }
        public string CopiedSetupPath { get; private set; }

        /// <summary>
        /// Whether the failure page should show the raw script output. Turned off for failures the
        /// user caused and can fix themselves (a declined UAC prompt), where the log would only add
        /// noise around one sentence they need to read.
        /// </summary>
        public bool ShowLog { get; set; } = true;

        /// <summary>True when the install restarted Explorer to pick up the new band module.</summary>
        public bool ShellRestarted { get; set; }

        /// <summary>
        /// True when the band's own window reappeared after that restart. False means the user has to
        /// tick the toolbar entry by hand, which is what the finish page then says.
        /// </summary>
        public bool BandRestored { get; set; }

        public static InstallResult Success(string copiedSetupPath)
        {
            return new InstallResult { Succeeded = true, CopiedSetupPath = copiedSetupPath };
        }

        public static InstallResult Failure(string error)
        {
            return new InstallResult { Succeeded = false, Error = error };
        }
    }
}
