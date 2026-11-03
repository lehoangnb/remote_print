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
public sealed record MeshTransportOptions(string Address, string UdpHost, int UdpPort, string StunHost = "", int StunPort = 3478);

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
    public string? ObservedEndpoint { get; set; }
    public List<MeshPeerState> Peers { get; set; } = [];
}
public sealed record MeshBinding(string Id, string Address, string PublicKey, int Port, string Path);
public sealed record MeshPeerDiagnostics(string Id, string[] Candidates, string? DirectEndpoint,
    long ProbesSent, long ProbesReceived, long AcksReceived, long DirectBytesSent, long DirectBytesReceived,
    long RelayBytesSent, long RelayBytesReceived);
public sealed record MeshTransportDiagnostics(int LocalUdpPort, string DiscoveryHost, string[] DiscoveryEndpoints,
    string? ObservedEndpoint, string? DnsError, string? UdpError, long DiscoverySent, long AuthenticationRejected,
    string StunServer, string[] PublicEndpoints, MeshPeerDiagnostics[] Peers);

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
        public IPEndPoint? Reflexive;
        public long ReflexiveProbeAt;
        public long LastAck;
        public byte[]? Challenge;
        public long ChallengeAt;
        public double ProbeRtt;
        public long ProbesSent, ProbesReceived, AcksReceived;
        public long DirectBytesSent, DirectBytesReceived, RelayBytesSent, RelayBytesReceived;
        public void Dispose() => Local.Dispose();
    }
    private readonly MeshTransportIdentity _config;
    private readonly MeshTransportOptions _info;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
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
    private IPEndPoint[] _discoveryEndpoints = [];
    private string? _observedEndpoint, _dnsError, _udpError;
    private long _discoverySent, _authenticationRejected;
    private sealed record StunPending(byte[] Request, IPEndPoint Server, long At);
    private readonly ConcurrentDictionary<string, StunPending> _stunRequests = new();
    private readonly ConcurrentDictionary<string, long> _publicEndpoints = new();
    private int _wireGuardPort;
    private int _trafficEnabled = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string? Error { get; private set; }
    public int LocalUdpPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public bool ForceRelay { get; set; }
    private string DiscoveryHost => string.IsNullOrEmpty(_info.UdpHost) ? new Uri(_config.ServerUrl).DnsSafeHost : _info.UdpHost;
    public MeshTransportDiagnostics Diagnostics => new(LocalUdpPort, DiscoveryHost,
        Volatile.Read(ref _discoveryEndpoints).Select(e => e.ToString()).ToArray(), _observedEndpoint,
        _dnsError, _udpError, Interlocked.Read(ref _discoverySent), Interlocked.Read(ref _authenticationRejected),
        string.IsNullOrEmpty(_info.StunHost) ? "" : $"{_info.StunHost}:{_info.StunPort}", PublicEndpoints,
        _peers.Values.Select(p => new MeshPeerDiagnostics(p.State.Id, p.State.Endpoints.ToArray(), p.Direct?.ToString(),
            Interlocked.Read(ref p.ProbesSent), Interlocked.Read(ref p.ProbesReceived), Interlocked.Read(ref p.AcksReceived),
            Interlocked.Read(ref p.DirectBytesSent), Interlocked.Read(ref p.DirectBytesReceived),
            Interlocked.Read(ref p.RelayBytesSent), Interlocked.Read(ref p.RelayBytesReceived))).ToArray());
    private string[] PublicEndpoints => _publicEndpoints.Where(p => Stopwatch.GetElapsedTime(p.Value).TotalSeconds < 60)
        .Select(p => p.Key).Take(4).ToArray();
    public IReadOnlyList<string> DirectPeerAddresses => _peers.Values.SelectMany(p => p.State.Endpoints
        .Select(e => IPEndPoint.Parse(e).Address.ToString())
        .Concat(p.Reflexive is null ? [] : new[] { p.Reflexive.Address.ToString() }))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public bool Authorized => Volatile.Read(ref _leaseAt) > 0 &&
        Stopwatch.GetElapsedTime(Volatile.Read(ref _leaseAt)).TotalMilliseconds < Volatile.Read(ref _leaseMs);
    public IReadOnlyList<MeshBinding> Bindings => !Authorized ? [] : _peers.Values.Select(p =>
        new MeshBinding(p.State.Id, p.State.Address, p.State.PublicKey, p.Port,
            !ForceRelay && p.Direct is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref p.LastAck)).TotalSeconds < 8 ? "Trực tiếp" : "Qua relay")).ToArray();

    public MeshTransport(MeshTransportIdentity config, MeshTransportOptions info,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    { _config = config; _info = info; _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct)); }
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
                _observedEndpoint = state.ObservedEndpoint;
                Volatile.Write(ref _leaseMs, state.LeaseMs);
                Volatile.Write(ref _leaseAt, Stopwatch.GetTimestamp());
            }
            else if (Authorized && count > 36 && _peers.TryGetValue(Encoding.ASCII.GetString(buffer, 0, 36), out var peer))
            {
                Interlocked.Add(ref peer.RelayBytesReceived, count - 36);
                await DeliverAsync(peer, buffer.AsMemory(36, count - 36), ct);
            }
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
        long resolvedAt = 0;
        long stunAt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (Volatile.Read(ref _leaseAt) > 0 && !Authorized) throw new IOException("Mesh lease hết hạn.");
            // A DNS failure at service startup must not disable discovery until
            // the next WS reconnect. Refresh successful answers too (DDNS/VPN).
            if (resolvedAt == 0 || Stopwatch.GetElapsedTime(resolvedAt).TotalSeconds >=
                (Volatile.Read(ref _discoveryEndpoints).Length == 0 ? 5 : 30))
            {
                resolvedAt = Stopwatch.GetTimestamp();
                using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dnsTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var ips = await _resolve(DiscoveryHost, dnsTimeout.Token);
                    var endpoints = ips.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Distinct()
                        .Take(8).Select(ip => new IPEndPoint(ip, _info.UdpPort)).ToArray();
                    Volatile.Write(ref _discoveryEndpoints, endpoints);
                    _dnsError = endpoints.Length == 0 ? "No IPv4 discovery address" : null;
                }
                catch (SocketException ex) { _dnsError = ex.SocketErrorCode.ToString(); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { _dnsError = "DNS timeout"; }
            }
            var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            var lan = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ip) && !WireGuardConfig.ValidAddress(ip.ToString()))
                .Select(ip => ip.ToString()).Distinct().Take(16).ToArray();
            foreach (var pending in _stunRequests.Where(p => Stopwatch.GetElapsedTime(p.Value.At).TotalSeconds > 10)) _stunRequests.TryRemove(pending.Key, out _);
            foreach (var known in _publicEndpoints.Where(p => Stopwatch.GetElapsedTime(p.Value).TotalSeconds >= 60)) _publicEndpoints.TryRemove(known.Key, out _);
            if (Authorized && !string.IsNullOrEmpty(_info.StunHost) &&
                (stunAt == 0 || Stopwatch.GetElapsedTime(stunAt).TotalSeconds >= 15))
            {
                stunAt = Stopwatch.GetTimestamp();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var ips = await Dns.GetHostAddressesAsync(_info.StunHost, timeout.Token);
                    foreach (var ip in ips.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Distinct().Take(2))
                    {
                        var request = StunProtocol.Request();
                        var server = new IPEndPoint(ip, _info.StunPort);
                        _stunRequests[Convert.ToHexString(request.AsSpan(8))] = new(request, server, Stopwatch.GetTimestamp());
                        await SendUdpAsync(request, server, ct);
                    }
                }
                catch (SocketException) { /* STUN is optional; retain native discovery/relay. */ }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            await SendWsAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "ping", lan, port, publicEndpoints = PublicEndpoints }), WebSocketMessageType.Text, ct);
            if (_cookie.Length == 64)
                foreach (var endpoint in Volatile.Read(ref _discoveryEndpoints))
                    if (await SendUdpAsync(Encoding.ASCII.GetBytes("RPM1" + _cookie), endpoint, ct))
                        Interlocked.Increment(ref _discoverySent);
            if (Authorized && !ForceRelay)
                foreach (var peer in _peers.Values)
                {
                    peer.Challenge = RandomNumberGenerator.GetBytes(8); peer.ChallengeAt = Stopwatch.GetTimestamp();
                    peer.ProbeRtt = double.PositiveInfinity;
                    var targets = peer.State.Endpoints
                        .Concat(peer.Reflexive is null ? [] : new[] { peer.Reflexive.ToString() })
                        .Concat(peer.Direct is null ? [] : new[] { peer.Direct.ToString() }).Distinct();
                    foreach (var target in targets)
                    {
                        var probe = EncodeDirect(_config.DeviceId, 1, peer.Challenge, [], peer.State.Token);
                        Interlocked.Increment(ref peer.ProbesSent);
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
                if (!ForceRelay && direct is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref peer.LastAck)).TotalSeconds < 8 &&
                    await SendUdpAsync(EncodeDirect(_config.DeviceId, 3, new byte[8], packet.Buffer, peer.State.Token), direct, ct))
                    Interlocked.Add(ref peer.DirectBytesSent, packet.Buffer.Length);
                else
                {
                    var frame = new byte[36 + packet.Buffer.Length];
                    Encoding.ASCII.GetBytes(peer.State.Id, frame); packet.Buffer.CopyTo(frame, 36);
                    await SendWsAsync(frame, WebSocketMessageType.Binary, ct);
                    Interlocked.Add(ref peer.RelayBytesSent, packet.Buffer.Length);
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
            if (Authorized && packet.Buffer.Length >= 20 && packet.Buffer[0] == 1 && packet.Buffer[1] == 1 &&
                _stunRequests.TryGetValue(Convert.ToHexString(packet.Buffer.AsSpan(8, 12)), out var pending) &&
                pending.Server.Equals(packet.RemoteEndPoint) && Stopwatch.GetElapsedTime(pending.At).TotalSeconds < 10 &&
                StunProtocol.Response(packet.Buffer, pending.Request) is { } mapped && !IsPrivate(mapped.Address) && ValidEndpoint(mapped.ToString()))
            {
                _stunRequests.TryRemove(Convert.ToHexString(packet.Buffer.AsSpan(8, 12)), out _);
                _publicEndpoints[mapped.ToString()] = Stopwatch.GetTimestamp();
                continue;
            }
            if (!Authorized || ForceRelay || packet.Buffer.Length < 77) continue;
            var id = Encoding.ASCII.GetString(packet.Buffer, 0, 36);
            if (!_peers.TryGetValue(id, out var peer) || !VerifyDirect(packet.Buffer, peer.State.Token))
            { Interlocked.Increment(ref _authenticationRejected); continue; }
            var kind = packet.Buffer[36];
            if (kind == 1 && packet.Buffer.Length == 77)
            {
                Interlocked.Increment(ref peer.ProbesReceived);
                await SendUdpAsync(EncodeDirect(_config.DeviceId, 2, packet.Buffer.AsSpan(37, 8).ToArray(), [], peer.State.Token), packet.RemoteEndPoint, ct);
                // The mapping to us can differ from the mapping to discovery.
                // Verify the newly observed source by challenging it too. Limit
                // reciprocal probes so two peers cannot form a probe storm.
                var probeReflexive = peer.Reflexive is null || !peer.Reflexive.Equals(packet.RemoteEndPoint) ||
                    Stopwatch.GetElapsedTime(peer.ReflexiveProbeAt).TotalSeconds >= 2;
                peer.Reflexive = packet.RemoteEndPoint;
                if (probeReflexive && peer.Challenge is { } currentChallenge && Stopwatch.GetElapsedTime(peer.ChallengeAt).TotalSeconds < 5)
                {
                    peer.ReflexiveProbeAt = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref peer.ProbesSent);
                    await SendUdpAsync(EncodeDirect(_config.DeviceId, 1, currentChallenge, [], peer.State.Token), packet.RemoteEndPoint, ct);
                }
            }
            else if (kind == 2 && packet.Buffer.Length == 77 && peer.Challenge is { } challenge &&
                Stopwatch.GetElapsedTime(peer.ChallengeAt).TotalSeconds < 5 && packet.Buffer.AsSpan(37, 8).SequenceEqual(challenge))
            {
                Interlocked.Increment(ref peer.AcksReceived);
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
            {
                Interlocked.Add(ref peer.DirectBytesReceived, packet.Buffer.Length - 77);
                await DeliverAsync(peer, packet.Buffer.AsMemory(45, packet.Buffer.Length - 77), ct);
            }
        }
    }
    private async Task<bool> SendUdpAsync(byte[] frame, IPEndPoint endpoint, CancellationToken ct)
    {
        try { await _udp.SendAsync(frame, endpoint, ct); return true; }
        catch (SocketException ex) { _udpError = ex.SocketErrorCode.ToString(); return false; }
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
