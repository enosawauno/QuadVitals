using System;
using System.Threading;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Network throughput on its own thread.
    ///
    /// Why: <c>NetworkInterface.GetAllNetworkInterfaces()</c> / <c>GetIPStatistics()</c> are
    /// blocking IP Helper calls with no latency bound. Measured under a 32-thread synthetic load
    /// one call took 80.9 s under the same load, which froze the 1 Hz publisher and
    /// turned every metric into "--" for that whole minute. design already requires slow
    /// sampling to stay out of the publisher's path; temperature already has its own worker, this is
    /// the same treatment for the network.
    ///
    /// The worker owns the sampler end to end, so <see cref="NetworkSampler"/> stays
    /// single-threaded: the publisher only reads a cached rate and never touches adapter state.
    /// </summary>
    internal sealed class NetworkWorker : IDisposable
    {
        private const int MinIntervalMs = 250;
        private const int MaxIntervalMs = 2500;

        /// <summary>A blocked IP Helper call cannot be interrupted; stop waiting after this.</summary>
        private const int StopJoinMs = 1500;

        private readonly NetworkSampler _sampler;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _gate = new object();

        private double _upBytesPerSec;
        private double _downBytesPerSec;
        private DateTime _sampleUtc = DateTime.MinValue;
        private string _adapterName = string.Empty;
        private string _selectorRequested;
        private string _pendingSelector;

        /// <summary>
        /// Bumped on every real selector change. A sample that started before the change must not
        /// commit its result, otherwise it resurrects the cache that <see cref="UpdateSelector"/>
        /// just invalidated and the network metric keeps showing the old adapter for a whole
        /// stale window.
        /// </summary>
        private int _selectorRevision;
        private int _intervalMs;
        private bool _disposed;

        public NetworkWorker(string selector, int intervalMs)
        {
            _sampler = new NetworkSampler(selector);
            _selectorRequested = string.IsNullOrEmpty(selector) ? "auto" : selector;
            _intervalMs = ClampInterval(intervalMs);
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "QuadVitals.Network"
            };
            _thread.Start();
        }

        public static int ClampInterval(int intervalMs)
        {
            if (intervalMs < MinIntervalMs) return MinIntervalMs;
            if (intervalMs > MaxIntervalMs) return MaxIntervalMs;
            return intervalMs;
        }

        public int IntervalMs { get { return _intervalMs; } }

        /// <summary>Name of the adapter the last sample actually read (may be empty).</summary>
        public string AdapterName
        {
            get { lock (_gate) { return _adapterName; } }
        }

        /// <summary>
        /// Hands a settings change to the worker thread. Applying it inline would race with the
        /// sampler's byte counters (the publisher thread must not mutate them).
        /// </summary>
        public void UpdateSelector(string selector)
        {
            string next = string.IsNullOrEmpty(selector) ? "auto" : selector;
            lock (_gate)
            {
                // Every settings save calls this, so only act when the selection really changed:
                // otherwise an unrelated edit (a colour, a width) would blank the network metric.
                if (string.Equals(next, _selectorRequested, StringComparison.OrdinalIgnoreCase)) return;

                _selectorRequested = next;
                _pendingSelector = next;
                _selectorRevision++;
                // The cached rate belongs to the *previous* adapter: hide it until the worker has a
                // sample for the new selection, instead of showing someone else's number for a
                // whole stale window.
                _sampleUtc = DateTime.MinValue;
                _adapterName = string.Empty;
            }
            _wake.Set();
        }

        /// <summary>Follows the global refresh period, i.e. the same rate window as before the
        /// sampling moved off the publisher thread; clamped so the window stays sane.</summary>
        public void SetInterval(int intervalMs)
        {
            int next = ClampInterval(intervalMs);
            if (next == _intervalMs) return;
            _intervalMs = next;
            _wake.Set();
        }

        /// <summary>
        /// Latest rate, or false when nothing recent is cached. The stale window is generous
        /// (3 x the sample interval, at least 3 s) because a rate needs a whole window to exist;
        /// when it does go stale only the network metric degrades, the rest keep publishing.
        /// </summary>
        public bool TryGet(DateTime nowUtc, out double bytesPerSecondUp, out double bytesPerSecondDown)
        {
            lock (_gate)
            {
                bytesPerSecondUp = _upBytesPerSec;
                bytesPerSecondDown = _downBytesPerSec;
                if (_sampleUtc == DateTime.MinValue) return false;
                int staleAfterMs = Math.Max(3000, _intervalMs * 3);
                return nowUtc - _sampleUtc <= TimeSpan.FromMilliseconds(staleAfterMs);
            }
        }

        private void Loop()
        {
            while (!_stop.IsSet)
            {
                string pending;
                int revision;
                lock (_gate)
                {
                    pending = _pendingSelector;
                    _pendingSelector = null;
                    revision = _selectorRevision;
                }
                if (!string.IsNullOrEmpty(pending)) _sampler.UpdateSelector(pending);

                try
                {
                    DateTime now = DateTime.UtcNow;
                    double up, down;
                    bool ok = _sampler.TrySample(now, out up, out down);
                    lock (_gate)
                    {
                        // Selector changed while this sample was in flight: drop it, it belongs to
                        // an adapter the user already switched away from.
                        if (revision == _selectorRevision)
                        {
                            _adapterName = _sampler.LastAdapterName ?? string.Empty;
                            if (ok)
                            {
                                _upBytesPerSec = up;
                                _downBytesPerSec = down;
                                _sampleUtc = now;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // A flaky IP Helper call must not kill the worker; the stale rule hides it.
                }

                try
                {
                    WaitHandle.WaitAny(new WaitHandle[] { _stop.WaitHandle, _wake }, _intervalMs);
                }
                catch (ObjectDisposedException)
                {
                    // Dispose() gave up waiting on a blocked IP Helper call and closed the handles;
                    // an unhandled exception on a background thread would take the process down.
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stop.Set();
            _wake.Set();
            bool stopped = _thread == null || !_thread.IsAlive;
            if (!stopped)
            {
                _thread.Join(StopJoinMs);
                stopped = !_thread.IsAlive;
            }

            // A blocked sampler cannot be interrupted. If it is still inside a call, leave the
            // handles alone: the worker may still be waiting on them and would throw.
            if (stopped)
            {
                _wake.Dispose();
                _stop.Dispose();
            }
        }
    }
}
