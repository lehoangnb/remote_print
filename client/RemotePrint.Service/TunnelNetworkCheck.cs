using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemotePrint.Service;

public static class TunnelNetworkCheck
{
    private const uint First = 0x0AF70000; // 10.247.0.0
    private const uint Last = 0x0AF7FFFF;

    public static bool Overlaps(IPAddress address, IPAddress mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork) return false;
        var ip = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var bits = BinaryPrimitives.ReadUInt32BigEndian(mask.GetAddressBytes());
        var first = ip & bits;
        var last = first | ~bits;
        return first <= Last && last >= First;
    }

    public static void RequireNoConflict(string managedAdapter)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                string.Equals(adapter.Name, managedAdapter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask is { } mask &&
                    Overlaps(address.Address, mask))
                    throw new InvalidOperationException($"Dải tunnel 10.247.0.0/16 trùng mạng của {adapter.Name}.");
        }
    }
}
