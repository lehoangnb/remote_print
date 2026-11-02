using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemotePrint.Client;
using RemotePrint.Service;

internal static partial class Program
{
    private static async Task CheckTestPrinterAsync(string root)
    {
        var directory = Path.Combine(root, "artifacts", "test-printer-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var relay = new NativePrintRelay(new RelayHost(directory));
        var id = Guid.NewGuid().ToString("N");
        var bytes = Encoding.ASCII.GetBytes("REMOTE PRINT TEST\r\nFrom: client A\r\nTo: client B\r\n\f");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = new LprCaptureServer(directory, (queue, ct) =>
        {
            if (queue != TestPrinter.QueueId) throw new UnauthorizedAccessException();
            return Task.FromResult(new LprTarget(queue, id));
        }, relay.CommitTestJobAsync, _ => { }, 0);
        var run = server.RunAsync(stop.Token);
        async Task<byte> Send(string queue, byte[] payload)
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port, stop.Token);
            var stream = client.GetStream();
            async Task<byte> Command(byte kind, string line)
            {
                await stream.WriteAsync(new[] { kind }, stop.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), stop.Token);
                var ack = new byte[1]; await stream.ReadExactlyAsync(ack, stop.Token); return ack[0];
            }
            var response = await Command(2, queue);
            if (response != 0) return response;
            if (await Command(2, "4 cfA001test") != 0) throw new Exception("Control command rejected");
            await stream.WriteAsync(new byte[] { 1, 2, 3, 4, 0 }, stop.Token);
            var reply = new byte[1]; await stream.ReadExactlyAsync(reply, stop.Token);
            if (await Command(3, payload.Length + " dfA001test") != 0) throw new Exception("Data command rejected");
            await stream.WriteAsync(payload, stop.Token); await stream.WriteAsync(new byte[] { 0 }, stop.Token);
            await stream.ReadExactlyAsync(reply, stop.Token); return reply[0];
        }
        try
        {
            Require(!TestPrinter.PortName.StartsWith("RP_"), "Virtual destination is eligible for sharing without re-sharing a relay queue");
            Require(await Send("unknown", bytes) != 0, "Unrecognized LPR queues cannot write test output");
            Require(await Send(TestPrinter.QueueId, bytes) == 0, "Virtual destination acknowledges a durable file over real LPR TCP");
            var raw = Path.Combine(relay.TestOutputDirectory, id + ".raw");
            Require(File.ReadAllBytes(raw).SequenceEqual(bytes), "Virtual printer preserves the complete test page bytes");
            using (var receipt = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(raw, ".json"))))
                Require(receipt.RootElement.GetProperty("Bytes").GetInt64() == bytes.Length &&
                    receipt.RootElement.GetProperty("Sha256").GetString() == Convert.ToHexString(SHA256.HashData(bytes)),
                    "Virtual printer persists a byte count, timestamp and SHA256 receipt before ACK");
            Require(await Send(TestPrinter.QueueId, bytes) == 0 && Directory.GetFiles(relay.TestOutputDirectory, "*.raw").Length == 1,
                "An LPR retry reuses the original file instead of duplicating the test page");
            var changed = (byte[])bytes.Clone(); changed[0] ^= 1;
            Require(await Send(TestPrinter.QueueId, changed) != 0 && File.ReadAllBytes(raw).SequenceEqual(bytes),
                "Repeated test identities cannot overwrite previously accepted content");
            Require(!Directory.GetFiles(directory, "pending-*.json", SearchOption.AllDirectories).Any(),
                "Virtual destination does not relay the received job back into the Room");
            var files = new TestPrintFiles(relay.TestOutputDirectory);
            var saved = files.List().Single();
            Require(saved.Id == id && saved.Bytes == bytes.Length, "Received test files are listed from completed receipts");
            var chunk = await files.ReadAsync(id, 0, stop.Token);
            Require(chunk.Offset == 0 && chunk.End && chunk.Data.SequenceEqual(bytes), "Selected test file can be read without opening the protected folder");
            try { await files.ReadAsync("../config", 0, stop.Token); throw new Exception("Traversal accepted"); }
            catch (InvalidDataException) { Require(true, "Test file reads reject arbitrary filesystem paths"); }
            try { await files.ReadAsync(id, -1, stop.Token); throw new Exception("Negative offset accepted"); }
            catch (InvalidDataException) { Require(true, "Test file reads reject invalid chunk offsets"); }
            File.WriteAllText(Path.Combine(relay.TestOutputDirectory, Guid.NewGuid().ToString("N") + ".json"), "{}");
            File.WriteAllText(Path.Combine(relay.TestOutputDirectory, "stray.json"), "{}");
            Require(files.List().Count == 1, "Incomplete or malformed test receipts do not prevent opening completed files");
            var pdfPath = Path.Combine(directory, "pdf-format-probe.raw");
            File.WriteAllText(pdfPath, "%PDF-1.7\nPDF format fixture\n%%EOF\n", Encoding.ASCII);
            Require(TestPrintFormat.Inspect(pdfPath) == ("pdf", true), "PDF test output is recognized separately from legacy text");
            File.WriteAllBytes(pdfPath, [12]);
            Require(TestPrintFormat.Inspect(pdfPath) == ("txt", false), "A one-byte form-feed is marked empty rather than reported as document content");
            File.WriteAllText(pdfPath, "%PDF-1.7\ntruncated", Encoding.ASCII);
            try { TestPrintFormat.Inspect(pdfPath); throw new Exception("Truncated PDF accepted"); }
            catch (InvalidDataException) { Require(true, "An incomplete PDF is rejected before acknowledging its output"); }
            var activeCapture = Path.Combine(relay.TestOutputDirectory, "capture-active.part");
            File.WriteAllText(activeCapture, "still receiving");
            files.Clear();
            Require(files.List().Count == 0 && !File.Exists(raw), "Cleanup removes received test payloads and receipts");
            Require(File.Exists(activeCapture) && File.Exists(Path.Combine(relay.TestOutputDirectory, "stray.json")),
                "Cleanup preserves in-progress capture and unrelated files");
            await CheckVirtualPdfRelayAsync(directory);
        }
        finally { stop.Cancel(); await run; }
    }

    private static async Task CheckTestFilePipeAsync(ServiceClient client, string directory)
    {
        Require((await client.GetTestFilesAsync()).Count == 0, "Test file list is empty before a virtual printer receives a job");
        var folder = Path.Combine(directory, "NativeRelay", "TestPrints");
        Directory.CreateDirectory(folder);
        var id = Guid.NewGuid().ToString("N");
        var bytes = RandomNumberGenerator.GetBytes(600 * 1024);
        var raw = Path.Combine(folder, id + ".raw");
        await File.WriteAllBytesAsync(raw, bytes);
        await File.WriteAllTextAsync(Path.ChangeExtension(raw, ".json"), JsonSerializer.Serialize(new
        {
            Printer = TestPrinter.Name, ReceivedAtUtc = DateTime.UtcNow, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        }));
        var file = (await client.GetTestFilesAsync()).Single();
        using var output = new MemoryStream();
        await client.CopyTestFileAsync(file, output);
        Require(output.ToArray().SequenceEqual(bytes), "Authorized IPC copies a complete test file across multiple bounded chunks and validates its hash");
        var corrupt = (byte[])bytes.Clone(); corrupt[0] ^= 1;
        await File.WriteAllBytesAsync(raw, corrupt);
        try
        {
            using var rejected = new MemoryStream();
            await client.CopyTestFileAsync(file, rejected);
            throw new Exception("Changed data was accepted");
        }
        catch (InvalidDataException) { Require(true, "Changed test files are rejected before opening a local copy"); }
        try
        {
            using var rejected = new MemoryStream();
            await client.CopyTestFileAsync(file with { Id = "../config" }, rejected);
            throw new Exception("IPC accepted a path");
        }
        catch (InvalidOperationException) { Require(true, "IPC cannot read files outside the virtual test output directory"); }
        await client.CallAsync(new("clear-test-files"));
        Require((await client.GetTestFilesAsync()).Count == 0 && !File.Exists(raw), "Authenticated IPC clears test files from protected service storage");
    }

    private static async Task CheckVirtualPdfRelayAsync(string root)
    {
        var relay = new NativePrintRelay(new PdfWindowsHost(Path.Combine(root, "pdf-relay")),
            (_, _, _, _) => throw new Exception("A received PDF must not be rendered again"), localPort: 0, relayPort: 0);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = relay.RunAsync(stop.Token);
        var id = Guid.NewGuid().ToString("N");
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nPDF relay format fixture\n%%EOF\n");
        async Task<NativePrintReply> Send(byte[] bytes, string job)
        {
            using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", relay.ListenPort, stop.Token);
            var stream = client.GetStream();
            await NativePrintProtocol.WriteAsync(stream, new NativePrintRequest("print", TestPrinter.Name, job, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes))), stop.Token);
            await stream.WriteAsync(bytes, stop.Token);
            return await NativePrintProtocol.ReadAsync<NativePrintReply>(stream, stop.Token);
        }
        try
        {
            Require((await Send(pdf, id)).Ok, "Remote virtual printer captures PDF directly without a second Windows render");
            Require((await Send(pdf, id)).Ok && new TestPrintFiles(relay.TestOutputDirectory).List().Count == 1,
                "Retrying a received PDF does not create duplicate test files");
            Require(!(await Send([12], Guid.NewGuid().ToString("N"))).Ok, "Remote test printer rejects a legacy empty text-only job");
        }
        finally { stop.Cancel(); await running; }
    }
}
