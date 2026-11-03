using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using Microsoft.Win32;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Static hardware identification for the overview page.
    ///
    /// This is *identity* data (model names, capacity), not a metric: it is read once, cached for
    /// the process lifetime and never published through the IPC snapshot — the 256-byte layout
    /// and the "agent owns the sensors" rule both stay untouched.
    ///
    /// WMI can take a few hundred milliseconds on a cold start, so the callers use
    /// <see cref="Load"/> from a worker thread and read the cached strings from the UI thread.
    /// Every failure degrades to an empty label; the page keeps showing the capacity only.
    /// </summary>
    internal static class HardwareInfo
    {
        private static readonly object Gate = new object();
        private static bool _loaded;
        private static string _cpuSignature = string.Empty;
        private static string _memoryLabel = string.Empty;
        private static int _physicalCores;
        private static int _logicalProcessors;
        private static string _gpuName = string.Empty;
        private static string _systemDrive = string.Empty;

        /// <summary>"Intel Core i9-13900K · 32 T", or empty when unknown.</summary>
        public static string CpuSignature
        {
            get { lock (Gate) { return _cpuSignature; } }
        }

        /// <summary>"G.Skill DDR5 4200", or empty when unknown.</summary>
        public static string MemoryLabel
        {
            get { lock (Gate) { return _memoryLabel; } }
        }

        /// <summary>Physical core count (0 when unknown). Used to derive the bar column count.</summary>
        public static int PhysicalCores
        {
            get { lock (Gate) { return _physicalCores; } }
        }

        /// <summary>Logical processor count (0 when unknown).</summary>
        public static int LogicalProcessors
        {
            get { lock (Gate) { return _logicalProcessors; } }
        }

        /// <summary>
        /// Discrete adapter name for the GPU caption, e.g. "NVIDIA GeForce RTX 4080". Virtual
        /// display adapters (remote-desktop drivers) are skipped so the caption names the card
        /// the numbers actually come from.
        /// </summary>
        public static string GpuName
        {
            get { lock (Gate) { return _gpuName; } }
        }

        /// <summary>
        /// True when the adapter is NVIDIA. GPU load comes from NVML only, so this is the one
        /// case where a never-valid GpuValid flag means a broken sampler rather than unsupported
        /// hardware; the dashboard uses it to decide whether a restart is worth trying.
        /// </summary>
        public static bool HasNvidiaGpu
        {
            get
            {
                string name = GpuName;
                if (string.IsNullOrEmpty(name)) return false;
                bool nvidia = name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("Quadro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("Tesla", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!nvidia) return false;

                // No nvml.dll means NVML is not installed at all; a restart cannot add it, so the
                // watchdog should not burn its attempts on a machine that simply lacks the source.
                try
                {
                    return File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "nvml.dll"));
                }
                catch (Exception)
                {
                    // Unable to check: assume the driver is present and let one restart decide.
                    return true;
                }
            }
        }

        /// <summary>System drive label with the colon, e.g. "C:". Empty when unknown.</summary>
        public static string SystemDrive
        {
            get { lock (Gate) { return _systemDrive; } }
        }

        public static bool IsLoaded
        {
            get { lock (Gate) { return _loaded; } }
        }

        /// <summary>Reads the identity strings once. Safe to call repeatedly (no-op after the first).</summary>
        public static void Load()
        {
            lock (Gate)
            {
                if (_loaded) return;
            }

            string cpu = string.Empty;
            string memory = string.Empty;
            try
            {
                int cores;
                int threads;
                ReadCoreCounts(out cores, out threads);
                lock (Gate)
                {
                    _physicalCores = cores;
                    _logicalProcessors = threads;
                }
                cpu = HardwareNaming.CpuSignature(ReadCpuName(), cores, threads);
            }
            catch (Exception)
            {
                // Registry read blocked: keep the line empty, the page falls back to capacity.
            }

            try
            {
                memory = ReadMemoryLabel();
            }
            catch (Exception)
            {
                // WMI unavailable / permission denied: same fallback as above.
            }

            string gpu = string.Empty;
            try
            {
                gpu = ReadGpuName();
            }
            catch (Exception)
            {
                // WMI unavailable: the GPU tile falls back to a generic caption.
            }

            string systemDrive = string.Empty;
            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows)) systemDrive = Path.GetPathRoot(windows).TrimEnd('\\');
            }
            catch (Exception)
            {
                // keep empty
            }

            lock (Gate)
            {
                _cpuSignature = cpu ?? string.Empty;
                _memoryLabel = memory ?? string.Empty;
                _gpuName = gpu ?? string.Empty;
                _systemDrive = systemDrive ?? string.Empty;
                _loaded = true;
            }
        }

        /// <summary>
        /// Picks the adapter that is not a virtual display driver. AdapterRAM is unreliable on
        /// modern cards (WMI caps it), so the first non-virtual entry wins rather than the biggest.
        /// </summary>
        private static string ReadGpuName()
        {
            string fallback = string.Empty;
            using (ManagementObjectSearcher searcher =
                   new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject adapter in results)
                {
                    using (adapter)
                    {
                        string name = AsString(adapter["Name"]).Trim();
                        if (name.Length == 0) continue;
                        if (fallback.Length == 0) fallback = name;
                        if (IsVirtualAdapter(name)) continue;
                        return name;
                    }
                }
            }
            return fallback;
        }

        private static bool IsVirtualAdapter(string name)
        {
            return name.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("todesk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("parsec", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("basic display", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("remote", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Physical cores and logical processors from Win32_Processor (summed over sockets).
        /// WMI reports the physical count; Environment.ProcessorCount only knows threads, so it is
        /// used as the fallback when the query fails.
        /// </summary>
        private static void ReadCoreCounts(out int physicalCores, out int logicalProcessors)
        {
            physicalCores = 0;
            logicalProcessors = 0;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject cpu in results)
                    {
                        using (cpu)
                        {
                            physicalCores += AsInt(cpu["NumberOfCores"]);
                            logicalProcessors += AsInt(cpu["NumberOfLogicalProcessors"]);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // fall through to the environment values below
            }

            if (logicalProcessors <= 0) logicalProcessors = Environment.ProcessorCount;
        }

        private static string ReadCpuName()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false))
            {
                if (key == null) return string.Empty;
                return key.GetValue("ProcessorNameString") as string ?? string.Empty;
            }
        }

        /// <summary>
        /// Aggregates the installed modules into one label. Two identical sticks read as one
        /// signature; mixed vendors fall back to a count so the line never claims a wrong brand.
        /// </summary>
        private static string ReadMemoryLabel()
        {
            List<string> brands = new List<string>();
            List<int> types = new List<int>();
            List<int> speeds = new List<int>();
            List<string> parts = new List<string>();

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, PartNumber, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject module in results)
                {
                    using (module)
                    {
                        brands.Add(AsString(module["Manufacturer"]));
                        parts.Add(AsString(module["PartNumber"]));
                        types.Add(AsInt(module["SMBIOSMemoryType"]));
                        speeds.Add(AsInt(module["Speed"]));
                    }
                }
            }

            if (brands.Count == 0) return string.Empty;

            // All modules identical → one signature; otherwise prefer the most common values.
            string vendor = brands[0];
            int type = types[0];
            int speed = speeds[0];
            bool uniform = true;
            for (int i = 1; i < brands.Count; i++)
            {
                if (!string.Equals(brands[i], vendor, StringComparison.OrdinalIgnoreCase) ||
                    types[i] != type || speeds[i] != speed)
                {
                    uniform = false;
                    break;
                }
            }

            string label = HardwareNaming.MemoryLabel(vendor, parts[0], type, speed);
            if (!uniform && label.Length > 0)
            {
                label += " ×" + brands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return label;
        }

        private static string AsString(object value)
        {
            string text = value as string;
            return text == null ? string.Empty : text.Trim();
        }

        private static int AsInt(object value)
        {
            if (value == null) return 0;
            try
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
