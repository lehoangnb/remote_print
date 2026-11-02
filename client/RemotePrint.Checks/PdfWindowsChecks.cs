using System.Net.Sockets;
using System.Security.Cryptography;
using RemotePrint.Client;
using RemotePrint.Service;

internal static partial class Program
{
    private static async Task CheckPdfWindowsAsync(string root, string? inputPdf, bool wordFixture = false)
    {
        var client = new ServiceClient();
        Require((await client.CallAsync(new("status"))).TestPdfPrinting, "Installed service supports PDF test printing");
        var before = (await client.GetTestFilesAsync()).Select(file => file.Id).ToHashSet();
        var outputRoot = Path.Combine(root, "artifacts", "pdf-windows-checks");
        Directory.CreateDirectory(outputRoot);
        string? expectedHash = null;
        TestPrintFile? relayReceived = null;
        if (wordFixture)
        {
            await PrintWordFixtureAsync();
            Require(true, "Microsoft Word prints a two-page Unicode document through the real PDF test queue");
        }
        else if (inputPdf is null)
        {
            var job = NativeTestPage.Print(TestPrinter.Name, "Windows PDF validation", () => true);
            Require(job > 0, "Real Windows GDI renders the test page through the installed PDF printer");
        }
        else
        {
            Require(TestPrintFormat.Inspect(inputPdf).Format == "pdf", "Relay input is a complete PDF");
            var bytes = await File.ReadAllBytesAsync(inputPdf);
            expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
            var directory = Path.Combine(outputRoot, "relay-" + Guid.NewGuid().ToString("N"));
            var relay = new NativePrintRelay(new PdfWindowsHost(directory), localPort: 0, relayPort: 0);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var running = relay.RunAsync(stop.Token);
            try
            {
                using var sender = new TcpClient();
                await sender.ConnectAsync("127.0.0.1", relay.ListenPort, stop.Token);
                var stream = sender.GetStream();
                await NativePrintProtocol.WriteAsync(stream, new NativePrintRequest("print", TestPrinter.Name, Guid.NewGuid().ToString("N"), bytes.Length, expectedHash), stop.Token);
                await stream.WriteAsync(bytes, stop.Token);
                var reply = await NativePrintProtocol.ReadAsync<NativePrintReply>(stream, stop.Token);
                Require(reply.Ok && reply.JobId == 0, "Real TCP relay commits the PDF into the virtual destination without re-rendering it");
                relayReceived = new TestPrintFiles(relay.TestOutputDirectory).List().Single();
                Require(relayReceived.Format == "pdf" && relayReceived.Sha256 == expectedHash,
                    "Virtual receiver preserves the Word PDF and hash exactly");
                var relayOutput = Path.Combine(outputRoot, "relay-" + relayReceived.Id + ".pdf");
                File.Copy(Path.Combine(relay.TestOutputDirectory, relayReceived.Id + ".raw"), relayOutput, true);
                Console.WriteLine("PDF_OUTPUT=" + relayOutput);
            }
            finally { stop.Cancel(); await running; }
            return;
        }
        TestPrintFile? received = null;
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            received = (await client.GetTestFilesAsync()).FirstOrDefault(file => !before.Contains(file.Id) && file.Format == "pdf" &&
                (expectedHash is null || file.Sha256 == expectedHash));
            if (received is not null) break;
            await Task.Delay(500);
        }
        Require(received is { Bytes: > 1000, HasContent: true }, "The real receiving LPR service captures a complete PDF with content");
        var output = Path.Combine(outputRoot, received!.Id + ".pdf");
        await using (var file = File.Create(output)) await client.CopyTestFileAsync(received, file);
        Require(TestPrintFormat.Inspect(output).Format == "pdf", "Opening a test file downloads and validates the actual PDF");
        if (expectedHash is not null) Require(received.Sha256 == expectedHash, "Word-rendered PDF survives TCP relay, Windows RAW spool and LPR capture byte for byte");
        Console.WriteLine("PDF_OUTPUT=" + output);
    }

    private static Task PrintWordFixtureAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            dynamic? word = null, document = null;
            Exception? failure = null;
            try
            {
                word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application") ?? throw new InvalidOperationException("Microsoft Word is not installed"));
                word!.Visible = false;
                word.DisplayAlerts = 0;
                document = word.Documents.Add();
                document.Content.Text = "REMOTE PRINT WORD TEST\rKiểm tra tiếng Việt: Xin chào, máy in từ xa!\rFont Arial, chữ đậm và hai trang phải hiển thị đầy đủ.\r" +
                    $"UTC: {DateTime.UtcNow:O}\r\fTRANG 2 - SECOND PAGE\rDữ liệu Word được giữ nguyên qua máy in PDF.\r";
                document.Content.Font.Name = "Arial";
                document.Content.Font.Size = 14;
                document.Paragraphs[1].Range.Font.Bold = 1;
                document.Paragraphs[1].Range.Font.Size = 20;
                // Unlike Application.ActivePrinter, this does not replace the
                // user's Windows default printer or touch their open documents.
                word.WordBasic.FilePrintSetup(Printer: TestPrinter.Name, DoNotSetAsSysDefault: 1);
                document.PrintOut(Background: false);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { if (document is not null) document.Close(SaveChanges: 0); }
                catch (Exception ex) { failure ??= ex; }
                try { if (word is not null) word.Quit(SaveChanges: 0); }
                catch (Exception ex) { failure ??= ex; }
            }
            if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private sealed class PdfWindowsHost(string root) : INativeRelayHost
    {
        public string DataDirectory => root;
        public string? NativePeerFor(string address, string localAddress) => address == "127.0.0.1" ? "pdf-validation-peer" : null;
        public bool NativePrinterSelected(string printer) => printer == TestPrinter.Name;
        public bool NativePeerAllowed(string id, string printer) => false;
        public PeerClient NativePeer(string id, string printer) => throw new UnauthorizedAccessException();
        public void NativeLog(string message) => Console.WriteLine(message);
    }
}
