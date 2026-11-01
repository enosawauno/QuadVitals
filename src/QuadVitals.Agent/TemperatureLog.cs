using System;
using System.IO;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Breadcrumb for the temperature sensor coming and going.
    ///
    /// CPU temperature is the one metric that can fail for reasons outside the process: the
    /// sensor driver is unloaded by security software or a game's anti-cheat, the driver file is
    /// removed, or the MSR read simply stops answering. Without a record, "sometimes it shows --"
    /// is indistinguishable from a code bug. Only transitions are written, and the file is capped,
    /// so the healthy path costs nothing.
    /// </summary>
    internal sealed class TemperatureLog
    {
        private const long MaxBytes = 64 * 1024;

        private readonly string _path;
        private readonly object _gate = new object();

        public TemperatureLog()
        {
            string dir = Path.GetDirectoryName(Settings.DefaultPath);
            _path = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "agent-temp.log");
        }

        public string FilePath { get { return _path; } }

        public void Report(string message)
        {
            try
            {
                lock (_gate)
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    {
                        // Diagnosis file: start over instead of rotating forever.
                        File.Delete(_path);
                    }
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("s") + "  [pid " + System.Diagnostics.Process.GetCurrentProcess().Id + "]  " +
                        message + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Diagnostics must never affect sampling.
            }
        }
    }
}
