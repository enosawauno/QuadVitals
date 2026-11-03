using System;
using System.Threading;

namespace QuadVitals.Core
{
    /// <summary>
    /// Session-local control events for the sampling agent. The agent owns them; the dashboard
    /// uses the quit event to make a running instance clean up before starting a fresh one, and
    /// the activate event to surface the tray UI. Names live here so both sides cannot drift.
    ///
    /// These are not metrics channels: nothing is published through them, and the only payload
    /// is "wake up". Like the metrics mapping they are scoped to the current session, so one
    /// user's dashboard can never reach another user's agent.
    /// </summary>
    public static class AgentControl
    {
        public const string ActivateEventName = @"Local\QuadVitals.Agent.Activate.v1";
        public const string QuitEventName = @"Local\QuadVitals.Agent.Quit.v1";
        /// <summary>Asks the agent to replace itself: a fresh process rebuilds the sensor sessions.</summary>
        public const string RestartEventName = @"Local\QuadVitals.Agent.Restart.v1";

        /// <summary>Asks a running agent to show itself. False when no agent is running.</summary>
        public static bool RequestActivate()
        {
            return Signal(ActivateEventName);
        }

        /// <summary>
        /// Asks a running agent to exit. False when the event does not exist, which means the
        /// agent is already gone (or never started in this session).
        /// </summary>
        public static bool RequestQuit()
        {
            return Signal(QuitEventName);
        }

        /// <summary>
        /// Asks a running agent to hand over to a new process. Unlike quit, the successor is started by
        /// the agent itself before the old one exits, so the single-instance mutex stays clean and the
        /// caller does not need the logon task. False when no agent is listening.
        /// </summary>
        public static bool RequestRestart()
        {
            return Signal(RestartEventName);
        }

        /// <param name="eventName">
        /// Tests pass a private name so a probe can never quit the agent that is actually running.
        /// </param>
        internal static bool Signal(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            try
            {
                using (EventWaitHandle handle = EventWaitHandle.OpenExisting(eventName))
                {
                    return handle.Set();
                }
            }
            catch (Exception)
            {
                // Missing object, access denied, or a session that cannot be reached: the caller
                // only needs to know the request was not delivered.
                return false;
            }
        }
    }
}
