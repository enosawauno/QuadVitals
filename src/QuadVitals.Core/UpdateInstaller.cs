using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QuadVitals.Core
{
    public static class UpdateHashing
    {
        public const int Sha256HexLength = 64;

        public static bool IsSha256Hex(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != Sha256HexLength) return false;
            foreach (char c in text)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        public static string ComputeFileSha256(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        public static bool Matches(string expected, string actual)
        {
            if (!IsSha256Hex(expected) || !IsSha256Hex(actual)) return false;
            return string.Equals(expected.ToLowerInvariant(), actual.ToLowerInvariant(), StringComparison.Ordinal);
        }
    }

    /// <summary>Local side of the update: where the package lands, whether it is trustworthy, how it starts.</summary>
    public static class UpdateInstaller
    {
        public const string InstallerExtension = ".exe";

        public static string UpdatesDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuadVitals", "updates");
        }

        public static string InstallerPath(UpdateManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            return Path.Combine(UpdatesDirectory(), manifest.SetupName);
        }

        /// <summary>
        /// A manifest name is joined onto a directory and later executed, so it must be a bare
        /// file name ending in .exe — no separators, no drive/stream colons, no control characters.
        /// </summary>
        public static bool IsSafeInstallerName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
            if (!name.EndsWith(InstallerExtension, StringComparison.OrdinalIgnoreCase)) return false;

            foreach (char c in name)
            {
                if (c < 0x20 || c == 0x7F) return false;
                switch (c)
                {
                    case '/':
                    case '\\':
                    case ':':
                    case '*':
                    case '?':
                    case '"':
                    case '<':
                    case '>':
                    case '|':
                        return false;
                }
            }

            string stem = name.Substring(0, name.Length - InstallerExtension.Length);
            if (stem.Trim().Length == 0) return false;
            if (stem.Trim('.').Length == 0) return false;
            return true;
        }

        /// <summary>Size first (cheap, catches a truncated transfer), then SHA-256 against the manifest.</summary>
        public static bool Verify(UpdateManifest manifest, string filePath, out string actualSha256, out string error)
        {
            actualSha256 = string.Empty;
            error = string.Empty;
            if (manifest == null) { error = "no-manifest"; return false; }

            FileInfo info;
            try
            {
                info = new FileInfo(filePath);
                if (!info.Exists) { error = "installer-missing"; return false; }
            }
            catch (Exception ex)
            {
                error = "installer-unreadable:" + ex.GetType().Name;
                return false;
            }

            if (manifest.SetupSize > 0 && info.Length != manifest.SetupSize)
            {
                error = "size-mismatch";
                return false;
            }

            try
            {
                actualSha256 = UpdateHashing.ComputeFileSha256(filePath);
            }
            catch (Exception ex)
            {
                error = "hash-failed:" + ex.GetType().Name;
                return false;
            }

            if (!UpdateHashing.Matches(manifest.SetupSha256, actualSha256))
            {
                error = "hash-mismatch";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Starts the verified installer with the user's own rights. The wizard elevates for the
        /// scheduled task itself; launching it elevated here would hand Explorer restart an
        /// administrator desktop.
        /// </summary>
        public static bool Launch(string installerPath, out string error)
        {
            error = string.Empty;
            try
            {
                if (!File.Exists(installerPath))
                {
                    error = "installer-missing";
                    return false;
                }
                ProcessStartInfo info = new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty
                };
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                error = "launch-failed:" + ex.GetType().Name;
                return false;
            }
        }
    }
}
