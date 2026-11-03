using System.Diagnostics;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using RemotePrint.Client;
using RemotePrint.Service;

internal static partial class Program
{
    private static async Task RunMeshChecksAsync(string root)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var peerId = Guid.NewGuid().ToString();
        var binding = new MeshBinding(peerId, "10.247.0.3", key, 31001, "Qua relay");
        var built = WireGuardConfig.BuildMesh(key, "10.247.0.2", [binding]);
        Require(built.Contains("Address = 10.247.0.2/32") && built.Contains("AllowedIPs = 10.247.0.3/32") &&
            built.Contains("Endpoint = 127.0.0.1:31001") && !built.Contains("/16") && !built.Contains("0.0.0.0/0") && !built.Contains("DNS"),
            "Mesh creates only peer /32 routes and preserves Internet routing/DNS");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var frame = MeshTransport.EncodeDirect(peerId, 1, RandomNumberGenerator.GetBytes(8), [], token);
        Require(MeshTransport.VerifyDirect(frame, token), "Direct probes authenticate with per-session pair token");
        frame[40] ^= 1;
        Require(!MeshTransport.VerifyDirect(frame, token), "Tampered direct probe is rejected");
        var stunRequest = StunProtocol.AuthenticatedRequest(token);
        var stunResponse = StunTestResponse(stunRequest, new(IPAddress.Parse("203.0.113.55"), 45678));
        Require(StunProtocol.Response(stunResponse, stunRequest)?.ToString() == "203.0.113.55:45678",
            "STUN decodes the XOR-mapped IPv4 endpoint");
        stunResponse[8] ^= 1;
        Require(StunProtocol.Response(stunResponse, stunRequest) is null &&
            StunProtocol.Response(stunResponse.AsSpan(0, 25), stunRequest) is null,
            "STUN rejects another transaction and truncated responses");
        Require(stunRequest.Length == 88 &&
            BinaryPrimitives.ReadUInt16BigEndian(stunRequest.AsSpan(2)) == 68 &&
            BinaryPrimitives.ReadUInt16BigEndian(stunRequest.AsSpan(20)) == 6 &&
            System.Text.Encoding.ASCII.GetString(stunRequest, 24, 64) == token,
            "Embedded STUN uses a live mesh cookie in an RFC 8489 Binding Request");
        var state = new MeshState { Type = "state", Address = "10.247.0.2", Cookie = token, LeaseMs = 15000,
            Peers = [new() { Id = peerId, Address = "10.247.0.3", PublicKey = key, Token = token, Endpoints = ["192.168.1.5:32000"] }] };
        MeshTransport.ValidateState(state, Guid.NewGuid().ToString(), state.Address);
        state.Peers[0].Endpoints = ["127.0.0.1:445"];
        try { MeshTransport.ValidateState(state, Guid.NewGuid().ToString(), state.Address); throw new Exception("Accepted loopback probe destination"); }
        catch (InvalidDataException) { Require(true, "Server-provided loopback/proxy destinations fail closed"); }
        Require(TunnelFirewall.NativeBlockedTcpPorts == "1-27443,27445-65535", "Mesh firewall restricts inbound TCP to the native relay");
        var privateIp = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
            !n.Name.StartsWith("RemotePrint", StringComparison.OrdinalIgnoreCase) &&
            n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(x => x.Address)
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
                (ip.GetAddressBytes()[0] == 10 || ip.ToString().StartsWith("192.168.") ||
                 (ip.GetAddressBytes()[0] == 172 && ip.GetAddressBytes()[1] is >= 16 and <= 31)));
        if (privateIp is null) throw new InvalidOperationException("Mesh direct check needs an active private IPv4 adapter.");
        var dir = Path.Combine(root, "artifacts", "mesh-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var udpReserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udpReserve.Client.LocalEndPoint!).Port; udpReserve.Dispose();
        var start = new ProcessStartInfo("node") { WorkingDirectory = Path.Combine(root, "server"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("src/index.js");
        foreach (var (name, value) in new Dictionary<string, string> {
            ["HOST"] = "127.0.0.1", ["PORT"] = port.ToString(), ["DATA_DIR"] = dir, ["TUNNEL_MODE"] = "mesh",
            ["MESH_UDP_PORT"] = udpPort.ToString(), ["MESH_PUBLIC_HOST"] = privateIp.ToString(),
            ["ADMIN_USERNAME"] = "admin", ["ADMIN_PASSWORD"] = "mesh-test-admin-password-123",
            ["JWT_SECRET"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(48)), ["TLS_CERT_PATH"] = "", ["TLS_KEY_PATH"] = "" })
            start.Environment[name] = value;
        using var server = Process.Start(start) ?? throw new Exception("Cannot start mesh test API");
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        var url = $"http://127.0.0.1:{port}";
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        try
        {
            await WaitMeshAsync(async () => { try { return (await http.GetAsync("/health")).IsSuccessStatusCode; } catch { return false; } });
            using var login = await http.PostAsJsonAsync("/api/admin/login", new { username = "admin", password = "mesh-test-admin-password-123" });
            login.EnsureSuccessStatusCode();
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            http.DefaultRequestHeaders.Authorization = new("Bearer", loginJson.RootElement.GetProperty("token").GetString());
            async Task<(ClientConfig Config, TunnelInfo Info)> Enroll(string room)
            {
                var config = new ClientConfig { ServerUrl = url };
                using var invitation = await http.PostAsJsonAsync("/api/admin/enrollment", new { serverUrl = url });
                invitation.EnsureSuccessStatusCode();
                using var invitationJson = JsonDocument.Parse(await invitation.Content.ReadAsStringAsync());
                var code = EnrollmentCode.Parse(invitationJson.RootElement.GetProperty("code").GetString()!)!;
                config.EnrollmentEndpoint = code.Endpoint; config.EnrollmentKey = code.Key;
                using var api = new RemotePrintApi(config);
                await api.RegisterAsync();
                using var approved = await http.PostAsJsonAsync($"/api/admin/clients/{config.DeviceId}/approve", new { room });
                approved.EnsureSuccessStatusCode();
                var info = await api.EnrollTunnelAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), CancellationToken.None);
                Require(info?.Mode == "mesh-v1" && info.ServerPublicKey == "" && info.EmbeddedStun,
                    "Mesh enrollment advertises Remote Print STUN and no relay decryption key");
                return (config, info!);
            }
            var a = await Enroll("one"); var b = await Enroll("one"); var c = await Enroll("two");
            MeshTransport MakeTransport((ClientConfig Config, TunnelInfo Info) x) => new(
                new(x.Config.ServerUrl, x.Config.DeviceId, x.Config.DeviceSecret),
                new(x.Info.Address, x.Info.UdpHost, x.Info.UdpPort, EmbeddedStun: x.Info.EmbeddedStun));
            var dnsAttempts = 0;
            await using var ta = new MeshTransport(new(a.Config.ServerUrl, a.Config.DeviceId, a.Config.DeviceSecret),
                new(a.Info.Address, a.Info.UdpHost, a.Info.UdpPort, EmbeddedStun: a.Info.EmbeddedStun), (_, _) =>
                {
                    if (Interlocked.Increment(ref dnsAttempts) == 1) throw new SocketException((int)SocketError.HostNotFound);
                    return Task.FromResult(new[] { IPAddress.Parse("192.0.2.1"), privateIp });
                });
            ta.ForceRelay = true;
            await using var tb = MakeTransport(b); tb.ForceRelay = true;
            await using var tc = MakeTransport(c);
            ta.Start(); tb.Start(); tc.Start();
            await WaitMeshAsync(() => Task.FromResult(ta.Bindings.Count == 1 && tb.Bindings.Count == 1 && tc.Authorized));
            Require(tc.Bindings.Count == 0, "A different Room receives no transport peers");
            using var wa = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var wb = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            ta.SetWireGuardPort(((IPEndPoint)wa.Client.LocalEndPoint!).Port);
            tb.SetWireGuardPort(((IPEndPoint)wb.Client.LocalEndPoint!).Port);
            async Task Exchange(string label)
            {
                var payload = RandomNumberGenerator.GetBytes(148);
                payload[0] = 1; payload[1] = payload[2] = payload[3] = 0;
                // Both fake WireGuard endpoints register their source ports first.
                await wb.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, tb.Bindings.Single().Port));
                await wa.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, ta.Bindings.Single().Port));
                await Task.Delay(100);
                await wa.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, ta.Bindings.Single().Port));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                UdpReceiveResult received;
                do { received = await wb.ReceiveAsync(timeout.Token); } while (!received.Buffer.SequenceEqual(payload));
                Require(true, label);
                while (wb.Available > 0) await wb.ReceiveAsync(timeout.Token);
            }
            await Exchange("C# clients relay opaque WireGuard packets intact through the Node server");
            await WaitMeshAsync(() => Task.FromResult(ta.Diagnostics.ObservedEndpoint is not null));
            await WaitMeshAsync(() => Task.FromResult(
                ta.Diagnostics.EmbeddedStunReplies > 0 && tb.Diagnostics.EmbeddedStunReplies > 0));
            Require(true, "Remote Print mesh UDP server answers authenticated STUN requests to both C# clients");
            Require(dnsAttempts >= 2 && ta.Diagnostics.DiscoveryEndpoints.Length == 2 && ta.Diagnostics.DnsError is null,
                "Discovery retries a startup DNS failure and reaches the server when the first A record is unusable");
            Require(ta.Diagnostics.StunServer.Contains("(Remote Print)") &&
                !ta.Diagnostics.StunServer.Contains(',') && ta.Diagnostics.PublicEndpoints.Length == 0,
                "Only Remote Print STUN is used; private NAT hairpin observations are not advertised as WAN candidates");
            ta.ForceRelay = tb.ForceRelay = false;
            await WaitMeshAsync(() => Task.FromResult(ta.Bindings.SingleOrDefault()?.Path == "Trực tiếp" && tb.Bindings.SingleOrDefault()?.Path == "Trực tiếp"));
            await Exchange("Authenticated UDP path moves packets directly between C# clients");
            Require(ta.Diagnostics.Peers.Single().AcksReceived > 0 && ta.Diagnostics.Peers.Single().ProbesSent > 0,
                "Diagnostics distinguish authenticated direct ACKs from discovery and relay traffic");
            Require(ta.Diagnostics.Peers.Single().DirectBytesSent > 0 && tb.Diagnostics.Peers.Single().DirectBytesReceived > 0 &&
                ta.Diagnostics.Peers.Single().RelayBytesSent > 0,
                "WireGuard data counters distinguish bytes delivered directly from earlier relayed bytes");
            tb.ForceRelay = true;
            var repliesBeforeLoss = ta.Diagnostics.EmbeddedStunReplies;
            await WaitMeshAsync(() => Task.FromResult(ta.Bindings.SingleOrDefault()?.Path == "Qua relay"));
            await Exchange("Loss of direct UDP falls back to WSS without replacing peer bindings");
            await WaitMeshAsync(() => Task.FromResult(ta.Diagnostics.EmbeddedStunReplies > repliesBeforeLoss));
            Require(true, "Expired direct ACK re-queries Remote Print STUN without a NIC event");
            var repliesBeforeNetworkChange = ta.Diagnostics.EmbeddedStunReplies;
            ta.NotifyNetworkChanged();
            await WaitMeshAsync(() => Task.FromResult(ta.Diagnostics.EmbeddedStunReplies > repliesBeforeNetworkChange));
            Require(true, "Network return retries Remote Print STUN after clearing stale candidates");
            tb.ForceRelay = false;
            await WaitMeshAsync(() => Task.FromResult(ta.Bindings.SingleOrDefault()?.Path == "Trực tiếp" &&
                tb.Bindings.SingleOrDefault()?.Path == "Trực tiếp"));
            await Exchange("Direct UDP recovers after network loss using Remote Print STUN");
            // Model a NAT mapping which is absent from discovery. The bridge
            // must challenge the authenticated incoming source, then keep that
            // learned endpoint alive after the original 8-second path lease.
            var reflexive = await Enroll("one");
            using (var control = new ClientWebSocket())
            using (var endpoint = new UdpClient(new IPEndPoint(privateIp, 0)))
            using (var stop = new CancellationTokenSource())
            {
                control.Options.SetRequestHeader("x-device-id", reflexive.Config.DeviceId);
                control.Options.SetRequestHeader("x-device-secret", reflexive.Config.DeviceSecret);
                await control.ConnectAsync(new Uri(url.Replace("http:", "ws:") + "/api/client/mesh"), CancellationToken.None);
                var buffer = new byte[256 * 1024];
                var welcome = await control.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                var welcomeState = JsonSerializer.Deserialize<MeshState>(buffer.AsSpan(0, welcome.Count), ServiceProtocol.Json)!;
                var pairToken = welcomeState.Peers.Single(p => p.Id == a.Config.DeviceId).Token;
                await WaitMeshAsync(() => Task.FromResult(ta.Bindings.Any(p => p.Id == reflexive.Config.DeviceId)));
                var target = new IPEndPoint(privateIp, ta.LocalUdpPort);
                var reply = Task.Run(async () =>
                {
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var packet = await endpoint.ReceiveAsync(stop.Token);
                            if (MeshTransport.VerifyDirect(packet.Buffer, pairToken) && packet.Buffer[36] == 1)
                                await endpoint.SendAsync(MeshTransport.EncodeDirect(reflexive.Config.DeviceId, 2,
                                    packet.Buffer.AsSpan(37, 8).ToArray(), [], pairToken), packet.RemoteEndPoint, stop.Token);
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                });
                try
                {
                    await endpoint.SendAsync(MeshTransport.EncodeDirect(reflexive.Config.DeviceId, 1,
                        RandomNumberGenerator.GetBytes(8), [], pairToken), target);
                    await WaitMeshAsync(() => Task.FromResult(ta.Bindings.FirstOrDefault(p => p.Id == reflexive.Config.DeviceId)?.Path == "Trực tiếp"));
                    await Task.Delay(9000);
                    Require(ta.Bindings.First(p => p.Id == reflexive.Config.DeviceId).Path == "Trực tiếp",
                        "Authenticated peer-reflexive NAT endpoint is challenged and stays direct beyond eight seconds");
                }
                finally { stop.Cancel(); await reply; control.Abort(); }
            }
            await WaitMeshAsync(() => Task.FromResult(ta.Bindings.Count == 1));
            using var moved = await http.PostAsJsonAsync($"/api/admin/clients/{b.Config.DeviceId}/approve", new { room = "two" });
            moved.EnsureSuccessStatusCode();
            await WaitMeshAsync(() => Task.FromResult(ta.Bindings.Count == 0));
            Require(ta.Authorized, "Room changes revoke direct and relay access while retaining server management");
            server.Kill(entireProcessTree: true); await server.WaitForExitAsync();
            await WaitMeshAsync(() => Task.FromResult(!ta.Authorized && !tb.Authorized && !tc.Authorized));
            Require(ta.Bindings.Count == 0 && tb.Bindings.Count == 0, "Server loss closes the mesh bridge and clears authorization");
            Console.WriteLine("Mesh transport checks passed. This exercises C# UDP/WSS bridges, not real Windows driver installation.");
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            File.WriteAllText(Path.Combine(dir, "server.log"), await stdout + await stderr);
        }
    }
    private static async Task WaitMeshAsync(Func<Task<bool>> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (await predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Mesh state did not converge within 20 seconds.");
    }

    private static byte[] StunTestResponse(byte[] request, IPEndPoint mapped)
    {
        var response = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x101);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 12);
        request.AsSpan(4, 16).CopyTo(response.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x20);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), 8);
        response[25] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(mapped.Port ^ 0x2112));
        var ip = mapped.Address.GetAddressBytes();
        for (var i = 0; i < 4; i++) response[i + 28] = (byte)(ip[i] ^ request[i + 4]);
        return response;
    }

}
