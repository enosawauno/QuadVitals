using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace QuadVitals.Core
{
    /// <summary>
    /// In-app update feed: one static manifest next to the release assets. Nothing here talks to
    /// the network by itself — transports are injected so assertions never open a socket.
    ///
    /// Shape of <c>latest.json</c> (produced by the release script, mirrored on GitHub Releases):
    /// <code>
    /// { "version": "1.2.3", "publishedAt": "…", "notes": ["…"],
    ///   "setup": { "name": "QuadVitals-Setup-0.9.1.exe", "size": 1436672, "sha256": "…" } }
    /// </code>
    /// The hash is not optional: a downloaded installer is executed, so an unverifiable package
    /// must be treated as a failed download rather than something the user can override.
    /// </summary>
    public enum UpdateCheckStatus
    {
        Current,
        Available,
        Failed
    }

    public sealed class UpdateManifest
    {
        public string Version = string.Empty;
        public DateTime PublishedAtUtc = DateTime.MinValue;
        public string[] Notes = new string[0];
        public string SetupName = string.Empty;
        public long SetupSize;
        public string SetupSha256 = string.Empty;

        /// <summary>"1.2.3 · YYYY-MM-DD" — one line for the About card, empty when the feed omits the date.</summary>
        public string DisplayVersion
        {
            get
            {
                if (PublishedAtUtc == DateTime.MinValue) return Version;
                return Version + " · " + PublishedAtUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Where the manifest and the installer live. One base URL, so switching feeds is one line.</summary>
    public static class UpdateFeed
    {
        /// <summary>
        /// GitHub Releases resolves <c>latest/download/&lt;asset&gt;</c> to the newest release, so the
        /// check needs no API call (and therefore no 60-requests-per-hour unauthenticated limit).
        /// Assets live in a release-only repository: the source tree is not published, and the
        /// updater must not depend on it being public.
        /// </summary>
        public const string DefaultBaseUrl =
            "https://github.com/enosawauno/QuadVitals-Release/releases/latest/download/";

        public const string ManifestFileName = "latest.json";

        /// <summary>
        /// Development override (local HTTP server or a staged folder). Only http(s) is accepted:
        /// a file path here would turn the update check into an arbitrary local file read whose
        /// result is then executed.
        /// </summary>
        public const string FeedEnvironmentVariable = "QUADVITALS_UPDATE_FEED";

        /// <summary>Hard ceiling on a package. The real installer is ~1.4 MB; this only stops a lying feed.</summary>
        public const long MaxInstallerBytes = 256L * 1024 * 1024;

        /// <summary>A manifest is a few hundred bytes; a bigger body means the URL is wrong.</summary>
        public const int MaxManifestBytes = 64 * 1024;

        public static string BaseUrl()
        {
            string overridden = null;
            try
            {
                overridden = Environment.GetEnvironmentVariable(FeedEnvironmentVariable);
            }
            catch (Exception)
            {
                // Reading the environment is not supposed to fail; if it does, fall back to the default feed.
            }
            return NormalizeBase(overridden);
        }

        public static string NormalizeBase(string candidate)
        {
            string text = (candidate ?? string.Empty).Trim();
            if (text.Length == 0) return DefaultBaseUrl;

            Uri parsed;
            if (!Uri.TryCreate(text, UriKind.Absolute, out parsed)) return DefaultBaseUrl;
            if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) return DefaultBaseUrl;

            text = parsed.AbsoluteUri;
            return text.EndsWith("/", StringComparison.Ordinal) ? text : text + "/";
        }

        public static string ManifestUrl(string baseUrl)
        {
            return NormalizeBase(baseUrl) + ManifestFileName;
        }

        public static string InstallerUrl(string baseUrl, string setupName)
        {
            if (!UpdateInstaller.IsSafeInstallerName(setupName))
            {
                throw new ArgumentException("unusable installer name", "setupName");
            }
            // The name has no separators (IsSafeInstallerName), so it cannot walk out of the base.
            return NormalizeBase(baseUrl) + setupName;
        }
    }

    /// <summary>Three-segment version comparison. Non-numbers compare as "not newer", never as newer.</summary>
    public static class UpdateVersions
    {
        public static bool TryParse(string text, out int major, out int minor, out int patch)
        {
            major = 0;
            minor = 0;
            patch = 0;
            if (string.IsNullOrEmpty(text)) return false;

            string[] parts = text.Split('.');
            if (parts.Length != 3) return false;
            return TrySegment(parts[0], out major) && TrySegment(parts[1], out minor) && TrySegment(parts[2], out patch);
        }

        private static bool TrySegment(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 9) return false;
            // NumberStyles.None rejects signs and spaces; digits only, so "+1" and " 1" fail.
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        public static int Compare(string left, string right)
        {
            int leftMajor, leftMinor, leftPatch, rightMajor, rightMinor, rightPatch;
            bool leftOk = TryParse(left, out leftMajor, out leftMinor, out leftPatch);
            bool rightOk = TryParse(right, out rightMajor, out rightMinor, out rightPatch);
            if (!leftOk || !rightOk) return 0;

            if (leftMajor != rightMajor) return leftMajor < rightMajor ? -1 : 1;
            if (leftMinor != rightMinor) return leftMinor < rightMinor ? -1 : 1;
            if (leftPatch != rightPatch) return leftPatch < rightPatch ? -1 : 1;
            return 0;
        }

        /// <summary>Strictly greater. Equal and unparsable both answer false, so a broken feed never offers a "new" build.</summary>
        public static bool IsNewer(string candidate, string current)
        {
            return Compare(candidate, current) > 0;
        }
    }

    /// <summary>Manifest parsing. Every rejection carries a reason so the card can say what is wrong.</summary>
    public static class UpdateManifestReader
    {
        [DataContract]
        private sealed class ManifestDto
        {
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "publishedAt")] public string PublishedAt { get; set; }
            [DataMember(Name = "notes")] public string[] Notes { get; set; }
            [DataMember(Name = "setup")] public AssetDto Setup { get; set; }
        }

        [DataContract]
        private sealed class AssetDto
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
            [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        }

        public const int MaxNotes = 20;
        public const int MaxNoteLength = 400;

        public static bool TryParse(string json, out UpdateManifest manifest, out string error)
        {
            manifest = null;
            error = string.Empty;
            if (string.IsNullOrEmpty(json))
            {
                error = "manifest-empty";
                return false;
            }

            ManifestDto dto;
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ManifestDto));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    dto = serializer.ReadObject(stream) as ManifestDto;
                }
            }
            catch (Exception)
            {
                error = "manifest-invalid-json";
                return false;
            }

            if (dto == null)
            {
                error = "manifest-empty";
                return false;
            }

            int major, minor, patch;
            if (!UpdateVersions.TryParse(dto.Version, out major, out minor, out patch))
            {
                error = "manifest-bad-version";
                return false;
            }

            if (dto.Setup == null)
            {
                error = "manifest-no-setup";
                return false;
            }

            if (!UpdateInstaller.IsSafeInstallerName(dto.Setup.Name))
            {
                error = "manifest-bad-name";
                return false;
            }

            if (dto.Setup.Size <= 0 || dto.Setup.Size > UpdateFeed.MaxInstallerBytes)
            {
                error = "manifest-bad-size";
                return false;
            }

            if (!UpdateHashing.IsSha256Hex(dto.Setup.Sha256))
            {
                error = "manifest-bad-hash";
                return false;
            }

            manifest = new UpdateManifest
            {
                Version = dto.Version,
                PublishedAtUtc = ParseTimestamp(dto.PublishedAt),
                Notes = CleanNotes(dto.Notes),
                SetupName = dto.Setup.Name,
                SetupSize = dto.Setup.Size,
                SetupSha256 = dto.Setup.Sha256.ToLowerInvariant()
            };
            return true;
        }

        private static DateTime ParseTimestamp(string text)
        {
            DateTime parsed;
            if (!string.IsNullOrEmpty(text) &&
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed;
            }
            return DateTime.MinValue;
        }

        private static string[] CleanNotes(string[] notes)
        {
            if (notes == null || notes.Length == 0) return new string[0];
            System.Collections.Generic.List<string> kept = new System.Collections.Generic.List<string>();
            foreach (string note in notes)
            {
                if (string.IsNullOrEmpty(note)) continue;
                string trimmed = note.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.Length > MaxNoteLength) trimmed = trimmed.Substring(0, MaxNoteLength);
                kept.Add(trimmed);
                if (kept.Count >= MaxNotes) break;
            }
            return kept.ToArray();
        }
    }
}
