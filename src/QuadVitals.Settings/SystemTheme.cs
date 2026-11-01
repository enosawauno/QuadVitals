using Microsoft.Win32;
using QuadVitals.Core;
using AppSettings = QuadVitals.Core.Settings;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Reads the current Windows accent colour so a brand-new element starts on the user's own
    /// colour instead of the product green.
    ///
    /// Only used to seed a new element; existing elements keep whatever colour they were saved
    /// with, and every failure falls back to the product green.
    /// </summary>
    internal static class SystemTheme
    {
        public static string AccentColor()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\DWM", false))
                {
                    object raw = key == null ? null : key.GetValue("ColorizationColor");
                    if (raw is int)
                    {
                        int value = (int)raw;
                        int r = (value >> 16) & 0xFF;
                        int g = (value >> 8) & 0xFF;
                        int b = value & 0xFF;
                        // Very dark accents are invisible on the dark band, and the value also
                        // carries an alpha byte in the top bits: only take sane, bright colours.
                        if (r + g + b >= 180)
                        {
                            return string.Format("#FF{0:X2}{1:X2}{2:X2}", r, g, b);
                        }
                    }
                }
            }
            catch (System.Exception)
            {
                // registry blocked: fall through to the product colour
            }
            return AppSettings.DefaultGreen;
        }
    }
}
