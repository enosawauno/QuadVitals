using System;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// CPU utilisation from GetSystemTimes deltas (deliberately no performance counters,
    /// design keeps the metric source in our own code).
    /// </summary>
    internal sealed class CpuSampler
    {
        private ulong _lastIdle;
        private ulong _lastTotal;
        private ulong _lastUser;
        private bool _hasPrevious;

        public bool HasPrevious { get { return _hasPrevious; } }

        /// <summary>Returns the busy percentage since the previous call; NaN on the first call.</summary>
        public double Sample()
        {
            double userPercent;
            double systemPercent;
            return Sample(out userPercent, out systemPercent);
        }

        /// <summary>
        /// Same sample, additionally split into user-mode and kernel-mode busy shares
        /// (kernel time includes idle time, so kernel-busy = kernel delta - idle delta).
        /// Both are NaN on the first call.
        /// </summary>
        public double Sample(out double userPercent, out double systemPercent)
        {
            userPercent = double.NaN;
            systemPercent = double.NaN;

            NativeMethods.FILETIME idle, kernel, user;
            if (!NativeMethods.GetSystemTimes(out idle, out kernel, out user))
            {
                return double.NaN;
            }

            ulong idleTicks = idle.ToUInt64();
            // kernel time already includes idle time, so total = kernel + user.
            ulong totalTicks = kernel.ToUInt64() + user.ToUInt64();

            ulong userTicks = user.ToUInt64();

            if (!_hasPrevious)
            {
                _lastIdle = idleTicks;
                _lastTotal = totalTicks;
                _lastUser = userTicks;
                _hasPrevious = true;
                return double.NaN;
            }

            ulong idleDelta = idleTicks - _lastIdle;
            ulong totalDelta = totalTicks - _lastTotal;
            ulong userDelta = userTicks - _lastUser;
            _lastIdle = idleTicks;
            _lastTotal = totalTicks;
            _lastUser = userTicks;

            if (totalDelta == 0) return double.NaN;

            double busy = 100.0 * (1.0 - (double)idleDelta / totalDelta);
            if (busy < 0) busy = 0;
            if (busy > 100) busy = 100;

            // kernel delta includes idle, so the kernel-busy part is kernel - idle
            ulong kernelBusyDelta = totalDelta >= idleDelta + userDelta
                ? totalDelta - idleDelta - userDelta
                : 0;

            userPercent = Clamp(100.0 * userDelta / totalDelta);
            systemPercent = Clamp(100.0 * kernelBusyDelta / totalDelta);
            return busy;
        }

        private static double Clamp(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        public void Reset()
        {
            _hasPrevious = false;
        }
    }

    /// <summary>
    /// Busy percentage per logical processor, one byte each (the band draws one column per core).
    /// Same delta maths as CpuSampler but on NtQuerySystemInformation's per-processor counters.
    /// </summary>
    internal sealed class PerCoreCpuSampler
    {
        private const int MaxCores = QuadVitals.Core.MetricsSnapshot.MaxCores;

        private readonly long[] _lastIdle = new long[MaxCores];
        private readonly long[] _lastTotal = new long[MaxCores];
        private readonly long[] _lastUser = new long[MaxCores];
        private int _cores;
        private bool _hasPrevious;

        public int CoreCount { get { return _cores; } }

        /// <summary>Busy share per logical processor plus its user / kernel split (0-100).</summary>
        public sealed class CoreSample
        {
            public byte[] Total = new byte[0];
            public byte[] User = new byte[0];
            public byte[] System = new byte[0];
        }

        /// <summary>Returns one entry per logical processor; all arrays are empty on the first call.</summary>
        public CoreSample Sample()
        {
            int count = Environment.ProcessorCount;
            CoreSample sample = new CoreSample();
            if (count < 1) return sample;
            if (count > MaxCores) count = MaxCores;

            int bytes = count * System.Runtime.InteropServices.Marshal.SizeOf(
                typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
            if (buffer == IntPtr.Zero) return sample;

            try
            {
                int returned;
                int status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemProcessorPerformanceInformation, buffer, bytes, out returned);
                if (status < 0) return sample;

                long[] idle = new long[count];
                long[] total = new long[count];
                long[] user = new long[count];
                int size = System.Runtime.InteropServices.Marshal.SizeOf(
                    typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buffer.ToInt64() + (long)i * size);
                    NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION info =
                        (NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION)
                        System.Runtime.InteropServices.Marshal.PtrToStructure(
                            item, typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
                    idle[i] = info.IdleTime;
                    total[i] = info.KernelTime + info.UserTime;   // kernel already includes idle
                    user[i] = info.UserTime;
                }

                if (!_hasPrevious)
                {
                    Array.Copy(idle, _lastIdle, count);
                    Array.Copy(total, _lastTotal, count);
                    Array.Copy(user, _lastUser, count);
                    _hasPrevious = true;
                    _cores = count;
                    return sample;
                }

                byte[] resultTotal = new byte[count];
                byte[] resultUser = new byte[count];
                byte[] resultSystem = new byte[count];

                for (int i = 0; i < count; i++)
                {
                    long idleDelta = idle[i] - _lastIdle[i];
                    long totalDelta = total[i] - _lastTotal[i];
                    long userDelta = user[i] - _lastUser[i];
                    double busy = totalDelta > 0 ? 100.0 * (1.0 - (double)idleDelta / totalDelta) : 0.0;
                    if (busy < 0) busy = 0;
                    if (busy > 100) busy = 100;

                    // kernel time includes idle, so the kernel-busy part is total - idle - user
                    long kernelBusyDelta = totalDelta - idleDelta - userDelta;
                    if (kernelBusyDelta < 0) kernelBusyDelta = 0;
                    long userDeltaForShare = userDelta < 0 ? 0 : userDelta;

                    resultTotal[i] = (byte)(busy + 0.5);
                    resultUser[i] = (byte)(totalDelta > 0 ? ClampPercent(100.0 * userDeltaForShare / totalDelta) : 0);
                    resultSystem[i] = (byte)(totalDelta > 0 ? ClampPercent(100.0 * kernelBusyDelta / totalDelta) : 0);

                    _lastIdle[i] = idle[i];
                    _lastTotal[i] = total[i];
                    _lastUser[i] = user[i];
                }
                _cores = count;
                sample.Total = resultTotal;
                sample.User = resultUser;
                sample.System = resultSystem;
                return sample;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }

        public void Reset()
        {
            _hasPrevious = false;
        }

        private static double ClampPercent(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }

    /// <summary>Physical memory from GlobalMemoryStatusEx.</summary>
    internal sealed class MemorySampler
    {
        public bool TrySample(out double percent, out long usedBytes, out long totalBytes)
        {
            percent = double.NaN;
            usedBytes = 0;
            totalBytes = 0;

            NativeMethods.MEMORYSTATUSEX status = new NativeMethods.MEMORYSTATUSEX();
            status.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX));
            if (!NativeMethods.GlobalMemoryStatusEx(ref status)) return false;

            totalBytes = (long)status.ullTotalPhys;
            long available = (long)status.ullAvailPhys;
            usedBytes = totalBytes - available;
            percent = status.dwMemoryLoad;
            return true;
        }
    }
}
