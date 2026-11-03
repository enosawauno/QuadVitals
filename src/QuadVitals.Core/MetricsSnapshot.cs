using System;
using System.Globalization;
using System.Text;

namespace QuadVitals.Core
{
    /// <summary>Which metrics inside a snapshot are trustworthy.</summary>
    [Flags]
    public enum MetricFlags : ushort
    {
        None = 0,
        CpuValid = 1 << 0,
        MemoryValid = 1 << 1,
        TemperatureValid = 1 << 2,
        NetworkValid = 1 << 3,
        /// <summary>Set by the agent while it is alive and writing; the band must not infer it.</summary>
        AgentAlive = 1 << 4,
        /// <summary>GPU load + dedicated memory read from the vendor runtime (NVML).</summary>
        GpuValid = 1 << 5,
        GpuTemperatureValid = 1 << 6,
        /// <summary>System drive capacity.</summary>
        DiskValid = 1 << 7,
        /// <summary>System-wide disk read+write throughput (NtQuerySystemInformation).</summary>
        DiskIoValid = 1 << 8,
        /// <summary>
        /// CPU package power was sampled. The value itself lives in the v3 reserved slot
        /// (offset 266, deciwatts) so the payload did not have to grow: a reader that predates
        /// this bit simply ignores both, and nothing else changes meaning.
        /// </summary>
        CpuPowerValid = 1 << 9,
        /// <summary>GPU package power present (NVML, appended field at offset 364).</summary>
        GpuPowerValid = 1 << 10,
        /// <summary>GPU graphics clock present (NVML, appended field at offset 366).</summary>
        GpuClockValid = 1 << 11,
        /// <summary>
        /// Motherboard fan channels were sampled (v4 tail). Individual readings carry their own
        /// validity byte, so a channel that is not connected simply does not appear.
        /// </summary>
        FanValid = 1 << 12,
        /// <summary>The writing agent reported its elevation state (paired with <see cref="Elevated"/>).</summary>
        ElevationKnown = 1 << 13,
        /// <summary>The writing agent has an elevated token; temperature and fan need it.</summary>
        Elevated = 1 << 14
    }

    /// <summary>
    /// The single payload the agent publishes into shared memory and the band reads.
    /// Fixed 256-byte layout, hand-serialised so the two processes cannot disagree about
    /// alignment or packing:
    ///
    ///   0   uint32  magic        'PULS'
    ///   4   uint16  version      1
    ///   6   uint16  flags        MetricFlags
    ///   8   int64   timestampUtc DateTime.UtcNow.Ticks when the sample was taken
    ///   16  float   cpuPercent
    ///   20  float   memoryPercent
    ///   24  float   temperatureC
    ///   28  uint32  sequence     seqlock counter: odd = write in progress
    ///   32  double  netUpBytesPerSec
    ///   40  double  netDownBytesPerSec
    ///   48  char[48] adapterName  UTF-16, null terminated, 96 bytes
    ///   144 int64   memoryUsedBytes
    ///   152 int64   memoryTotalBytes
    ///   160 float   cpuUserPercent    (reserved area, see note below)
    ///   164 float   cpuSystemPercent  (reserved area)
    ///   168 uint16  coreCount         0 = agent did not sample per-core
    ///   170 uint16  reserved
    ///   172 uint8[32] corePercent      busy per logical processor, 0-100
    ///   204 uint8[32] coreSystemPercent kernel share per logical processor (user = busy - this)
    ///   236 float   gpuLoadPercent     GPU utilisation (0 = not sampled)
    ///   240 float   gpuMemoryPercent   dedicated VRAM used
    ///   244 float   gpuTemperatureC
    ///   248 float   diskPercent       system drive capacity used
    ///   252 uint16  diskUsedGb
    ///   254 uint16  diskTotalGb
    ///   256 float   diskIoReadBytesPerSec
    ///   260 float   diskIoWriteBytesPerSec
    ///   264 uint16  volumeCount    (v3; 0 = agent did not sample per-volume)
    ///   266 uint16  cpuPowerDeciwatts (0 = not sampled; added after v3 without growing the payload)
    ///   268 8 x 12 B  volume slots: uint16 letter, uint16 flags, float read, float write
    ///   364 uint16  gpuPowerDeciwatts (appended; old readers map only the first 364 bytes)
    ///   366 uint16  gpuClockMhz      (appended)
    ///   368 uint16  fanCount        (v4; 0 = not sampled)
    ///   370 uint16  reserved
    ///   372 8 x 4 B fan slots: uint8 kind, uint8 channel, uint16 rpm
    ///   404         end
    ///
    /// v2 grew the payload from 256 to 264 bytes for the disk-I/O pair; v3 appended a per-volume
    /// table (local fixed/removable volumes only) so a text element can pick one drive instead of
    /// the whole-system total; v4 appends the motherboard fan channels. Readers accept version 3
    /// and 4 so a new dashboard still reads an old agent (no fans, no GPU tail from a 364-byte
    /// frame) while agent and band are upgraded in one installer run.
    /// </summary>
    public sealed class MetricsSnapshot
    {
        /// <summary>Payload size written by this build (v4: the v3 368-byte payload plus the fan tail).</summary>
        public const int SizeBytes = 404;

