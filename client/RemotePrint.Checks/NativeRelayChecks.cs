using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemotePrint.Client;
using RemotePrint.Service;

internal static partial class Program
{
    private static async Task CheckNativeRelayAsync(string root)
    {
        var directory = Path.Combine(root, "artifacts", "native-relay-checks", Guid.NewGuid().ToString("N"));
        Require(TunnelFirewall.NativeBlockedTcpPorts == "1-27443,27445-65535", "Mesh inbound firewall admits only the native relay port; SMB/RPC remain blocked");
        Directory.CreateDirectory(directory);
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var host = new RelayHost(directory);
        var submissions = 0;
        var relay = new NativePrintRelay(host, (printer, file, started, allowed) =>
        {
            Require(allowed() && File.ReadAllBytes(file).SequenceEqual(bytes), "RAW bytes reach the local spool sink unchanged");
            submissions++; started(42); return 42;
        }, localPort: 0, relayPort: 0);
        using var stop = new CancellationTokenSource();
        var running = relay.RunAsync(stop.Token);
        async Task<NativePrintReply> Submit(string id, byte[] payload, string digest)
        {
            using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", relay.ListenPort);
            var stream = client.GetStream();
            await NativePrintProtocol.WriteAsync(stream, new NativePrintRequest("print", "Printer", id, payload.Length, digest), stop.Token);
            await stream.WriteAsync(payload, stop.Token);
            return await NativePrintProtocol.ReadAsync<NativePrintReply>(stream, stop.Token);
        }
        try
        {
            await CheckLocalLprAsync(directory, bytes, stop.Token);
            var id = Guid.NewGuid().ToString("N");
            var first = await Submit(id, bytes, hash);
            Require(first.Ok && first.JobId == 42, "Native relay acknowledges the Windows spool job ID");
            Require((await Submit(id, bytes, hash)).Ok && submissions == 1, "Lost acknowledgements can be retried without duplicate printing");
            var corrupt = (byte[])bytes.Clone(); corrupt[0] ^= 1;
            Require(!(await Submit(Guid.NewGuid().ToString("N"), corrupt, hash)).Ok && submissions == 1,
                "Corrupt RAW payload never reaches the spooler");
            var uncertainId = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(directory, "NativeRelay", "receipt-" + host.PeerId + "-" + uncertainId + ".json"),
                System.Text.Json.JsonSerializer.Serialize(new NativeReceipt("submitting", hash, 41)));
            Require(!(await Submit(uncertainId, bytes, hash)).Ok && submissions == 1,
                "An uncertain spool result fails closed instead of printing twice");
            host.Allowed = false;
            using (var denied = new TcpClient())
            {
                await denied.ConnectAsync("127.0.0.1", relay.ListenPort);
                Require(await denied.GetStream().ReadAsync(new byte[1], stop.Token) == 0, "Clients outside the authorized Room are disconnected");
            }
            using var input = new MemoryStream(bytes); using var output = new MemoryStream(); var checks = 0;
            try
            {
                await NativePrintProtocol.ReceiveAsync(input, output, bytes.Length, hash, () => ++checks < 3, stop.Token);
                throw new Exception("Revocation was ignored");
            }
            catch (UnauthorizedAccessException) { Require(output.Length < bytes.Length, "Revocation interrupts an active RAW transfer"); }
            var badZip = Path.Combine(directory, "bad.zip");
            using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create)) zip.CreateEntry("../escape.inf");
            try { NativePrinterSetup.ExtractChecked(badZip, Path.Combine(directory, "driver")); throw new Exception("Zip traversal accepted"); }
            catch (InvalidDataException) { Require(true, "Driver archives cannot write outside the staging directory"); }

            var printer = PrinterService.GetServicePrinters().FirstOrDefault();
            if (printer is not null)
            {
                var package = Path.Combine(directory, "real-driver.zip");
                var driver = await NativePrinterSetup.ExportAsync(printer, package, stop.Token);
                NativePrinterSetup.ExtractChecked(package, Path.Combine(directory, "exported"));
                Require(File.Exists(Path.Combine(directory, "exported", driver.InfFile)) && driver.Bytes > 0,
                    "Real Windows printer driver package exports with INF and hashes; no Windows user or share is created");
            }
        }
        finally { stop.Cancel(); await running; }
    }
    private sealed class RelayHost(string root) : INativeRelayHost
    {
        public string PeerId { get; } = Guid.NewGuid().ToString();
        public bool Allowed = true;
        public string DataDirectory => root;
        public string? NativePeerFor(string address, string localAddress) => Allowed && address == "127.0.0.1" ? PeerId : null;
        public bool NativePrinterSelected(string printer) => printer == "Printer";
        public bool NativePeerAllowed(string id, string printer) => false;
        public PeerClient NativePeer(string id, string printer) => throw new UnauthorizedAccessException();
        public void NativeLog(string message) { }
    }

    private static async Task CheckLocalLprAsync(string root, byte[] payload, CancellationToken ct)
    {
        var committed = false; var reject = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var server = new LprCaptureServer(root, (queue, token) => Task.FromResult(new LprTarget(queue, "job")),
            (target, file, size, token) =>
            {
                if (reject) throw new IOException("Test spool full");
                Require(size == payload.Length && File.ReadAllBytes(file).SequenceEqual(payload), "Windows LPR capture preserves native driver bytes");
                committed = true; return Task.CompletedTask;
            }, _ => { }, 0);
        var run = server.RunAsync(stop.Token);
        async Task<int> Send()
        {
            using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.Port, ct);
            var stream = client.GetStream();
            async Task Command(byte kind, string text)
            {
                await stream.WriteAsync(new[] { kind }, ct);
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(text + "\n"), ct);
                var ack = new byte[1]; await stream.ReadExactlyAsync(ack, ct);
                if (ack[0] != 0) throw new Exception("LPR command rejected");
            }
            await Command(2, "queue");
            await Command(2, "4 cfA001client");
            await stream.WriteAsync(new byte[] { 1, 2, 3, 4, 0 }, ct);
            var reply = new byte[1]; await stream.ReadExactlyAsync(reply, ct);
            await Command(3, payload.Length + " dfA001client");
            await stream.WriteAsync(payload, ct); await stream.WriteAsync(new byte[] { 0 }, ct);
            await stream.ReadExactlyAsync(reply, ct); return reply[0];
        }
        try
        {
            Require(await Send() == 0 && committed, "LPR acknowledges only after the local durable spool accepts data");
            reject = true; committed = false;
            Require(await Send() != 0 && !committed, "A failed local commit sends negative LPR ACK so Windows retains its print job");
        }
        finally { stop.Cancel(); await run; }
    }
}
