using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Owns the sampling loop and the shared-memory writer, independent of how the agent is
    /// hosted (console probe or tray app). One thread publishes at the configured period; the
    /// tick only composes cached values, so a slow sensor never stretches the interval.
    /// </summary>
    internal sealed class AgentRuntime : IDisposable
    {
        private readonly AgentLoop _loop;
        private readonly MetricsChannel.Writer _writer;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        // 面板上的「刷新」：被 Set 时立刻走下一轮发布，不等剩下的周期。
        private readonly EventWaitHandle _refresh = CreateRefreshEvent();
        // 强制发布的间隔下限：同会话任意进程都能 Set 这个事件，不设下限就变成一个发布循环。
        private DateTime _lastForcedPublishUtc = DateTime.MinValue;
        private const int ForcedPublishFloorMs = 200;
        private readonly object _gate = new object();
        private readonly SettingsMonitor _monitor;
        private readonly bool _followSettingsPeriod;
        private readonly SlowTickLog _slowLog = new SlowTickLog();
        private volatile int _periodMs;

        private MetricsSnapshot _last;
        private int _ticks;
        private int _settingsRevision;
        private bool _disposed;

        /// <param name="followSettingsPeriod">
        /// False when the caller pinned the period explicitly (the --interval probe flag), so a
        /// settings edit only changes the adapter and not the probe cadence.
        /// </param>
        public AgentRuntime(AgentLoop loop, int periodMs, SettingsMonitor monitor = null,
                            bool followSettingsPeriod = true)
        {
            if (loop == null) throw new ArgumentNullException(nameof(loop));
            _loop = loop;
            _monitor = monitor;
            _followSettingsPeriod = followSettingsPeriod;
            _periodMs = ClampPeriod(periodMs);
            _writer = new MetricsChannel.Writer();
            _thread = new Thread(PublishLoop)
            {
                IsBackground = true,
                Name = "QuadVitals.Publish",
                // The band turns into "--" when a frame is older than 3 s. At Normal priority the
                // scheduler can keep this thread off a CPU for seconds when every core is busy
                // (measured 3-6 s under a 32-thread load), so the
                // heartbeat gets the same small bump Windows gives its own UI timers.
                Priority = ThreadPriority.Highest
            };
        }

        /// <summary>
        /// The event is session-local. The explicit DACL adds nothing against other processes of the
        /// same user — they can already write the shared memory directly, and an ACE cannot tell two
        /// processes of one SID apart. What actually keeps this handle from becoming a publish loop
        /// is the forced-publish floor in the publish loop; the fallback constructor is fine when the
        /// ACL APIs are unavailable.
        /// </summary>
        private static EventWaitHandle CreateRefreshEvent()
        {
            try
            {
                EventWaitHandleSecurity security = new EventWaitHandleSecurity();
                SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
                if (user != null)
                {
                    security.AddAccessRule(new EventWaitHandleAccessRule(
                        user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
                    bool created;
                    return new EventWaitHandle(false, EventResetMode.AutoReset,
                        MetricsChannel.RefreshEventName, out created, security);
                }
            }
            catch (Exception)
            {
                // ACL APIs can be unavailable on constrained systems; the plain handle still works.
            }
            return new EventWaitHandle(false, EventResetMode.AutoReset, MetricsChannel.RefreshEventName);
        }

        private static int ClampPeriod(int periodMs)
        {
            if (periodMs < Settings.MinRefreshMs) return Settings.DefaultRefreshMs;
            if (periodMs > Settings.MaxRefreshMs) return Settings.MaxRefreshMs;
            return periodMs;
        }

        /// <summary>Raised on the publishing thread after every snapshot is written.</summary>
        public event Action<MetricsSnapshot> Ticked;

        public int TickCount { get { lock (_gate) { return _ticks; } } }

        public int PeriodMs { get { return _periodMs; } }

        /// <summary>Increments every time a settings edit was applied without a restart.</summary>
        public int SettingsRevision { get { lock (_gate) { return _settingsRevision; } } }

        public string TemperatureDiagnostic
        {
            get
            {
                TemperatureWorker worker = _loop.Temperature;
                if (worker == null) return "未配置";
                if (!worker.HasSource) return "LHM 不可用";
                double celsius;
                string reason;
                return worker.TryGet(DateTime.UtcNow, TimeSpan.FromSeconds(6), out celsius, out reason)
                    ? "可用"
                    : (reason ?? "无数据");
            }
        }

        public MetricsSnapshot Last
        {
            get { lock (_gate) { return _last; } }
        }

        public void Start()
        {
            _thread.Start();
        }

        private void PublishLoop()
        {
            while (!_stop.IsSet)
            {
                DateTime startedAt = DateTime.UtcNow;
                TickTimings timings = new TickTimings();
                MetricsSnapshot published = null;
                int overrunMs = int.MinValue;   // int.MinValue = no wait happened this round

                try
                {
                    // Inside the try: a bad settings application must not kill publishing.
                    ApplyPendingSettings();

                    MetricsSnapshot snapshot = _loop.Tick(startedAt);
                    timings = _loop.LastTimings;
                    _writer.Publish(snapshot);
                    published = snapshot;
                    lock (_gate)
                    {
                        _last = snapshot;
                        _ticks++;
                    }

                    Action<MetricsSnapshot> handler = Ticked;
                    if (handler != null) handler(snapshot);
                }
                catch (Exception ex)
                {
                    // Never let one bad tick kill the loop; the band shows "--" when stale.
                    Console.Error.WriteLine("tick 失败: " + ex.GetType().Name + ": " + ex.Message);
                }

                int spent = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                int wait = _periodMs - spent;
                if (wait < 50) wait = 50;

                DateTime waitStartedAt = DateTime.UtcNow;
                bool wokeForSettings = false;
                {
                    // Three wake-ups: stop, a settings edit (so a 5 s -> 1 s change lands now) and the
                    // dashboard's refresh button (publish one extra frame immediately).
                    WaitHandle[] handles = _monitor != null
                        ? new WaitHandle[] { _stop.WaitHandle, _monitor.WakeHandle, _refresh }
                        : new WaitHandle[] { _stop.WaitHandle, _refresh };
                    int signalled;
                    try
                    {
                        signalled = WaitHandle.WaitAny(handles, wait);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Dispose() stopped waiting after two seconds and closed the handles under a
                        // tick that is still finishing; an exception here would end the process.
                        return;
                    }
                    if (signalled == 0) return;
                    wokeForSettings = _monitor != null && signalled == 1;

                    bool refresh = _monitor != null ? signalled == 2 : signalled == 1;
                    if (refresh)
                    {
                        // Flood guard: anyone in the session can set the event, so cap forced
                        // publishes at one per floor. Too soon means "finish the original wait".
                        int since = (int)(DateTime.UtcNow - _lastForcedPublishUtc).TotalMilliseconds;
                        if (since >= 0 && since < ForcedPublishFloorMs)
                        {
                            _stop.WaitHandle.WaitOne(ForcedPublishFloorMs - since);
                            if (_stop.IsSet) return;
                        }
                        _lastForcedPublishUtc = DateTime.UtcNow;
                    }
                }

                if (!wokeForSettings)
                {
                    // Negative means the wait ended early; a large positive value means the wait
                    // had already expired but this thread still was not scheduled (starvation).
                    overrunMs = (int)(DateTime.UtcNow - waitStartedAt).TotalMilliseconds - wait;
                }

                _slowLog.Report(startedAt, spent, wait, overrunMs, timings, published);
            }
        }

        private void ApplyPendingSettings()
        {
            if (_monitor == null) return;

            Settings changed;
            if (!_monitor.TryTake(out changed)) return;

            if (_followSettingsPeriod) _periodMs = ClampPeriod(changed.RefreshMs);
            _loop.ApplySettings(changed);

            lock (_gate) { _settingsRevision++; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stop.Set();
            _refresh.Set();   // 唤醒可能正卡在 WaitAny 上的发布线程
            if (_thread != null && _thread.IsAlive) _thread.Join(2000);
            _refresh.Dispose();
            _stop.Dispose();
            _writer.Dispose();
            _loop.Dispose();
        }
    }
}
