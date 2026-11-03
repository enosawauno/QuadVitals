using System;
using System.Threading;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Single-instance guard with an activation channel: launching the agent again does
    /// not start a second sampler, it pokes the running instance instead (bring up the tray UI /
    /// eventually the dashboard) and exits.
    ///
    /// Names are session-local ("Local\"), like the metrics channel: the agent is per-user.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private const string MutexName = @"Local\QuadVitals.Agent.SingleInstance.v1";

        private readonly Mutex _mutex;
        private readonly EventWaitHandle _activate;
        private readonly EventWaitHandle _quit;
        private readonly bool _isOwner;
        private Thread _watch;
        private bool _disposed;

        private SingleInstance(Mutex mutex, EventWaitHandle activate, EventWaitHandle quit, bool isOwner)
        {
            _mutex = mutex;
            _activate = activate;
            _quit = quit;
            _isOwner = isOwner;
        }

        public bool IsOwner { get { return _isOwner; } }

        /// <summary>Raised (on a worker thread) when another launch asked us to show ourselves.</summary>
        public event Action ActivateRequested;

        /// <summary>Raised (on a worker thread) when another launch asked us to quit.</summary>
        public event Action QuitRequested;

        public static SingleInstance Acquire()
        {
            Mutex mutex = new Mutex(false, MutexName);
            EventWaitHandle activate = new EventWaitHandle(false, EventResetMode.AutoReset,
                AgentControl.ActivateEventName);
            EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset,
                AgentControl.QuitEventName);

            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner died without releasing; we inherit the instance.
                owned = true;
            }

            return new SingleInstance(mutex, activate, quit, owned);
        }

        /// <summary>Owner only: start listening for activate/quit pokes.</summary>
        public void StartWatching()
        {
            if (!_isOwner || _watch != null) return;
            _watch = new Thread(Watch)
            {
                IsBackground = true,
                Name = "QuadVitals.SingleInstance"
            };
            _watch.Start();
        }

        private void Watch()
        {
            WaitHandle[] handles = { _activate, _quit };
            while (true)
            {
                int index = WaitHandle.WaitAny(handles, 500);
                if (_disposed) return;
                if (index == 0)
                {
                    Action handler = ActivateRequested;
                    if (handler != null) handler();
                }
                else if (index == 1)
                {
                    Action handler = QuitRequested;
                    if (handler != null) handler();
                    return;
                }
                else if (index == WaitHandle.WaitTimeout)
                {
                    continue;
                }
            }
        }

        /// <summary>Second instance: ask the running one to surface, then leave.</summary>
        public static bool PokeActivate()
        {
            return AgentControl.RequestActivate();
        }

        /// <summary>Second instance: ask the running one to exit (used by --exit and uninstall).</summary>
        public static bool PokeQuit()
        {
            return AgentControl.RequestQuit();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_watch != null && _watch.IsAlive) _watch.Join(1000);
            if (_isOwner)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
            _mutex.Dispose();
            _activate.Dispose();
            _quit.Dispose();
        }
    }
}
