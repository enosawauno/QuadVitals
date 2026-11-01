using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

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
                Application app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (sender, e) =>
                {
                    Log(e.Exception);
                    MessageBox.Show("QuadVitals 设置窗口出错：\n\n" + e.Exception.Message +
                                    "\n\n详细信息已写入 %APPDATA%\\QuadVitals\\settings-crash.log",
                        "QuadVitals", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };

                string startPage = "overview";
                bool openAgentStatus = false;
                if (args != null)
                {
                    foreach (string arg in args)
                    {
                        if (string.Equals(arg, "--feedback", StringComparison.OrdinalIgnoreCase))
                        {
                            startPage = "feedback";
                        }
                        else if (string.Equals(arg, "--about", StringComparison.OrdinalIgnoreCase))
                        {
                            // Lets the About page be opened/screenshotted without driving the
                            // mouse through the sidebar (UI self-tests cannot touch the pointer).
                            startPage = "about";
                        }
                        else if (string.Equals(arg, "--agent-status", StringComparison.OrdinalIgnoreCase))
                        {
                            // Same reason as --about: the status panel has to be openable without
                            // driving the pointer, otherwise it cannot be screenshotted or tested.
                            openAgentStatus = true;
                        }
                        else if (arg != null && arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                        {
                            // --page=overview|elements|tools|network|feedback|about
                            startPage = arg.Substring("--page=".Length);
                        }
                    }
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

                SettingsWindow window = new SettingsWindow(startPage);
                if (openAgentStatus) window.Loaded += (sender, e) => window.ShowAgentStatus();
                app.Run(window);
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
