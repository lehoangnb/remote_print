using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RemotePrint.Service;

public static class WireGuardConfig
{
    public static string BuildMesh(string privateKey, string address, IEnumerable<MeshBinding> bindings, int listenPort = 0)
    {
        if (!ValidKey(privateKey) || !ValidAddress(address) || listenPort is < 0 or > 65535) throw new InvalidDataException("Mesh identity không hợp lệ.");
        var peers = bindings.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (peers.Any(p => !ValidKey(p.PublicKey) || !ValidAddress(p.Address) || p.Address == address || p.Port is < 1 or > 65535) ||
            peers.Select(p => p.PublicKey).Distinct().Count() != peers.Length || peers.Select(p => p.Address).Distinct().Count() != peers.Length)
            throw new InvalidDataException("Mesh peer không hợp lệ.");
        var config = new StringBuilder().AppendLine("[Interface]").Append("PrivateKey = ").AppendLine(privateKey)
            .Append("Address = ").Append(address).AppendLine("/32").AppendLine("MTU = 1280")
            .Append("ListenPort = ").AppendLine(listenPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var peer in peers)
            config.AppendLine().AppendLine("[Peer]").Append("PublicKey = ").AppendLine(peer.PublicKey)
                .Append("AllowedIPs = ").Append(peer.Address).AppendLine("/32")
                .Append("Endpoint = 127.0.0.1:").AppendLine(peer.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine("PersistentKeepalive = 15");
        return config.ToString();
    }
    public static bool ValidKey(string? value)
    {
        if (value is null || value.Length != 44) return false;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 32 && bytes.Any(x => x != 0) && Convert.ToBase64String(bytes) == value;
        }
        catch (FormatException) { return false; }
    }

    public static bool ValidAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            address.ToString() != value) return false;
        var bytes = address.GetAddressBytes();
        var index = bytes[2] * 256 + bytes[3];
        return bytes[0] == 10 && bytes[1] == 247 && index is >= 2 and <= 65534;
    }

    public static string AllowedIps(string address, IEnumerable<string> peerAddresses)
    {
        if (!ValidAddress(address)) throw new InvalidDataException("Địa chỉ tunnel không hợp lệ.");
        // The relay is the only WireGuard peer. Limit the inner source and
        // destination addresses it may carry to this Room's discovered peers.
        var peers = peerAddresses.ToArray();
        if (peers.Any(peer => !ValidAddress(peer) || peer == address))
            throw new InvalidDataException("Danh sách tunnel peer không hợp lệ.");
        return string.Join(',', new[] { "10.247.0.1" }
            .Concat(peers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(peer => peer + "/32"));
    }

    public static string Build(string privateKey, string address, string serverKey, string endpoint,
        IEnumerable<string>? peerAddresses = null)
    {
        if (!ValidKey(privateKey) || !ValidKey(serverKey) || !ValidAddress(address))
            throw new InvalidDataException("Cấu hình khóa hoặc địa chỉ tunnel không hợp lệ.");
        var match = Regex.Match(endpoint ?? "", @"^(\[[0-9a-fA-F:.]+\]|[a-zA-Z0-9.-]+):([0-9]{1,5})$");
        if (endpoint is null || endpoint.Length > 260 || !match.Success ||
            !int.TryParse(match.Groups[2].Value, out var port) || port is < 1 or > 65535 ||
            !Uri.TryCreate("http://" + endpoint, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidDataException("Địa chỉ relay không hợp lệ.");
        var allowedIps = AllowedIps(address, peerAddresses ?? []);
        // Only values checked above can enter the WireGuard configuration.
        return new StringBuilder().AppendLine("[Interface]").Append("PrivateKey = ").AppendLine(privateKey)
            // The connected /16 route remains on the adapter when wg.exe changes
            // the live AllowedIPs. With /32, Windows retains only the routes
            // created at service start and newly approved peers are unreachable.
            .Append("Address = ").Append(address).AppendLine("/16").AppendLine()
            .AppendLine("[Peer]").Append("PublicKey = ").AppendLine(serverKey)
            .Append("Endpoint = ").AppendLine(endpoint).Append("AllowedIPs = ").AppendLine(allowedIps)
            .AppendLine("PersistentKeepalive = 25").ToString();
    }
}
