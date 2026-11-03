using System;

namespace QuadVitals.Settings
{
    /// <summary>
    /// How hard the dashboard tries to bring a dead agent back on its own.
    ///
    /// The chip used to be a read-only label: when the agent died the dashboard stayed empty until
    /// the next logon. Recovery now runs without a click, with two guards: one confirming tick (so
    /// a start at logon is not fought over) and a backoff, so a permanently broken agent cannot turn
    /// into a start-loop. When the attempts are used up the panel's manual button takes over.
    /// </summary>
    internal static class AgentAutoRecovery
    {
        /// <summary>Seconds to wait before each attempt: immediate, then 3 / 10 / 30 / 60.</summary>
        // 0 = 离线确认后立即拉起；之后按 3 / 5 / 10 / 30 秒退避，共五次。
        public static readonly int[] DelaysSeconds = new int[] { 0, 3, 5, 10, 30 };

        public static int MaxAttempts
        {
            get { return DelaysSeconds.Length; }
        }

        /// <summary>True when another automatic start should be issued now.</summary>
        public static bool ShouldAttempt(bool alive, int deadTicks, int attempts, DateTime nowUtc, DateTime nextAllowedUtc)
        {
            if (alive) return false;
            if (deadTicks < 2) return false;
            if (attempts < 0 || attempts >= DelaysSeconds.Length) return false;
            return nowUtc >= nextAllowedUtc;
        }

        /// <summary>Seconds to wait after the given number of attempts have already been made.</summary>
        public static int DelayAfterAttempt(int attempts)
        {
            int index = attempts;
            if (index < 0) index = 0;
            if (index >= DelaysSeconds.Length) index = DelaysSeconds.Length - 1;
            return DelaysSeconds[index];
        }
    }
}
