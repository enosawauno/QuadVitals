using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace QuadVitals.Agent
{
    /// <summary>
    /// CPU temperature via LibreHardwareMonitorLib, loaded by reflection so the agent
    /// still runs (and simply reports "--") when the DLL or the sensor driver is missing — temperature
    /// wants temperature to degrade, not to take the agent down.
    ///
    /// The driver needs an elevated process; when running unelevated this reports Unavailable
    /// with a reason instead of failing.
    /// </summary>
    internal sealed class TemperatureSource : IDisposable
    {
        private object _computer;
        private Type _computerType;
        private string _unavailableReason;
        private DateTime _lastUpdateUtc = DateTime.MinValue;
        private int _emptyRefreshes;
        private int _sensorsSeen;
        private int _sensorsValid;
        private int _sensorsZero;
        private const int RefreshMs = 5000;   // LHM Update() walks every core; 5 s keeps budget
                                              // comfortable and is still fast enough for the band

        private TemperatureSource()
        {
            _unavailableReason = "未初始化";
        }

        public bool IsAvailable { get { return _computer != null && _emptyRefreshes < 2; } }

        public string UnavailableReason { get { return _unavailableReason; } }

        /// <summary>Diagnostic detail: which sensor fed the value.</summary>
        public string LastSensorName { get; private set; }

        /// <summary>
        /// What the last failed walk actually saw: how many temperature sensors were enumerated,
        /// how many returned a usable number and how many returned 0/NaN. "No data" can mean a
        /// dead driver handle, a blocked read or a sensor that answers 0; this tells them apart.
        /// </summary>
        public string LastDiagnostic { get; private set; }

        /// <summary>
        /// Drops the current LHM session and opens a new one. The sensor driver can be unloaded
        /// underneath a long-running agent (security software, a game's anti-cheat, or the driver
        /// file being removed) and LHM keeps reporting empty values from the dead handle; only a
        /// fresh Open() re-extracts and reloads it. Called by TemperatureWorker when the sensor has
        /// been silent for a while, never from the publish path.
        /// </summary>
        public bool Reopen()
        {
            try
            {
                CloseComputer();
                string error;
                bool opened = TryOpen(out error);
                if (!opened) _unavailableReason = error;
                return opened;
            }
            catch (Exception ex)
            {
                _unavailableReason = "重连 LHM 异常: " + ex.GetType().Name;
                return false;
            }
        }

        public static TemperatureSource Create(out string error)
        {
            error = null;
            TemperatureSource source = new TemperatureSource();
            if (source.TryOpen(out error)) source._unavailableReason = null;
            return source;
        }

        private bool TryOpen(out string error)
        {
            error = null;

            Type computerType = Type.GetType("LibreHardwareMonitor.Hardware.Computer, LibreHardwareMonitorLib", false);
            if (computerType == null)
            {
                // Look next to the exe and in ..\..\spikes\packages (dev layout).
                string probe = FindLibrary();
                if (probe != null)
                {
                    try
                    {
                        Assembly.LoadFrom(probe);
                        computerType = Type.GetType("LibreHardwareMonitor.Hardware.Computer, LibreHardwareMonitorLib", false);
                    }
                    catch (Exception ex)
                    {
                        error = "LibreHardwareMonitorLib 加载失败: " + ex.GetType().Name;
                        _unavailableReason = "LHM 加载失败";
                        return false;
                    }
                }
            }

            if (computerType == null)
            {
                error = "找不到 LibreHardwareMonitorLib.dll（温度不可用，其余指标继续）";
                _unavailableReason = "LHM 未安装";
                return false;
            }

            try
            {
                object computer = Activator.CreateInstance(computerType);
                // Only the CPU is enabled: opening storage/mainboard sensors needs more drivers
                // and more privileges than v1 needs (S1 finding).
                computerType.GetProperty("IsCpuEnabled").SetValue(computer, true, null);
                computerType.GetMethod("Open").Invoke(computer, null);

                _computer = computer;
                _computerType = computerType;
                _emptyRefreshes = 0;
                _lastUpdateUtc = DateTime.MinValue;
                LastValue = double.NaN;
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "LHM 初始化失败: " + (ex.InnerException ?? ex).Message;
                _unavailableReason = "LHM 初始化失败";
                return false;
            }
            catch (Exception ex)
            {
                error = "LHM 初始化失败: " + ex.GetType().Name;
                _unavailableReason = "LHM 初始化失败";
                return false;
            }
        }

        private void CloseComputer()
        {
            object computer = _computer;
            Type type = _computerType;
            _computer = null;
            _computerType = null;
            if (computer == null || type == null) return;
            try
            {
                type.GetMethod("Close").Invoke(computer, null);
            }
            catch (Exception)
            {
                // closing is best effort
            }
        }

        /// <summary>
        /// Returns degrees Celsius, or NaN when unavailable. Refreshes at most once per second,
        /// which is what the S1 spike measured as sufficient.
        /// </summary>
        public double Sample(DateTime nowUtc)
        {
            if (_computer == null) return double.NaN;
            if ((nowUtc - _lastUpdateUtc).TotalMilliseconds < RefreshMs) return LastValue;

            _lastUpdateUtc = nowUtc;

            _sensorsSeen = 0;
            LastPowerWatts = double.NaN;
            _sensorsValid = 0;
            _sensorsZero = 0;

            try
            {
                // Collect() refreshes every CPU hardware node, so no IVisitor round trip is
                // needed (and no compile-time dependency on the library's interfaces).
                double package = double.NaN;
                double coreMax = double.NaN;
                double anyMax = double.NaN;

                IList hardware = GetList(_computer, "Hardware");
                if (hardware != null)
                {
                    foreach (object hw in hardware)
                    {
                        Collect(hw, ref package, ref coreMax, ref anyMax, 0);
                    }
                }

                double value = !double.IsNaN(package) ? package
                             : !double.IsNaN(coreMax) ? coreMax
                             : anyMax;

                if (double.IsNaN(value))
                {
                    // The library opened but yields nothing. Report the counts instead of
                    // guessing a cause: 0 sensors enrolled means the hardware walk failed
                    // (driver/handle), sensors that answer 0 mean the read itself is refused.
                    _emptyRefreshes++;
                    LastDiagnostic = "温度传感器 " + _sensorsSeen + " 个，其中 " + _sensorsValid +
                                     " 个有效、" + _sensorsZero + " 个为 0/非法";
                    if (_emptyRefreshes >= 2)
                    {
                        _unavailableReason = "传感器未返回数据（" + LastDiagnostic + "）";
                    }
                }
                else
                {
                    _emptyRefreshes = 0;
                }

                LastValue = value;
                return value;
            }
            catch (Exception ex)
            {
                // Driver hiccup (sleep/resume, security software): keep the last good value for
                // one cycle and let the caller's staleness rule hide it.
                LastDiagnostic = "Update 抛异常: " + ex.GetType().Name + ": " + ex.Message;
                return LastValue;
            }
        }

        public double LastValue { get; private set; }

        /// <summary>CPU package power in watts, NaN when the library does not expose it.</summary>
        public double LastPowerWatts { get; private set; } = double.NaN;

        private void Collect(object hardware, ref double package, ref double coreMax, ref double anyMax, int depth)
        {
            if (hardware == null || depth > 3) return;

            try
            {
                hardware.GetType().GetMethod("Update").Invoke(hardware, null);

                IList sensors = GetList(hardware, "Sensors");
                if (sensors != null)
                {
                    foreach (object sensor in sensors)
                    {
                        Type sensorType = sensor.GetType();
                        object kind = sensorType.GetProperty("SensorType").GetValue(sensor, null);
                        if (kind == null) continue;
                        string kindName = kind.ToString();

                        // 功耗走同一个 Collect 遍历：LHM 的 Update() 已经刷新了整棵传感器树，
                        // 顺手取 Package Power 不多花一次调用（温度与功耗同源更新）。
                        if (kindName == "Power")
                        {
                            object powerRaw = sensorType.GetProperty("Value").GetValue(sensor, null);
                            if (powerRaw == null) continue;
                            string powerName = (string)sensorType.GetProperty("Name").GetValue(sensor, null) ?? string.Empty;
                            double watts = Convert.ToDouble(powerRaw);
                            if (powerName.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                !double.IsNaN(watts) && watts > 0 && watts < 2000 && double.IsNaN(LastPowerWatts))
                            {
                                LastPowerWatts = watts;
                            }
                            continue;
                        }

                        if (kindName != "Temperature") continue;

                        object raw = sensorType.GetProperty("Value").GetValue(sensor, null);
                        if (raw == null) continue;
                        _sensorsSeen++;
                        double value = Convert.ToDouble(raw);
                        if (double.IsNaN(value) || value <= 0 || value > 150)
                        {
                            _sensorsZero++;
                            continue;
                        }
                        _sensorsValid++;

                        string name = (string)sensorType.GetProperty("Name").GetValue(sensor, null) ?? string.Empty;
                        if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 && double.IsNaN(package))
                        {
                            package = value;
                            LastSensorName = name;
                        }
                        else if (name.IndexOf("Core Max", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (double.IsNaN(coreMax)) { coreMax = value; if (LastSensorName == null) LastSensorName = name; }
                        }

                        if (double.IsNaN(anyMax) || value > anyMax) anyMax = value;
                    }
                }

                IList subHardware = GetList(hardware, "SubHardware");
                if (subHardware != null)
                {
                    foreach (object sub in subHardware) Collect(sub, ref package, ref coreMax, ref anyMax, depth + 1);
                }
            }
            catch (TargetInvocationException)
            {
                // one flaky sensor must not abort the whole walk
            }
        }

        private static IList GetList(object target, string property)
        {
            object value = target.GetType().GetProperty(property).GetValue(target, null);
            return value as IList;
        }

        private static string FindLibrary()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            List<string> candidates = new List<string>
            {
                Path.Combine(exeDir, "LibreHardwareMonitorLib.dll"),
                Path.Combine(exeDir, "packages", "librehardwaremonitorlib-0.9.2", "lib", "net472", "LibreHardwareMonitorLib.dll")
            };

            // Dev layout: repo\src\QuadVitals.Agent\bin -> repo\spikes\packages\...
            string repo = Path.GetFullPath(Path.Combine(exeDir, "..", "..", ".."));
            candidates.Add(Path.Combine(repo, "spikes", "packages", "librehardwaremonitorlib-0.9.2", "lib", "net472",
                "LibreHardwareMonitorLib.dll"));

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public void Dispose()
        {
            CloseComputer();
        }
    }
}
