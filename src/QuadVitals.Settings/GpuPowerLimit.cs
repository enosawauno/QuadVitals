using System;
using System.Runtime.InteropServices;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 显卡的功耗上限，直接问 NVML（NVIDIA 驱动自带）。它返回的是驱动当前生效的
    /// enforced power limit，默认就是这块卡的 TDP（超频软件改过的也算数）。
    /// 取不到时返回 0，调用方退回型号表；非 NVIDIA 机器本来也没有 GPU 功耗数据。
    /// </summary>
    internal static class GpuPowerLimit
    {
        private static bool _probed;
        private static double _watts;
        private static string _source = "未读取";

        public static double ReadWatts()
        {
            if (!_probed)
            {
                _probed = true;
                _watts = Probe(out _source);
            }
            return _watts;
        }

        /// <summary>一句话说明这个数字从哪来，给 tooltip 用。</summary>
        public static string Source
        {
            get { ReadWatts(); return _source; }
        }

        private static double Probe(out string source)
        {
            source = "驱动未提供功耗上限";
            try
            {
                if (Native.NvmlInit() != 0)
                {
                    source = "NVML 不可用";
                    return 0;
                }

                uint count;
                if (Native.NvmlDeviceGetCount(out count) != 0 || count == 0)
                {
                    source = "未找到显卡";
                    return 0;
                }

                IntPtr device;
                if (Native.NvmlDeviceGetHandleByIndex(0, out device) != 0)
                {
                    source = "取显卡句柄失败";
                    return 0;
                }

                uint milliwatts;
                if (Native.NvmlDeviceGetEnforcedPowerLimit(device, out milliwatts) != 0 || milliwatts == 0)
                {
                    return 0;
                }

                source = "驱动报告";
                return milliwatts / 1000.0;
            }
            catch (DllNotFoundException)
            {
                source = "无 NVML";
            }
            catch (EntryPointNotFoundException)
            {
                source = "NVML 版本过旧";
            }
            catch (Exception)
            {
                source = "NVML 读取异常";
            }
            return 0;
        }

        private static class Native
        {
            [DllImport("nvml.dll", EntryPoint = "nvmlInit")]
            internal static extern int NvmlInit();

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount")]
            internal static extern int NvmlDeviceGetCount(out uint count);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex")]
            internal static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
            internal static extern int NvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        }
    }
}
