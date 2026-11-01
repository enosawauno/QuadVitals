using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace QuadVitals.Core
{
    /// <summary>
    /// Transport boundary: assertions feed in-memory streams, production uses
    /// <see cref="HttpUpdateTransport"/>. Both the check and the download go through it, so the
    /// whole flow can be driven without a socket.
    /// </summary>
    public interface IUpdateTransport
    {
        UpdateResponse Open(string url, TimeSpan timeout);
    }

    public sealed class UpdateResponse : IDisposable
    {
        public Stream Body;
        /// <summary>Content-Length, or -1 when the server does not send one.</summary>
        public long ContentLength = -1;
        /// <summary>Set when no usable response was received (DNS/TLS/timeout/HTTP error).</summary>
        public string Failure;

        public void Dispose()
        {
            if (Body == null) return;
            try
            {
                Body.Dispose();
            }
            catch (Exception)
            {
                // Disposing a failed network stream must not mask the caller's own error handling.
            }
            Body = null;
        }
    }

    public sealed class UpdateCheckOutcome
    {
        public UpdateCheckStatus Status;
        public string CurrentVersion = string.Empty;
        public string LatestVersion = string.Empty;
        public UpdateManifest Manifest;
        public string ManifestUrl = string.Empty;
        public string Error = string.Empty;
    }

    public static class UpdateChecker
    {
        /// <summary>
        /// Short on purpose: this runs behind a button, not on a schedule. Generous enough for a
        /// slow proxy — measured 9-11 s against GitHub through a local proxy, where 8 s gave up
        /// before the manifest arrived and the user saw "connection timed out" every time.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        public static UpdateCheckOutcome Check(IUpdateTransport transport, string baseUrl, string currentVersion)
        {
            return Check(transport, baseUrl, currentVersion, DefaultTimeout);
        }

        public static UpdateCheckOutcome Check(IUpdateTransport transport, string baseUrl, string currentVersion, TimeSpan timeout)
        {
            UpdateCheckOutcome outcome = new UpdateCheckOutcome
            {
                Status = UpdateCheckStatus.Failed,
                CurrentVersion = currentVersion ?? string.Empty,
                ManifestUrl = UpdateFeed.ManifestUrl(baseUrl)
            };

            if (transport == null)
            {
                outcome.Error = "no-transport";
                return outcome;
            }

            try
            {
                using (UpdateResponse response = transport.Open(outcome.ManifestUrl, timeout))
                {
                    if (response == null)
                    {
                        outcome.Error = "no-response";
                        return outcome;
                    }
                    if (!string.IsNullOrEmpty(response.Failure))
                    {
                        outcome.Error = response.Failure;
                        return outcome;
                    }
                    if (response.Body == null)
                    {
                        outcome.Error = "empty-body";
                        return outcome;
                    }

                    string json = ReadText(response.Body, UpdateFeed.MaxManifestBytes);

                    UpdateManifest manifest;
                    string error;
                    if (!UpdateManifestReader.TryParse(json, out manifest, out error))
                    {
                        outcome.Error = error;
                        return outcome;
                    }

                    outcome.Manifest = manifest;
                    outcome.LatestVersion = manifest.Version;
                    outcome.Status = UpdateVersions.IsNewer(manifest.Version, outcome.CurrentVersion)
                        ? UpdateCheckStatus.Available
                        : UpdateCheckStatus.Current;
                    outcome.Error = string.Empty;
                    return outcome;
                }
            }
            catch (Exception ex)
            {
                outcome.Error = "check-exception:" + ex.GetType().Name;
                return outcome;
            }
        }

        private static string ReadText(Stream body, int maxBytes)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int total = 0;
                while (true)
                {
                    int read = body.Read(chunk, 0, chunk.Length);
                    if (read <= 0) break;
                    total += read;
                    if (total > maxBytes) throw new InvalidOperationException("manifest-too-large");
                    buffer.Write(chunk, 0, read);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }
    }

    public sealed class UpdateProgress
    {
        public long Received;
        /// <summary>Expected size, or -1 when the server did not say.</summary>
        public long Total;
        public double BytesPerSecond;

        public double Fraction
        {
            get
            {
                if (Total <= 0) return 0.0;
                double value = (double)Received / Total;
                return value > 1.0 ? 1.0 : value;
            }
        }
    }

    public sealed class UpdateDownloadResult
    {
        public bool Ok;
        public bool Cancelled;
        public string Path = string.Empty;
        public long Bytes;
        public string Error = string.Empty;
    }

    /// <summary>
    /// Streams the installer to disk with progress. The file only appears under its final name
    /// after a complete transfer, so a half-written package can never be launched — the previous
    /// download (if any) stays intact until the new one is whole.
    /// </summary>
    public static class UpdateDownloader
    {
        public const int DefaultBufferSize = 131072;

        /// <summary>
        /// Much longer than the check budget: a 1.5 MB package over a slow proxy can take tens of
        /// seconds, and this deadline also bounds each read so a stalled transfer still fails.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

        public static UpdateDownloadResult Download(IUpdateTransport transport, string url, string destinationPath,
                                                    Action<UpdateProgress> onProgress,
                                                    Func<bool> cancelled,
                                                    TimeSpan? timeout)
        {
            UpdateDownloadResult result = new UpdateDownloadResult { Path = destinationPath };
            if (transport == null) { result.Error = "no-transport"; return result; }
            if (string.IsNullOrEmpty(destinationPath)) { result.Error = "no-destination"; return result; }

            string part = destinationPath + ".part";
            try
            {
                string directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                TryDelete(part);
            }
            catch (Exception ex)
            {
                result.Error = "prepare-failed:" + ex.GetType().Name;
                return result;
            }

            try
            {
                using (UpdateResponse response = transport.Open(url, timeout ?? DefaultTimeout))
                {
                    if (response == null) { result.Error = "no-response"; return result; }
                    if (!string.IsNullOrEmpty(response.Failure)) { result.Error = response.Failure; return result; }
                    if (response.Body == null) { result.Error = "empty-body"; return result; }
                    if (response.ContentLength > UpdateFeed.MaxInstallerBytes)
                    {
                        result.Error = "too-large";
                        return result;
                    }

                    long received = 0;
                    Stopwatch clock = Stopwatch.StartNew();
                    byte[] buffer = new byte[DefaultBufferSize];
                    using (FileStream file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize))
                    {
                        while (true)
                        {
                            if (cancelled != null && cancelled())
                            {
                                file.Dispose();
                                TryDelete(part);
                                result.Cancelled = true;
                                return result;
                            }

                            int read = response.Body.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;

                            received += read;
                            if (received > UpdateFeed.MaxInstallerBytes)
                            {
                                file.Dispose();
                                TryDelete(part);
                                result.Error = "too-large";
                                return result;
                            }

                            file.Write(buffer, 0, read);
                            if (onProgress != null)
                            {
                                double seconds = clock.Elapsed.TotalSeconds;
                                onProgress(new UpdateProgress
                                {
                                    Received = received,
                                    Total = response.ContentLength,
                                    BytesPerSecond = seconds > 0 ? received / seconds : 0
                                });
                            }
                        }
                    }

                    if (response.ContentLength > 0 && received != response.ContentLength)
                    {
                        TryDelete(part);
                        result.Error = "short-read";
                        return result;
                    }
                    if (received <= 0)
                    {
                        TryDelete(part);
                        result.Error = "empty-body";
                        return result;
                    }

                    if (File.Exists(destinationPath)) File.Delete(destinationPath);
                    File.Move(part, destinationPath);
                    result.Ok = true;
                    result.Bytes = received;
                    return result;
                }
            }
            catch (Exception ex)
            {
                TryDelete(part);
                result.Error = "download-failed:" + ex.GetType().Name;
                return result;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A leftover .part is harmless: it is never launched and the next attempt overwrites it.
            }
        }
    }

    /// <summary>
    /// Production transport: plain HTTP(S) with redirects (GitHub Releases redirects an asset to a
    /// signed object URL, so the check must follow redirects instead of trusting the first response).
    ///
    /// The deadline is enforced with <see cref="HttpWebRequest.Abort"/> rather than
    /// <c>HttpWebRequest.Timeout</c>: behind a proxy the framework timeout only starts once the
    /// tunnel is up, so a stalled CONNECT or a hanging TLS handshake leaves GetResponse blocked
    /// far past the budget — the update card then stays on "checking" with no way out.
    /// </summary>
    public sealed class HttpUpdateTransport : IUpdateTransport
    {
        public const string UserAgent = "QuadVitals-Update/1";

        public UpdateResponse Open(string url, TimeSpan timeout)
        {
            try
            {
                // .NET Framework 4.8 may still negotiate TLS 1.0 by default; both hosts are TLS 1.2+.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
                // An old machine refusing the upgrade should still get a normal request attempt.
            }

            HttpWebRequest request = null;
            IAsyncResult pending = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 5;
                request.Timeout = (int)timeout.TotalMilliseconds;
                request.ReadWriteTimeout = (int)timeout.TotalMilliseconds;
                request.UserAgent = UserAgent;
                request.KeepAlive = false;
                request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);

                pending = request.BeginGetResponse(null, null);
                if (!pending.AsyncWaitHandle.WaitOne(timeout))
                {
                    // Abort unblocks the pending call; the callback's exception is never observed
                    // because nothing consumed it, and the request is done with either way.
                    request.Abort();
                    return new UpdateResponse { Failure = "Timeout" };
                }

                HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(pending);
                return new UpdateResponse
                {
                    Body = response.GetResponseStream(),
                    ContentLength = response.ContentLength
                };
            }
            catch (WebException exception)
            {
                if (exception.Response != null)
                {
                    try { exception.Response.Dispose(); }
                    catch (Exception) { }
                }
                return new UpdateResponse { Failure = exception.Status.ToString() };
            }
            catch (Exception exception)
            {
                return new UpdateResponse { Failure = exception.GetType().Name };
            }
            finally
            {
                if (pending != null)
                {
                    try { pending.AsyncWaitHandle.Close(); }
                    catch (Exception) { }
                }
            }
        }
    }
}
