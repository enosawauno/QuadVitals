using System;
using System.Runtime.InteropServices;

namespace QuadVitals.Agent
{
    /// <summary>
    /// System-wide disk throughput, from the byte counters inside
    /// NtQuerySystemInformation(SystemPerformanceInformation).
    ///
    /// Why not the obvious sources: per-volume IOCTL_DISK_PERFORMANCE needs an elevated handle and a
    /// new snapshot field per volume, and PDH/performance counters are ruled out as a metric source
    ///. The NtQuerySystemInformation call is the same family as the per-core CPU sampler,
    /// costs microseconds, needs no elevation, and covers every device the OS writes to.
    ///
    /// The counters are cumulative bytes since boot, so one sample only establishes a baseline; the
    /// caller gets a rate from the second sample on. A counter that went backwards (driver reset,
    /// sleep/resume) yields 0 for that tick instead of a huge negative rate.
    /// </summary>
    internal sealed class DiskIoSampler
    {
        /// <summary>Sanity ceiling: 100 GB/s is far above any desktop SSD and keeps float sane.</summary>
        private const double MaxBytesPerSecond = 100.0 * 1024 * 1024 * 1024;

        private long _lastRead = -1;
        private long _lastWrite = -1;
        private DateTime _lastSampleUtc;

        public bool TrySample(DateTime nowUtc, out double readBytesPerSecond, out double writeBytesPerSecond)
        {
            readBytesPerSecond = 0;
            writeBytesPerSecond = 0;

            // The API validates the length against the whole SYSTEM_PERFORMANCE_INFORMATION
            // (a few hundred bytes) and answers STATUS_INFO_LENGTH_MISMATCH otherwise, so ask for
            // a full-size buffer and read only the four leading counters.
            const int bufferSize = 512;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            if (buffer == IntPtr.Zero) return false;

            long read;
            long write;
            try
            {
                int returned;
                int status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemPerformanceInformation, buffer, bufferSize, out returned);
                if (status < 0) return false;

                NativeMethods.SYSTEM_PERFORMANCE_INFORMATION info =
                    (NativeMethods.SYSTEM_PERFORMANCE_INFORMATION)Marshal.PtrToStructure(
                        buffer, typeof(NativeMethods.SYSTEM_PERFORMANCE_INFORMATION));
                read = info.IoReadTransferCount;
                write = info.IoWriteTransferCount;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (_lastRead < 0)
            {
                _lastRead = read;
                _lastWrite = write;
                _lastSampleUtc = nowUtc;
                return false;
            }

            double seconds = (nowUtc - _lastSampleUtc).TotalSeconds;
            if (seconds <= 0.001) return false;

            long readDelta = read >= _lastRead ? read - _lastRead : 0;
            long writeDelta = write >= _lastWrite ? write - _lastWrite : 0;
            _lastRead = read;
            _lastWrite = write;
            _lastSampleUtc = nowUtc;

            readBytesPerSecond = Math.Min(MaxBytesPerSecond, readDelta / seconds);
            writeBytesPerSecond = Math.Min(MaxBytesPerSecond, writeDelta / seconds);
            return true;
        }
    }
}
