using RemotePrint.Client;
using System.Net;
using System.Net.Sockets;

namespace RemotePrint.Service;

// Authorization is independent of discovery/configuration polling. Expiring
// the control lease stops the packet bridge without waiting for the UI.
public sealed class MeshRuntime : IAsyncDisposable
{
    private readonly WireGuardManager _wireguard;
    private readonly TunnelFirewall _firewall;
    private readonly MeshTransport _transport;
    private readonly TunnelInfo _info;
    private readonly Func<bool> _sharesEnabled;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private readonly int _listenPort;
    private IReadOnlyList<MeshBinding> _ready = [];
    public IReadOnlyList<MeshBinding> Ready => Volatile.Read(ref _ready);
    public string Status { get; private set; } = "Đang kết nối relay";
    public bool Authorized => _transport.Authorized;
    public MeshTransportDiagnostics Diagnostics => _transport.Diagnostics;
    public bool Matches(TunnelInfo info) => _info.Address == info.Address && _info.UdpHost == info.UdpHost && _info.UdpPort == info.UdpPort &&
        _info.StunHost == info.StunHost && _info.StunPort == info.StunPort;

    public MeshRuntime(ClientConfig config, TunnelInfo info, WireGuardManager wireguard,
        TunnelFirewall firewall, Func<bool> sharesEnabled)
    {
        _info = info; _wireguard = wireguard; _firewall = firewall; _sharesEnabled = sharesEnabled;
        _transport = new MeshTransport(new(config.ServerUrl, config.DeviceId, config.DeviceSecret),
            new(info.Address, info.UdpHost, info.UdpPort, info.StunHost, info.StunPort));
        using (var port = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            _listenPort = ((IPEndPoint)port.Client.LocalEndPoint!).Port;
        _transport.SetWireGuardPort(_listenPort);
        _transport.SetTrafficEnabled(false);
        _transport.Start(); _run = RunAsync(_stop.Token);
    }
    private async Task RunAsync(CancellationToken ct)
    {
        var active = false;
        var applied = "";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_transport.Authorized)
                    {
                        Volatile.Write(ref _ready, []);
                        _transport.SetTrafficEnabled(false); applied = "";
                        Status = "Đang kết nối relay";
                        if (active)
                        {
                            _firewall.Remove(); await _wireguard.StopAsync(ct); active = false;
                        }
                        else _firewall.ApplyDirect(_transport.LocalUdpPort, []);
                    }
                    else
                    {
                        var peers = _transport.Bindings;
                        var enabled = _sharesEnabled();
                        var spec = enabled + ":" + string.Join(';', peers.OrderBy(p => p.Id).Select(p => $"{p.PublicKey}:{p.Address}:{p.Port}"));
                        if (applied != spec) _transport.SetTrafficEnabled(false);
                        TunnelNetworkCheck.RequireNoConflict(_wireguard.TunnelName);
                        await _wireguard.EnsureMeshAsync(_info.Address, peers, _listenPort, ct);
                        active = true;
                        _firewall.ApplyMesh(_info.Address, enabled ? peers.Select(p => p.Address) : []);
                        _firewall.ApplyDirect(_transport.LocalUdpPort, _transport.DirectPeerAddresses);
                        if (_transport.Authorized) { _transport.SetTrafficEnabled(true); applied = spec; }
                        var handshakes = await _wireguard.RecentMeshHandshakesAsync(ct);
                        Volatile.Write(ref _ready, peers.Where(p => handshakes.Contains(p.PublicKey)).ToArray());
                        Status = "Đã kết nối";
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Status = "Tunnel lỗi: " + ex.Message;
                    _transport.SetTrafficEnabled(false); applied = "";
                    Volatile.Write(ref _ready, []);
                    try { _firewall.Remove(); await _wireguard.StopAsync(ct); } catch { }
                    active = false;
                }
                await Task.Delay(2000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _transport.DisposeAsync();
        await _run;
        _stop.Dispose();
        _firewall.Remove();
        await _wireguard.StopAsync(CancellationToken.None);
    }
}
