using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LocalModelHub.Core.Services;

/// <summary>局域网地址探测，用于展示 API 接入地址。</summary>
public static class NetworkHelper
{
    /// <summary>返回首个可用的局域网 IPv4 地址；找不到时回退到 127.0.0.1。</summary>
    public static string GetLanAddress()
    {
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Unknown)
                {
                    continue;
                }

                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;

                    var bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA，未拿到 DHCP
                    return unicast.Address.ToString();
                }
            }
        }
        catch
        {
            // 网络接口枚举失败时回退到本机地址
        }

        return "127.0.0.1";
    }
}