        /// <summary>
        /// How long a snapshot may be shown as live data. Two seconds, not three: the agent publishes
        /// at 1 Hz, so a single missed frame is normal but two are already a stall, and the dashboard
        /// has to say so before the user starts doubting the numbers.
        /// </summary>
        public static readonly TimeSpan AliveTimeout = TimeSpan.FromSeconds(2);
        /// <summary>
        /// v3 payload length. Readers accept this or <see cref="SizeBytes"/>: the two GPU fields were
        /// appended at the tail and the taskbar band still maps exactly 364 bytes, so an old reader
        /// keeps working and a new reader falls back to no GPU power/clock from an old agent.
        /// </summary>
        public const int LegacySizeBytes = 364;
        /// <summary>v3 written size: has the GPU power/clock tail but no fan block.</summary>
        public const int GpuTailSizeBytes = 368;
        /// <summary>Fan block (v4): uint16 count at 368, 8 slots of 4 bytes from 372.</summary>
        public const int FanCountOffset = 368;
        public const int FanTableOffset = 372;
        public const int FanSlotBytes = 4;
        public const int MaxFans = 8;
        // Little endian in memory, so the four bytes read "PULS" in a hex dump.
        public const uint Magic = 0x534C5550;
        public const ushort CurrentVersion = 4;
        /// <summary>Oldest frame this build still parses (v3 = no fans).</summary>
        public const ushort MinimumSupportedVersion = 3;
        /// <summary>Offset of the seqlock counter (uint32). Shared with MetricsClient.cpp.</summary>
        public const int SequenceOffset = 28;
        /// <summary>Offsets of the CPU split (float). In the former reserved area; shared with MetricsClient.cpp.</summary>
        public const int UserPercentOffset = 160;
        public const int SystemPercentOffset = 164;
        /// <summary>Offset of coreCount (uint16) and of the per-core percentages (uint8 each).</summary>
        public const int CoreCountOffset = 168;
        public const int CorePercentsOffset = 172;
        public const int CoreSystemPercentsOffset = 204;
        /// <summary>GPU and disk block: floats at 236/240/244/248, capacity in GB at 252/254.</summary>
        public const int GpuLoadPercentOffset = 236;
        public const int GpuMemoryPercentOffset = 240;
        public const int GpuTemperatureOffset = 244;
        public const int DiskPercentOffset = 248;
        public const int DiskCapacityOffset = 252;
        /// <summary>Disk I/O block (v2): two floats at 256 / 260.</summary>
        public const int DiskIoReadOffset = 256;
        public const int DiskIoWriteOffset = 260;
        /// <summary>Per-volume table (v3): uint16 count at 264, slots from 268 (12 bytes each).</summary>
        public const int VolumeCountOffset = 264;
        /// <summary>CPU package power in deciwatts; this slot was reserved in v3 and is still inside the 364-byte payload.</summary>
        public const int CpuPowerOffset = 266;
        /// <summary>GPU package power in deciwatts (appended after the v3 payload).</summary>
        public const int GpuPowerOffset = 364;
        /// <summary>GPU graphics clock in MHz (appended after the v3 payload).</summary>
        public const int GpuClockOffset = 366;
        /// <summary>Where the per-volume table starts.</summary>
        public const int VolumeTableOffset = 268;
        public const int VolumeSlotBytes = 12;
        /// <summary>How many local volumes the payload can carry (C:..H: is already generous).</summary>
        public const int MaxVolumes = 8;
        /// <summary>
        /// How many logical processors fit in the payload. Three byte arrays (total / user /
        /// kernel) share the tail, so 32 is the ceiling - the 32-thread test machine fits exactly.
        /// </summary>
        public const int MaxCores = 32;
        private const int AdapterChars = 48;

