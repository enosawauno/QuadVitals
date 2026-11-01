using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QuadVitals.Agent
{
    /// <summary>
    /// Network throughput from per-adapter IPv4 byte counters. Default aggregation is
    /// "all physical adapters that are up", which matches the design wording; a settings value
    /// ("auto" or an adapter name/description/id fragment) narrows it to one adapter.
    /// </summary>
    internal sealed class NetworkSampler
    {
        private const int MaxBytesPerSecond = 1000 * 1000 * 1000; // 1 GB/s sanity ceiling

        private string _selector;
        private ulong _lastReceived;
        private ulong _lastSent;
        private DateTime _lastSampleUtc;
        private bool _hasPrevious;

        public NetworkSampler(string adapterSelector)
        {
            _selector = string.IsNullOrEmpty(adapterSelector) ? "auto" : adapterSelector;
        }

        /// <summary>
        /// Switches the adapter selector at runtime (settings hot reload). Resets the byte
        /// counters so the first sample after the switch is "--" instead of a delta that spans
        /// two different adapters.
        /// </summary>
        public void UpdateSelector(string adapterSelector)
        {
            string next = string.IsNullOrEmpty(adapterSelector) ? "auto" : adapterSelector;
            if (string.Equals(next, _selector, StringComparison.OrdinalIgnoreCase)) return;

            _selector = next;
            _hasPrevious = false;
            LastAdapterName = string.Empty;
            LastSelectionFound = false;
        }

        public string LastAdapterName { get; private set; }
        public bool LastSelectionFound { get; private set; }

        /// <summary>
        /// Fills up (bytes/s) and down (bytes/s). Returns false when no adapter matches or this
        /// is the first call (a delta needs two samples).
        /// </summary>
        public bool TrySample(DateTime nowUtc, out double bytesPerSecondUp, out double bytesPerSecondDown)
        {
            bytesPerSecondUp = 0;
            bytesPerSecondDown = 0;

            NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
            ulong received = 0;
            ulong sent = 0;
            string firstName = null;
            int matched = 0;

            bool anyPhysical = false;
            foreach (NetworkInterface adapter in adapters)
            {
                if (IsCandidate(adapter) && MatchesSelector(adapter) && IsPhysical(adapter))
                {
                    anyPhysical = true;
                    break;
                }
            }

            foreach (NetworkInterface adapter in adapters)
            {
                if (!IsCandidate(adapter)) continue;
                if (!MatchesSelector(adapter)) continue;
                if (anyPhysical && !IsPhysical(adapter)) continue;

                ulong adapterReceived;
                ulong adapterSent;
                if (!TryReadCounters(adapter, out adapterReceived, out adapterSent)) continue;

                received += adapterReceived;
                sent += adapterSent;
                if (firstName == null) firstName = adapter.Name;
                matched++;
            }

            LastSelectionFound = matched > 0;
            LastAdapterName = firstName ?? string.Empty;

            if (matched == 0) { _hasPrevious = false; return false; }

            if (!_hasPrevious)
            {
                _lastReceived = received;
                _lastSent = sent;
                _lastSampleUtc = nowUtc;
                _hasPrevious = true;
                return false;
            }

            double seconds = (nowUtc - _lastSampleUtc).TotalSeconds;
            if (seconds <= 0.001)
            {
                return false;
            }

            ulong receivedDelta = received >= _lastReceived ? received - _lastReceived : 0;
            ulong sentDelta = sent >= _lastSent ? sent - _lastSent : 0;

            _lastReceived = received;
            _lastSent = sent;
            _lastSampleUtc = nowUtc;

            bytesPerSecondDown = receivedDelta / seconds;
            bytesPerSecondUp = sentDelta / seconds;

            if (bytesPerSecondDown > MaxBytesPerSecond) bytesPerSecondDown = MaxBytesPerSecond;
            if (bytesPerSecondUp > MaxBytesPerSecond) bytesPerSecondUp = MaxBytesPerSecond;
            return true;
        }

        /// <summary>
        /// Sums IPv4 and IPv6 counters. Reading only IPv4 (the obvious call) under-reports by
        /// roughly the IPv6 share of traffic, which showed up as a consistent ~10% gap against
        /// Windows' own adapter statistics during S3/M2 verification.
        /// </summary>
        private static bool TryReadCounters(NetworkInterface adapter, out ulong received, out ulong sent)
        {
            received = 0;
            sent = 0;
            bool any = false;

            // GetIPStatistics covers IPv4 + IPv6 (what the adapter's own counters report).
            try
            {
                IPInterfaceStatistics ip = adapter.GetIPStatistics();
                received += (ulong)ip.BytesReceived;
                sent += (ulong)ip.BytesSent;
                any = true;
            }
            catch (NetworkInformationException) { }
            catch (NotSupportedException) { }

            if (!any)
            {
                // Very old drivers only expose the IPv4 shape; better a slight under-count than
                // a dead network metric.
                try
                {
                    IPv4InterfaceStatistics v4 = adapter.GetIPv4Statistics();
                    received += (ulong)v4.BytesReceived;
                    sent += (ulong)v4.BytesSent;
                    any = true;
                }
                catch (NetworkInformationException) { }
                catch (NotSupportedException) { }
            }

            return any;
        }

        /// <summary>Names of adapters the current selector would use (for the settings UI).</summary>
        public static List<string> ListCandidates()
        {
            List<string> names = new List<string>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsCandidate(adapter)) continue;
                names.Add(adapter.Name + " | " + adapter.Description + (IsPhysical(adapter) ? "" : "  [虚拟]"));
            }
            return names;
        }

        private bool MatchesSelector(NetworkInterface adapter)
        {
            if (string.Equals(_selector, "auto", StringComparison.OrdinalIgnoreCase)) return true;

            return Contains(adapter.Name) || Contains(adapter.Description) || Contains(adapter.Id);

            bool Contains(string value)
            {
                return !string.IsNullOrEmpty(value)
                    && value.IndexOf(_selector, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static bool IsCandidate(NetworkInterface adapter)
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return false;
            if (adapter.OperationalStatus != OperationalStatus.Up) return false;
            if (!adapter.Supports(NetworkInterfaceComponent.IPv4)) return false;
            return true;
        }

        private static readonly string[] VirtualMarkers =
        {
            "vmware", "virtualbox", "hyper-v", "vethernet", "virtual", "tap-", "tap adapter",
            "loopback", "bluetooth", "vpn", "wintun", "wireguard"
        };

        /// <summary>
        /// Virtual adapters (VMware/Hyper-V/VPN) are up and countable but they carry the same
        /// traffic as the physical NIC, so summing them double counts. design asks for physical
        /// adapters; virtual ones are used only when no physical adapter is up.
        /// </summary>
        internal static bool IsPhysical(NetworkInterface adapter)
        {
            string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            foreach (string marker in VirtualMarkers)
            {
                if (text.Contains(marker)) return false;
            }
            return true;
        }

        public void Reset()
        {
            _hasPrevious = false;
        }
    }
}
