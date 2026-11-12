using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace QuadVitals.Core
{
    /// <summary>
    /// What the dashboard remembers about the last update check: when it ran and which version
    /// the owner asked to skip.
    ///
    /// Why a file instead of process state: the dashboard is opened and closed all day, and the
    /// cost of an unthrottled check is a network round trip on every open. Remembering the
    /// timestamp makes "check on open" mean "at most once a day" without adding a scheduler.
    /// A skipped version is remembered separately so a deliberate "later" is not undone by the
    /// next open.
    ///
    /// Everything degrades to "check now": a missing, half-written or corrupted file only costs
    /// one extra request, never an exception in the window.
    /// </summary>
    public sealed class UpdateCheckCache
    {
        /// <summary>One automatic check per day; a manual check in the About page is never throttled.</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(24);

        /// <summary>A clock that jumped backwards by more than this is treated as "no record".</summary>
        private const int MaxClockSkewMinutes = 5;

        /// <summary>Versions are three numeric segments; this only bounds a hostile file.</summary>
        private const int MaxVersionLength = 32;

        [DataContract]
        private sealed class CacheDto
        {
            [DataMember(Name = "lastCheckUtc")] public string LastCheckUtc { get; set; }
            [DataMember(Name = "latestVersion")] public string LatestVersion { get; set; }
            [DataMember(Name = "ignoredVersion")] public string IgnoredVersion { get; set; }
        }

        public DateTime LastCheckUtc { get; private set; }

        /// <summary>Version the feed reported last time (may equal the running version).</summary>
        public string LatestVersion { get; private set; }

        /// <summary>Version the owner chose not to be prompted about again.</summary>
        public string IgnoredVersion { get; private set; }

        private UpdateCheckCache()
        {
            LastCheckUtc = DateTime.MinValue;
            LatestVersion = string.Empty;
            IgnoredVersion = string.Empty;
        }

        public static string DefaultPath()
        {
            string directory = Path.GetDirectoryName(Settings.DefaultPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuadVitals");
            }
            return Path.Combine(directory, "update-check.json");
        }

        /// <summary>Never throws: an unreadable file is the same as no file.</summary>
        public static UpdateCheckCache Load(string path)
        {
            UpdateCheckCache cache = new UpdateCheckCache();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return cache;

            try
            {
                CacheDto dto;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CacheDto));
                    dto = serializer.ReadObject(stream) as CacheDto;
                }
                if (dto == null) return cache;

                DateTime parsed;
                if (!string.IsNullOrEmpty(dto.LastCheckUtc) &&
                    DateTime.TryParse(dto.LastCheckUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                {
                    cache.LastCheckUtc = parsed;
                }

                cache.LatestVersion = CleanVersion(dto.LatestVersion);
                cache.IgnoredVersion = CleanVersion(dto.IgnoredVersion);
            }
            catch (Exception)
            {
                // Corrupt cache: behave like a first run.
                return new UpdateCheckCache();
            }
            return cache;
        }

        /// <summary>
        /// True when the automatic check should run. A saved timestamp in the future beyond the
        /// skew allowance means the clock was moved; check instead of freezing for a day.
        /// </summary>
        public bool ShouldCheck(DateTime nowUtc)
        {
            return ShouldCheck(nowUtc, DefaultInterval);
        }

        public bool ShouldCheck(DateTime nowUtc, TimeSpan interval)
        {
            if (LastCheckUtc == DateTime.MinValue) return true;
            if (LastCheckUtc - nowUtc > TimeSpan.FromMinutes(MaxClockSkewMinutes)) return true;
            return nowUtc - LastCheckUtc >= interval;
        }

        /// <summary>
        /// True when this version is worth a prompt: newer than what runs now and not skipped.
        /// </summary>
        public bool ShouldOffer(string candidateVersion, string currentVersion)
        {
            if (!UpdateVersions.IsNewer(candidateVersion, currentVersion)) return false;
            if (string.IsNullOrEmpty(IgnoredVersion)) return true;
            return !string.Equals(candidateVersion, IgnoredVersion, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsIgnored(string version)
        {
            return !string.IsNullOrEmpty(version) &&
                   string.Equals(version, IgnoredVersion, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Records a completed check. A newer version clears a skip from an older one.</summary>
        public void Record(string latestVersion, DateTime nowUtc)
        {
            LastCheckUtc = nowUtc;
            LatestVersion = CleanVersion(latestVersion);
        }

        public void Ignore(string version)
        {
            IgnoredVersion = CleanVersion(version);
        }

        /// <summary>Best effort: a cache that cannot be written only costs the next check.</summary>
        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string temporary = path + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                CacheDto dto = new CacheDto
                {
                    LastCheckUtc = LastCheckUtc == DateTime.MinValue
                        ? string.Empty
                        : LastCheckUtc.ToString("o", CultureInfo.InvariantCulture),
                    LatestVersion = LatestVersion,
                    IgnoredVersion = IgnoredVersion
                };

                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CacheDto));
                    serializer.WriteObject(stream, dto);
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception)
                {
                    // A stray .tmp file is harmless; the next save overwrites it.
                }
            }
        }

        private static string CleanVersion(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string trimmed = value.Trim();
            if (trimmed.Length > MaxVersionLength) trimmed = trimmed.Substring(0, MaxVersionLength);
            return IsVersionShape(trimmed) ? trimmed : string.Empty;
        }

        /// <summary>Digits and dots only: the field is written by this app, so anything else is damage.</summary>
        private static bool IsVersionShape(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            bool digit = false;
            foreach (char c in value)
            {
                if (c >= '0' && c <= '9') { digit = true; continue; }
                if (c == '.') continue;
                return false;
            }
            return digit;
        }
    }
}