        public MetricFlags Flags { get; set; }
        public DateTime TimestampUtc { get; set; }
        public float CpuPercent { get; set; }
        public float MemoryPercent { get; set; }
        public float TemperatureC { get; set; }
        public double NetUpBytesPerSec { get; set; }
        public double NetDownBytesPerSec { get; set; }
        public string AdapterName { get; set; }
        public long MemoryUsedBytes { get; set; }
        public long MemoryTotalBytes { get; set; }

        /// <summary>User-mode share of the CPU sample, 0-100 (XV_c49d4816 follow-up).</summary>
        public float CpuUserPercent { get; set; }

        /// <summary>
        /// Kernel-mode share (kernel time minus idle time), 0-100. The bar paints this first and
        /// then overpaints the user share, so only the part above the user colour is visible.
        /// </summary>
        public float CpuSystemPercent { get; set; }

        /// <summary>
        /// Per-logical-processor busy percentages, 0-100, in processor order. Empty when the agent
        /// does not publish them (older agent, or sampling failed) - the band then falls back to
        /// the total.
        /// </summary>
        public byte[] CorePercents { get; set; }

        /// <summary>Per-core user-mode share, same length as CorePercents (may be empty).</summary>
        public byte[] CoreUserPercents { get; set; }

        /// <summary>Per-core kernel-mode share, same length as CorePercents (may be empty).</summary>
        public byte[] CoreSystemPercents { get; set; }

        /// <summary>GPU utilisation in percent; GpuValid says whether it was sampled at all.</summary>
        public float GpuLoadPercent { get; set; }

        /// <summary>Dedicated video memory used, in percent (0..100).</summary>
        public float GpuMemoryPercent { get; set; }

        public float GpuTemperatureC { get; set; }

        /// <summary>System drive capacity used, in percent.</summary>
        public float DiskPercent { get; set; }

        public int DiskUsedGb { get; set; }
        public int DiskTotalGb { get; set; }

        /// <summary>System-wide disk read throughput, bytes/s (v2 field).</summary>
        public float DiskIoReadBytesPerSec { get; set; }

        /// <summary>System-wide disk write throughput, bytes/s (v2 field).</summary>
        public float DiskIoWriteBytesPerSec { get; set; }

        /// <summary>
        /// CPU package power in watts, 0 when the agent could not sample it. Stored in the two
        /// bytes the v3 layout had reserved (deciwatts, so 253.4 W round-trips as 2534) — the
        /// payload stays 364 bytes and the taskbar band does not have to be rebuilt.
        /// </summary>
        public float CpuPowerWatts { get; set; }

        /// <summary>GPU package power in watts (appended field, 0 = not sampled).</summary>
        public float GpuPowerWatts { get; set; }

        /// <summary>GPU graphics clock in MHz (appended field, 0 = not sampled).</summary>
        public float GpuClockMhz { get; set; }

