using System;
using System.Globalization;
using System.IO;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Breadcrumb for a frame that took too long.
    ///
    /// Why: the band shows "--" when a snapshot is older than 3 s. A single frame that
    /// spends seconds in one stage produces exactly that blink even though nothing is broken, and
    /// without a record there is no way to tell "this thread was not scheduled" apart from
    /// "GetAllNetworkInterfaces() was slow". Only slow frames are written, and the file is capped,
    /// so the normal path costs one timer read plus one comparison per tick.
    ///
    /// This is diagnosis, not a feature: it never changes what is sampled or published.
    /// </summary>
    internal sealed class SlowTickLog
    {
        /// <summary>A tick that costs this much is already 40% of the 1 s period.</summary>
        private const int TickThresholdMs = 400;

        /// <summary>Woken this much later than requested means the thread was not scheduled.</summary>
        private const int OverrunThresholdMs = 200;

        private const long MaxBytes = 256 * 1024;

        private readonly string _path;
        private readonly object _gate = new object();

        public SlowTickLog()
        {
            string dir = Path.GetDirectoryName(Settings.DefaultPath);
            _path = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "agent-slow.log");
        }

        public string FilePath { get { return _path; } }

        /// <param name="overrunMs">int.MinValue when the loop did not wait (woken by settings).</param>
        public void Report(DateTime startedAtUtc, int tickMs, int waitMs, int overrunMs,
                           TickTimings timings, MetricsSnapshot snapshot)
        {
            bool slowTick = tickMs >= TickThresholdMs;
            bool slowWake = overrunMs >= OverrunThresholdMs;
            if (!slowTick && !slowWake) return;

            string overrun = overrunMs == int.MinValue
                ? "n/a"
                : overrunMs.ToString(CultureInfo.InvariantCulture) + "ms";
            string temp = snapshot != null && snapshot.Has(MetricFlags.TemperatureValid)
                ? snapshot.TemperatureC.ToString("F1", CultureInfo.InvariantCulture) + "C"
                : "--";

            string line = string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} tick={1}ms wait={2}ms overrun={3} seq={4} stages[{5}] temp={6}",
                DateTime.Now, tickMs, waitMs, overrun,
                snapshot == null ? 0u : snapshot.Sequence, timings, temp);

            try
            {
                lock (_gate)
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    {
                        // Diagnosis file: start over instead of rotating forever.
                        File.Delete(_path);
                    }
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Diagnostics must never affect sampling.
            }
        }
    }
}
