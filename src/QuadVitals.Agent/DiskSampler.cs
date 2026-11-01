using System;
using System.IO;

namespace QuadVitals.Agent
{
    /// <summary>
    /// System drive capacity (the "磁盘 · 占用" gauge on the dashboard).
    ///
    /// Capacity, not throughput: the gauge is a percentage with a fill bar, and free/total is a
    /// single cheap query with no counter infrastructure behind it. Read/write rates would need
    /// either the Windows performance counters (ruled out by the metric-source decision) or an
    /// elevated per-disk IOCTL, so they are not part of this metric.
    /// </summary>
    internal sealed class DiskSampler
    {
        private const long BytesPerGb = 1024L * 1024L * 1024L;

        private readonly string _root;
        private DateTime _sampledUtc = DateTime.MinValue;
        private double _percent = double.NaN;
        private int _usedGb;
        private int _totalGb;

        public DiskSampler()
        {
            try
            {
                string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                _root = string.IsNullOrEmpty(system) ? null : Path.GetPathRoot(system);
            }
            catch (Exception)
            {
                _root = null;
            }
            if (string.IsNullOrEmpty(_root)) _root = "C:\\";
        }

        /// <summary>Drive label used for the dashboard caption, e.g. "C:".</summary>
        public string Root { get { return _root == null ? string.Empty : _root.TrimEnd('\\'); } }

        /// <summary>
        /// Capacity once a second at most: the numbers only move when something writes a lot, and
        /// a directory query has no business running inside the 1 Hz publish path needlessly.
        /// </summary>
        public bool TrySample(DateTime nowUtc, out double percent, out int usedGb, out int totalGb)
        {
            percent = _percent;
            usedGb = _usedGb;
            totalGb = _totalGb;
            if (_root == null) return false;

            if (_sampledUtc != DateTime.MinValue && (nowUtc - _sampledUtc).TotalMilliseconds < 1000)
            {
                return !double.IsNaN(_percent);
            }
            _sampledUtc = nowUtc;

            try
            {
                DriveInfo drive = new DriveInfo(_root);
                if (!drive.IsReady) return false;

                long total = drive.TotalSize;
                long free = drive.TotalFreeSpace;
                if (total <= 0) return false;

                long used = total - free;
                _percent = Math.Round(100.0 * used / total, 1);
                _totalGb = (int)(total / BytesPerGb);
                _usedGb = (int)(used / BytesPerGb);
                percent = _percent;
                usedGb = _usedGb;
                totalGb = _totalGb;
                return true;
            }
            catch (Exception)
            {
                // Removable/locked drive or a transient IO error: keep the last good numbers.
                return !double.IsNaN(_percent);
            }
        }
    }
}