        /// <summary>
        /// One local volume's throughput (v3 field). `Letter` is the drive letter without the colon
        /// (e.g. 'C'); the band matches it against the element's configured drive.
        /// </summary>
        public sealed class VolumeIo
        {
            public char Letter { get; set; }
            public bool Valid { get; set; }
            public float ReadBytesPerSec { get; set; }
            public float WriteBytesPerSec { get; set; }

            public VolumeIo Clone()
            {
                return new VolumeIo
                {
                    Letter = Letter,
                    Valid = Valid,
                    ReadBytesPerSec = ReadBytesPerSec,
                    WriteBytesPerSec = WriteBytesPerSec
                };
            }
        }

        /// <summary>Per-volume throughput table (v3). Empty when the agent did not sample volumes.</summary>
        public VolumeIo[] Volumes { get; set; }

        /// <summary>
        /// One motherboard fan channel (v4). LHM 0.9.2 only exposes positional names ("Fan #2"),
        /// so the channel number is the identity; `Rpm` 0 means the header reported a stopped or
        /// unconnected fan, and the UI decides whether to show that row.
        /// </summary>
        public sealed class FanReading
        {
            /// <summary>Which header this channel is (see <see cref="FanKinds"/>); 0 = unknown.</summary>
            public int Kind { get; set; }

            public int Channel { get; set; }
            public int Rpm { get; set; }

            /// <summary>Implied by the agent publishing the slot at all; not stored separately.</summary>
            public bool Valid { get; set; }

            public FanReading Clone()
            {
                return new FanReading { Kind = Kind, Channel = Channel, Rpm = Rpm, Valid = Valid };
            }
        }

        /// <summary>Fan channels the agent sampled this tick (v4). Empty when not sampled.</summary>
        public FanReading[] Fans { get; set; }

        /// <summary>
        /// Seqlock counter (offset 28). The writer bumps it to an odd value before copying and
        /// to an even value after, so a reader can detect a torn copy without locking.
        /// </summary>
        public uint Sequence { get; set; }

        public MetricsSnapshot()
        {
            AdapterName = string.Empty;
            TimestampUtc = DateTime.MinValue;
            CorePercents = new byte[0];
            CoreUserPercents = new byte[0];
            CoreSystemPercents = new byte[0];
            Volumes = new VolumeIo[0];
            Fans = new FanReading[0];
        }

        public bool Has(MetricFlags flag)
        {
            return (Flags & flag) == flag;
        }

        public TimeSpan Age(DateTime nowUtc)
        {
            if (TimestampUtc == DateTime.MinValue) return TimeSpan.MaxValue;
            return nowUtc - TimestampUtc;
        }

        /// <summary>True when the snapshot may still be shown as live data.</summary>
        public bool IsAlive(DateTime nowUtc, TimeSpan staleAfter)
        {
            if (!Has(MetricFlags.AgentAlive)) return false;
            TimeSpan age = Age(nowUtc);
            return age >= TimeSpan.Zero && age <= staleAfter;
        }

        public byte[] ToBytes()
        {
            byte[] buffer = new byte[SizeBytes];
            WriteTo(buffer);
            return buffer;
        }

        public void WriteTo(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length < SizeBytes)
                throw new ArgumentException("buffer must be at least " + SizeBytes + " bytes", nameof(buffer));

            Array.Clear(buffer, 0, SizeBytes);

            WriteUInt32(buffer, 0, Magic);
            WriteUInt16(buffer, 4, CurrentVersion);
            WriteUInt16(buffer, 6, (ushort)Flags);
            WriteInt64(buffer, 8, TimestampUtc == DateTime.MinValue ? 0L : TimestampUtc.Ticks);
            WriteSingle(buffer, 16, CpuPercent);
            WriteSingle(buffer, 20, MemoryPercent);
            WriteSingle(buffer, 24, TemperatureC);
            WriteUInt32(buffer, SequenceOffset, Sequence);
            WriteDouble(buffer, 32, NetUpBytesPerSec);
            WriteDouble(buffer, 40, NetDownBytesPerSec);
            WriteFixedString(buffer, 48, AdapterChars, AdapterName);
            WriteInt64(buffer, 144, MemoryUsedBytes);
            WriteInt64(buffer, 152, MemoryTotalBytes);
            WriteSingle(buffer, UserPercentOffset, CpuUserPercent);
            WriteSingle(buffer, SystemPercentOffset, CpuSystemPercent);

