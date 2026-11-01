using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuadVitals.Agent
{
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint LowDateTime;
            public uint HighDateTime;

            public ulong ToUInt64()
            {
                return ((ulong)HighDateTime << 32) | LowDateTime;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        // Per-logical-processor times. NtQuerySystemInformation is undocumented but stable since
        // NT4 and is what Task Manager itself uses; the alternative (PDH counter paths) is exactly
        // the fragility design avoided.
        internal const int SystemProcessorPerformanceInformation = 8;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        [DllImport("ntdll.dll")]
        internal static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        // System-wide performance counters. Same undocumented-but-stable API as the per-core CPU
        // sampler; the first four fields are all the disk rate needs (transfer counts in bytes).
        internal const int SystemPerformanceInformation = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_PERFORMANCE_INFORMATION
        {
            public long IdleProcessTime;
            public long IoReadTransferCount;
            public long IoWriteTransferCount;
            public long IoOtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ---- NVML (ships with the NVIDIA driver; absent on every other machine) ----------

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlMemoryInfo
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        internal static extern int NvmlInit();

        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        internal static extern int NvmlShutdown();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
        internal static extern int NvmlDeviceGetCount(out uint count);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        internal static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
        internal static extern int NvmlDeviceGetName(IntPtr device, System.Text.StringBuilder name, uint length);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
        internal static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
        internal static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemoryInfo memory);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
        internal static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
        internal static extern int NvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
        internal static extern int NvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint celsius);

        internal static void ThrowIfFalse(bool ok, string what)
        {
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), what + " failed");
            }
        }
    }
}
