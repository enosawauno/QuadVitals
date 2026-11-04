namespace QuadVitals.Core
{
    /// <summary>How the dashboard can start the agent, in the order it is allowed to try.</summary>
    public enum AgentLaunchMode
    {
        /// <summary>The logon task ran the exe: high integrity, no UAC prompt.</summary>
        LogonTask,
        /// <summary>No task: start the exe through the shell's "runas" verb, one UAC prompt.</summary>
        ElevatedShell,
        /// <summary>No task and no exe: there is nothing to start.</summary>
        MissingExe
    }

    /// <summary>
    /// Decides how a start request reaches the agent.
    ///
    /// Only the logon task and an elevated shell hand the agent an admin token. Without it the
    /// sensor driver cannot read MSR or the SuperIO chip, so CPU temperature and fan channels stay
    /// empty while every other metric looks healthy. A plain process start inherits the dashboard's
    /// own token, which is how a "restart sensor" button could come back with a running agent and
    /// no temperature at all.
    /// </summary>
    public static class AgentLaunchPlan
    {
        /// <summary>ERROR_CANCELLED: the owner dismissed the UAC prompt.</summary>
        public const int ErrorCancelled = 1223;

        /// <param name="logonTaskStarted">True when the logon task accepted the run request.</param>
        /// <param name="exePath">Resolved agent exe, or null when no copy was found.</param>
        public static AgentLaunchMode Decide(bool logonTaskStarted, string exePath)
        {
            if (logonTaskStarted) return AgentLaunchMode.LogonTask;
            if (string.IsNullOrEmpty(exePath)) return AgentLaunchMode.MissingExe;
            return AgentLaunchMode.ElevatedShell;
        }

        /// <summary>True when the chosen mode goes through the shell, i.e. may raise a UAC prompt.</summary>
        public static bool PromptsForElevation(AgentLaunchMode mode)
        {
            return mode == AgentLaunchMode.ElevatedShell;
        }

        public static bool IsElevationCancelled(int nativeErrorCode)
        {
            return nativeErrorCode == ErrorCancelled;
        }
    }
}
