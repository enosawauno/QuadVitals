using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Restarts the whole agent when the temperature sensor stays dead.
    ///
    /// Measured twice: once WinRing0 fails to load in a process, reopening the LHM session does
    /// not bring MSR back (the sensor walk keeps enumerating zero temperature sensors), while a
    /// fresh process reads temperatures immediately. So the only reliable cure is a new process.
    ///
    /// The successor is started first and waits for this process to exit, which keeps the
    /// single-instance mutex clean; if the successor cannot be started, nothing is quit.
    /// Guard: at most one automatic restart per hour (stamp file survives the restart), so a
    /// permanently blocked driver cannot spin the agent.
    /// </summary>
    internal sealed class AgentRestarter
    {
        private const int MinIntervalMs = 60 * 60 * 1000;

        private readonly string _stampPath;
        private readonly TemperatureLog _log = new TemperatureLog();
        private int _used;

        public AgentRestarter()
        {
            string dir = Path.GetDirectoryName(Settings.DefaultPath);
            _stampPath = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "agent-restart.txt");
        }

        /// <param name="force">
        /// Manual restarts skip both the once-per-process guard and the hourly stamp: the user asked for it,
        /// and consuming the automatic budget would then block a real stall from being cured.
        /// </param>
        public bool Request(string reason, Action quit, bool spawn = true, bool force = false)
        {
            if (force) Interlocked.Exchange(ref _used, 1);
            else if (Interlocked.CompareExchange(ref _used, 1, 0) != 0) return false;

            DateTime last = ReadStamp();
            if (!force && last != DateTime.MinValue &&
                (DateTime.UtcNow - last).TotalMilliseconds < MinIntervalMs)
            {
                _log.Report("跳过自动重启：" + reason + "（" + last.ToLocalTime().ToString("HH:mm") +
                            " 已重启过，一小时内只允许一次）");
                return false;
            }

            int pid = Process.GetCurrentProcess().Id;
            if (spawn)
            {
                string exe;
                try
                {
                    exe = Process.GetCurrentProcess().MainModule.FileName;
                }
                catch (Exception ex)
                {
                    _log.Report("自动重启失败（取自身路径）: " + ex.GetType().Name);
                    return false;
                }

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "--wait-for-pid " + pid,
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(exe)
                    });
                }
                catch (Exception ex)
                {
                    _log.Report("自动重启失败（拉新进程）: " + ex.GetType().Name + ": " + ex.Message);
                    return false;
                }
            }

            if (!force) WriteStamp(DateTime.UtcNow);
            _log.Report("重启 Agent：" + reason + "（旧 pid " + pid + " 退出后由新进程接管）");
            if (quit != null) quit();
            return true;
        }

        private DateTime ReadStamp()
        {
            try
            {
                if (!File.Exists(_stampPath)) return DateTime.MinValue;
                string text = File.ReadAllText(_stampPath).Trim();
                DateTime parsed;
                return DateTime.TryParse(text, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out parsed) ? parsed : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private void WriteStamp(DateTime utc)
        {
            try
            {
                string dir = Path.GetDirectoryName(_stampPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_stampPath, utc.ToString("o"));
            }
            catch (Exception)
            {
                // A missing stamp only weakens the guard; it must never stop the restart itself.
            }
        }
    }
}
