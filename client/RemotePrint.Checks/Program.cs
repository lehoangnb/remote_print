using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RemotePrint.Client;
using RemotePrint.Service;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args[0] == "--prepare-migration")
        {
            Console.WriteLine(new ConfigService().PrepareServiceMigration());
            return;
        }
        if (args[0] == "--installed-status")
        {
            var snapshot = new ServiceClient().CallAsync(new("status")).GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new { snapshot.Connection, Status = snapshot.State.Status,
                Room = snapshot.State.Room, Printers = snapshot.Printers.Count, Pending = snapshot.Pending.Count }));
            return;
        }
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            try
            {
                if (args[0] == "--render-client") await RenderClientAsync(args[1]);
                else await RunChecksAsync(args[0]);
                application.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); application.Shutdown(1); }
        };
        application.Run();
    }

    private static async Task RenderClientAsync(string root)
    {
        var directory = Path.Combine(root, "artifacts", "client-layout");
        Directory.CreateDirectory(directory);
        var window = new MainWindow { ShowActivated = false, Opacity = 0 };
        window.Show();
        await Task.Delay(1800);
        window.StopAgent();
        var grid = (System.Windows.Controls.Grid)window.Content;
        foreach (var dimensions in new[] { (1160, 840), (1000, 740) })
        {
            window.Width = dimensions.Item1;
            window.Height = dimensions.Item2;
            window.UpdateLayout();
            var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("PrinterScroll");
            Console.WriteLine($"Printer viewport at {dimensions.Item1}x{dimensions.Item2}: {scroll.ViewportHeight:F0} px");
            var activity = (System.Windows.Controls.ListBox)window.FindName("ActivityList");
            Console.WriteLine($"Activity viewport at {dimensions.Item1}x{dimensions.Item2}: {activity.ActualHeight:F0} px");
            Render(grid, Path.Combine(directory, $"client-{dimensions.Item1}x{dimensions.Item2}.png"),
                (int)grid.ActualWidth, (int)grid.ActualHeight);
        }
        window.Close();
        Console.WriteLine("Layout renders: " + directory);
    }

    private static async Task RunChecksAsync(string root)
    {
        var testRoot = Path.Combine(root, "artifacts", "service-checks", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(testRoot);
        var data = Path.Combine(testRoot, "data");
        var spool = Path.Combine(testRoot, "spool");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(data, "access.json"),
            JsonSerializer.Serialize(new ServiceSettings(WindowsIdentity.GetCurrent().User!.Value)));
        var pdf = CreatePdf();
        var input = Path.Combine(testRoot, "input.pdf");
        var output = Path.Combine(testRoot, "printed.pdf");
        File.WriteAllBytes(input, pdf);
        await Task.Run(() => PdfPrinter.Print(input, "Microsoft Print to PDF", output));
        Require(File.Exists(output) && new FileInfo(output).Length > 100, "Native PDF printing without WebView2");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:17444");
        await using var server = builder.Build();
        var statusCalls = 0;
        var acceptedJobs = 0;
        var state = new ClientState { Status = "approved", Room = "Checks" };
        using var recipientKey = System.Security.Cryptography.RSA.Create(3072);
        var peer = new PeerClient { Id = Guid.NewGuid().ToString(), Name = "Office-Test",
            Printers = ["Test Printer"], PublicKey = Convert.ToBase64String(recipientKey.ExportSubjectPublicKeyInfo()), LastSeenAt = DateTime.UtcNow };
        server.MapPost("/api/client/register", () => state);
        server.MapGet("/api/client/status", () => { Interlocked.Increment(ref statusCalls); return state; });
        server.MapGet("/api/client/peers", () => { peer.LastSeenAt = DateTime.UtcNow; return new[] { peer }; });
        server.MapGet("/api/jobs/pending", () => Array.Empty<PrintJob>());
        server.MapPost("/api/jobs", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            var payload = body.RootElement.GetProperty("payload").Deserialize<EncryptedPayload>(ServiceProtocol.Json)!;
            using var key = System.Security.Cryptography.RSA.Create();
            key.ImportPkcs8PrivateKey(recipientKey.ExportPkcs8PrivateKey(), out _);
            var decrypted = EncryptionService.Decrypt(payload, key);
            Require(decrypted.SequenceEqual(pdf), "Service encrypts and sends the captured PDF");
            Interlocked.Increment(ref acceptedJobs);
            return new PrintJob { Id = Guid.NewGuid().ToString() };
        });
        await server.StartAsync();
        var executable = Path.Combine(root, "dist", "client-win-x64", "Service", "RemotePrint.Service.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--DataDirectory=" + data);
        start.ArgumentList.Add("--SpoolDirectory=" + spool);
        using var service = Process.Start(start)!;
        try
        {
            var ipc = new ServiceClient();
            ServiceSnapshot? snapshot = null;
            for (var i = 0; i < 12; i++)
            {
                try { snapshot = await ipc.CallAsync(new("status")); break; }
                catch { await Task.Delay(500); }
            }
            Require(snapshot is not null, "Service starts independently and authenticates local IPC");
            Require(!JsonSerializer.Serialize(snapshot).Contains("ProtectedPrivateKey"), "IPC never returns the private key");
            var printer = snapshot!.Printers.FirstOrDefault() ?? "Microsoft Print to PDF";
            await ipc.CallAsync(new("configure", new DeviceSettings("Service-Test", "http://127.0.0.1:17444", [printer])));
            await Task.Delay(2600);
            snapshot = await ipc.CallAsync(new("status"));
            Require(snapshot.State.Status == "approved" && snapshot.Peers.Count == 1, "Service registration and Room peers");

            var window = new MainWindow { ShowActivated = false, Opacity = 0 };
            window.Show();
            window.Close();
            Require(!window.IsVisible, "Closing the manager hides it");
            var callsBefore = statusCalls;
            await Task.Delay(2500);
            Require(!service.HasExited && statusCalls > callsBefore, "Service keeps polling after the manager closes");
            window.StopAgent();
            var grid = (System.Windows.Controls.Grid)window.Content;
            Render(grid, Path.Combine(testRoot, "client-ui.png"), 1144, 750);

            // Capture and durability work with the UI closed, including service restarts.
            File.Copy(input, Path.Combine(spool, "capture.pdf"));
            await Task.Delay(2500);
            snapshot = await ipc.CallAsync(new("status"));
            Require(snapshot.Pending.Count == 1, "Service captures a virtual print while the manager is closed");
            var pendingId = snapshot.Pending[0].Id;
            service.Kill(true);
            await service.WaitForExitAsync();
            using var restarted = Process.Start(start)!;
            try
            {
                await Task.Delay(2500);
                snapshot = await ipc.CallAsync(new("status"));
                Require(snapshot.Pending.Any(item => item.Id == pendingId), "Pending document survives service restart");
                await ipc.CallAsync(new("send", DocumentId: pendingId, TargetId: peer.Id, PrinterName: "Test Printer"));
                Require(acceptedJobs == 1, "Service relays the encrypted job");
                snapshot = await ipc.CallAsync(new("status"));
                Require(snapshot.Pending.Count == 0, "Sent document is removed from the local pending queue");
                var rejected = false;
                try { await ipc.CallAsync(new("cancel", DocumentId: "../config")); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "IPC rejects file path traversal");
            }
            finally { if (!restarted.HasExited) { restarted.Kill(true); await restarted.WaitForExitAsync(); } }

            var emptySelector = new RoutePrintWindow([], input);
            Render((System.Windows.Controls.Grid)emptySelector.Content, Path.Combine(testRoot, "selector-empty.png"), 704, 480);
            var selector = new RoutePrintWindow([peer], input);
            Require(selector.TargetClient?.Id == peer.Id && selector.PrinterName == "Test Printer", "Selector preselects a valid printer");
            Render((System.Windows.Controls.Grid)selector.Content, Path.Combine(testRoot, "selector-ready.png"), 704, 480);
            Console.WriteLine("Artifacts: " + testRoot);
        }
        finally
        {
            if (!service.HasExited) { service.Kill(true); await service.WaitForExitAsync(); }
            await server.StopAsync();
        }
    }

    private static void Render(System.Windows.UIElement control, string path, int width, int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static byte[] CreatePdf()
    {
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var stream = "BT /F1 24 Tf 60 760 Td (Remote Print Service Check) Tj ET";
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var obj in objects)
        { offsets.Add(text.Length); text.Append($"{offsets.Count - 1} 0 obj\n{obj}\nendobj\n"); }
        offsets.Add(text.Length);
        text.Append($"5 0 obj\n<< /Length {stream.Length} >>\nstream\n{stream}\nendstream\nendobj\n");
        var xref = text.Length;
        text.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static void Require(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("FAIL: " + description); Console.WriteLine("PASS: " + description); }
}
