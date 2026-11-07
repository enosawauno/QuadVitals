using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QuadVitals.UI
{
    /// <summary>
    /// Answers one question for the "全屏停止动画" policy: is the foreground window covering its
    /// whole monitor? A maximized window still shows the taskbar, so it does not match; a game or
    /// a video player in borderless fullscreen does. The desktop and the taskbar itself are
    /// excluded by class name, and the dashboard's own process is excluded so a maximized settings
    /// window can never freeze its own animations.
    /// </summary>
    internal static class FullscreenDetector
    {
        private const int DwmwaExtendedFrameBounds = 9;
        private const int DwmwaCloaked = 14;
        private const uint MonitorDefaultToNearest = 2;
        private const int TolerancePx = 2;
        private const int GwlStyle = -16;
        private const long WsCaption = 0x00C00000L;
        private static readonly int OwnProcessId = Process.GetCurrentProcess().Id;
        private static readonly string[] ShellClasses =
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
        };
        private static readonly string[] ShellProcesses =
        {
            "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost", "LockApp", "LogonUI"
        };

        public static bool IsForegroundFullscreen()
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return false;

                uint processId;
                GetWindowThreadProcessId(foreground, out processId);
                if (processId == OwnProcessId) return false;
                if (!IsWindowVisible(foreground) || IsIconic(foreground)) return false;

                int cloaked;
                if (DwmGetWindowAttribute(foreground, DwmwaCloaked, out cloaked, sizeof(int)) == 0 &&
                    cloaked != 0)
                {
                    return false;
                }

                string className = ClassName(foreground);
                for (int i = 0; i < ShellClasses.Length; i++)
                {
                    if (string.Equals(className, ShellClasses[i], StringComparison.Ordinal)) return false;
                }
                if (IsShellProcess(processId)) return false;

                // A maximized window with a caption is the ordinary "fill the work area" state,
                // not fullscreen. This matters when the taskbar auto-hides: its work area then
                // equals the monitor and a plain maximized window would otherwise look fullscreen.
                long style = GetWindowLongPtr(foreground, GwlStyle).ToInt64();
                if (IsZoomed(foreground) && (style & WsCaption) == WsCaption) return false;

                NativeRect window;
                if (!GetWindowRect(foreground, out window)) return false;

                // Extended frame bounds describe what is actually visible; GetWindowRect includes
                // the invisible resize border of a maximized window.
                NativeRect visible;
                if (DwmGetWindowAttribute(foreground, DwmwaExtendedFrameBounds, out visible,
                                          Marshal.SizeOf(typeof(NativeRect))) == 0)
                {
                    window = visible;
                }

                IntPtr monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return false;
                MonitorInfo info = new MonitorInfo();
                info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                if (!GetMonitorInfo(monitor, ref info)) return false;

                NativeRect target = info.Monitor;
                return window.Left <= target.Left + TolerancePx &&
                       window.Top <= target.Top + TolerancePx &&
                       window.Right >= target.Right - TolerancePx &&
                       window.Bottom >= target.Bottom - TolerancePx;
            }
            catch (DllNotFoundException)
            {
                // dwmapi is present on every supported Windows 10 build; if it is not, treating
                // the desktop as "not fullscreen" is the safe answer (no freeze, no crash).
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static bool IsShellProcess(uint processId)
        {
            try
            {
                string name = Process.GetProcessById((int)processId).ProcessName;
                for (int i = 0; i < ShellProcesses.Length; i++)
                {
                    if (string.Equals(name, ShellProcesses[i], StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch (ArgumentException)
            {
                // The process exited between the foreground query and this lookup: no fullscreen.
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // A protected process (anti-cheat, system service) may refuse its name; keep
                // going with the window rectangle instead of crashing the 1 Hz timer.
                return false;
            }
        }

        private static string ClassName(IntPtr hwnd)
        {
            StringBuilder buffer = new StringBuilder(256);
            int length = GetClassName(hwnd, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : string.Empty;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
    }
}
