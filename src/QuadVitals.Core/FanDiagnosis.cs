namespace QuadVitals.Core
{
    /// <summary>
    /// What a board's fan readings actually mean.
    ///
    /// Every enumerated channel goes into the snapshot, including the ones reading 0 RPM: a stopped
    /// fan and an unread fan header look identical at the register level, so hiding zeros hides the
    /// one failure a monitoring tool exists to show. The reader decides what to display, which is
    /// why "no channel at all" and "channels but all stopped" have to stay distinguishable here.
    ///
    /// Pure text and pure predicates, kept in Core so the wording is one place and can be asserted
    /// without starting the agent (which needs a driver and administrator rights).
    /// </summary>
    public static class FanDiagnosis
    {
        public const int MaxPlausibleRpm = 20000;

        /// <summary>True when the board exposed fan channels at all.</summary>
        public static bool HasChannels(int channelsSeen)
        {
            return channelsSeen > 0;
        }

        /// <summary>True when at least one channel is turning.</summary>
        public static bool AnySpinning(int channelsSeen, int spinning)
        {
            return HasChannels(channelsSeen) && spinning > 0;
        }

        /// <summary>
        /// One sentence for the temperature log when every channel reads 0; null when there is
        /// nothing to explain (no channels, or at least one is turning).
        /// </summary>
        public static string ExplainAllZero(int channelsSeen, int spinning)
        {
            if (!HasChannels(channelsSeen) || spinning > 0) return null;
            return "主板枚举到 " + channelsSeen +
                   " 路风扇通道但读数全为 0（风扇可能确实停转、通道被其它硬件监控软件占用，或风扇接在控制器上）";
        }

        /// <summary>Plausibility gate for one channel: NaN/infinity/negative/absurd are dropped, 0 is kept.</summary>
        public static bool IsPlausible(double rpm)
        {
            return !double.IsNaN(rpm) && !double.IsInfinity(rpm) && rpm >= 0 && rpm <= MaxPlausibleRpm;
        }
    }
}
