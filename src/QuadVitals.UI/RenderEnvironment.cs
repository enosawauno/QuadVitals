using System;
using System.Windows;
using System.Windows.Media;

namespace QuadVitals.UI
{
    /// <summary>
    /// The environment ceiling for animations. Two signals are reliable enough to act on:
    /// an RDP session (frames travel over the network) and WPF tier 0 (no hardware acceleration,
    /// every frame is rasterised on the CPU). Battery and VM/GPC passthrough are deliberately not
    /// folded in here yet; they need a product decision, not a guess.
    ///
    /// This is a ceiling, not a user preference: the dashboard still refreshes its values at the
    /// configured period. Only the per-frame loops and the easing snap are disabled.
    /// </summary>
    public static class RenderEnvironment
    {
        private static bool _animationsDisabled;
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

        /// <summary>Raised when <see cref="AnimationsDisabled"/> flips.</summary>
        public static event Action Changed;

        /// <summary>Re-reads the environment. Cheap enough for the dashboard's 1 Hz chip timer.</summary>
        public static void Refresh()
        {
            bool disabled = _hasOverride ? _override : Detect();
            if (disabled == _animationsDisabled) return;
            _animationsDisabled = disabled;
            Action handler = Changed;
            if (handler != null) handler();
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

        private static bool Detect()
        {
            if (SystemParameters.IsRemoteSession) return true;
            if ((RenderCapability.Tier >> 16) == 0) return true;
            PowerLineStatus power = _powerOverride ?? SystemParameters.PowerLineStatus;
            return _pauseOnBattery && power == PowerLineStatus.Offline;
        }

        /// <summary>Test seam: force the ceiling without an actual RDP session or tier-0 adapter.</summary>
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
    }
}