            int cores = CorePercents == null ? 0 : CorePercents.Length;
            if (cores > MaxCores) cores = MaxCores;
            WriteUInt16(buffer, CoreCountOffset, (ushort)cores);
            for (int i = 0; i < cores; i++)
            {
                buffer[CorePercentsOffset + i] = CorePercents[i];
                if (CoreSystemPercents != null && i < CoreSystemPercents.Length)
                    buffer[CoreSystemPercentsOffset + i] = CoreSystemPercents[i];
            }

            WriteSingle(buffer, GpuLoadPercentOffset, GpuLoadPercent);
            WriteSingle(buffer, GpuMemoryPercentOffset, GpuMemoryPercent);
            WriteSingle(buffer, GpuTemperatureOffset, GpuTemperatureC);
            WriteSingle(buffer, DiskPercentOffset, DiskPercent);
            WriteUInt16(buffer, DiskCapacityOffset, (ushort)ClampGb(DiskUsedGb));
            WriteUInt16(buffer, DiskCapacityOffset + 2, (ushort)ClampGb(DiskTotalGb));
            WriteSingle(buffer, DiskIoReadOffset, DiskIoReadBytesPerSec);
            WriteSingle(buffer, DiskIoWriteOffset, DiskIoWriteBytesPerSec);

            // 0 = 未采样；deciwatt 精度（2534 = 253.4 W），与旧版保留槽兼容。
            float watts = CpuPowerWatts > 0 ? CpuPowerWatts : 0;
            int deciwatts = (int)Math.Round(watts * 10);
            if (deciwatts < 0) deciwatts = 0;
            if (deciwatts > ushort.MaxValue) deciwatts = ushort.MaxValue;
            WriteUInt16(buffer, CpuPowerOffset, (ushort)deciwatts);

            int volumes = Volumes == null ? 0 : Volumes.Length;
            if (volumes > MaxVolumes) volumes = MaxVolumes;
            WriteUInt16(buffer, VolumeCountOffset, (ushort)volumes);
            for (int i = 0; i < volumes; i++)
            {
                VolumeIo volume = Volumes[i];
                int offset = VolumeTableOffset + i * VolumeSlotBytes;
                WriteUInt16(buffer, offset, (ushort)(volume == null ? 0 : volume.Letter));
                WriteUInt16(buffer, offset + 2, (ushort)(volume != null && volume.Valid ? 1 : 0));
                if (volume == null) continue;
                WriteSingle(buffer, offset + 4, volume.ReadBytesPerSec);
                WriteSingle(buffer, offset + 8, volume.WriteBytesPerSec);
            }

            // 尾部追加的两个 GPU 字段（364 之后）：deciwatt / MHz，0 = 未采样。
            // 旧读端只映射前 364 字节，所以看不到它们、也不受影响。
            WriteUInt16(buffer, GpuPowerOffset, (ushort)ClampU16(Math.Round((GpuPowerWatts > 0 ? GpuPowerWatts : 0) * 10)));
            WriteUInt16(buffer, GpuClockOffset, (ushort)ClampU16(Math.Round(GpuClockMhz > 0 ? GpuClockMhz : 0)));

            int fans = Fans == null ? 0 : Fans.Length;
            if (fans > MaxFans) fans = MaxFans;
            WriteUInt16(buffer, FanCountOffset, (ushort)fans);
            for (int i = 0; i < fans; i++)
            {
                FanReading fan = Fans[i];
                int offset = FanTableOffset + i * FanSlotBytes;
                buffer[offset] = (byte)(fan == null ? 0 : fan.Kind);
                buffer[offset + 1] = (byte)(fan == null ? 0 : fan.Channel);
                WriteUInt16(buffer, offset + 2, (ushort)(fan == null ? 0 : ClampU16(fan.Rpm)));
            }
        }

