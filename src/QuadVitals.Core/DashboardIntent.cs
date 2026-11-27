using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// What a second dashboard launch asks the running window to show. The value is written into a
    /// session-local control block, so the numbering is part of that contract and no free text is
    /// allowed through: the receiving window only trusts the actions it offers in its own menus.
    /// </summary>
    public enum DashboardIntentKind
    {
        None = 0,
        Overview = 1,
        Elements = 2,
        About = 3,
        Feedback = 4,
        General = 5,
        AgentStatus = 6
    }

    /// <summary>
    /// Command line of the settings program turned into the action a running instance should take.
    /// Kept free of WPF types so the parsing can be asserted without opening a window.
    /// </summary>
    public static class DashboardIntent
    {
        /// <summary>Bypasses the single-instance guard for tools that need a window of their own.</summary>
        public const string NewInstanceSwitch = "--new-instance";

        public static bool IsNewInstance(string[] args)
        {
            return Has(args, NewInstanceSwitch);
        }

        public static bool Has(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Later switches win, so a menu that appends one flag to an existing command line still
        /// means what it says. Unknown pages fall back to the overview, like a first launch does.
        /// </summary>
        public static DashboardIntentKind Parse(string[] args)
        {
            DashboardIntentKind kind = DashboardIntentKind.None;
            if (args == null) return kind;
            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg)) continue;
                if (Equals(arg, "--feedback")) kind = DashboardIntentKind.Feedback;
                else if (Equals(arg, "--general")) kind = DashboardIntentKind.General;
                else if (Equals(arg, "--agent-status")) kind = DashboardIntentKind.AgentStatus;
                else if (Equals(arg, "--about")) kind = DashboardIntentKind.About;
                else if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                    kind = PageKind(arg.Substring("--page=".Length));
            }
            return kind;
        }

        /// <summary>Sidebar page for a start-up intent; panels stay on the overview behind them.</summary>
        public static string PageKey(DashboardIntentKind kind)
        {
            switch (kind)
            {
                case DashboardIntentKind.Elements: return "elements";
                case DashboardIntentKind.About: return "about";
                default: return "overview";
            }
        }

        public static DashboardIntentKind PageKind(string key)
        {
            if (string.Equals(key, "elements", StringComparison.OrdinalIgnoreCase))
                return DashboardIntentKind.Elements;
            if (string.Equals(key, "about", StringComparison.OrdinalIgnoreCase))
                return DashboardIntentKind.About;
            return DashboardIntentKind.Overview;
        }

        private static bool Equals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
