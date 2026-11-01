using System;
using System.Runtime.InteropServices;
using System.Text;

namespace QuadVitals.Agent
{
    /// <summary>
    /// GPU utilisation, dedicated-memory use and temperature through NVML (the runtime that
    /// ships with the vendor driver).
    ///
    /// Why not LibreHardwareMonitor for this: one LHM GPU Update() walks every D3D counter and
    /// measured 82 ms on the test machine, i.e. 1.6% CPU at a 5 s cadence, which breaks the
    /// agent's CPU budget. nvmlDeviceGetUtilizationRates costs about 0.1 us, so the publisher
    /// can call it inline at 1 Hz.
    ///
    /// Nothing here needs elevation, and nothing is loaded when nvml.dll is absent (anything
    /// other than an NVIDIA driver): every call then reports NaN and the snapshot simply
    /// leaves GpuValid off, which the UI renders as "--".
    /// </summary>
    internal sealed class GpuSource : IDisposable
    {
        /// <summary>NVML_TEMPERATURE_GPU: the edge sensor, not the hotspot.</summary>
        private const int TemperatureGpu = 0;

        private readonly IntPtr _device;
        private bool _initialised;
        private string _unavailableReason;
        private string _name = string.Empty;

        private GpuSource(string reason)
        {
            _unavailableReason = reason;
        }

        private GpuSource(IntPtr device, string name)
        {
            _device = device;
            _name = name;
        }

        public bool IsAvailable { get { return _initialised; } }

        public string UnavailableReason { get { return _unavailableReason; } }

        /// <summary>Adapter name as reported by the driver, e.g. "NVIDIA GeForce RTX 4080".</summary>
        public string Name { get { return _name; } }

        public static GpuSource Create(out string error)
        {
            error = null;
            try
            {
                int status = NativeMethods.NvmlInit();
                if (status != 0)
                {
                    error = "NVML 初始化失败（" + status + "），显卡指标显示 --";
                    return new GpuSource("NVML 初始化失败");
                }

                uint count;
                status = NativeMethods.NvmlDeviceGetCount(out count);
                if (status != 0 || count == 0)
                {
                    error = "NVML 没有找到显卡，显卡指标显示 --";
                    return new GpuSource("未检测到显卡");
                }

                IntPtr device;
                status = NativeMethods.NvmlDeviceGetHandleByIndex(0, out device);
                if (status != 0)
                {
                    error = "NVML 取显卡句柄失败（" + status + "）";
                    return new GpuSource("NVML 取句柄失败");
                }

                GpuSource source = new GpuSource(device, ReadName(device)) { _initialised = true };
                return source;
            }
            catch (DllNotFoundException)
            {
                error = "本机没有 nvml.dll（非 NVIDIA 驱动），显卡指标显示 --";
                return new GpuSource("无 NVML");
            }
            catch (EntryPointNotFoundException)
            {
                error = "nvml.dll 版本过旧，显卡指标显示 --";
                return new GpuSource("NVML 版本不兼容");
            }
            catch (Exception ex)
            {
                error = "NVML 加载异常: " + ex.GetType().Name;
                return new GpuSource("NVML 加载异常");
            }
        }

        /// <summary>
        /// Reads utilisation, VRAM use, temperature, package power and graphics clock. Any field the
        /// driver refuses stays NaN so the caller can publish it as invalid instead of a fake zero.
        /// Power and clock cost the same microseconds as the rest of the NVML calls (no elevation).
        /// </summary>
        public void Sample(out double loadPercent, out double memoryPercent, out double celsius,
                          out double powerWatts, out double clockMhz)
        {
            loadPercent = double.NaN;
            memoryPercent = double.NaN;
            celsius = double.NaN;
            powerWatts = double.NaN;
            clockMhz = double.NaN;
            if (!_initialised) return;

            try
            {
                NativeMethods.NvmlUtilization utilization;
                if (NativeMethods.NvmlDeviceGetUtilizationRates(_device, out utilization) == 0)
                {
                    loadPercent = utilization.Gpu;
                }

                NativeMethods.NvmlMemoryInfo memory;
                if (NativeMethods.NvmlDeviceGetMemoryInfo(_device, out memory) == 0 && memory.Total > 0)
                {
                    memoryPercent = 100.0 * (double)memory.Used / (double)memory.Total;
                }

                uint temperature;
                if (NativeMethods.NvmlDeviceGetTemperature(_device, TemperatureGpu, out temperature) == 0)
                {
                    celsius = temperature;
                }

                uint milliwatts;
                if (NativeMethods.NvmlDeviceGetPowerUsage(_device, out milliwatts) == 0 && milliwatts > 0)
                {
                    powerWatts = milliwatts / 1000.0;
                }

                // NVML_CLOCK_GRAPHICS = 0：游戏/计算实际跑的核心频率。
                uint mhz;
                if (NativeMethods.NvmlDeviceGetClockInfo(_device, 0, out mhz) == 0 && mhz > 0)
                {
                    clockMhz = mhz;
                }
            }
            catch (Exception)
            {
                // A driver hiccup must not take the agent down; the fields stay NaN for this tick.
            }
        }

        private static string ReadName(IntPtr device)
        {
            try
            {
                StringBuilder name = new StringBuilder(96);
                if (NativeMethods.NvmlDeviceGetName(device, name, (uint)name.Capacity) == 0) return name.ToString();
            }
            catch (Exception)
            {
                // name is diagnostic only
            }
            return string.Empty;
        }

        public void Dispose()
        {
            if (!_initialised) return;
            _initialised = false;
            try
            {
                NativeMethods.NvmlShutdown();
            }
            catch (Exception)
            {
                // shutting down is best effort and the process is about to exit anyway
            }
        }
    }
}