        private static double ClampU16(double value)
        {
            if (double.IsNaN(value) || value < 0) return 0;
            if (value > ushort.MaxValue) return ushort.MaxValue;
            return value;
        }

        private static int ClampGb(int gigabytes)
        {
            if (gigabytes < 0) return 0;
            if (gigabytes > ushort.MaxValue) return ushort.MaxValue;
            return gigabytes;
        }

        /// <summary>
        /// Parses a snapshot. Returns false (and a reason) on a torn/incompatible buffer
        /// instead of throwing, because the band must never crash Explorer.
        /// </summary>
        public static bool TryParse(byte[] buffer, out MetricsSnapshot snapshot, out SnapshotError error)
        {
            snapshot = null;
            error = SnapshotError.None;

            if (buffer == null) { error = SnapshotError.NullBuffer; return false; }
            if (buffer.Length < LegacySizeBytes) { error = SnapshotError.BufferTooSmall; return false; }
            if (ReadUInt32(buffer, 0) != Magic) { error = SnapshotError.BadMagic; return false; }

            ushort version = ReadUInt16(buffer, 4);
            if (version < MinimumSupportedVersion || version > CurrentVersion) { error = SnapshotError.VersionMismatch; return false; }

            long ticks = ReadInt64(buffer, 8);
            MetricsSnapshot parsed = new MetricsSnapshot
            {
                Flags = (MetricFlags)ReadUInt16(buffer, 6),
                TimestampUtc = ticks <= 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc),
                CpuPercent = ReadSingle(buffer, 16),
                MemoryPercent = ReadSingle(buffer, 20),
                TemperatureC = ReadSingle(buffer, 24),
                NetUpBytesPerSec = ReadDouble(buffer, 32),
                NetDownBytesPerSec = ReadDouble(buffer, 40),
                AdapterName = ReadFixedString(buffer, 48, AdapterChars),
                MemoryUsedBytes = ReadInt64(buffer, 144),
                MemoryTotalBytes = ReadInt64(buffer, 152),
                CpuUserPercent = ReadSingle(buffer, UserPercentOffset),
                CpuSystemPercent = ReadSingle(buffer, SystemPercentOffset),
                GpuLoadPercent = ReadSingle(buffer, GpuLoadPercentOffset),
                GpuMemoryPercent = ReadSingle(buffer, GpuMemoryPercentOffset),
                GpuTemperatureC = ReadSingle(buffer, GpuTemperatureOffset),
                DiskPercent = ReadSingle(buffer, DiskPercentOffset),
                DiskUsedGb = ReadUInt16(buffer, DiskCapacityOffset),
                DiskTotalGb = ReadUInt16(buffer, DiskCapacityOffset + 2),
                CpuPowerWatts = ReadUInt16(buffer, CpuPowerOffset) / 10f,
                GpuPowerWatts = buffer.Length >= GpuTailSizeBytes ? ReadUInt16(buffer, GpuPowerOffset) / 10f : 0f,
                GpuClockMhz = buffer.Length >= GpuTailSizeBytes ? ReadUInt16(buffer, GpuClockOffset) : 0f,
                DiskIoReadBytesPerSec = ReadSingle(buffer, DiskIoReadOffset),
                DiskIoWriteBytesPerSec = ReadSingle(buffer, DiskIoWriteOffset),
                Sequence = ReadUInt32(buffer, SequenceOffset)
            };

