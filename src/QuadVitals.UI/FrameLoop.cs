using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace QuadVitals.UI
{
    /// <summary>
    /// One animation clock for the dashboard, shared by everything that needs per-frame work.
    ///
    /// Two things it defends against, both of which cost real power on a monitoring app that is
    /// supposed to sit still:
    ///
    /// 1. <c>CompositionTarget.Rendering</c> fires once per monitor refresh, not once per "frame we
    ///    want". On a 165 Hz panel that is 165 callbacks a second for a fan that turns 1.25 times a
    ///    second. The loop therefore drops every callback that arrives sooner than 1/maxFps, and
    ///    leaves the timestamp alone when it does, so the next vertical blank is accepted.
    /// 2. A minimized window keeps <c>UIElement.IsVisible</c> true in WPF, so "is the element
    ///    visible" is not the same question as "is it on screen". The loop unsubscribes entirely
    ///    while the hosting window is minimized or hidden and resubscribes on restore; a card that
    ///    keeps spinning behind a minimized dashboard is a bug, not a detail.
    ///
    /// Deliberately not handled: occlusion by another window. Asking the shell is unreliable
    /// (undo/minimize animations report transient states) and the worst case is the same work we
    /// did before, so it stays out.
    /// </summary>
    public sealed class FrameLoop : IDisposable
    {
        /// <summary>
        /// 30 Hz: the fan turns at most 1.25 times a second, so a frame is 15 degrees at worst, and
        /// the fill bars only ease towards a value that changes once a second. Doubling this would
        /// double the software-rasterised work for nothing the eye can see.
        /// </summary>
        public const int DefaultMaxFps = 30;

        /// <summary>A frame longer than this means the UI thread was blocked; treat it as 100 ms.</summary>
        private const double MaxStepSeconds = 0.1;

        private readonly FrameworkElement _owner;
        private readonly Action<double> _onTick;
        private readonly double _minFrameSeconds;
        private readonly EventHandler _renderHandler;
        private readonly EventHandler _stateChangedHandler;
        private readonly Action _environmentHandler;
        private readonly EventHandler _activatedHandler;
        private readonly EventHandler _deactivatedHandler;
        private readonly DependencyPropertyChangedEventHandler _visibilityHandler;

        private Window _window;
        private long _lastTicks;
        private bool _wantRunning;
        private bool _renderingHooked;
        private bool _everActivated;
        private bool _focusSuspended;
        private bool _disposed;

        /// <summary>Frames delivered by every loop in the process; the suites assert the cap.</summary>
        public static long TickCount;

        public FrameLoop(FrameworkElement owner, Action<double> onTick)
            : this(owner, onTick, DefaultMaxFps)
        {
        }

        public FrameLoop(FrameworkElement owner, Action<double> onTick, int maxFps)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (onTick == null) throw new ArgumentNullException("onTick");
            if (maxFps <= 0) maxFps = DefaultMaxFps;
            _owner = owner;
            _onTick = onTick;
            _minFrameSeconds = 1.0 / maxFps;
            _renderHandler = OnRendering;
            _stateChangedHandler = OnHostStateChanged;
            _environmentHandler = OnEnvironmentChanged;
            _activatedHandler = OnHostActivated;
            _deactivatedHandler = OnHostDeactivated;
            _visibilityHandler = OnHostVisibilityChanged;
        }

        /// <summary>True while callbacks are actually being delivered (not merely requested).</summary>
        public bool IsRunning
        {
            get { return _renderingHooked; }
        }

        /// <summary>Frames this loop delivered; a test seam for the frame cap.</summary>
        public long FramesDelivered { get; private set; }

        /// <summary>Asks for frames. Cheap to call repeatedly (the overview tick calls it every second).</summary>
        public void Start()
        {
            if (_disposed) return;
            if (!_wantRunning)
            {
                _wantRunning = true;
                _owner.IsVisibleChanged += _visibilityHandler;
                RenderEnvironment.Changed += _environmentHandler;
                HookWindow();
            }
            // Start() may be called again after a pause while the loop was already attached to
            // this window, in which case HookWindow() returns early; resync the focus gate here.
            if (_window != null)
            {
                if (_window.IsActive)
                {
                    _everActivated = true;
                    _focusSuspended = false;
                }
                else if (_everActivated)
                {
                    _focusSuspended = true;
                }
            }
            Refresh();
        }

        /// <summary>Stops for good until <see cref="Start"/> is called again (e.g. no data to animate).</summary>
        public void Stop()
        {
            if (!_wantRunning)
            {
                Unhook();
                return;
            }
            _wantRunning = false;
            _owner.IsVisibleChanged -= _visibilityHandler;
            RenderEnvironment.Changed -= _environmentHandler;
            UnhookWindow();
            Unhook();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }

        private bool CanRender()
        {
            if (RenderEnvironment.AnimationsDisabled) return false;
            if (_focusSuspended) return false;
            if (!_owner.IsVisible) return false;
            Window window = _window ?? Window.GetWindow(_owner);
            if (window == null) return true;              // not in a window yet: the owner decides
            if (!window.IsVisible) return false;
            return window.WindowState != WindowState.Minimized;
        }

        private void Refresh()
        {
            if (!_disposed && _wantRunning && CanRender()) Hook();
            else Unhook();
        }

        private void Hook()
        {
            if (_renderingHooked) return;
            _renderingHooked = true;
            _lastTicks = 0;
            CompositionTarget.Rendering += _renderHandler;
        }

        private void Unhook()
        {
            if (!_renderingHooked) return;
            _renderingHooked = false;
            CompositionTarget.Rendering -= _renderHandler;
            _lastTicks = 0;
        }

        private void HookWindow()
        {
            Window window = Window.GetWindow(_owner);
            if (ReferenceEquals(window, _window)) return;
            UnhookWindow();
            _window = window;
            if (_window == null) return;
            _window.StateChanged += _stateChangedHandler;
            _window.IsVisibleChanged += _visibilityHandler;
            _window.Activated += _activatedHandler;
            _window.Deactivated += _deactivatedHandler;

            // A window that was never activated (headless suites, a window shown with
            // ShowActivated=false) must keep its old behaviour. Once a real window has been
            // active, losing focus suspends every loop in it.
            if (_window.IsActive)
            {
                _everActivated = true;
                _focusSuspended = false;
            }
            else if (_everActivated)
            {
                _focusSuspended = true;
            }
        }

        private void UnhookWindow()
        {
            if (_window == null) return;
            _window.StateChanged -= _stateChangedHandler;
            _window.IsVisibleChanged -= _visibilityHandler;
            _window.Activated -= _activatedHandler;
            _window.Deactivated -= _deactivatedHandler;
            _window = null;
        }

        private void OnHostActivated(object sender, EventArgs e)
        {
            _everActivated = true;
            _focusSuspended = false;
            if (_wantRunning) HookWindow();
            Refresh();
        }

        private void OnHostDeactivated(object sender, EventArgs e)
        {
            // Focus-aware pause: the window is still visible, so the old IsVisible check would
            // keep every frame loop running behind another app.
            if (!_everActivated) return;
            _focusSuspended = true;
            Refresh();
        }

        private void OnEnvironmentChanged()
        {
            if (_wantRunning) Refresh();
        }

        private void OnHostStateChanged(object sender, EventArgs e)
        {
            // A window can be created, shown, minimized and restored after Start(); the tree may
            // also have moved to another window, so resolve it again before deciding.
            if (_wantRunning) HookWindow();
            Refresh();
        }

        private void OnHostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_wantRunning) HookWindow();
            Refresh();
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (!CanRender())
            {
                // State that never raised an event (a window destroyed underneath us, a page hidden
                // through a parent) still has to stop the loop.
                Unhook();
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (_lastTicks == 0)
            {
                _lastTicks = now;
                return;
            }
            double elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (elapsed < _minFrameSeconds) return;       // throttle; next vsync will pass
            _lastTicks = now;
            if (elapsed > MaxStepSeconds) elapsed = MaxStepSeconds;

            TickCount++;
            FramesDelivered++;
            _onTick(elapsed);
        }
    }
}
