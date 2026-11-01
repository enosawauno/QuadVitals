using System;
using System.Runtime.InteropServices;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 当前 CPU 主频。走 <c>CallNtPowerInformation(ProcessorInformation)</c>：这是电源管理接口，
    /// 不是性能计数器（design 排除的是 PDH），也不需要管理员权限，比每帧读 LHM 便宜得多。
    ///
    /// 只给仪表盘的「CPU · 频率」这一格用；任务栏条不需要它，所以值不进共享内存。
    /// 读不到就返回 0，界面按 `--` 显示。
    /// </summary>
    internal static class CpuClock
    {
        private const int ProcessorInformation = 11;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorPowerInformation
        {
            public uint Number;
            public uint MaxMhz;
            public uint CurrentMhz;
            public uint MhzLimit;
            public uint MaxIdleState;
            public uint CurrentIdleState;
        }

        /// <summary>一次读取的结果；CurrentMhz 为 0 表示读不到（例如接口被策略关掉）。</summary>
        public struct Reading
        {
            public uint CurrentMhz;
            public uint MaxMhz;
        }

        public static Reading Read()
        {
            try
            {
                int count = Environment.ProcessorCount;
                int entrySize = Marshal.SizeOf(typeof(ProcessorPowerInformation));
                int size = entrySize * count;
                IntPtr buffer = Marshal.AllocCoTaskMem(size);
                try
                {
                    int status = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)size);
                    if (status != 0) return new Reading();

                    // 各逻辑处理器取最大值：单核睿频时“当前主频”才是实际频率。
                    Reading best = new Reading();
                    for (int i = 0; i < count; i++)
                    {
                        ProcessorPowerInformation info = (ProcessorPowerInformation)Marshal.PtrToStructure(
                            new IntPtr(buffer.ToInt64() + i * entrySize), typeof(ProcessorPowerInformation));
                        if (info.CurrentMhz > best.CurrentMhz) best.CurrentMhz = info.CurrentMhz;
                        if (info.MaxMhz > best.MaxMhz) best.MaxMhz = info.MaxMhz;
                    }
                    return best;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
            }
            catch (Exception)
            {
                return new Reading();
            }
        }

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern int CallNtPowerInformation(
            int informationLevel, IntPtr inputBuffer, uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize);
    }
}
