using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    /// <summary>
    /// The "check when the dashboard opens" trigger.
    ///
    /// The app is resident, so there is no second launch to hang the check on; opening the
    /// dashboard is the only moment someone is looking. The check therefore runs at most once a
    /// day (remembered in <see cref="UpdateCheckCache"/>), stays silent on failure, and only
    /// raises the dialog when the feed offers something newer than the running version and the
    /// owner has not skipped it.
    ///
    /// Network work stays in this process: the agent and the taskbar band never make requests.
    /// </summary>
    internal static class UpdateAutoCheck
    {
        /// <summary>Set by the UI regression suite and by screenshots so a test never opens a socket.</summary>
        private const string DisableVariable = "QUADVITALS_NO_UPDATE_CHECK";

        private static bool _attemptedThisProcess;

        public static void Begin(SettingsWindow window)
        {
            if (window == null) return;
            if (Environment.GetEnvironmentVariable(DisableVariable) == "1") return;
            if (_attemptedThisProcess) return;
            _attemptedThisProcess = true;

            string cachePath = UpdateCheckCache.DefaultPath();
            UpdateCheckCache cache = UpdateCheckCache.Load(cachePath);
            if (!cache.ShouldCheck(DateTime.UtcNow)) return;

            Task.Run(() =>
            {
                try
                {
                    UpdateCheckOutcome outcome = UpdateChecker.Check(
                        new HttpUpdateTransport(), UpdateFeed.BaseUrl(), ProductVersion.SemVer);

                    // A failed check is not news: the About page explains it when asked, and the
                    // next open retries because nothing is recorded.
                    if (outcome.Status == UpdateCheckStatus.Failed) return;

                    cache.Record(outcome.LatestVersion, DateTime.UtcNow);
                    cache.Save(cachePath);

                    if (outcome.Status != UpdateCheckStatus.Available) return;
                    if (outcome.Manifest == null) return;
                    if (!cache.ShouldOffer(outcome.Manifest.Version, ProductVersion.SemVer)) return;

                    Dispatcher dispatcher = window.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted) return;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        // A panel the owner is already working in must not be replaced by a prompt.
                        if (!window.IsLoaded) return;
                        if (window.IsOverlayOpen) return;

                        UpdateDialog.Present(window, outcome.Manifest, () =>
                        {
                            cache.Ignore(outcome.Manifest.Version);
                            cache.Save(cachePath);
                        });
                    }));
                }
                catch (Exception)
                {
                    // Offline, proxy down, TLS refused: all the same here, and none is an error state.
                }
            });
        }
    }
}
