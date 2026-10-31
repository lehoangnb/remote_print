using System.Net.NetworkInformation;
using RemotePrint.Client;

namespace RemotePrint.Service;

// Restrict the inbound rule to the managed tunnel adapter, its assigned address,
// and the current Room's peers. Mesh traffic remains encrypted at the relay,
// so Windows scopes inbound access to the native relay service executable.
public sealed class TunnelFirewall(string tunnelName)
{
    private string RuleName => "Remote Print tunnel " + tunnelName;
    private const string Marker = "RemotePrint managed tunnel firewall rule v1";
    private string? _lastSpec;
    private readonly string[] _meshSuffixes = [" SMB", " RPC mapper", " Spooler", " Native relay", " Other TCP", " UDP", " ICMP"];

    public void ApplyMesh(string localAddress, IEnumerable<string> peers)
    {
        var addresses = peers.Distinct(StringComparer.Ordinal).Order().ToArray();
        if (!WireGuardConfig.ValidAddress(localAddress) || addresses.Any(a => !WireGuardConfig.ValidAddress(a) || a == localAddress))
            throw new InvalidDataException("Địa chỉ mesh firewall không hợp lệ.");
        var spec = "native:" + localAddress + ":" + string.Join(',', addresses);
        if (_lastSpec == spec) return;
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(tunnelName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Không tìm thấy card mạng WireGuard.");
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        RemoveOwnedRule(policy);
        foreach (var suffix in _meshSuffixes) RemoveOwnedRule(policy, RuleName + suffix);
        if (addresses.Length > 0)
        {
            Add(" Native relay", 6, NativePrintProtocol.Port.ToString(), 1, application: Environment.ProcessPath);
        }
        // Explicit deny rules prevent existing broad Windows sharing rules from
        // opening unrelated TCP ports/UDP/ICMP on this managed adapter.
        Add(" Other TCP", 6, NativeBlockedTcpPorts, 0);
        Add(" UDP", 17, "1-65535", 0);
        Add(" ICMP", 1, null, 0);
        _lastSpec = spec;

        void Add(string suffix, int protocol, string? ports, int action, string? service = null, string? application = null)
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            rule.Name = RuleName + suffix; rule.Description = Marker; rule.Protocol = protocol;
            if (ports is not null) rule.LocalPorts = ports;
            rule.LocalAddresses = localAddress;
            rule.RemoteAddresses = action == 1 ? string.Join(',', addresses) : "*";
            rule.Interfaces = new object[] { adapter.Name };
            rule.Direction = 1; rule.Action = action; rule.Profiles = 7;
            if (service is not null) rule.ServiceName = service;
            if (application is not null) rule.ApplicationName = application;
            rule.Enabled = true; policy.Rules.Add(rule);
        }
    }

    public static string NativeBlockedTcpPorts => $"1-{NativePrintProtocol.Port - 1},{NativePrintProtocol.Port + 1}-65535";

    public void Remove()
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        RemoveOwnedRule(policy);
        foreach (var suffix in _meshSuffixes) RemoveOwnedRule(policy, RuleName + suffix);
        _lastSpec = null;
    }

    private void RemoveOwnedRule(dynamic policy, string? name = null)
    {
        name ??= RuleName;
        foreach (var item in policy.Rules)
        {
            dynamic rule = item;
            if (!string.Equals((string)rule.Name, name, StringComparison.Ordinal)) continue;
            if (!string.Equals((string)rule.Description, Marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Tên quy tắc firewall đã được ứng dụng khác sử dụng.");
            policy.Rules.Remove(name);
            return;
        }
    }
}
