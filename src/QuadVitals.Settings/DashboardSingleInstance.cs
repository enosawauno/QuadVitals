using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Keeps one dashboard per session. A second launch writes what it wants into a small
    /// session-local block, wakes the running window and exits; that window restores itself and
    /// applies the request.
    ///
    /// The block carries an enum and a process id only. Two processes of the same user can already
    /// write the metrics mapping, so this channel adds no reach, and keeping free text out of it
    /// means it can never become a way to make the window do something its menus do not offer.
    ///
    /// The owner answers every wake-up with the acknowledgement event. A launch that never gets
    /// one treats the running window as hung and opens a window of its own instead of leaving the
    /// click without a result.
    /// </summary>
    internal sealed class DashboardSingleInstance : IDisposable
    {
        private const string MutexName = @"Local\QuadVitals.Settings.SingleInstance.v1";
        private const string ActivateEventName = @"Local\QuadVitals.Settings.Activate.v1";
        private const string AckEventName = @"Local\QuadVitals.Settings.ActivateAck.v1";
        private const string IntentMapName = @"Local\QuadVitals.Settings.Intent.v1";
        private const int IntentMapBytes = 8;
        private const int KindOffset = 0;
        private const int OwnerPidOffset = 4;
        private const int OwnerWaitMs = 2500;

        private const int SwRestore = 9;

        private readonly Mutex _mutex;
        private readonly EventWaitHandle _activate;
        private readonly EventWaitHandle _ack;
        private readonly MemoryMappedFile _intent;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly bool _isOwner;
        private Thread _watch;
        private volatile bool _disposed;

        private DashboardSingleInstance(Mutex mutex, bool isOwner, EventWaitHandle activate,
                                        EventWaitHandle ack, MemoryMappedFile intent)
        {
            _mutex = mutex;
            _isOwner = isOwner;
            _activate = activate;
            _ack = ack;
            _intent = intent;
        }

        public bool IsOwner { get { return _isOwner; } }

        /// <summary>
        /// Takes the session guard when it is free. Everything else (events, block) is created or
        /// opened either way, so a launch that lands while the first one is still building its
        /// window finds the channel instead of falling back to a second window.
        /// </summary>
        public static DashboardSingleInstance Acquire()
        {
            Mutex mutex = new Mutex(false, MutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing; the session still wants one window.
                owned = true;
            }

            DashboardSingleInstance instance = new DashboardSingleInstance(mutex, owned,
                CreateEvent(ActivateEventName), CreateEvent(AckEventName), CreateIntentMap());
            if (owned) instance.PublishOwner();
            return instance;
        }

        private static EventWaitHandle CreateEvent(string name)
        {
            try
            {
                return new EventWaitHandle(false, EventResetMode.AutoReset, name);
            }
            catch (Exception)
            {
                // A session that cannot create the event still runs; it just cannot be re-activated.
                return null;
            }
        }

        private static MemoryMappedFile CreateIntentMap()
        {
            try
            {
                return MemoryMappedFile.CreateOrOpen(IntentMapName, IntentMapBytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void PublishOwner()
        {
            if (_intent == null) return;
            try
            {
                using (MemoryMappedViewAccessor view = _intent.CreateViewAccessor())
                {
                    view.Write(OwnerPidOffset, Process.GetCurrentProcess().Id);
                }
            }
            catch (Exception)
            {
                // Without a pid the second launch can still wake us; it just cannot hand us focus.
            }
        }

        /// <summary>
        /// Second launch: write the request, wake the owner, wait for its acknowledgement.
        /// False means nobody answered (missing channel, hung window, or a session that vanished).
        /// </summary>
        public bool TryActivate(DashboardIntentKind kind)
        {
            if (_isOwner || _disposed || _activate == null) return false;

            int ownerPid = 0;
            try
            {
                if (_intent != null)
                {
                    using (MemoryMappedViewAccessor view = _intent.CreateViewAccessor())
                    {
                        view.Write(KindOffset, (int)kind);
                        ownerPid = view.ReadInt32(OwnerPidOffset);
                    }
                }
                System.Threading.Thread.MemoryBarrier();

                // The caller is the process the user just launched, so it is allowed to hand its
                // foreground right to the window it is about to wake.
                if (ownerPid > 0) AllowSetForegroundWindow((uint)ownerPid);

                if (_ack != null)
                {
                    try { _ack.Reset(); }
                    catch (Exception) { }
                }
                _activate.Set();
                return _ack == null || _ack.WaitOne(OwnerWaitMs);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Owner only: listen for wake-ups once the window exists.</summary>
        public void StartWatching(SettingsWindow window)
        {
            if (!_isOwner || window == null || _watch != null) return;
            _watch = new Thread(() => Watch(window))
            {
                IsBackground = true,
                Name = "QuadVitals.DashboardSingleInstance"
            };
            _watch.Start();
        }

        private void Watch(SettingsWindow window)
        {
            WaitHandle[] handles = { _stop.WaitHandle, _activate };
            while (!_disposed)
            {
                int signalled;
                try
                {
                    signalled = WaitHandle.WaitAny(handles, 500);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (_disposed) return;
                if (signalled != 1) continue;

                Thread.MemoryBarrier();
                DashboardIntentKind kind = ReadIntent();
                try
                {
                    window.Dispatcher.BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            RestoreAndActivate(window);
                            window.ApplyIntent(kind);
                        }
                        catch (Exception)
                        {
                            // A page that fails to build must not leave the caller waiting.
                        }
                        finally
                        {
                            Acknowledge();
                        }
                    }));
                }
                catch (Exception)
                {
                    // Dispatcher already shutting down.
                    Acknowledge();
                }
            }
        }

        private DashboardIntentKind ReadIntent()
        {
            if (_intent == null) return DashboardIntentKind.None;
            try
            {
                using (MemoryMappedViewAccessor view = _intent.CreateViewAccessor())
                {
                    int raw = view.ReadInt32(KindOffset);
                    if (raw < (int)DashboardIntentKind.None || raw > (int)DashboardIntentKind.AgentStatus)
                        return DashboardIntentKind.None;
                    return (DashboardIntentKind)raw;
                }
            }
            catch (Exception)
            {
                return DashboardIntentKind.None;
            }
        }

        private void Acknowledge()
        {
            if (_ack == null) return;
            try { _ack.Set(); }
            catch (Exception) { }
        }

        /// <summary>
        /// Brings the running window back: restore from minimize, then take the foreground. A
        /// refused foreground call is expected when another app owns it, so the topmost nudge is
        /// the fallback rather than a permanent always-on-top window.
        /// </summary>
        public static void RestoreAndActivate(Window window)
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                window.Activate();
                return;
            }

            ShowWindow(handle, SwRestore);
            if (!SetForegroundWindow(handle))
            {
                bool topmost = window.Topmost;
                window.Topmost = true;
                window.Topmost = false;
                window.Topmost = topmost;
                window.Activate();
            }
            window.Focus();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stop.Set();
            if (_activate != null)
            {
                try { _activate.Set(); }
                catch (Exception) { }
            }
            if (_watch != null && _watch.IsAlive) _watch.Join(1000);

            if (_isOwner)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
            _mutex.Dispose();
            if (_activate != null) _activate.Dispose();
            if (_ack != null) _ack.Dispose();
            if (_intent != null) _intent.Dispose();
            _stop.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);
    }
}
