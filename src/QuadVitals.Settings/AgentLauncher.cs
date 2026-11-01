using System;
using System.Diagnostics;
using System.IO;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Starts the sampling agent when the dashboard finds it missing.
    ///
    /// The logon task is tried first: it launches at high integrity, which the temperature sensor
    /// needs, and it raises no UAC prompt. A direct process start is the fallback for a developer
    /// build that has no task registered (temperature then reports "--" until it runs elevated).
    /// </summary>
    internal static class AgentLauncher
    {
        public const string TaskName = "QuadVitals Agent";

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
            if (TryRunTask()) return null;

            string exe = ResolveExe();
            if (exe == null) return "找不到传感器程序（QuadVitals.Agent.exe）：它应与设置程序同目录，或已安装到本机";

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true
                };
                Process.Start(info);
                return null;
            }
            catch (Exception ex)
            {
                return "无法启动传感器：" + ex.Message;
            }
        }

        /// <summary>True when the logon task exists and accepted the run request.</summary>
        private static bool TryRunTask()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", "/Run /TN \"" + TaskName + "\"")
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
                // No task (or no schtasks): fall through to a direct start.
                return false;
            }
        }
    }
}
