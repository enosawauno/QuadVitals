using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace QuadVitals.Core
{
    /// <summary>
    /// The shared-memory channel between the agent (single writer) and the band (readers),
    /// design. Name is session-local so it never leaks across users or to the network.
    ///
    /// Torn reads are handled with a seqlock on <see cref="MetricsSnapshot.Sequence"/>
    /// instead of a lock: the band must never block Explorer.
    /// </summary>
    public static class MetricsChannel
    {
        public const string MapName = @"Local\QuadVitals.Metrics.v1";

        /// <summary>
        /// 面板上「刷新」按的开关：Agent 等它、任务栏条也等它。一次 Set 同时产生“立刻采样一帧”
        /// 和“立刻重画一次”，否则按下去只有面板自己在变，任务栏要等它自己的 1 s 节拍。
        /// </summary>
        public const string RefreshEventName = @"Local\QuadVitals.Refresh.v1";

        /// <summary>Agent 没在跑时返回 false：调用方仍然可以自己重读快照，不会卡住。</summary>
        public static bool RequestRefresh()
        {
            try
            {
                using (EventWaitHandle handle = EventWaitHandle.OpenExisting(RefreshEventName))
                {
                    handle.Set();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Agent side. Holds the mapping open for its lifetime.</summary>
        public sealed class Writer : IDisposable
        {
            private MemoryMappedFile _file;
            private MemoryMappedViewAccessor _view;
            private readonly byte[] _buffer = new byte[MetricsSnapshot.SizeBytes];
            private uint _sequence;

            public Writer() : this(MapName)
            {
            }

            /// <param name="mapName">
            /// Tests pass a private name so they never publish fake values into the mapping the
            /// live band is reading. Keep the parameterless overload: it is the signature older
            /// binaries (the installed dashboard) were compiled against.
            /// </param>
            public Writer(string mapName)
            {
                string effective = string.IsNullOrEmpty(mapName) ? MapName : mapName;
                _file = MemoryMappedFile.CreateOrOpen(effective, MetricsSnapshot.SizeBytes);
                _view = _file.CreateViewAccessor(0, MetricsSnapshot.SizeBytes, MemoryMappedFileAccess.ReadWrite);

                // Publish an initially-empty snapshot so readers see a valid (stale) header
                // instead of random bytes before the first sample lands.
                MetricsSnapshot empty = new MetricsSnapshot
                {
                    Flags = MetricFlags.None,
                    TimestampUtc = DateTime.MinValue
                };
                Publish(empty);
            }

            public void Publish(MetricsSnapshot snapshot)
            {
                if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

                // Seqlock: odd = being written, even = stable. The marker must be written
                // DIRECTLY to the mapping first: if it were only part of the payload copy, a
                // reader could see the old even value while a new frame was half copied and
                // accept a mix of old and new fields. The payload carries the same odd value
                // so a reader that samples the mapping mid-copy sees "busy" either way; the
                // even value is published only after the whole frame has landed.
                _sequence += 1;
                snapshot.Sequence = _sequence;
                _view.Write((long)MetricsSnapshot.SequenceOffset, _sequence);
                Thread.MemoryBarrier();   // odd marker must be visible before payload writes

                snapshot.WriteTo(_buffer);
                _view.WriteArray(0, _buffer, 0, MetricsSnapshot.SizeBytes);
                Thread.MemoryBarrier();   // payload must be visible before the even marker

                _sequence += 1;
                snapshot.Sequence = _sequence;
                _view.Write((long)MetricsSnapshot.SequenceOffset, _sequence);
                Thread.MemoryBarrier();
            }

            public void Dispose()
            {
                if (_view != null) { _view.Dispose(); _view = null; }
                if (_file != null) { _file.Dispose(); _file = null; }
            }
        }

        /// <summary>Reader side. Cheap to open, safe to fail: no agent = no mapping.</summary>
        public sealed class Reader : IDisposable
        {
            private MemoryMappedFile _file;
            private MemoryMappedViewAccessor _view;
            private readonly byte[] _buffer = new byte[MetricsSnapshot.SizeBytes];

            private Reader(MemoryMappedFile file, MemoryMappedViewAccessor view)
            {
                _file = file;
                _view = view;
            }

            /// <summary>Returns null when the agent has never run in this session.</summary>
            public static Reader TryOpen()
            {
                return TryOpen(MapName);
            }

            /// <summary>Same as <see cref="TryOpen()"/> but on an explicit channel name.</summary>
            public static Reader TryOpen(string mapName)
            {
                string effective = string.IsNullOrEmpty(mapName) ? MapName : mapName;
                try
                {
                    MemoryMappedFile file = MemoryMappedFile.OpenExisting(effective, MemoryMappedFileRights.Read);
                    MemoryMappedViewAccessor view;
                    try
                    {
                        view = file.CreateViewAccessor(0, MetricsSnapshot.SizeBytes, MemoryMappedFileAccess.Read);
                    }
                    catch (ArgumentException)
                    {
                        // Older agents publish a shorter payload. Try the v3 length first so the
                        // GPU tail is still readable, then the 364-byte v2 frame. The parser
                        // reports whatever is past the mapped bytes as absent.
                        try
                        {
                            view = file.CreateViewAccessor(0, MetricsSnapshot.GpuTailSizeBytes, MemoryMappedFileAccess.Read);
                        }
                        catch (ArgumentException)
                        {
                            view = file.CreateViewAccessor(0, MetricsSnapshot.LegacySizeBytes, MemoryMappedFileAccess.Read);
                        }
                    }
                    return new Reader(file, view);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }

            /// <summary>
            /// Reads one consistent snapshot. Returns false with a reason when the agent is gone,
            /// the buffer is unreadable, or it could not get a stable copy.
            /// </summary>
            /// <summary>
            /// Copies what the view actually has and zeroes the rest: an older agent publishes 364
            /// bytes while this build expects 368, and the appended GPU fields must read as absent
            /// rather than pick up garbage.
            /// </summary>
            private void ReadIntoBuffer()
            {
                int readable = (int)Math.Min(MetricsSnapshot.SizeBytes, _view.Capacity);
                _view.ReadArray(0, _buffer, 0, readable);
                if (readable < _buffer.Length) Array.Clear(_buffer, readable, _buffer.Length - readable);
            }

            public bool TryRead(out MetricsSnapshot snapshot, out SnapshotError error)
            {
                snapshot = null;
                error = SnapshotError.None;

                if (_view == null) { error = SnapshotError.ChannelClosed; return false; }

                // Three attempts is plenty at 2 x 256 bytes per write; never spin.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    ReadIntoBuffer();
                    uint before = MetricsSnapshot.ReadUInt32(_buffer, MetricsSnapshot.SequenceOffset);

                    if ((before & 1u) != 0) continue; // writer mid-copy
                    Thread.MemoryBarrier();           // payload reads must not move before the marker read

                    MetricsSnapshot parsed;
                    SnapshotError localError;
                    if (!MetricsSnapshot.TryParse(_buffer, out parsed, out localError))
                    {
                        error = localError;
                        return false;
                    }

                    ReadIntoBuffer();
                    Thread.MemoryBarrier();
                    uint after = MetricsSnapshot.ReadUInt32(_buffer, MetricsSnapshot.SequenceOffset);
                    if (after != before) continue;

                    parsed.Sequence = after;
                    snapshot = parsed;
                    return true;
                }

                error = SnapshotError.TornRead;
                return false;
            }

            /// <summary>
            /// Convenience for tools and scripts (out parameters are awkward from PowerShell):
            /// returns null instead of a reason code.
            /// </summary>
            public MetricsSnapshot TryRead()
            {
                MetricsSnapshot snapshot;
                SnapshotError error;
                return TryRead(out snapshot, out error) ? snapshot : null;
            }

            public void Dispose()
            {
                if (_view != null) { _view.Dispose(); _view = null; }
                if (_file != null) { _file.Dispose(); _file = null; }
            }
        }
    }
}
