using System;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    internal sealed partial class SettingsWindow
    {
        /// <summary>
        /// A second launch asked for a page or a panel while this window was already open. Page
        /// requests close whatever panel is on top; otherwise the requested page would stay hidden
        /// behind it and the click would look ignored.
        /// </summary>
        public void ApplyIntent(DashboardIntentKind kind)
        {
            switch (kind)
            {
                case DashboardIntentKind.None:
                    return;
                case DashboardIntentKind.Overview:
                    ShowPageOverPanel("overview");
                    return;
                case DashboardIntentKind.Elements:
                    ShowPageOverPanel("elements");
                    return;
                case DashboardIntentKind.About:
                    ShowPageOverPanel("about");
                    return;
                case DashboardIntentKind.Feedback:
                    ShowPanelOverCurrent(ShowFeedback);
                    return;
                case DashboardIntentKind.General:
                    ShowPanelOverCurrent(ShowGeneralSettings);
                    return;
                case DashboardIntentKind.AgentStatus:
                    ShowPanelOverCurrent(ShowAgentStatus);
                    return;
            }
        }

        private void ShowPageOverPanel(string key)
        {
            if (IsOverlayOpen) CloseOverlay();
            ShowPage(key);
        }

        private void ShowPanelOverCurrent(Action show)
        {
            if (IsOverlayOpen) CloseOverlay();
            show();
        }
    }
}
