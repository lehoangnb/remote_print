using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
                Require(info?.Mode == "mesh-v1" && info.ServerPublicKey == "", "Mesh enrollment does not distribute a relay decryption key");
                return (config, info!);
            }
            var a = await Enroll("one"); var b = await Enroll("one"); var c = await Enroll("two");
            MeshTransport MakeTransport((ClientConfig Config, TunnelInfo Info) x) => new(
                new(x.Config.ServerUrl, x.Config.DeviceId, x.Config.DeviceSecret), new(x.Info.Address, x.Info.UdpHost, x.Info.UdpPort));
            await using var ta = MakeTransport(a); ta.ForceRelay = true;
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
            ta.ForceRelay = tb.ForceRelay = false;
            await WaitMeshAsync(() => Task.FromResult(ta.Bindings.SingleOrDefault()?.Path == "Trực tiếp" && tb.Bindings.SingleOrDefault()?.Path == "Trực tiếp"));
            await Exchange("Authenticated UDP path moves packets directly between C# clients");
            tb.ForceRelay = true;
            await WaitMeshAsync(() => Task.FromResult(ta.Bindings.SingleOrDefault()?.Path == "Qua relay"));
            await Exchange("Loss of direct UDP falls back to WSS without replacing peer bindings");
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
}
