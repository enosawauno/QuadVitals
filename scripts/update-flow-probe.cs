// End-to-end probe for the in-app update path (scripts\test-update-flow.ps1 compiles and runs it).
//
// The Core suite proves the logic against in-memory streams; this probe proves the same path over a
// real socket: HttpListener serves a staged feed, HttpUpdateTransport fetches it with redirects and
// content length, the package is streamed to disk and hashed. Nothing here is a stand-in for the
// network, which is exactly the part unit tests cannot reach.
//
// It also hosts the negatives the card depends on: a manifest whose hash does not match the package
// must fail verification, and an unreachable feed must produce a Failed outcome instead of throwing.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using QuadVitals.Core;

internal static class UpdateFlowProbe
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        string feedDirectory = null;
        string setupPath = null;
        string version = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--feed") feedDirectory = args[i + 1];
            else if (args[i] == "--setup") setupPath = args[i + 1];
            else if (args[i] == "--version") version = args[i + 1];
        }

        if (feedDirectory == null || setupPath == null || version == null)
        {
            Console.WriteLine("usage: UpdateFlowProbe --feed <dir> --setup <exe> --version <x.y.z>");
            return 2;
        }

        Directory.CreateDirectory(feedDirectory);

        int port = FreePort();
        string baseUrl = "http://localhost:" + port + "/";
        using (HttpListener listener = StartServer(port, feedDirectory))
        {
            if (listener == null)
            {
                Console.WriteLine("FAIL  HttpListener could not start (port " + port + ")");
                return 1;
            }

            string setupName = Path.GetFileName(setupPath);
            long size = new FileInfo(setupPath).Length;
            string hash = UpdateHashing.ComputeFileSha256(setupPath);

            WriteManifest(feedDirectory, version, setupName, size, hash);

            IUpdateTransport transport = new HttpUpdateTransport();

            // 1. Check against the staged feed.
            UpdateCheckOutcome check = UpdateChecker.Check(transport, baseUrl, "0.0.0");
            Check("the feed is reachable over HTTP", check.Status == UpdateCheckStatus.Available,
                "status=" + check.Status + " error=" + check.Error);
            Check("the manifest version is served", check.LatestVersion == version, "latest=" + check.LatestVersion);
            Check("the installer name survives HTTP", check.Manifest != null && check.Manifest.SetupName == setupName,
                "name=" + (check.Manifest == null ? "(null)" : check.Manifest.SetupName));
            Check("the content length is used as the expected size",
                check.Manifest != null && check.Manifest.SetupSize == size, "size=" + size);

            // 2. Same manifest against a newer local version: no update offered.
            UpdateCheckOutcome same = UpdateChecker.Check(transport, baseUrl, version);
            Check("an equal version is reported as current", same.Status == UpdateCheckStatus.Current,
                "status=" + same.Status);

            // 3. Download + verify over HTTP.
            string destination = Path.Combine(feedDirectory, "downloaded", setupName);
            List<UpdateProgress> progress = new List<UpdateProgress>();
            UpdateDownloadResult download = UpdateDownloader.Download(
                transport, UpdateFeed.InstallerUrl(baseUrl, setupName), destination,
                p => progress.Add(p), null, null);
            Check("the package downloads over HTTP", download.Ok, "error=" + download.Error);
            Check("the downloaded size matches", download.Bytes == size, "bytes=" + download.Bytes);
            Check("progress was reported", progress.Count > 0, "events=" + progress.Count);

            string actualHash;
            string verifyError;
            bool verified = UpdateInstaller.Verify(check.Manifest, destination, out actualHash, out verifyError);
            Check("the downloaded package verifies against the manifest", verified,
                "error=" + verifyError + " hash=" + actualHash);
            Check("the on-disk hash equals the published hash", UpdateHashing.Matches(hash, actualHash),
                "actual=" + actualHash);

            // 4. A manifest that lies about the hash must not verify (and the file must stay put so the
            //    caller can decide to delete it — the probe deletes it here).
            WriteManifest(feedDirectory, version, setupName, size, new string('a', 64));
            UpdateCheckOutcome corrupt = UpdateChecker.Check(transport, baseUrl, "0.0.0");
            Check("a manifest with a bad hash still parses", corrupt.Status == UpdateCheckStatus.Available,
                "status=" + corrupt.Status);
            bool corruptVerified = UpdateInstaller.Verify(corrupt.Manifest, destination, out actualHash, out verifyError);
            Check("a hash mismatch is rejected", !corruptVerified && verifyError == "hash-mismatch",
                "error=" + verifyError);

            // 5. Garbage manifest: failed check with a reason, no exception.
            File.WriteAllText(Path.Combine(feedDirectory, UpdateFeed.ManifestFileName), "not json");
            UpdateCheckOutcome broken = UpdateChecker.Check(transport, baseUrl, "0.0.0");
            Check("a broken manifest fails the check", broken.Status == UpdateCheckStatus.Failed,
                "status=" + broken.Status + " error=" + broken.Error);

            // 6. Serve nothing at all: transport failure surfaces as Failed, never as an exception.
            File.Delete(Path.Combine(feedDirectory, UpdateFeed.ManifestFileName));
            UpdateCheckOutcome missing = UpdateChecker.Check(transport, baseUrl, "0.0.0");
            Check("a missing manifest fails the check", missing.Status == UpdateCheckStatus.Failed,
                "status=" + missing.Status + " error=" + missing.Error);

            string fullHash = UpdateHashing.ComputeFileSha256(setupPath);
            Check("hashing the same file twice is stable", fullHash == hash, "hash=" + hash);

            // 7. A peer that accepts the connection and then says nothing must not be able to hang
            //    the check past its budget: behind a proxy the framework's own timeout does not fire
            //    until the tunnel is up, which is how the card could sit on "checking" forever.
            int holePort = FreePort();
            System.Net.Sockets.TcpListener hole = StartBlackHole(holePort);
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                UpdateCheckOutcome hung = UpdateChecker.Check(new HttpUpdateTransport(),
                    "http://localhost:" + holePort + "/", "0.0.0", TimeSpan.FromSeconds(1));
                clock.Stop();
                Check("a silent peer cannot hang the check past its budget",
                    hung.Status == UpdateCheckStatus.Failed && clock.Elapsed < TimeSpan.FromSeconds(4),
                    "status=" + hung.Status + " elapsed=" + clock.ElapsedMilliseconds + "ms error=" + hung.Error);
            }
            finally
            {
                // TcpListener has no IDisposable on .NET Framework.
                try { hole.Stop(); }
                catch (Exception) { }
            }
        }

        Console.WriteLine();
        Console.WriteLine("===== update flow probe: " + _passed + " passed, " + _failed + " failed =====");
        return _failed;
    }

    private static void WriteManifest(string directory, string version, string name, long size, string sha256)
    {
        string json =
            "{\n" +
            "  \"version\": \"" + version + "\",\n" +
            "  \"publishedAt\": \"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",\n" +
            "  \"notes\": [\"probe\"],\n" +
            "  \"setup\": { \"name\": \"" + name + "\", \"size\": " + size + ", \"sha256\": \"" + sha256 + "\" }\n" +
            "}\n";
        File.WriteAllText(Path.Combine(directory, UpdateFeed.ManifestFileName), json, new UTF8Encoding(false));
    }

    private static int FreePort()
    {
        System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Minimal static file server: manifest and package, nothing else.</summary>
    private static HttpListener StartServer(int port, string root)
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:" + port + "/");
        try
        {
            listener.Start();
        }
        catch (Exception)
        {
            return null;
        }

        Thread worker = new Thread(() =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception)
                {
                    return;   // listener stopped
                }

                try
                {
                    string name = Path.GetFileName(context.Request.Url.AbsolutePath);
                    string file = Path.Combine(root, name);
                    if (!File.Exists(file))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        continue;
                    }

                    byte[] content = File.ReadAllBytes(file);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength64 = content.Length;
                    context.Response.OutputStream.Write(content, 0, content.Length);
                    context.Response.Close();
                }
                catch (Exception)
                {
                    try { context.Response.Abort(); }
                    catch (Exception) { }
                }
            }
        });
        worker.IsBackground = true;
        worker.Start();
        return listener;
    }

    /// <summary>Accepts connections and never writes: the shape of a stalled proxy tunnel.</summary>
    private static System.Net.Sockets.TcpListener StartBlackHole(int port)
    {
        System.Net.Sockets.TcpListener listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Thread worker = new Thread(() =>
        {
            List<System.Net.Sockets.TcpClient> held = new List<System.Net.Sockets.TcpClient>();
            while (true)
            {
                try
                {
                    System.Net.Sockets.TcpClient client = listener.AcceptTcpClient();
                    held.Add(client);
                }
                catch (Exception)
                {
                    return;   // listener stopped
                }
            }
        });
        worker.IsBackground = true;
        worker.Start();
        return listener;
    }

    private static void Check(string what, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  PASS  " + what + "  " + detail);
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL  " + what + "  " + detail);
        }
    }
}
