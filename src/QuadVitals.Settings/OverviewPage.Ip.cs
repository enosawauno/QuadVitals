using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    internal sealed partial class OverviewPage
    {
        private void UpdateLanIp(MetricsSnapshot snapshot)
        {
            string adapter = snapshot == null ? string.Empty : (snapshot.AdapterName ?? string.Empty);
            bool changed = !string.Equals(adapter, _lanAdapterName, StringComparison.OrdinalIgnoreCase);
            if (!changed && (DateTime.UtcNow - _lanCheckedUtc).TotalSeconds < 5) return;

            _lanAdapterName = adapter;
            _lanCheckedUtc = DateTime.UtcNow;

            string ip = ResolveLanIp(adapter);
            if (string.Equals(_lanIp, ip, StringComparison.Ordinal)) return;

            _lanIp = ip;
            _lanIpValue.ToolTip = ip == MetricFormat.Invalid
                ? "未找到活动网卡的 IPv4 地址"
                : "局域网 IPv4（当前网卡）";
            ApplyIpDisplay();
        }

        private static string ResolveLanIp(string adapterName)
        {
            NetworkInterface[] adapters;
            try
            {
                adapters = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                return MetricFormat.Invalid;
            }

            NetworkInterface chosen = null;

            if (!string.IsNullOrEmpty(adapterName))
            {
                foreach (NetworkInterface adapter in adapters)
                {
                    if (IsUsable(adapter) && string.Equals(adapter.Name, adapterName, StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = adapter;
                        break;
                    }
                }
            }

            if (chosen == null)
            {
                foreach (NetworkInterface adapter in adapters)
                {
                    if (IsUsable(adapter) && !IsVirtual(adapter) && HasGateway(adapter) && FirstIPv4(adapter) != null)
                    {
                        chosen = adapter;
                        break;
                    }
                }
            }

            if (chosen == null)
            {
                foreach (NetworkInterface adapter in adapters)
                {
                    if (IsUsable(adapter) && !IsVirtual(adapter) && FirstIPv4(adapter) != null)
                    {
                        chosen = adapter;
                        break;
                    }
                }
            }

            if (chosen == null)
            {
                foreach (NetworkInterface adapter in adapters)
                {
                    if (IsUsable(adapter) && FirstIPv4(adapter) != null)
                    {
                        chosen = adapter;
                        break;
                    }
                }
            }

            return chosen == null ? MetricFormat.Invalid : (FirstIPv4(chosen) ?? MetricFormat.Invalid);
        }

        private static bool IsUsable(NetworkInterface adapter)
        {
            return adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                && adapter.Supports(NetworkInterfaceComponent.IPv4);
        }

        private static bool HasGateway(NetworkInterface adapter)
        {
            try { return adapter.GetIPProperties().GatewayAddresses.Count > 0; }
            catch (NetworkInformationException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        private static bool IsVirtual(NetworkInterface adapter)
        {
            string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            return text.Contains("vmware") || text.Contains("virtualbox") || text.Contains("hyper-v")
                || text.Contains("vethernet") || text.Contains("virtual") || text.Contains("tap-")
                || text.Contains("vpn") || text.Contains("wintun") || text.Contains("wireguard")
                || text.Contains("bluetooth");
        }

        private static string FirstIPv4(NetworkInterface adapter)
        {
            try
            {
                foreach (UnicastIPAddressInformation info in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(info.Address)) continue;
                    string text = info.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    return text;
                }
            }
            catch (NetworkInformationException) { }
            catch (NotSupportedException) { }
            return null;
        }

        private async void RefreshPublicIp(bool force)
        {
            if (_publicIpFetching) return;
            if (!force && (DateTime.UtcNow - _publicIpFetchedUtc).TotalMinutes < 5) return;

            _publicIpFetching = true;
            ApplyIpDisplay();

            try
            {
                string ip = await Task.Run(() => FetchPublicIp());
                _publicIpFetchedUtc = DateTime.UtcNow;

                if (!string.IsNullOrEmpty(ip))
                {
                    _publicIp = ip;
                    _publicIpValue.ToolTip = "公网 IP（每 5 分钟更新）";
                }
                else
                {
                    _publicIp = string.Empty;
                    _publicIpValue.ToolTip = "公网 IP 获取失败（网络不可用）";
                }
            }
            catch (Exception)
            {
                _publicIp = string.Empty;
                _publicIpValue.ToolTip = "公网 IP 获取失败（网络不可用）";
            }
            finally
            {
                _publicIpFetching = false;
                ApplyIpDisplay();
            }
        }

        private static string FetchPublicIp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            foreach (string endpoint in PublicIpEndpoints)
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
                    request.Timeout = 6000;
                    request.ReadWriteTimeout = 6000;
                    request.UserAgent = "QuadVitals/1.0";

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        string text = reader.ReadToEnd().Trim();
                        IPAddress address;
                        if (IPAddress.TryParse(text, out address)) return address.ToString();
                    }
                }
                catch (Exception)
                {
                    // Try the next endpoint; a blocked public-IP service must not affect the dashboard.
                }
            }
            return null;
        }
    }
}
