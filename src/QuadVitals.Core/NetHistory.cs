using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace QuadVitals.Core
{
    /// <summary>
    /// Network-rate points for the overview sparkline, persisted so a reopened dashboard can
    /// continue the curve from where it stopped instead of starting over at the right edge.
    ///
    /// The file is state, not settings: it lives next to settings.json but is written by the
    /// dashboard only. Every parse/write failure is swallowed on purpose — a broken history must
    /// never stop the dashboard from opening, it just starts empty (same rule as settings).
    /// </summary>
    public sealed class NetHistory
    {
        /// <summary>Same window as the sparkline: one point per refresh, 60 points.</summary>
        public const int Capacity = 60;

        private const int CurrentVersion = 1;
        /// <summary>A healthy file is a few KB; anything larger is not ours, so don't parse it.</summary>
        private const long MaxFileBytes = 64 * 1024;
        private const double MaxRate = 1e15;

        private readonly List<NetHistoryPoint> _points = new List<NetHistoryPoint>();

        public int Count { get { return _points.Count; } }

        /// <summary>
        /// Appends one sample. Equal or older timestamps are ignored: the dashboard timer can
        /// read the same agent frame twice, and appending it again would draw a fake plateau.
        /// </summary>
        public void Push(DateTime stampUtc, double upBytesPerSec, double downBytesPerSec)
        {
            if (stampUtc == DateTime.MinValue) return;

            long ticks = stampUtc.Ticks;
            if (_points.Count > 0 && ticks <= _points[_points.Count - 1].StampUtcTicks) return;

            _points.Add(new NetHistoryPoint
            {
                StampUtcTicks = ticks,
                Up = Sanitize(upBytesPerSec),
                Down = Sanitize(downBytesPerSec)
            });

            while (_points.Count > Capacity) _points.RemoveAt(0);
        }

        /// <summary>Copy of the up-rate values, oldest first (right-aligned by Sparkline).</summary>
        public double[] UpValues()
        {
            double[] values = new double[_points.Count];
            for (int i = 0; i < values.Length; i++) values[i] = _points[i].Up;
            return values;
        }

        /// <summary>Copy of the down-rate values, oldest first.</summary>
        public double[] DownValues()
        {
            double[] values = new double[_points.Count];
            for (int i = 0; i < values.Length; i++) values[i] = _points[i].Down;
            return values;
        }

        /// <summary>Copy of the sample timestamps, oldest first (hover labels use them).</summary>
        public DateTime[] Stamps()
        {
            DateTime[] stamps = new DateTime[_points.Count];
            for (int i = 0; i < stamps.Length; i++)
            {
                stamps[i] = new DateTime(_points[i].StampUtcTicks, DateTimeKind.Utc);
            }
            return stamps;
        }

        /// <summary>Reads the last saved curve. Missing/corrupt/oversized files yield an empty history.</summary>
        public static NetHistory Load(string path)
        {
            NetHistory history = new NetHistory();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return history;

                FileInfo info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaxFileBytes) return history;

                string json = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
                if (json.Length == 0) return history;

                NetHistoryFile file;
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(NetHistoryFile));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    file = (NetHistoryFile)serializer.ReadObject(stream);
                }

                if (file == null || file.Version != CurrentVersion || file.Points == null) return history;

                // Sort defensively: a hand-edited or half-written file must not put a future
                // sample before an older one. Then dedupe and keep only the tail.
                List<NetHistoryPoint> points = new List<NetHistoryPoint>();
                foreach (NetHistoryPoint point in file.Points)
                {
                    if (point == null) continue;
                    if (point.StampUtcTicks <= 0 || point.StampUtcTicks > DateTime.MaxValue.Ticks) continue;
                    points.Add(point);
                }
                points.Sort((left, right) => left.StampUtcTicks.CompareTo(right.StampUtcTicks));

                long lastTicks = 0;
                int start = Math.Max(0, points.Count - Capacity);
                for (int i = start; i < points.Count; i++)
                {
                    NetHistoryPoint point = points[i];
                    if (point.StampUtcTicks == lastTicks) continue;
                    lastTicks = point.StampUtcTicks;
                    history._points.Add(new NetHistoryPoint
                    {
                        StampUtcTicks = point.StampUtcTicks,
                        Up = Sanitize(point.Up),
                        Down = Sanitize(point.Down)
                    });
                }
            }
            catch (Exception)
            {
                // A history file is a convenience; never let it block the dashboard.
                history._points.Clear();
            }
            return history;
        }

        /// <summary>Writes the current curve. Failures are ignored; the next save retries.</summary>
        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                NetHistoryFile file = new NetHistoryFile
                {
                    Version = CurrentVersion,
                    Points = new List<NetHistoryPoint>(_points)
                };

                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(NetHistoryFile));
                string json;
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, file);
                    json = Encoding.UTF8.GetString(stream.ToArray());
                }

                string temp = path + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(temp, path, null, true); }
                    catch (Exception)
                    {
                        File.Delete(path);
                        File.Move(temp, path);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception)
            {
                // Same rule as load: a failed save must not surface in the UI.
            }
        }

        /// <summary>Default location: %APPDATA%\QuadVitals\net-history.json.</summary>
        public static string DefaultPath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(appData, "QuadVitals"), "net-history.json");
            }
        }

        private static double Sanitize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return 0;
            return value > MaxRate ? MaxRate : value;
        }

        [DataContract]
        private sealed class NetHistoryFile
        {
            [DataMember(Name = "version")]
            public int Version { get; set; }

            [DataMember(Name = "points")]
            public List<NetHistoryPoint> Points { get; set; }
        }

        [DataContract]
        private sealed class NetHistoryPoint
        {
            [DataMember(Name = "t")]
            public long StampUtcTicks { get; set; }

            [DataMember(Name = "up")]
            public double Up { get; set; }

            [DataMember(Name = "down")]
            public double Down { get; set; }
        }
    }
}