            int volumeCount = ReadUInt16(buffer, VolumeCountOffset);
            if (volumeCount > MaxVolumes) volumeCount = MaxVolumes;
            if (volumeCount > 0)
            {
                VolumeIo[] volumes = new VolumeIo[volumeCount];
                for (int i = 0; i < volumeCount; i++)
                {
                    int offset = VolumeTableOffset + i * VolumeSlotBytes;
                    volumes[i] = new VolumeIo
                    {
                        Letter = (char)ReadUInt16(buffer, offset),
                        Valid = ReadUInt16(buffer, offset + 2) != 0,
                        ReadBytesPerSec = ReadSingle(buffer, offset + 4),
                        WriteBytesPerSec = ReadSingle(buffer, offset + 8)
                    };
                }
                parsed.Volumes = volumes;
            }

            if (buffer.Length >= SizeBytes)
            {
                int fanCount = ReadUInt16(buffer, FanCountOffset);
                if (fanCount > MaxFans) fanCount = MaxFans;
                if (fanCount > 0)
                {
                    FanReading[] fans = new FanReading[fanCount];
                    for (int i = 0; i < fanCount; i++)
                    {
                        int offset = FanTableOffset + i * FanSlotBytes;
                        fans[i] = new FanReading
                        {
                            Kind = buffer[offset],
                            Channel = buffer[offset + 1],
                            Rpm = ReadUInt16(buffer, offset + 2)
                        };
                        fans[i].Valid = fans[i].Rpm > 0;
                    }
                    parsed.Fans = fans;
                }
            }

            int coreCount = ReadUInt16(buffer, CoreCountOffset);
            if (coreCount > MaxCores) coreCount = MaxCores;
            if (coreCount > 0)
            {
                byte[] cores = new byte[coreCount];
                byte[] users = new byte[coreCount];
                byte[] systems = new byte[coreCount];
                for (int i = 0; i < coreCount; i++)
                {
                    cores[i] = buffer[CorePercentsOffset + i];
                    systems[i] = buffer[CoreSystemPercentsOffset + i];
                    // user share is the remainder: it is not stored a third time
                    int user = cores[i] - systems[i];
                    users[i] = (byte)(user < 0 ? 0 : user);
                }
                parsed.CorePercents = cores;
                parsed.CoreUserPercents = users;
                parsed.CoreSystemPercents = systems;
            }

