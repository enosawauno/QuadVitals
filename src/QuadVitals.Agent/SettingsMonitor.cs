using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Watches settings.json while the agent runs so edits take effect without a restart
    ///. Cheap by design: stat the file twice a second and only parse
    /// when the write time changes. A missing, half-written or corrupt file keeps the last
    /// known-good settings instead of resetting the sampler to defaults.
    ///
    /// The pending value is handed to the publisher thread, which is the only thread that
    /// touches the sampling loop; the sampler itself lives on the network worker thread
    /// (NetworkWorker), so no lock is needed in either 1 Hz path.
    /// </summary>
    internal sealed class SettingsMonitor : IDisposable
    {
        private readonly string _path;
        private readonly int _pollMs;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly AutoResetEvent _changed = new AutoResetEvent(false);
        private readonly object _gate = new object();

        private DateTime _lastWriteUtc;
        private Settings _pending;
        private bool _disposed;

        public SettingsMonitor(string path = null, int pollMs = 500)
        {
            _path = string.IsNullOrEmpty(path) ? Settings.DefaultPath : path;
            _pollMs = pollMs < 100 ? 500 : pollMs;
            // Start "unknown" so the first poll applies whatever is on disk: Program loads the
            // file before LHM initialisation, and an edit during that window must not be missed.
            _lastWriteUtc = DateTime.MinValue;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "QuadVitals.Settings"
            };
            _thread.Start();
        }

        public string Path { get { return _path; } }

        /// <summary>Signalled when a new settings snapshot is waiting in <see cref="TryTake"/>.</summary>
        public WaitHandle WakeHandle { get { return _changed; } }

        /// <summary>Takes the newest parsed settings, if any. Called by the publisher thread.</summary>
        public bool TryTake(out Settings settings)
        {
            lock (_gate)
            {
                settings = _pending;
                _pending = null;
                return settings != null;
            }
        }

        private void Loop()
        {
            while (!_stop.IsSet)
            {
                try
                {
                    DateTime stamp = ReadStamp();
                    if (stamp != _lastWriteUtc)
                    {
                        if (stamp == DateTime.MinValue)
                        {
                            // No file: nothing to apply, and nothing to retry until it appears.
                            _lastWriteUtc = DateTime.MinValue;
                        }
                        else
                        {
                            Settings loaded;
                            List<string> warnings;
                            if (Settings.TryLoad(_path, out loaded, out warnings))
                            {
                                // Remember the stamp only after the file parsed. Recording it up
                                // front (the old order) meant a save that was caught half-written
                                // was marked as "seen", TryLoad failed, and that edit was dropped
                                // until something else wrote the file again - an intermittent
                                // "setting did nothing" with no error anywhere.
                                _lastWriteUtc = stamp;
                                lock (_gate) { _pending = loaded; }
                                _changed.Set();
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // A watcher must never take the agent down; try again on the next poll.
                }

                if (_stop.Wait(_pollMs)) return;
            }
        }

        private DateTime ReadStamp()
        {
            try
            {
                FileInfo info = new FileInfo(_path);
                return info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stop.Set();
            _changed.Set();
            if (_thread != null && _thread.IsAlive) _thread.Join(1000);
            _changed.Dispose();
            _stop.Dispose();
        }
    }
}
