using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Starts the sampling agent when the dashboard finds it missing.
    ///
    /// The logon task is tried first: it launches at high integrity, which the temperature sensor
    /// needs, and it raises no UAC prompt. When the task is unavailable the exe is started through
    /// the shell's "runas" verb instead of a plain process start: a plain start inherits the
    /// dashboard's token, and an unelevated agent reads no CPU temperature and no fan channels -
    /// a restart that reports success and delivers half the metrics.
    /// </summary>
    internal static class AgentLauncher
    {
        public const string TaskName = "QuadVitals Agent";
        /// <summary>Process name without the .exe suffix, as the task manager reports it.</summary>
        public const string ProcessName = "QuadVitals.Agent";

        /// <summary>Full path of the agent exe, or null when it cannot be found.</summary>
        public static string ResolveExe()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "QuadVitals.Agent.exe"),
                // Developer layout: build\out\ui\QuadVitals.Settings.exe next to build\out\agent\.
                Path.Combine(baseDir, @"..\agent\QuadVitals.Agent.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"QuadVitals\QuadVitals.Agent.exe")
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (Exception)
                {
                    // 一个候选路径不可访问就试下一个；这里抛出去只会让按钮无声失败。
                }
            }
            return null;
        }

        /// <summary>Starts the agent. Returns null when a start was triggered, else a sentence to show.</summary>
        public static string Start()
        {
            bool logonTaskStarted = TryRunTask();
            string exe = ResolveExe();

            AgentLaunchMode mode = AgentLaunchPlan.Decide(logonTaskStarted, exe);
            if (mode == AgentLaunchMode.LogonTask) return null;
            if (mode == AgentLaunchMode.MissingExe)
            {
                return "找不到传感器程序（QuadVitals.Agent.exe）：它应与设置程序同目录，或已安装到本机";
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(info);
                return null;
            }
            catch (Win32Exception ex)
            {
                if (AgentLaunchPlan.IsElevationCancelled(ex.NativeErrorCode))
                {
                    return AgentDiagnostics.ElevationCancelled;
                }
                return "无法启动传感器：" + ex.Message;
            }
            catch (Exception ex)
            {
                return "无法启动传感器：" + ex.Message;
            }
        }

        /// <summary>
        /// Recreates the logon task with one UAC prompt. This is the cure for the state the fallback
        /// above only papers over: a disabled or missing task means every start is a fallback, and a
        /// fallback that is cancelled leaves the agent without temperature and fans.
        /// </summary>
        public static string RepairLogonTask()
        {
            string exe = ResolveExe();
            if (exe == null)
            {
                return "找不到传感器程序（QuadVitals.Agent.exe）：它应与设置程序同目录，或已安装到本机";
            }

            string arguments = "/Create /TN \"" + TaskName + "\" /TR \"" + exe + " --quiet\"" +
                               " /SC ONLOGON /RL HIGHEST /F";
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(info))
                {
                    if (process == null) return AgentDiagnostics.TaskRepairFailed("schtasks 无法启动");
                    if (!process.WaitForExit(20000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return AgentDiagnostics.TaskRepairFailed("schtasks 超时");
                    }
                    if (process.ExitCode != 0)
                    {
                        return AgentDiagnostics.TaskRepairFailed("schtasks 退出码 " + process.ExitCode);
                    }
                    return null;
                }
            }
            catch (Win32Exception ex)
            {
                if (AgentLaunchPlan.IsElevationCancelled(ex.NativeErrorCode))
                {
                    return AgentDiagnostics.TaskRepairFailed("已取消管理员授权");
                }
                return AgentDiagnostics.TaskRepairFailed(ex.Message);
            }
            catch (Exception ex)
            {
                return AgentDiagnostics.TaskRepairFailed(ex.Message);
            }
        }

        /// <summary>
        /// Asks the running agent to exit. The quit event is the same channel <c>--exit</c> and the
        /// uninstaller use, so an elevated instance closes its sensor handles instead of being
        /// terminated. Falls back to ending the logon task when the event cannot be reached.
        /// </summary>
        public static bool RequestExit()
        {
            if (AgentControl.RequestQuit()) return true;
            return RunSchtasks("/End");
        }

        /// <summary>True when no agent process is left within the timeout.</summary>
        public static bool WaitForExit(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsRunning()) return true;
                System.Threading.Thread.Sleep(150);
            }
            return !IsRunning();
        }

        /// <summary>True when an agent process appears within the timeout.</summary>
        public static bool WaitForStart(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (IsRunning()) return true;
                System.Threading.Thread.Sleep(150);
            }
            return IsRunning();
        }

        public static bool IsRunning()
        {
            try
            {
                return Process.GetProcessesByName(ProcessName).Length > 0;
            }
            catch (Exception)
            {
                // Failing to enumerate processes is not evidence that the agent is gone.
                return true;
            }
        }

        /// <summary>True when the logon task exists and accepted the run request.</summary>
        private static bool TryRunTask()
        {
            return RunSchtasks("/Run");
        }

        private static bool RunSchtasks(string verb)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", verb + " /TN \"" + TaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(info))
                {
                    if (process == null) return false;
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                // No task (or no schtasks): callers decide whether to fall back.
                return false;
            }
        }
    }
}
