using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace QuadVitals.Setup
{
    /// <summary>
    /// Entry point of the single-file installer.
    ///
    /// Default: the wizard (src/QuadVitals.Setup/SetupWindow.cs). The payload is the release zip
    /// embedded as a resource, and the install itself is the same <c>scripts\install.ps1</c> the
    /// repo tests, run hidden — shipping a second install path is how an installer starts to drift
    /// from the thing that was verified.
    ///
    /// Switch conventions: this exe's own flags are <c>--word</c>, everything else is handed to
    /// install.ps1 (<c>-Task</c> / <c>-NoTask</c> / <c>-InstallDir</c>), so the two never collide.
    /// </summary>
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        private const int AttachParentProcess = -1;
        private static string _version = "0.0.0";

        [STAThread]
        private static int Main(string[] args)
        {
            _version = typeof(Program).Assembly.GetName().Version.ToString(3);
            ResolveEmbeddedAssemblies();

            bool silent = HasFlag(args, "/S") || HasFlag(args, "--silent");
            // The copy installed next to the product is called QuadVitals-Uninstall.exe; starting it by
            // name must go straight to uninstall, the way unins000.exe does for other installers.
            bool uninstall = HasFlag(args, "--uninstall") ||
                             SelfPath().IndexOf("QuadVitals-Uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
            bool console = HasFlag(args, "--help") || HasFlag(args, "--version") ||
                           HasFlag(args, "--extract-only") || HasFlag(args, "--screenshot");

            if (console) AttachParentConsole();

            if (HasFlag(args, "--help"))
            {
                PrintHelp();
                return 0;
            }
            if (HasFlag(args, "--version"))
            {
                // The product version does not change between builds, so a build stamp is the only way to answer
                // "is the file I am looking at the one that was just fixed?" without comparing hashes by hand.
                Console.WriteLine("QuadVitals Setup " + _version + "  构建 " + BuildStamp());
                return 0;
            }

            if (HasFlag(args, "--extract-only"))
            {
                InstallerCore core = new InstallerCore(_version);
                string root = core.ExtractPayload();
                Console.WriteLine("QuadVitals Setup " + _version);
                Console.WriteLine("  解包目录 : " + root);
                Console.WriteLine("  已按 --extract-only 停止（未执行安装）");
                return 0;
            }

            if (silent) return RunSilent(args, uninstall);

            // --screenshot <png>: render the window to a file instead of showing it. Screen capture
            // of this window depends on what else happens to be on the desktop (a terminal in front
            // produces a screenshot of the terminal), so the UI check renders the visual tree itself.
            if (HasFlag(args, "--screenshot"))
            {
                return RenderScreenshot(args, uninstall);
            }

            try
            {
                Application application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                SetupWindow window = new SetupWindow(_version, uninstall, ReadArg(args, "--preview"));
                application.Run(window);
                return window.ExitCode;
            }
            catch (Exception ex)
            {
                // A crash in a GUI installer has nowhere to print; leave a breadcrumb next to %TEMP%
                // and let the exit code say it failed.
                LogCrash(ex);
                return 1;
            }
        }

        /// <summary>
        /// Renders the wizard to a PNG and exits. Development aid only: it never installs anything.
        /// </summary>
        private static int RenderScreenshot(string[] args, bool uninstall)
        {
            string path = ReadArg(args, "--screenshot");
            if (string.IsNullOrEmpty(path))
            {
                path = Path.Combine(Path.GetTempPath(), "QuadVitals-Setup-preview.png");
            }

            try
            {
                Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SetupWindow window = new SetupWindow(_version, uninstall, ReadArg(args, "--preview"));
                window.ContentRendered += (sender, e) =>
                {
                    window.SaveScreenshot(path);
                    Console.WriteLine("saved " + path);
                    application.Shutdown();
                };
                application.Run(window);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("screenshot failed: " + ex.GetType().Name + ": " + ex.Message);
                LogCrash(ex);
                return 1;
            }
        }

        /// <summary>
        /// /S: no window at all (scripted installs, and the QuietUninstallString in the uninstall
        /// entry). Prints to the calling console when there is one, so a check can read the result.
        /// </summary>
        private static int RunSilent(string[] args, bool uninstall)
        {
            AttachParentConsole();
            Console.WriteLine("QuadVitals Setup " + _version + (uninstall ? "（静默卸载）" : "（静默安装）"));

            InstallerCore core = new InstallerCore(_version);
            core.OnStatus = stage => Console.WriteLine("  " + stage);

            string installDir = ReadArg(args, "-InstallDir") ?? InstallerCore.DefaultInstallDir;
            // Autostart is part of the product (an unelevated agent cannot read CPU temperature), so the
            // silent path creates it unless the caller explicitly opts out.
            bool createTask = !HasFlag(args, "-NoTask");
            bool startAgent = !HasFlag(args, "-NoStart");
            bool desktopShortcut = !HasFlag(args, "-NoShortcut");
            bool keepSettings = !HasFlag(args, "-RemoveSettings");

            string payloadRoot = null;
            try
            {
                payloadRoot = core.ExtractPayload();
                InstallResult result = uninstall
                    ? core.Uninstall(payloadRoot, installDir, keepSettings)
                    : core.Install(payloadRoot, installDir, createTask, startAgent, desktopShortcut);

                if (!result.Succeeded)
                {
                    Console.WriteLine("失败：" + result.Error);
                    if (result.ShowLog) Console.WriteLine(core.LogText);
                    return 1;
                }

                Console.WriteLine(uninstall ? "卸载完成。" : "安装完成：" + installDir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("异常：" + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
            finally
            {
                if (payloadRoot != null) core.TryDeletePayload(payloadRoot);
            }
        }

        // ---- embedded assemblies -------------------------------------------------------

        /// <summary>
        /// QuadVitals.UI (and the Core it depends on) are compiled in as resources so the installer
        /// stays one file while still using the product's own palette, buttons and check boxes.
        /// Both are plain code assemblies with no XAML, so loading them from memory is safe.
        /// </summary>
        private static void ResolveEmbeddedAssemblies()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                try
                {
                    AssemblyName requested = new AssemblyName(eventArgs.Name);
                    string name = requested.Name;
                    if (!string.Equals(name, "QuadVitals.UI", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "QuadVitals.Core", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    using (Stream stream = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream(name + ".dll"))
                    {
                        if (stream == null) return null;
                        byte[] raw = new byte[stream.Length];
                        int read = 0;
                        while (read < raw.Length)
                        {
                            int chunk = stream.Read(raw, read, raw.Length - read);
                            if (chunk <= 0) break;
                            read += chunk;
                        }
                        return Assembly.Load(raw);
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            };
        }

        // ---- mark of the web -----------------------------------------------------------

        private static string SelfPath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch (Exception) { return Assembly.GetExecutingAssembly().Location; }
        }

        /// <summary>Local write time of this exe, shown by --version.</summary>
        private static string BuildStamp()
        {
            try { return File.GetLastWriteTime(SelfPath()).ToString("yyyy-MM-dd HH:mm"); }
            catch (Exception) { return "unknown"; }
        }

        // ---- plumbing ------------------------------------------------------------------

        private static void LogCrash(Exception ex)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "QuadVitals-Setup-crash.log");
                File.AppendAllText(path, DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine);
            }
            catch (Exception)
            {
                // never mask the original failure
            }
        }

        private static bool HasFlag(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string ReadArg(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        private static void AttachParentConsole()
        {
            AttachConsole(AttachParentProcess);
            try
            {
                Stream stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // No console to attach to (double-clicked): stay silent.
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("QuadVitals Setup " + _version);
            Console.WriteLine();
            Console.WriteLine("  QuadVitals-Setup-" + _version + ".exe                安装向导");
            Console.WriteLine("  QuadVitals-Setup-" + _version + ".exe --uninstall    卸载向导");
            Console.WriteLine("  QuadVitals-Setup-" + _version + ".exe /S             静默安装（脚本/自动化）");
            Console.WriteLine("  QuadVitals-Setup-" + _version + ".exe --uninstall /S 静默卸载");
            Console.WriteLine();
            Console.WriteLine("  透传给 install.ps1 的参数：-Task / -NoTask / -NoStart / -NoShortcut / -InstallDir <路径>");
            Console.WriteLine();
            Console.WriteLine("  仅安装器自己的开关：");
            Console.WriteLine("    --version       打印版本");
            Console.WriteLine("    --help          打印本说明");
            Console.WriteLine("    --extract-only  只解包到临时目录并打印路径，不安装");
        }
    }
}
