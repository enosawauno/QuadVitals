using System;
using System.Windows;
using System.Windows.Media;

namespace QuadVitals.UI
{
    /// <summary>
    /// The environment ceiling for animations. Three signals are reliable enough to act on:
    /// an RDP session (frames travel over the network), WPF tier 0 (no hardware acceleration,
    /// every frame is rasterised on the CPU), and battery when the user keeps that preference on.
    ///
    /// This is a ceiling, not the user's "when to render" choice: the dashboard still refreshes
    /// its values at the configured period. Only the per-frame loops and the easing snap are
    /// disabled. The focus/fullscreen policy lives here too so every frame loop reads the same
    /// answer instead of each one deciding for itself.
    /// </summary>
    public static class RenderEnvironment
    {
        private static bool _animationsDisabled;
        private static bool _pauseWhenUnfocused = true;
        private static bool _pauseInFullscreen;
        private static bool _fullscreenActive;
        private static bool _hasFullscreenOverride;
        private static bool _fullscreenOverride;
        private static bool _hasOverride;
        private static bool _override;
        private static bool _pauseOnBattery = true;
        private static PowerLineStatus? _powerOverride;

        static RenderEnvironment()
        {
            // Tier can change when an RDP session starts/ends or the driver resets. Remote-session
            // state is a system parameter, so it arrives through the same static notification.
            RenderCapability.TierChanged += (sender, e) => Refresh();
            SystemParameters.StaticPropertyChanged += (sender, e) =>
            {
                if (string.Equals(e.PropertyName, "IsRemoteSession", StringComparison.Ordinal)) Refresh();
            };
            Refresh();
        }

        /// <summary>True while per-frame animation and easing should not run.</summary>
        public static bool AnimationsDisabled
        {
            get { return _animationsDisabled; }
        }

        /// <summary>True when losing focus should stop per-frame work (the "失焦停止动画" policy).</summary>
        public static bool PauseWhenUnfocused
        {
            get { return _pauseWhenUnfocused; }
        }

        /// <summary>True when a foreground fullscreen window should stop per-frame work.</summary>
        public static bool PauseInFullscreen
        {
            get { return _pauseInFullscreen; }
        }

        /// <summary>True while the foreground window covers its whole monitor.</summary>
        public static bool FullscreenActive
        {
            get { return _fullscreenActive; }
        }

        /// <summary>Raised when the effective animation ceiling or the render policy changes.</summary>
        public static event Action Changed;

        /// <summary>Raised only when <see cref="FullscreenActive"/> flips.</summary>
        public static event Action FullscreenChanged;

        /// <summary>Re-reads the environment. Cheap enough for the dashboard's 1 Hz chip timer.</summary>
        public static void Refresh()
        {
            bool fullscreen = false;
            if (_pauseInFullscreen)
            {
                fullscreen = _hasFullscreenOverride ? _fullscreenOverride
                                                    : FullscreenDetector.IsForegroundFullscreen();
            }
            bool fullscreenChanged = fullscreen != _fullscreenActive;
            _fullscreenActive = fullscreen;

            bool disabled = fullscreen || (_hasOverride ? _override : Detect());
            bool disabledChanged = disabled != _animationsDisabled;
            _animationsDisabled = disabled;

            if (fullscreenChanged) Raise(FullscreenChanged);
            if (disabledChanged || fullscreenChanged) Raise(Changed);
        }

        /// <summary>
        /// Applies the user's render policy from settings. The two flags are independent so the
        /// caller never has to translate the three UI options into a single enum here.
        /// </summary>
        public static void ConfigureRenderPolicy(bool pauseWhenUnfocused, bool pauseInFullscreen)
        {
            bool changed = _pauseWhenUnfocused != pauseWhenUnfocused ||
                           _pauseInFullscreen != pauseInFullscreen;
            _pauseWhenUnfocused = pauseWhenUnfocused;
            _pauseInFullscreen = pauseInFullscreen;

            // Leaving the fullscreen policy must release the freeze immediately; the next Refresh()
            // would also do it, but the event tells the overview page which reason disappeared.
            if (!pauseInFullscreen && _fullscreenActive)
            {
                _fullscreenActive = false;
                Raise(FullscreenChanged);
            }

            // The focus gate lives in FrameLoop and cannot see a policy flip through
            // AnimationsDisabled (both "always" and "unfocused" leave it false), so policy changes
            // always notify even when the effective ceiling stayed the same.
            if (changed) Raise(Changed);
            Refresh();
        }

        /// <summary>
        /// The user preference for battery: true (default) pauses animations on battery; false lets
        /// the dashboard animate on battery again. RDP / tier 0 are never affected by this switch.
        /// </summary>
        public static void ConfigureBatteryPolicy(bool pauseOnBattery)
        {
            if (_pauseOnBattery == pauseOnBattery) return;
            _pauseOnBattery = pauseOnBattery;
            Refresh();
        }

        private static void Raise(Action handler)
        {
            if (handler != null) handler();
        }

        private static bool Detect()
        {
            if (SystemParameters.IsRemoteSession) return true;
            if ((RenderCapability.Tier >> 16) == 0) return true;
            PowerLineStatus power = _powerOverride ?? SystemParameters.PowerLineStatus;
            return _pauseOnBattery && power == PowerLineStatus.Offline;
        }

        /// <summary>Test seam: force the battery state without draining a real battery.</summary>
        internal static void SetPowerLineStatusForTests(PowerLineStatus? status)
        {
            _powerOverride = status;
            Refresh();
        }

        /// <summary>Test seam: force the ceiling without an actual RDP session or tier-0 adapter.</summary>
        internal static void SetOverrideForTests(bool? value)
        {
            _hasOverride = value.HasValue;
            _override = value.GetValueOrDefault();
            Refresh();
        }

        /// <summary>Test seam: answer "is a fullscreen app in front" without one on screen.</summary>
        internal static void SetFullscreenForTests(bool? value)
        {
            _hasFullscreenOverride = value.HasValue;
            _fullscreenOverride = value.GetValueOrDefault();
            Refresh();
        }
    }
}
