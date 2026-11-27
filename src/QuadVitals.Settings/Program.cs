using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // A WPF app that dies silently is impossible to debug from screenshots, so every
            // unhandled exception leaves a breadcrumb next to the agent's crash log.
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Log(e.ExceptionObject as Exception);
            Dispatcher.CurrentDispatcher.UnhandledException += (sender, e) =>
            {
                Log(e.Exception);
                e.Handled = false;
            };

            try
            {
                DashboardIntentKind intent = DashboardIntent.Parse(args);
                bool openDiagnostics = DashboardIntent.Has(args, "--diagnostics");
                bool forceNew = DashboardIntent.IsNewInstance(args);
#if QV_DEV_TOOLS
                bool updatePreview = DashboardIntent.Has(args, "--update-preview");
                // Screenshot entry: it needs its own window even when one is already open.
                if (updatePreview) forceNew = true;
#endif

                // The diagnostics card is not the dashboard. Keeping it outside the session guard
                // means a tray click cannot become "the one instance" and leave a later taskbar
                // double click with a window that has no pages.
                DashboardSingleInstance guard = null;
                if (!openDiagnostics)
                {
                    guard = DashboardSingleInstance.Acquire();
                    if (!guard.IsOwner)
                    {
                        if (!forceNew && guard.TryActivate(intent))
                        {
                            guard.Dispose();
                            return;
                        }

                        // No answer: the owner may have exited between the wake and the wait. Take
                        // the guard if it is free now; otherwise open one window anyway so a hung
                        // dashboard cannot swallow the click.
                        guard.Dispose();
                        guard = DashboardSingleInstance.Acquire();
                        if (!guard.IsOwner)
                        {
                            guard.Dispose();
                            guard = null;
                        }
                    }
                }

                Application app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (sender, e) =>
                {
                    Log(e.Exception);
                    MessageBox.Show("QuadVitals 设置窗口出错：\n\n" + e.Exception.Message +
                                    "\n\n详细信息已写入 %APPDATA%\\QuadVitals\\settings-crash.log",
                        "QuadVitals", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };

                if (openDiagnostics)
                {
                    app.Run(new DiagnosticsWindow());
                    return;
                }

                // design：每次启动补传上次没发出去的反馈（成功即删本地包，失败保留）。
                // 放到后台线程，不拖慢窗口显示。
                Task.Run(() =>
                {
                    try
                    {
                        string dataDirectory = Path.GetDirectoryName(QuadVitals.Core.Settings.DefaultPath);
                        QuadVitals.Core.FeedbackRecovery.Flush(dataDirectory, new QuadVitals.Core.HttpFeedbackTransport());
                    }
                    catch (Exception)
                    {
                        // 补传失败不能影响设置窗口
                    }
                });

                SettingsWindow window = new SettingsWindow(DashboardIntent.PageKey(intent));
                if (intent == DashboardIntentKind.Feedback)
                    window.Loaded += (sender, e) => window.ShowFeedback();
                if (intent == DashboardIntentKind.General)
                    window.Loaded += (sender, e) => window.ShowGeneralSettings();
                if (intent == DashboardIntentKind.AgentStatus)
                    window.Loaded += (sender, e) => window.ShowAgentStatus();
                DashboardSingleInstance watcher = guard;
                if (watcher != null) window.Loaded += (sender, e) => watcher.StartWatching(window);
#if QV_DEV_TOOLS
                if (updatePreview)
                {
                    window.SuppressUpdateCheck = true;
                    window.Loaded += (sender, e) => UpdateDialog.ShowPreview(window);
                }
#endif
                app.Run(window);
                if (guard != null) guard.Dispose();
            }
            catch (Exception ex)
            {
                Log(ex);
                throw;
            }
        }

        private static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuadVitals");
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "settings-crash.log"),
                    DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine);
            }
            catch (Exception)
            {
                // logging must never mask the original failure
            }
        }
    }
}
