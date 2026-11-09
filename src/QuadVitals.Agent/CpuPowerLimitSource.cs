using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// CPU TDP from the firmware: Intel's package power info MSR 0x614 (bits 14:0, 1/8 W).
    /// Read through LibreHardwareMonitor's Ring0, i.e. the same kernel driver temperature already
    /// needs, so the agent has to be elevated and the first read may have to wait until the
    /// driver is open. 0x614 is Intel-specific, so AMD machines are skipped instead of probing an
    /// undocumented MSR. Any failure keeps the value at 0, which makes the dashboard fall back to
    /// its model table and mark the number as an estimate.
    /// </summary>
    internal sealed class CpuPowerLimitSource
    {
        private const uint PackagePowerInfoMsr = 0x614;
        private const uint PackagePowerLimitMsr = 0x610;
        private const int RetrySeconds = 30;
        private const int MaxAttempts = 10;

        private double _watts;
        private string _detail = "未读取";
        private DateTime _nextAttemptUtc = DateTime.MinValue;
        private int _attempts;
        private bool _gaveUp;

        public double Watts { get { return _watts; } }

        /// <summary>Where the value came from (or why it did not), for --status and logs.</summary>
        public string Detail { get { return _detail; } }

        public double Read(DateTime nowUtc)
        {
            if (_watts > 0 || _gaveUp) return _watts;
            if (nowUtc < _nextAttemptUtc) return 0;
            _nextAttemptUtc = nowUtc.AddSeconds(RetrySeconds);
            Probe();
            return _watts;
        }

        private void Probe()
        {
            _attempts++;
            try
            {
                if (!IsIntel())
                {
                    _detail = "非 Intel";
                    _gaveUp = true;
                    return;
                }

                Assembly lhm = FindLhm();
                if (lhm == null)
                {
                    _detail = "LHM 未加载";
                    GiveUpWhenExhausted();
                    return;
                }

                Type ring0 = lhm.GetType("LibreHardwareMonitor.Hardware.Ring0");
                MethodInfo read = ring0 == null ? null : ring0.GetMethod("ReadMsr",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(uint), typeof(uint).MakeByRefType(), typeof(uint).MakeByRefType() },
                    null);
                if (read == null)
                {
                    _detail = "LHM 无 Ring0";
                    _gaveUp = true;
                    return;
                }

                PropertyInfo isOpen = ring0.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Static);
                if (isOpen != null && !(bool)isOpen.GetValue(null, null))
                {
                    MethodInfo open = ring0.GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
                    if (open != null) open.Invoke(null, null);
                }
                if (isOpen != null && !(bool)isOpen.GetValue(null, null))
                {
                    _detail = "驱动未打开";
                    GiveUpWhenExhausted();
                    return;
                }

                double tdp = ReadWatts(read, PackagePowerInfoMsr);
                if (tdp <= 0) tdp = ReadWatts(read, PackagePowerLimitMsr);
                if (tdp > 0)
                {
                    _watts = tdp;
                    _detail = "MSR";
                    return;
                }

                _detail = "MSR 未给出有效值";
                GiveUpWhenExhausted();
            }
            catch (Exception ex)
            {
                _detail = ex.GetType().Name;
                GiveUpWhenExhausted();
            }
        }

        private void GiveUpWhenExhausted()
        {
            if (_attempts >= MaxAttempts) _gaveUp = true;
        }

        private static double ReadWatts(MethodInfo read, uint index)
        {
            object[] args = new object[] { index, 0u, 0u };
            read.Invoke(null, args);
            return PowerLimits.DecodePackageWatts((uint)args[1], (uint)args[2]);
        }

        private static bool IsIntel()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string vendor = key == null ? null : key.GetValue("VendorIdentifier") as string;
                    return vendor != null &&
                           vendor.IndexOf("GenuineIntel", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Assembly FindLhm()
        {
            Type computer = Type.GetType("LibreHardwareMonitor.Hardware.Computer, LibreHardwareMonitorLib", false);
            if (computer != null) return computer.Assembly;

            string probe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LibreHardwareMonitorLib.dll");
            if (!File.Exists(probe)) return null;
            try
            {
                return Assembly.LoadFrom(probe);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
