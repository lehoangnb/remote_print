using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemotePrint.Service;

public sealed record MeshTransportIdentity(string ServerUrl, string DeviceId, string DeviceSecret);
public sealed record MeshTransportOptions(string Address, string UdpHost, int UdpPort);

public sealed class MeshPeerState
{
    public string Id { get; set; } = "";
    public string Address { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Token { get; set; } = "";
    public List<string> Endpoints { get; set; } = [];
}
public sealed class MeshState
{
    public string Type { get; set; } = "";
    public string Address { get; set; } = "";
    public string Cookie { get; set; } = "";
    public int LeaseMs { get; set; }
    public List<MeshPeerState> Peers { get; set; } = [];
}
public sealed record MeshBinding(string Id, string Address, string PublicKey, int Port, string Path);

// WireGuard performs all data encryption/authentication. This bridge only moves
// its already-encrypted UDP packets, using authenticated probes to select a path.
// No custom cipher and no decrypted document/IP packet enters this class.
public sealed class MeshTransport : IAsyncDisposable
{
    private sealed class Peer : IDisposable
    {
        public MeshPeerState State;
        public readonly UdpClient Local;
        public readonly int Port;
        public Peer(MeshPeerState state)
        {
            State = state;
            Local = new(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)Local.Client.LocalEndPoint!).Port;
        }
        public IPEndPoint? WireGuard;
        public IPEndPoint? Direct;
        public long LastAck;
        public byte[]? Challenge;
        public long ChallengeAt;
        public double ProbeRtt;
        public void Dispose() => Local.Dispose();
    }
    private readonly MeshTransportIdentity _config;
    private readonly MeshTransportOptions _info;
    private readonly CancellationTokenSource _stop = new();
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Any, 0));
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly List<Task> _localLoops = [];
    private ClientWebSocket? _ws;
    private Task? _run;
    private long _leaseAt;
    private int _leaseMs;
    private string _cookie = "";
    private int _wireGuardPort;
    private int _trafficEnabled = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string? Error { get; private set; }
    public int LocalUdpPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public bool ForceRelay { get; set; }
    public bool Authorized => Volatile.Read(ref _leaseAt) > 0 &&
        Stopwatch.GetElapsedTime(Volatile.Read(ref _leaseAt)).TotalMilliseconds < Volatile.Read(ref _leaseMs);
    public IReadOnlyList<MeshBinding> Bindings => !Authorized ? [] : _peers.Values.Select(p =>
        new MeshBinding(p.State.Id, p.State.Address, p.State.PublicKey, p.Port,
            !ForceRelay && p.Direct is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref p.LastAck)).TotalSeconds < 8 ? "Trực tiếp" : "Qua relay")).ToArray();

    public MeshTransport(MeshTransportIdentity config, MeshTransportOptions info) { _config = config; _info = info; }
    public void SetWireGuardPort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Volatile.Write(ref _wireGuardPort, port);
    }
    public void SetTrafficEnabled(bool enabled) => Volatile.Write(ref _trafficEnabled, enabled ? 1 : 0);
    public void Start() => _run ??= RunAsync(_stop.Token);
    private async Task RunAsync(CancellationToken ct)
    {
        var receiveUdp = ReceiveUdpAsync(ct);
        var retrySeconds = 1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var ws = new ClientWebSocket();
                    _ws = ws;
                    ws.Options.SetRequestHeader("x-device-id", _config.DeviceId);
                    ws.Options.SetRequestHeader("x-device-secret", _config.DeviceSecret);
                    var uri = new UriBuilder(_config.ServerUrl.TrimEnd('/') + "/api/client/mesh");
                    if (uri.Scheme != "https" && !uri.Uri.IsLoopback) throw new InvalidDataException("Mesh yêu cầu HTTPS.");
                    uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connect.CancelAfter(TimeSpan.FromSeconds(10));
                    await ws.ConnectAsync(uri.Uri, connect.Token);
                    Error = null;
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var read = ReadAsync(ws, session.Token);
                    var maintain = MaintainAsync(ws, session.Token);
                    await Task.WhenAny(read, maintain);
                    session.Cancel(); ws.Abort();
                    try { await Task.WhenAll(read, maintain); } catch (OperationCanceledException) { }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Error = ex.Message; }
                finally { _ws = null; Revoke(); }
                retrySeconds = Stopwatch.GetElapsedTime(started).TotalSeconds > 10 ? 1 : Math.Min(30, retrySeconds * 2);
                await Task.Delay(TimeSpan.FromMilliseconds(retrySeconds * 1000 + Random.Shared.Next(500)), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            Revoke(); _stop.Cancel();
            try { await receiveUdp; } catch (OperationCanceledException) { }
        }
    }

    private void Revoke()
    {
        Volatile.Write(ref _leaseAt, 0);
        foreach (var (id, peer) in _peers) if (_peers.TryRemove(id, out _)) peer.Dispose();
    }
    private async Task ReadAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var count = 0;
            WebSocketReceiveResult message;
            do
            {
                if (count == buffer.Length) throw new InvalidDataException("Mesh frame quá lớn.");
                message = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), ct);
                if (message.MessageType == WebSocketMessageType.Close) return;
                count += message.Count;
            } while (!message.EndOfMessage);
            if (message.MessageType == WebSocketMessageType.Text)
            {
                var state = JsonSerializer.Deserialize<MeshState>(buffer.AsSpan(0, count), JsonOptions)
                    ?? throw new InvalidDataException("Thiếu mesh state.");
                ValidateState(state, _config.DeviceId, _info.Address);
                var desired = state.Peers.ToDictionary(p => p.Id, StringComparer.Ordinal);
                foreach (var (id, p) in _peers)
                    if (!desired.TryGetValue(id, out var next) || next.Address != p.State.Address ||
                        next.PublicKey != p.State.PublicKey || next.Token != p.State.Token)
                        if (_peers.TryRemove(id, out _)) p.Dispose();
                foreach (var p in state.Peers)
                {
                    if (_peers.TryGetValue(p.Id, out var existing)) { existing.State = p; continue; }
                    var peer = new Peer(p);
                    _peers[p.Id] = peer;
                    lock (_localLoops) { _localLoops.RemoveAll(t => t.IsCompleted); _localLoops.Add(ReceiveLocalAsync(peer, ct)); }
                }
                _cookie = state.Cookie;
                Volatile.Write(ref _leaseMs, state.LeaseMs);
                Volatile.Write(ref _leaseAt, Stopwatch.GetTimestamp());
            }
            else if (Authorized && count > 36 && _peers.TryGetValue(Encoding.ASCII.GetString(buffer, 0, 36), out var peer))
                await DeliverAsync(peer, buffer.AsMemory(36, count - 36), ct);
        }
    }

    public static void ValidateState(MeshState state, string ownId, string address)
    {
        if (state.Type != "state" || state.Address != address || !WireGuardConfig.ValidAddress(address) ||
            state.LeaseMs is < 2000 or > 15000 || !HexToken(state.Cookie) || state.Peers.Count > 256 ||
            state.Peers.Any(p => !Guid.TryParseExact(p.Id, "D", out _) || p.Id == ownId ||
                !WireGuardConfig.ValidAddress(p.Address) || p.Address == address || !WireGuardConfig.ValidKey(p.PublicKey) ||
                !HexToken(p.Token) || p.Endpoints.Count > 17 || p.Endpoints.Any(e => !ValidEndpoint(e))) ||
            state.Peers.Select(p => p.Id).Distinct().Count() != state.Peers.Count ||
            state.Peers.Select(p => p.Address).Distinct().Count() != state.Peers.Count ||
            state.Peers.Select(p => p.PublicKey).Distinct().Count() != state.Peers.Count)
            throw new InvalidDataException("Mesh state không hợp lệ.");
    }
    private static bool HexToken(string token) => token.Length == 64 && token.All(c => char.IsAsciiHexDigit(c));
    private static bool ValidEndpoint(string text) => IPEndPoint.TryParse(text, out var ep) &&
        ep.Address.AddressFamily == AddressFamily.InterNetwork && ep.Port > 0 &&
        !IPAddress.IsLoopback(ep.Address) && ep.Address.GetAddressBytes()[0] is > 0 and < 224 &&
        !ep.Address.Equals(IPAddress.Broadcast) && !WireGuardConfig.ValidAddress(ep.Address.ToString());

    private async Task MaintainAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var host = string.IsNullOrEmpty(_info.UdpHost) ? new Uri(_config.ServerUrl).DnsSafeHost : _info.UdpHost;
        IPAddress[] ips;
        using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dnsTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { ips = await Dns.GetHostAddressesAsync(host, dnsTimeout.Token); }
        catch (SocketException) { ips = []; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ips = []; }
        var server = ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
        // UDP is optional: networks without usable IPv4 still retain WSS relay.
        var endpoint = server is null ? null : new IPEndPoint(server, _info.UdpPort);
        while (!ct.IsCancellationRequested)
        {
            if (Volatile.Read(ref _leaseAt) > 0 && !Authorized) throw new IOException("Mesh lease hết hạn.");
            var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            var lan = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ip) && !WireGuardConfig.ValidAddress(ip.ToString()))
                .Select(ip => ip.ToString()).Distinct().Take(16).ToArray();
            await SendWsAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "ping", lan, port }), WebSocketMessageType.Text, ct);
            if (_cookie.Length == 64 && endpoint is not null)
                await SendUdpAsync(Encoding.ASCII.GetBytes("RPM1" + _cookie), endpoint, ct);
            if (Authorized && !ForceRelay)
                foreach (var peer in _peers.Values)
                {
                    peer.Challenge = RandomNumberGenerator.GetBytes(8); peer.ChallengeAt = Stopwatch.GetTimestamp();
                    peer.ProbeRtt = double.PositiveInfinity;
                    foreach (var target in peer.State.Endpoints)
                    {
                        var probe = EncodeDirect(_config.DeviceId, 1, peer.Challenge, [], peer.State.Token);
                        await SendUdpAsync(probe, IPEndPoint.Parse(target), ct);
                    }
                }
            await Task.Delay(2000, ct);
        }
    }
    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
    private async Task ReceiveLocalAsync(Peer peer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await peer.Local.ReceiveAsync(ct);
                if (!Authorized || Volatile.Read(ref _trafficEnabled) == 0 || !_peers.TryGetValue(peer.State.Id, out var current) || current != peer ||
                    !IPAddress.IsLoopback(packet.RemoteEndPoint.Address) || !WireGuardPacket(packet.Buffer)) continue;
                var expectedPort = Volatile.Read(ref _wireGuardPort);
                if (expectedPort > 0 && packet.RemoteEndPoint.Port != expectedPort) continue;
                if (expectedPort == 0 && peer.WireGuard is not null && !peer.WireGuard.Equals(packet.RemoteEndPoint)) continue;
                peer.WireGuard = packet.RemoteEndPoint;
                var direct = peer.Direct;
                if (!ForceRelay && direct is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref peer.LastAck)).TotalSeconds < 8)
                    await SendUdpAsync(EncodeDirect(_config.DeviceId, 3, new byte[8], packet.Buffer, peer.State.Token), direct, ct);
                else
                {
                    var frame = new byte[36 + packet.Buffer.Length];
                    Encoding.ASCII.GetBytes(peer.State.Id, frame); packet.Buffer.CopyTo(frame, 36);
                    await SendWsAsync(frame, WebSocketMessageType.Binary, ct);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or WebSocketException) { }
    }
    private async Task ReceiveUdpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(ct); }
            catch (SocketException) { continue; }
            if (!Authorized || ForceRelay || packet.Buffer.Length < 77) continue;
            var id = Encoding.ASCII.GetString(packet.Buffer, 0, 36);
            if (!_peers.TryGetValue(id, out var peer) || !VerifyDirect(packet.Buffer, peer.State.Token)) continue;
            var kind = packet.Buffer[36];
            if (kind == 1 && packet.Buffer.Length == 77)
                await SendUdpAsync(EncodeDirect(_config.DeviceId, 2, packet.Buffer.AsSpan(37, 8).ToArray(), [], peer.State.Token), packet.RemoteEndPoint, ct);
            else if (kind == 2 && packet.Buffer.Length == 77 && peer.Challenge is { } challenge &&
                Stopwatch.GetElapsedTime(peer.ChallengeAt).TotalSeconds < 5 && packet.Buffer.AsSpan(37, 8).SequenceEqual(challenge))
            {
                var rtt = Stopwatch.GetElapsedTime(peer.ChallengeAt).TotalMilliseconds;
                if (rtt < peer.ProbeRtt)
                {
                    peer.ProbeRtt = rtt;
                    peer.Direct = packet.RemoteEndPoint;
                    Volatile.Write(ref peer.LastAck, Stopwatch.GetTimestamp());
                }
            }
            // NAT can permit only one direction directly. Receiving a packet
            // does not require our outgoing path to be direct too: the pair
            // MAC and WireGuard authenticate it independently. Outgoing path
            // selection still requires a response to our fresh challenge.
            else if (kind == 3)
                await DeliverAsync(peer, packet.Buffer.AsMemory(45, packet.Buffer.Length - 77), ct);
        }
    }
    private async Task SendUdpAsync(byte[] frame, IPEndPoint endpoint, CancellationToken ct)
    {
        try { await _udp.SendAsync(frame, endpoint, ct); }
        catch (SocketException) { /* UDP blocked/unreachable: WSS remains usable. */ }
    }
    private async Task DeliverAsync(Peer peer, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!Authorized || Volatile.Read(ref _trafficEnabled) == 0 || !_peers.TryGetValue(peer.State.Id, out var current) || current != peer) return;
        var port = Volatile.Read(ref _wireGuardPort);
        var wg = port > 0 ? new IPEndPoint(IPAddress.Loopback, port) : peer.WireGuard;
        if (wg is not null && WireGuardPacket(payload.Span))
            try { await peer.Local.SendAsync(payload, wg, ct); }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { }
    }
    private async Task SendWsAsync(byte[] frame, WebSocketMessageType type, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (_ws is { State: WebSocketState.Open } ws) await ws.SendAsync(frame.AsMemory(), type, true, timeout.Token);
        }
        finally { _send.Release(); }
    }
    public static bool WireGuardPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4 || packet.Length > 2048) return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(packet) switch
        { 1 => packet.Length == 148, 2 => packet.Length == 92, 3 => packet.Length == 64,
            4 => packet.Length >= 32 && packet.Length % 16 == 0, _ => false };
    }
    public static byte[] EncodeDirect(string sender, byte kind, byte[] nonce, byte[] payload, string token)
    {
        var frame = new byte[77 + payload.Length];
        Encoding.ASCII.GetBytes(sender, frame); frame[36] = kind;
        nonce.CopyTo(frame, 37); payload.CopyTo(frame, 45);
        HMACSHA256.HashData(Convert.FromHexString(token), frame.AsSpan(0, frame.Length - 32), frame.AsSpan(frame.Length - 32));
        return frame;
    }
    public static bool VerifyDirect(byte[] frame, string token) => frame.Length is >= 77 and <= 2125 &&
        CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Convert.FromHexString(token), frame.AsSpan(0, frame.Length - 32)), frame.AsSpan(frame.Length - 32));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _ws?.Abort(); Revoke();
        if (_run is not null) await _run;
        Task[] loops; lock (_localLoops) loops = _localLoops.ToArray();
        await Task.WhenAll(loops);
        _udp.Dispose(); _stop.Dispose(); _send.Dispose();
    }
}