            snapshot = parsed;
            return true;
        }

        public static ushort VersionOf(byte[] buffer)
        {
            return buffer != null && buffer.Length >= 6 ? ReadUInt16(buffer, 4) : (ushort)0;
        }

        // ---- primitive helpers (little endian, matching x64 Windows) -----------------

        internal static void WriteUInt16(byte[] b, int offset, ushort value)
        {
            b[offset] = (byte)value;
            b[offset + 1] = (byte)(value >> 8);
        }

        internal static void WriteUInt32(byte[] b, int offset, uint value)
        {
            b[offset] = (byte)value;
            b[offset + 1] = (byte)(value >> 8);
            b[offset + 2] = (byte)(value >> 16);
            b[offset + 3] = (byte)(value >> 24);
        }

        internal static void WriteInt64(byte[] b, int offset, long value)
        {
            for (int i = 0; i < 8; i++) b[offset + i] = (byte)(value >> (8 * i));
        }

        internal static void WriteSingle(byte[] b, int offset, float value)
        {
            byte[] raw = BitConverter.GetBytes(value);
            Buffer.BlockCopy(raw, 0, b, offset, 4);
        }

        internal static void WriteDouble(byte[] b, int offset, double value)
        {
            byte[] raw = BitConverter.GetBytes(value);
            Buffer.BlockCopy(raw, 0, b, offset, 8);
        }

        internal static ushort ReadUInt16(byte[] b, int offset)
        {
            return (ushort)(b[offset] | (b[offset + 1] << 8));
        }

        internal static uint ReadUInt32(byte[] b, int offset)
        {
            return (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));
        }

        internal static long ReadInt64(byte[] b, int offset)
        {
            long value = 0;
            for (int i = 7; i >= 0; i--) value = (value << 8) | b[offset + i];
            return value;
        }

        internal static float ReadSingle(byte[] b, int offset)
        {
            return BitConverter.ToSingle(b, offset);
        }

        internal static double ReadDouble(byte[] b, int offset)
        {
            return BitConverter.ToDouble(b, offset);
        }

        internal static void WriteFixedString(byte[] b, int offset, int charCount, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int chars = Math.Min(text.Length, charCount - 1); // keep a terminating null
            for (int i = 0; i < chars; i++)
            {
                char c = text[i];
                b[offset + i * 2] = (byte)c;
                b[offset + i * 2 + 1] = (byte)(c >> 8);
            }
        }

        internal static string ReadFixedString(byte[] b, int offset, int charCount)
        {
            char[] chars = new char[charCount];
            int length = 0;
            while (length < charCount)
            {
                char c = (char)(b[offset + length * 2] | (b[offset + length * 2 + 1] << 8));
                if (c == '\0') break;
                chars[length++] = c;
            }
            return new string(chars, 0, length);
        }

        public override string ToString()
        {
            // Render per validity flag: an invalid metric reads "--", never a misleading 0.
            string cpu = Has(MetricFlags.CpuValid) ? CpuPercent.ToString("F1", CultureInfo.InvariantCulture) : "--";
            string memory = Has(MetricFlags.MemoryValid) ? MemoryPercent.ToString("F1", CultureInfo.InvariantCulture) : "--";
            string temp = Has(MetricFlags.TemperatureValid) ? TemperatureC.ToString("F1", CultureInfo.InvariantCulture) : "--";
            string up = Has(MetricFlags.NetworkValid) ? NetUpBytesPerSec.ToString("F0", CultureInfo.InvariantCulture) : "--";
            string down = Has(MetricFlags.NetworkValid) ? NetDownBytesPerSec.ToString("F0", CultureInfo.InvariantCulture) : "--";
            string split = Has(MetricFlags.CpuValid)
                ? "u=" + CpuUserPercent.ToString("F1", CultureInfo.InvariantCulture) +
                  " s=" + CpuSystemPercent.ToString("F1", CultureInfo.InvariantCulture)
                : "u=-- s=--";
            string gpu = Has(MetricFlags.GpuValid)
                ? GpuLoadPercent.ToString("F1", CultureInfo.InvariantCulture) + "%/" +
                  GpuMemoryPercent.ToString("F1", CultureInfo.InvariantCulture) + "%"
                : "--";
            string gpuTemp = Has(MetricFlags.GpuTemperatureValid)
                ? GpuTemperatureC.ToString("F1", CultureInfo.InvariantCulture) : "--";
            string disk = Has(MetricFlags.DiskValid)
                ? DiskPercent.ToString("F1", CultureInfo.InvariantCulture) + "% (" +
                  DiskUsedGb + "/" + DiskTotalGb + " GB)"
                : "--";
            string fanText = Has(MetricFlags.FanValid)
                ? (Fans == null ? "0" : Fans.Length.ToString(CultureInfo.InvariantCulture))
                : "--";

            return string.Format(CultureInfo.InvariantCulture,
                "seq={0} cpu={1} ({8}) mem={2} temp={3} up={4} down={5} flags={6} adapter='{7}' gpu={9} gpuTemp={10} disk={11} diskIo={12} fans={13}",
                Sequence, cpu, memory, temp, up, down, Flags, AdapterName, split, gpu, gpuTemp, disk,
                Has(MetricFlags.DiskIoValid)
                    ? (DiskIoReadBytesPerSec + DiskIoWriteBytesPerSec).ToString("F0", CultureInfo.InvariantCulture)
                    : "--",
                fanText);
        }
    }

    public enum SnapshotError
    {
        None = 0,
        NullBuffer,
        BufferTooSmall,
        BadMagic,
        VersionMismatch,
        /// <summary>The shared-memory channel was already disposed / never opened.</summary>
        ChannelClosed,
        /// <summary>Three consistent copies could not be obtained; skip this tick.</summary>
        TornRead
    }
}
