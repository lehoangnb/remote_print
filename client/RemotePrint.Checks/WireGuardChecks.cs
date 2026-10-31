using System.Runtime.InteropServices;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RemotePrint.Service;
using RemotePrint.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

internal static partial class Program
{
    private static async Task RunWireGuardCheck(string? wireGuardDirectory)
    {
        var key = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        var serverKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());
        if ((await ProbeTunnelAsync(404, "<html>Not Found</html>")).Enabled ||
            !(await ProbeTunnelAsync(404, "{\"error\":\"TUNNEL_NOT_ENROLLED\"}")).Enabled ||
            (await ProbeTunnelAsync(503, "{\"error\":\"TUNNEL_NOT_CONFIGURED\"}")).Enabled)
            throw new Exception("Tunnel availability response was misclassified");
        try { await ProbeTunnelAsync(503, "<html>Proxy unavailable</html>"); throw new Exception("Proxy failure was accepted"); }
        catch (System.Net.Http.HttpRequestException) { }
        if (!TunnelNetworkCheck.Overlaps(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.0.0.0")) ||
            !TunnelNetworkCheck.Overlaps(IPAddress.Parse("10.247.10.1"), IPAddress.Parse("255.255.255.0")) ||
            TunnelNetworkCheck.Overlaps(IPAddress.Parse("10.246.10.1"), IPAddress.Parse("255.255.0.0")) ||
            TunnelNetworkCheck.Overlaps(IPAddress.Parse("192.168.0.1"), IPAddress.Parse("255.255.255.0")))
            throw new Exception("Tunnel network overlap check failed");
        if (!WireGuardManager.HasRecentHandshake($"{serverKey}\t990\n", serverKey, 1000) ||
            WireGuardManager.HasRecentHandshake($"{serverKey}\t0\n", serverKey, 1000) ||
            WireGuardManager.HasRecentHandshake($"{serverKey}\t800\n", serverKey, 1000) ||
            WireGuardManager.HasRecentHandshake($"{key}\t990\n", serverKey, 1000))
            throw new Exception("Invalid relay handshake status");
        var config = WireGuardConfig.Build(key, "10.247.0.2", serverKey, "vpn.example.com:51820",
            ["10.247.1.5", "10.247.0.3", "10.247.1.5"]);
        if (!config.Contains("Address = 10.247.0.2/16") ||
            !config.Contains("AllowedIPs = 10.247.0.1/32,10.247.0.3/32,10.247.1.5/32") ||
            config.Contains("10.247.0.0/16") || config.Contains("10.247.0.4/32") ||
            WireGuardConfig.AllowedIps("10.247.0.2", ["10.247.1.5", "10.247.0.3"]) !=
            WireGuardConfig.AllowedIps("10.247.0.2", ["10.247.0.3", "10.247.1.5"]) ||
            !WireGuardConfig.Build(key, "10.247.0.2", serverKey, "vpn.example.com:51820")
                .Contains("AllowedIPs = 10.247.0.1/32"))
            throw new Exception("WireGuard routes are not restricted to approved Room peers");
        foreach (var badPeers in new[] { new[] { "10.247.0.2" }, new[] { "10.247.0.1" },
                     new[] { "10.247.0.4\nPostUp = cmd" }, new[] { "192.168.1.2" } })
        {
            try { WireGuardConfig.Build(key, "10.247.0.2", serverKey, "vpn.example.com:51820", badPeers);
                throw new Exception("Accepted unauthorized tunnel peer address"); }
            catch (InvalidDataException) { }
        }
        foreach (var address in new[] { "10.247.0.1", "10.247.0.256", "192.168.1.1", "10.247.0.2\nDNS=1.1.1.1" })
            if (WireGuardConfig.ValidAddress(address)) throw new Exception("Accepted invalid address");
        foreach (var endpoint in new[] { "vpn.example.com", "vpn.example.com:0", "vpn.example.com:51820\nPostUp=cmd", "[::1]" })
        {
            try { WireGuardConfig.Build(key, "10.247.0.2", serverKey, endpoint); throw new Exception("Accepted invalid endpoint"); }
            catch (InvalidDataException) { }
        }
        var encrypted = WireGuardManager.ProtectForWireGuard(Encoding.UTF8.GetBytes(config), "RemotePrintTest");
        var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
        if (Encoding.UTF8.GetString(decrypted) != config) throw new Exception("DPAPI config mismatch");
        CryptographicOperations.ZeroMemory(decrypted);
        var input = new DataBlob { Length = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) };
        try
        {
            Marshal.Copy(encrypted, 0, input.Data, input.Length);
            if (!CryptUnprotectData(ref input, out var description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (Marshal.PtrToStringUni(description) != "RemotePrintTest") throw new Exception("Wrong WireGuard DPAPI description");
            }
            finally { LocalFree(description); LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
        var state = new ClientConfig { SharedPrinters = ["Managed", "Existing"] };
        var fake = new FakeWindowsShares();
        fake.Shares["Existing"] = "AlreadyShared";
        var saves = 0;
        var managerShares = new WindowsShareManager(state, () => saves++, fake);
        if (!managerShares.Synchronize(true) || !fake.Shares.ContainsKey("Managed") || saves == 0)
            throw new Exception("Managed Windows share not created");
        if (fake.Shares["Existing"] != "AlreadyShared" || managerShares.Synchronize(true))
            throw new Exception("Existing share changed or reconciliation is not idempotent");
        fake.Shares["Managed"] = "AdminChanged";
        state.SharedPrinters = [];
        managerShares.Synchronize(true);
        if (fake.Shares["Managed"] != "AdminChanged" || fake.Shares["Existing"] != "AlreadyShared")
            throw new Exception("Administrator-owned shares were removed");
        state.SharedPrinters = ["Another"];
        managerShares.Synchronize(true);
        managerShares.Synchronize(false);
        if (fake.Shares.ContainsKey("Another")) throw new Exception("Managed share was not removed");
        state.SharedPrinters = ["Temporarily missing"];
        if (managerShares.Synchronize(true, []) || fake.Shares.ContainsKey("Temporarily missing"))
            throw new Exception("Unavailable queue was shared");
        if (!managerShares.Synchronize(true, ["Temporarily missing"]) || !fake.Shares.ContainsKey("Temporarily missing"))
            throw new Exception("Reappeared queue was not shared");
        var dir = Path.Combine(Path.GetTempPath(), "RemotePrint-wireguard-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var failingConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["DataDirectory"] = Path.Combine(dir, "failing-share") }).Build();
            using (var failingEngine = new ClientEngine(failingConfig, new FailingWindowsShares()))
            {
                var sync = typeof(ClientEngine).GetMethod("SynchronizeWindowsShares",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                sync.Invoke(failingEngine, [true]);
                sync.Invoke(failingEngine, [false]);
                var log = File.ReadAllText(Path.Combine(failingEngine.DataDirectory, "service.log"));
                if (!log.Contains("Windows 5") || !log.Contains("Không đồng bộ được chia sẻ máy in"))
                    throw new Exception("Share error was not isolated and reported with the Windows error code");
            }
            Console.WriteLine("PASS: Printer-share errors are reported without escaping into tunnel lifecycle");
            var store = new MachineStore(dir);
            var stored = store.Load();
            stored.SharedPrinters = ["RemotePrint-test-missing-" + Guid.NewGuid().ToString("N")];
            store.Save(stored);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["DataDirectory"] = dir }).Build();
            using (var engine = new ClientEngine(configuration))
            {
                await engine.TickAsync(CancellationToken.None);
                if (!engine.Snapshot.Settings.SharedPrinters.SequenceEqual(stored.SharedPrinters) ||
                    !store.Load().SharedPrinters.SequenceEqual(stored.SharedPrinters))
                    throw new Exception("Unavailable printer configuration was lost");
            }
            await CheckUnavailablePrinterRegistrationAsync(dir, stored.SharedPrinters[0]);
            if (wireGuardDirectory is null)
            {
                Console.WriteLine("Tunnel configuration, Room routes, DPAPI and share checks passed (WireGuard binaries not exercised).");
                return;
            }
            var manager = new WireGuardManager(dir, Guid.NewGuid().ToString(), wireGuardDirectory);
            if (!manager.Available) throw new Exception("WireGuard binaries unavailable");
            var first = await manager.PublicKeyAsync(CancellationToken.None);
            var second = await new WireGuardManager(dir, Guid.NewGuid().ToString(), wireGuardDirectory).PublicKeyAsync(CancellationToken.None);
            if (!WireGuardConfig.ValidKey(first) || first != second) throw new Exception("WireGuard key did not persist");
            Console.WriteLine("WireGuard config, DPAPI description and persisted key checks passed.");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async Task CheckUnavailablePrinterRegistrationAsync(string directory, string missingPrinter)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        string[]? advertised = null;
        app.MapPost("/api/client/register", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            advertised = body.RootElement.GetProperty("printers").EnumerateArray().Select(x => x.GetString()!).ToArray();
            return Results.Json(new { status = "approved", room = "test" });
        });
        app.MapGet("/api/client/status", () => Results.Json(new { status = "approved", room = "test" }));
        app.MapGet("/api/client/peers", () => Results.Json(Array.Empty<object>()));
        app.MapGet("/api/client/tunnel", () => Results.Json(new { error = "TUNNEL_NOT_CONFIGURED" }, statusCode: 503));
        await app.StartAsync();
        try
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var store = new MachineStore(directory);
            var config = store.Load();
            config.ServerUrl = url;
            config.SharedPrinters = [missingPrinter];
            store.Save(config);
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["DataDirectory"] = directory }).Build();
            using var engine = new ClientEngine(settings);
            await engine.TickAsync(CancellationToken.None);
            if (advertised is null || advertised.Length != 0 || !store.Load().SharedPrinters.Contains(missingPrinter))
                throw new Exception("Unavailable queue was advertised or removed from configuration");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<(bool Enabled, TunnelInfo? Info)> ProbeTunnelAsync(int status, string body)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(bytes);
        });
        try
        {
            using var api = new RemotePrintApi(new ClientConfig { ServerUrl = $"http://127.0.0.1:{port}" });
            return await api.GetTunnelAsync(CancellationToken.None);
        }
        finally { listener.Stop(); await reply; }
    }

    private sealed class FailingWindowsShares : IWindowsShareAccess
    {
        public Dictionary<string, string> ReadShares() => throw new System.ComponentModel.Win32Exception(5, "Test printer");
        public void SetShare(string printer, string? expectedShare, string? newShare) => throw new InvalidOperationException();
    }

    private sealed class FakeWindowsShares : IWindowsShareAccess
    {
        public Dictionary<string, string> Shares { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ReadShares() => new(Shares, StringComparer.OrdinalIgnoreCase);
        public void SetShare(string printer, string? expectedShare, string? newShare)
        {
            Shares.TryGetValue(printer, out var current);
            if (!string.Equals(current, expectedShare, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Share changed");
            if (newShare is null) Shares.Remove(printer); else Shares[printer] = newShare;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
