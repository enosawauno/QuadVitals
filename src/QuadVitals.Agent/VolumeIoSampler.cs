using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Read/write throughput per local volume (C:, D:, an external USB disk, ...) for the
    /// per-drive disk-I/O elements.
    ///
    /// Source: IOCTL_DISK_PERFORMANCE on the volume handle. Opening `\\.\C:` needs Administrator
    /// (measured: ACCESS_DENIED in a user session), which the delivered agent already has for the
    /// temperature sensor; when it is not elevated every volume reports invalid and the band shows
    /// "--". The performance counters were tried first but this machine answers with the two
    /// byte counters swapped (writing 2 GB through E: moved "Disk Read Bytes/sec", not
    /// "Disk Write Bytes/sec"), so they are not a trustworthy source here.
    ///
    /// Network drives are not sampled: they have no disk device to query.
    /// </summary>
    internal sealed class VolumeIoSampler : IDisposable
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;
        private const uint IoctlDiskPerformance = 0x00070020;
        private const int MaxVolumes = MetricsSnapshot.MaxVolumes;
        private const int RefreshMs = 30000;

        [StructLayout(LayoutKind.Sequential)]
        private struct DiskPerformance
        {
            public long BytesRead;
            public long BytesWritten;
            public long ReadTime;
            public long WriteTime;
            public long IdleTime;
            public uint ReadCount;
            public uint WriteCount;
            public uint QueueDepth;
            public uint SplitCount;
            public long QueryTime;
            public uint StorageDeviceNumber;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public ushort[] StorageManagerName;
        }

        private sealed class Entry
        {
            public char Letter;
            public IntPtr Handle = IntPtr.Zero;
            public long LastRead;
            public long LastWrite;
            public DateTime LastSampleUtc = DateTime.MinValue;
            public bool HasBaseline;
        }

        private List<Entry> _entries = new List<Entry>();
        private DateTime _lastRefreshUtc = DateTime.MinValue;
        private bool _disposed;

        public VolumeIoSampler()
        {
            Refresh(true);
        }

        /// <summary>Drive letters currently sampled, e.g. "CDE". Diagnostics only.</summary>
        public string VolumeLetters
        {
            get
            {
                lock (this)
                {
                    char[] letters = new char[_entries.Count];
                    for (int i = 0; i < _entries.Count; i++) letters[i] = _entries[i].Letter;
                    return new string(letters);
                }
            }
        }

        public MetricsSnapshot.VolumeIo[] Sample(DateTime nowUtc)
        {
            lock (this)
            {
                if (_disposed) return new MetricsSnapshot.VolumeIo[0];
                if (nowUtc - _lastRefreshUtc >= TimeSpan.FromMilliseconds(RefreshMs)) Refresh(false);
                if (_entries.Count == 0) return new MetricsSnapshot.VolumeIo[0];

                MetricsSnapshot.VolumeIo[] result = new MetricsSnapshot.VolumeIo[_entries.Count];
                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry entry = _entries[i];
                    MetricsSnapshot.VolumeIo volume = new MetricsSnapshot.VolumeIo { Letter = entry.Letter };
                    DiskPerformance perf;
                    if (entry.Handle != IntPtr.Zero && TryQuery(entry.Handle, out perf))
                    {
                        double seconds = entry.HasBaseline
                            ? (nowUtc - entry.LastSampleUtc).TotalSeconds
                            : 0;
                        if (entry.HasBaseline && seconds > 0.001)
                        {
                            long readDelta = perf.BytesRead >= entry.LastRead ? perf.BytesRead - entry.LastRead : 0;
                            long writeDelta = perf.BytesWritten >= entry.LastWrite ? perf.BytesWritten - entry.LastWrite : 0;
                            volume.Valid = true;
                            volume.ReadBytesPerSec = (float)(readDelta / seconds);
                            volume.WriteBytesPerSec = (float)(writeDelta / seconds);
                        }
                        entry.LastRead = perf.BytesRead;
                        entry.LastWrite = perf.BytesWritten;
                        entry.LastSampleUtc = nowUtc;
                        entry.HasBaseline = true;
                    }
                    else
                    {
                        // Handle lost (volume removed, driver reset): drop it and let the next
                        // refresh reopen. Until then this volume is invalid, not zero.
                        CloseEntry(entry);
                        entry.HasBaseline = false;
                    }
                    result[i] = volume;
                }
                return result;
            }
        }

        private void Refresh(bool force)
        {
            if (_disposed) return;
            if (!force && _lastRefreshUtc != DateTime.MinValue &&
                DateTime.UtcNow - _lastRefreshUtc < TimeSpan.FromMilliseconds(RefreshMs)) return;

            List<char> letters = EnumerateVolumes();
            _lastRefreshUtc = DateTime.UtcNow;

            if (SameLetters(letters)) return;

            foreach (Entry entry in _entries) CloseEntry(entry);
            List<Entry> entries = new List<Entry>(letters.Count);
            foreach (char letter in letters)
            {
                Entry entry = new Entry { Letter = letter };
                string device = "\\\\.\\" + letter + ":";
                IntPtr handle = CreateFileW(device, GenericRead, ShareReadWrite, IntPtr.Zero,
                                            OpenExisting, 0, IntPtr.Zero);
                if (handle != new IntPtr(-1)) entry.Handle = handle;
                entries.Add(entry);
            }
            _entries = entries;
        }

        private static bool TryQuery(IntPtr handle, out DiskPerformance perf)
        {
            perf = new DiskPerformance();
            uint returned;
            return DeviceIoControl(handle, IoctlDiskPerformance, IntPtr.Zero, 0,
                                   ref perf, (uint)Marshal.SizeOf(typeof(DiskPerformance)),
                                   out returned, IntPtr.Zero);
        }

        private bool SameLetters(List<char> letters)
        {
            if (letters.Count != _entries.Count) return false;
            for (int i = 0; i < letters.Count; i++)
            {
                if (letters[i] != _entries[i].Letter) return false;
            }
            return true;
        }

        private static List<char> EnumerateVolumes()
        {
            List<char> letters = new List<char>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    // Local fixed disks and removable (USB) disks only: network mappings have no
                    // disk device to query.
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                    try
                    {
                        if (!drive.IsReady) continue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    string name = drive.Name;
                    if (name.Length < 2 || name[1] != ':') continue;
                    char letter = char.ToUpperInvariant(name[0]);
                    if (letter < 'A' || letter > 'Z') continue;
                    if (letters.Contains(letter)) continue;
                    letters.Add(letter);
                }
            }
            catch (Exception)
            {
                // Enumeration is best effort: no volumes just means no per-drive numbers.
            }
            letters.Sort();
            if (letters.Count > MaxVolumes) letters.RemoveRange(MaxVolumes, letters.Count - MaxVolumes);
            return letters;
        }

        private static void CloseEntry(Entry entry)
        {
            if (entry == null || entry.Handle == IntPtr.Zero) return;
            try
            {
                CloseHandle(entry.Handle);
            }
            catch (Exception)
            {
                // closing is best effort
            }
            entry.Handle = IntPtr.Zero;
        }

        public void Dispose()
        {
            lock (this)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (Entry entry in _entries) CloseEntry(entry);
                _entries = new List<Entry>();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
                                                 uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuffer, uint inSize,
                                                   ref DiskPerformance outBuffer, uint outSize,
                                                   out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
