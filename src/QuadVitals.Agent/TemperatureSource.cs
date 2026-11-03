using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using QuadVitals.Core;

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
        private readonly List<MetricsSnapshot.FanReading> _fans = new List<MetricsSnapshot.FanReading>();
        private int _fanChannelsSeen;
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
        /// Fan channels the last walk saw with a non-zero reading. LHM 0.9.2 names them by
        /// position ("Fan #2"), so the channel number is all we can publish; the dashboard turns
        /// it into a label.
        /// </summary>
        public IList<MetricsSnapshot.FanReading> LastFans { get { return _fans; } }

        /// <summary>Fan channels the last walk enumerated, including the ones reading 0.</summary>
        public int LastFanChannelCount { get { return _fanChannelsSeen; } }

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
                // CPU for temperature/power, motherboard for the SuperIO fan channels. Opening the
                // motherboard node costs nothing measurable next to the CPU walk (both are inside
                // the same 5 s Update), and it is the only place fan RPM exists. EC stays off: this
                // board reports through SuperIO and the controller path is board-specific.
                computerType.GetProperty("IsCpuEnabled").SetValue(computer, true, null);
                computerType.GetProperty("IsMotherboardEnabled").SetValue(computer, true, null);
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
        /// Returns degrees Celsius, or NaN when unavailable. The caller drives it on a 5 s rhythm
        /// (<see cref="TemperatureWorker"/>) because LHM's Update cannot refresh a single sensor.
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
            _fans.Clear();
            _fanChannelsSeen = 0;

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

                // 通道号排序：LHM 的枚举顺序不是通道号顺序，“自动（第一路）”才有确定含义。
                _fans.Sort(delegate (MetricsSnapshot.FanReading a, MetricsSnapshot.FanReading b)
                {
                    return a.Channel.CompareTo(b.Channel);
                });

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
                MethodInfo update = UpdateMethod(hardware.GetType());
                if (update != null) update.Invoke(hardware, null);

                IList sensors = GetList(hardware, "Sensors");
                if (sensors != null)
                {
                    foreach (object sensor in sensors)
                    {
                        Type sensorType = sensor.GetType();
                        PropertyInfo kindProperty = Property(sensorType, "SensorType");
                        object kind = kindProperty == null ? null : kindProperty.GetValue(sensor, null);
                        if (kind == null) continue;
                        string kindName = kind.ToString();

                        // 功耗走同一个 Collect 遍历：LHM 的 Update() 已经刷新了整棵传感器树，
                        // 顺手取 Package Power 不多花一次调用（温度与功耗同源更新）。
                        if (kindName == "Power")
                        {
                            PropertyInfo powerValue = Property(sensorType, "Value");
                            object powerRaw = powerValue == null ? null : powerValue.GetValue(sensor, null);
                            if (powerRaw == null) continue;
                            PropertyInfo powerNameProperty = Property(sensorType, "Name");
                            string powerName = (powerNameProperty == null ? null : (string)powerNameProperty.GetValue(sensor, null)) ?? string.Empty;
                            double watts = Convert.ToDouble(powerRaw);
                            if (powerName.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                !double.IsNaN(watts) && watts > 0 && watts < 2000 && double.IsNaN(LastPowerWatts))
                            {
                                LastPowerWatts = watts;
                            }
                            continue;
                        }

                        // 风扇与温度同一个遍历：主板 SuperIO 已经在这棵树里刷新过，不另开一次 Update。
                        // 只发布“这次真的在转”的通道（> 0）；未接的风扇头 LHM 读 0，发上去只会多出空行。
                        if (kindName == "Fan")
                        {
                            // 先记账再看值：读到 0 的通道也是“主板有这个风扇头”的证据，
                            // 空态文案要靠它区分“没有通道”和“有通道但全读 0”。
                            _fanChannelsSeen++;
                            PropertyInfo fanValue = Property(sensorType, "Value");
                            object fanRaw = fanValue == null ? null : fanValue.GetValue(sensor, null);
                            if (fanRaw == null) continue;
                            double rpmValue = Convert.ToDouble(fanRaw);
                            // 0 RPM 也发布：停转和“这个头没接”在寄存器上长得一样，
                            // 藏掉 0 就等于把“风扇真停了”一起藏掉（由卡片决定怎么说）。
                            if (!FanDiagnosis.IsPlausible(rpmValue)) continue;
                            PropertyInfo fanNameProperty = Property(sensorType, "Name");
                            string fanName = (fanNameProperty == null ? null : (string)fanNameProperty.GetValue(sensor, null)) ?? string.Empty;
                            int fanChannel = ParseFanChannel(fanName);
                            _fans.Add(new MetricsSnapshot.FanReading
                            {
                                Kind = FanLabels.KindForBoard(BoardProduct(), fanChannel),
                                Channel = fanChannel,
                                Rpm = (int)Math.Round(rpmValue),
                                Valid = true
                            });
                            continue;
                        }

                        if (kindName != "Temperature") continue;

                        PropertyInfo valueProperty = Property(sensorType, "Value");
                        object raw = valueProperty == null ? null : valueProperty.GetValue(sensor, null);
                        if (raw == null) continue;
                        _sensorsSeen++;
                        double value = Convert.ToDouble(raw);
                        if (double.IsNaN(value) || value <= 0 || value > 150)
                        {
                            _sensorsZero++;
                            continue;
                        }
                        _sensorsValid++;

                        PropertyInfo nameProperty = Property(sensorType, "Name");
                        string name = (nameProperty == null ? null : (string)nameProperty.GetValue(sensor, null)) ?? string.Empty;
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

        /// <summary>
        /// LHM names SuperIO fan channels positionally ("Fan #2"). Anything without a number is
        /// kept as channel 0 so it still shows up instead of being dropped.
        /// </summary>
        private static int ParseFanChannel(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int i = name.IndexOf('#');
            if (i < 0) return 0;
            int value = 0;
            bool any = false;
            for (i = i + 1; i < name.Length; i++)
            {
                if (name[i] < '0' || name[i] > '9') break;
                any = true;
                value = value * 10 + (name[i] - '0');
                if (value > 99) return 99;
            }
            return any ? value : 0;
        }

        /// <summary>
        /// Board model from the registry (the same string the BIOS posts). Read once: it never
        /// changes while the process runs and the table lookup happens per fan sample.
        /// WMI is avoided here because the agent's sampling thread must not touch it.
        /// </summary>
        private static string BoardProduct()
        {
            if (_boardProduct != null) return _boardProduct;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    _boardProduct = key == null ? string.Empty : (key.GetValue("BaseBoardProduct") as string) ?? string.Empty;
                }
            }
            catch (Exception)
            {
                _boardProduct = string.Empty;
            }
            return _boardProduct;
        }

        private static string _boardProduct;

        // 反射查找只做一次：LHM 的传感器类型就那几个，每次 5 秒的遍历里按 Type + 名字缓存
        // MethodInfo / PropertyInfo，省掉成千上万次 GetMethod/GetProperty。遍历只在
        // TemperatureWorker 这一个线程上跑，所以普通 Dictionary 就够，不需要锁。
        private static readonly System.Collections.Generic.Dictionary<Type, MethodInfo> UpdateMethods =
            new System.Collections.Generic.Dictionary<Type, MethodInfo>();
        private static readonly System.Collections.Generic.Dictionary<Type, System.Collections.Generic.Dictionary<string, PropertyInfo>> Properties =
            new System.Collections.Generic.Dictionary<Type, System.Collections.Generic.Dictionary<string, PropertyInfo>>();

        private static MethodInfo UpdateMethod(Type type)
        {
            MethodInfo cached;
            if (UpdateMethods.TryGetValue(type, out cached)) return cached;
            cached = type.GetMethod("Update");
            UpdateMethods[type] = cached;
            return cached;
        }

        private static PropertyInfo Property(Type type, string name)
        {
            System.Collections.Generic.Dictionary<string, PropertyInfo> map;
            if (!Properties.TryGetValue(type, out map))
            {
                map = new System.Collections.Generic.Dictionary<string, PropertyInfo>();
                Properties[type] = map;
            }
            PropertyInfo cached;
            if (map.TryGetValue(name, out cached)) return cached;
            cached = type.GetProperty(name);
            map[name] = cached;
            return cached;
        }

        private static IList GetList(object target, string property)
        {
            PropertyInfo info = Property(target.GetType(), property);
            if (info == null) return null;
            object value = info.GetValue(target, null);
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
