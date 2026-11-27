using System;

namespace LibreHardwareMonitor.Hardware
{
    public sealed class Computer
    {
        public static int OpenFailures;
        public static int Opens;
        public static int Closes;
        public static bool Empty;
        public bool IsCpuEnabled { get; set; }
        public bool IsMotherboardEnabled { get; set; }
        public object[] Hardware { get { return new object[] { new HardwareNode() }; } }

        public void Open()
        {
            Opens++;
            if (OpenFailures > 0)
            {
                OpenFailures--;
                throw new InvalidOperationException("driver not ready");
            }
        }

        public void Close() { Closes++; }
    }

    public sealed class HardwareNode
    {
        public void Update() { }
        public object[] SubHardware { get { return new object[0]; } }
        public object[] Sensors
        {
            get
            {
                return Computer.Empty ? new object[0] : new object[]
                {
                    new Sensor("Temperature", "CPU Package", 55),
                    new Sensor("Power", "CPU Package", 30),
                    new Sensor("Fan", "Fan #2", 900)
                };
            }
        }
    }

    public sealed class Sensor
    {
        public string SensorType { get; private set; }
        public string Name { get; private set; }
        public float Value { get; private set; }

        public Sensor(string type, string name, float value)
        {
            SensorType = type;
            Name = name;
            Value = value;
        }
    }
}

namespace LibreHardwareMonitor.PawnIo
{
    public static class PawnIo
    {
        public static bool IsInstalled { get; set; } = true;
        public static Version Version { get; set; } = new Version(2, 2, 0, 0);
    }

    public sealed class IntelMsr
    {
        public static bool FailRead;
        public bool ReadMsr(uint index, out uint eax, out uint edx)
        {
            eax = index == 0x614 ? 1000u : 2024u;
            edx = 0;
            return !FailRead;
        }
        public void Close() { }
    }
}
