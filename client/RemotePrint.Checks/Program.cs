using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemotePrint.Client;
using RemotePrint.Service;

internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        var root = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
        switch (args.FirstOrDefault())
        {
            case "--update-check": CheckClientUpdatesAsync(root).GetAwaiter().GetResult(); return;
            case "--mesh-check": RunMeshChecksAsync(root).GetAwaiter().GetResult(); return;
            case "--native-relay-check": CheckNativeRelayAsync(root).GetAwaiter().GetResult(); return;
            case "--installed-printer-check": CheckInstalledPrintersAsync(root).GetAwaiter().GetResult(); return;
            case "--test-printer-check": CheckTestPrinterAsync(root).GetAwaiter().GetResult(); return;
            case "--pdf-windows-check": CheckPdfWindowsAsync(root, args.Length > 2 ? args[2] : null).GetAwaiter().GetResult(); return;
            case "--pdf-word-windows-check": CheckPdfWindowsAsync(root, null, wordFixture: true).GetAwaiter().GetResult(); return;
            case "--wireguard-check": RunWireGuardCheck(root).GetAwaiter().GetResult(); return;
            case "--tunnel-config-check": RunWireGuardCheck(null).GetAwaiter().GetResult(); return;
            case "--installer-check": RunInstallerCheck(root); return;
            case "--printer-read-check": CheckPrinterRead(args[1]); return;
            case "--autosave-check": CheckPrinterAutosave(); return;
            case "--ui-fit-check": case "--render-client": CheckManager(root); return;
            default: RunServiceChecksAsync(args.FirstOrDefault() ?? root).GetAwaiter().GetResult(); return;
        }
    }

    private static async Task RunServiceChecksAsync(string root)
    {
        var directory = Path.Combine(root, "artifacts", "service-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        var registered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        var state = new ClientState { Status = "approved", Room = "Checks" };
        api.MapPost("/api/client/register", async (HttpRequest request) => {
            using var body = await JsonDocument.ParseAsync(request.Body);
            registered.TrySetResult(body.RootElement.Clone());
            registrations.Writer.TryWrite(body.RootElement.Clone());
            return Results.Json(state);
        });
        api.MapGet("/api/client/status", () => Results.Json(state));
        const string inviteId = "11111111111111111111111111111111";
        var inviteKey = new string('k', 43);
        var paired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.MapPost("/api/client/enroll/" + inviteId, (HttpRequest request) =>
        {
            paired.TrySetResult(request.Headers["x-enrollment-key"] == inviteKey);
            return Results.Json(state);
        });
        var peerId = Guid.NewGuid().ToString();
        api.MapGet("/api/client/peers", () => Results.Json(new[] { new PeerClient {
            Id = peerId, Name = "Office", LastSeenAt = DateTime.UtcNow,
            NativePrinting = true, Printers = ["Office Printer"], WindowsPrinterShares = new() { ["Office Printer"] = "Office" }
        } }));
        api.MapGet("/api/client/tunnel", () => Results.Json(new { error = "TUNNEL_NOT_CONFIGURED" }, statusCode: 503));
        await api.StartAsync();
        var address = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var store = new MachineStore(directory);
        var config = store.Load(); config.ServerUrl = address; store.Save(config);
        var legacyConfig = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.ConfigPath))!;
        legacyConfig["PublicKey"] = "retired-document-key";
        legacyConfig["ProtectedPrivateKey"] = "retired-protected-document-key";
        File.WriteAllText(store.ConfigPath, legacyConfig.ToJsonString());
        var pipeName = "RemotePrint.Checks." + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(directory, "access.json"), JsonSerializer.Serialize(new ServiceSettings(WindowsIdentity.GetCurrent().User!.Value)));
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = directory, ["PipeName"] = pipeName, ["SkipTestPrinterUpgrade"] = "true" });
        hostBuilder.Services.AddSingleton<ClientEngine>();
        hostBuilder.Services.AddHostedService<DiscoveryWorker>();
        hostBuilder.Services.AddHostedService<PipeWorker>();
        using var host = hostBuilder.Build();
        await host.StartAsync();
        try
        {
            var registration = await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!registration.TryGetProperty("publicKey", out _) && !registration.TryGetProperty("printerCapabilities", out _), "Registration contains device/share metadata only");
            var client = new ServiceClient(pipeName);
            ServiceSnapshot? snapshot = null;
            await WaitMeshAsync(async () => { snapshot = await client.CallAsync(new("status")); return snapshot.State.Status == "approved" && snapshot.Peers.Count == 1; });
            Require(snapshot!.Peers.Single().NativePrinting, "IPC discovers native printer relay support in the approved Room");
            Require(snapshot.PrinterRemoval && snapshot.InstalledPrinters.Count == 0, "IPC advertises printer removal and the installed queue list");
            try { await client.CallAsync(new("remove-printer", QueueId: "unknown")); throw new Exception("Unknown queue accepted through IPC"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("không còn")) { Require(true, "IPC removal rejects queues not managed by this service"); }
            Require(snapshot.TestFileAccess, "IPC advertises the installed service's test file reader capability");
            await CheckTestFilePipeAsync(client, directory);
            var invitationJson = JsonSerializer.Serialize(new { version = 1, serverUrl = address,
                endpoint = "api/client/enroll/" + inviteId, key = inviteKey, expiresAt = DateTime.UtcNow.AddMinutes(5) });
            var invitationCode = "RPPAIR1." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(invitationJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var pairSnapshot = await client.CallAsync(new("configure", new DeviceSettings("Test PC", invitationCode, [])));
            Require(pairSnapshot.Settings.ServerUrl == address && !JsonSerializer.Serialize(pairSnapshot).Contains(inviteKey),
                "IPC normalizes invitation into the server URL without exposing the temporary key");
            Require(await paired.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Client enrolls through the temporary endpoint with its temporary key");
            await WaitMeshAsync(() => {
                using var saved = JsonDocument.Parse(File.ReadAllText(store.ConfigPath));
                return Task.FromResult(saved.RootElement.GetProperty("EnrollmentKey").GetString()!.Length == 0);
            });
            Require(true, "Temporary enrollment key is removed after successful registration");
            var localPrinter = snapshot.Printers.FirstOrDefault();
            if (localPrinter is not null)
            {
                var before = await client.CallAsync(new("status"));
                var configured = await client.CallAsync(new("configure", new DeviceSettings("Test PC", address, [localPrinter])));
                Require(configured.Connection == before.Connection && configured.State.Status == before.State.Status &&
                    configured.Peers.Select(p => p.Id).SequenceEqual(before.Peers.Select(p => p.Id)),
                    "Changing printers preserves the connected session and Room peers");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                JsonElement next;
                do { next = await registrations.Reader.ReadAsync(timeout.Token); }
                while (!next.GetProperty("printers").EnumerateArray().Any(p => p.GetString() == localPrinter));
                Require(true, "Selecting a local printer after connection re-registers it with the server");
            }
            else Console.WriteLine("SKIP: No local printer available for registration change check");
            var json = JsonSerializer.Serialize(snapshot, ServiceProtocol.Json);
            Require(!json.Contains(config.DeviceSecret) && !json.Contains("pending", StringComparison.OrdinalIgnoreCase), "IPC exposes no device secret or document queue");
            foreach (var action in new[] { "send", "inspect", "cancel" })
            {
                try { await client.CallAsync(new(action)); throw new Exception("Removed action was accepted"); }
                catch (InvalidOperationException) { Require(true, $"Removed document action {action} is rejected"); }
            }
            Require(!Directory.Exists(Path.Combine(directory, "Pending")) && !Directory.Exists(Path.Combine(directory, "Receipts")), "Service creates no document storage");
            Require(new MachineStore(directory).Load().DeviceId == config.DeviceId, "Device identity survives configuration normalization");
            using var normalized = JsonDocument.Parse(File.ReadAllText(store.ConfigPath));
            Require(!normalized.RootElement.TryGetProperty("PublicKey", out _) &&
                !normalized.RootElement.TryGetProperty("ProtectedPrivateKey", out _) &&
                normalized.RootElement.GetProperty("DeviceSecret").GetString() == config.DeviceSecret,
                "Upgrade removes document keys and preserves device authentication");
        }
        finally { await host.StopAsync(); await api.StopAsync(); }
    }

    private static void CheckManager(string root)
    {
        Application.EnableVisualStyles();
        var originalLanguage = L.Language;
        L.UseLanguage("vi");
        using var manager = new MainForm(headless: true);
        manager.Show(); manager.StopAgent();
        var snapshot = new ServiceSnapshot { NativePrinting = true, Connection = "Đã kết nối", TunnelStatus = "Đã kết nối",
            State = new ClientState { Status = "approved", Room = "Office" },
            Settings = new DeviceSettings("This PC", "https://print.example.com", ["Local queue"]), Printers = ["Local queue"],
            Peers = [new() { Id = Guid.NewGuid().ToString(), Name = "Office PC", TunnelAddress = "10.247.0.3", TunnelPath = "Trực tiếp",
                NativePrinting = true, Printers = ["Office Printer", "Second Printer"] }] };
        var apply = typeof(MainForm).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Apply(ServiceSnapshot state) => apply.Invoke(manager, [state]);
        Apply(snapshot);
        Require(manager.RoomText.Text == "Office", "Legacy single Room snapshots remain readable");
        snapshot.State.Rooms = ["Office", "Warehouse"]; Apply(snapshot);
        Require(manager.RoomText.Text == "Office, Warehouse" && manager.RoomText.GetTip() == "Office, Warehouse",
            "Windows shows all assigned Rooms and preserves the full list in a tooltip");
        Require(!manager.InstalledPrintersItem.Enabled, "Older services cannot receive unsupported printer removal commands");
        snapshot.PrinterRemoval = true; Apply(snapshot);
        Require(manager.InstalledPrintersItem.Enabled, "Installed printers can be managed without selecting an online peer");
        Require(!manager.ConnectPrinterButton.Enabled, "Connection requires an explicit peer selection");
        manager.PeersGrid.SelectedIndex = 0;
        manager.PerformLayout();
        Require(manager.ConnectPrinterButton.Enabled && manager.RemotePrinters.Items.Count == 2, "Native printers are selectable without a Windows SMB share");
        var selectedPeer = snapshot.Peers[0];
        var refreshedPeer = new PeerClient { Id = selectedPeer.Id, Name = selectedPeer.Name,
            TunnelAddress = selectedPeer.TunnelAddress, TunnelPath = selectedPeer.TunnelPath,
            NativePrinting = true, Printers = selectedPeer.Printers, WindowsPrinterShares = selectedPeer.WindowsPrinterShares };
        snapshot.Peers = [refreshedPeer]; Apply(snapshot);
        Require(ReferenceEquals(manager.PeersGrid.SelectedItem, selectedPeer) && manager.ConnectPrinterButton.Enabled,
            "Unchanged discovery preserves the peer and printer selection");
        refreshedPeer.TunnelAddress = "10.247.0.4"; Apply(snapshot);
        Require(ReferenceEquals(manager.PeersGrid.SelectedItem, refreshedPeer) && manager.ConnectPrinterButton.Enabled,
            "A changed mesh address updates the selected peer");
        snapshot.TunnelStatus = "Mất kết nối"; Apply(snapshot);
        Require(!manager.ConnectPrinterButton.Enabled, "Connection is disabled when the tunnel disconnects");
        snapshot.TunnelStatus = "Đã kết nối"; Apply(snapshot);
        Require(manager.ConnectPrinterButton.Enabled, "Connection becomes available when the tunnel recovers");
        Require(!manager.PrintTestButton.Enabled, "Test page action does not target a physical printer");
        snapshot.TestPrinting = true;
        snapshot.TestPdfPrinting = true;
        Require(!manager.OpenTestFilesButton.Enabled, "Older services cannot open test files through unsupported IPC");
        snapshot.TestFileAccess = true; Apply(snapshot);
        Require(!manager.OpenTestFilesButton.Visible && !manager.OpenTestFilesButton.Enabled, "Opening test files is hidden until a virtual printer is installed");
        snapshot.TestPrinterInstalled = true; snapshot.TestFileMaintenance = true; Apply(snapshot);
        Require(manager.OpenTestFilesButton.Visible && manager.OpenTestFilesButton.Enabled, "Installing the virtual printer reveals the received file action");
        snapshot.TestPrinterInstalled = false; Apply(snapshot);
        Require(!manager.OpenTestFilesButton.Visible, "Uninstalling the virtual printer hides the file action again");
        snapshot.TestPrinterInstalled = true; Apply(snapshot);
        // Replace the row so the UI observes a changed printer list.
        snapshot.Peers = [new PeerClient { Id = refreshedPeer.Id, Name = refreshedPeer.Name, TunnelAddress = refreshedPeer.TunnelAddress,
            NativePrinting = true, Printers = [TestPrinter.Name] }];
        Apply(snapshot);
        Require(manager.PrintTestButton.Enabled, "Virtual printer selection enables the test page action");
        snapshot.TunnelStatus = "Mất kết nối"; Apply(snapshot);
        Require(!manager.PrintTestButton.Enabled, "Test printing is disabled when the tunnel disconnects");
        snapshot.TunnelStatus = "Đã kết nối"; snapshot.TestPrinting = false; Apply(snapshot);
        Require(!manager.PrintTestButton.Enabled, "Older services cannot receive unsupported test printing commands");
        snapshot.TestPrinting = true; Apply(snapshot);
        snapshot.Printers = []; Apply(snapshot);
        var absentIndex = manager.PrintersList.Items.IndexOf("Local queue");
        Require(absentIndex >= 0 && manager.PrintersList.GetItemChecked(absentIndex) &&
            manager.PrintersList.GetItemText(manager.PrintersList.Items[absentIndex])?.Contains("không khả dụng") == true,
            "Configured shares remain visible when the local queue disappears");
        snapshot.Printers = ["Local queue"]; Apply(snapshot);
        manager.PrintersList.SetItemChecked(0, false);
        snapshot.Printers = []; Apply(snapshot);
        snapshot.Printers = ["Local queue"]; Apply(snapshot);
        Require(!manager.PrintersList.GetItemChecked(0), "Unsaved share selection survives discovery changes");
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        foreach (var button in Descendants(manager).OfType<Button>())
            Require(TextRenderer.MeasureText(button.Text, button.Font).Width + 8 <= button.Width, "Button caption fits: " + button.Text);
        snapshot.Activity = [new(DateTime.Now, "Đã kết nối server."),
            new(DateTime.Now, "Tunnel lỗi: Địa chỉ server không hợp lệ."),
            new(DateTime.Now, "Đã nhận lệnh in RAW vào Windows: Máy in A, job 42.")];
        snapshot.TunnelStatus = "Đang kết nối relay";
        Apply(snapshot);
        var languageEvents = 0;
        manager.LanguageChanged += (_, _) => languageEvents++;
        manager.ApplyLanguage("en");
        Require(manager.InstalledPrintersItem.Text == "Installed printers…", "Installed printer menu switches to English");
        using (var installed = new InstalledPrintersForm(new ServiceClient("RemotePrint.Unused.Checks")))
        {
            installed.ApplySnapshot(new ServiceSnapshot { InstalledPrinters = [new("id", "Office_Canon")] });
            Require(!installed.RemoveButton.Enabled, "Printer removal requires an explicit selection");
            installed.Printers.SelectedIndex = 0;
            Require(installed.RemoveButton.Enabled, "Installed printers remain removable with no online peers or tunnel");
            Require(TextRenderer.MeasureText(installed.RemoveButton.Text, installed.RemoveButton.Font).Width + 8 <= installed.RemoveButton.Width,
                "English remove printer button fits");
            installed.ApplySnapshot(new ServiceSnapshot());
            Require(!installed.RemoveButton.Enabled && installed.Printers.Items.Count == 0, "Removing the last printer clears the list and disables removal");
        }
        Require(manager.ConnectPrinterButton.Text == "Connect printer" && manager.ScanPrintersButton.Text == "Rescan",
            "Switching to English updates the open window");
        Require(manager.PrintTestButton.Text == "Print test", "Virtual printer test button switches to English");
        Require(manager.OpenTestFilesButton.Text == "Open test files…", "Received test file action switches to English");
        Require(!manager.PrintersList.GetItemChecked(0) && manager.PeersGrid.SelectedItem is PeerClient,
            "Switching language preserves unsaved printer selection and selected peer");
        Require(manager.PeersSummaryText.Text.Contains("online clients"), "Dynamic status is refreshed in English");
        Require(manager.TunnelText.Text == "Connecting to relay", "Relay connection status is translated");
        Require(PeerListBox.PeerTitle(new PeerClient { Name = "Máy A", TunnelPath = "Trực tiếp" }) == "Máy A · Direct",
            "Peer route is translated without changing the computer name");
        Require(manager.ActivityList.Items.Cast<string>().Any(x => x.Contains("Connected to the server.")) &&
            manager.ActivityList.Items.Cast<string>().Any(x => x.Contains("Tunnel error: Invalid server address.")) &&
            manager.ActivityList.Items.Cast<string>().Any(x => x.Contains("Submitted RAW print job to Windows: Máy in A, job 42.")),
            "Activity messages and nested errors are translated while printer names are preserved");
        Require(L.Message("Mã ghép nối không hợp lệ.") == "Invalid pairing code." &&
            L.Message("Windows error 123") == "Windows error 123", "Known service errors are translated and unknown errors are preserved");
        foreach (var button in Descendants(manager).OfType<Button>())
            Require(TextRenderer.MeasureText(button.Text, button.Font).Width + 8 <= button.Width, "English button caption fits: " + button.Text);
        manager.ApplyLanguage("vi");
        Require(manager.ConnectPrinterButton.Text == "Kết nối máy in" && manager.ScanPrintersButton.Text == "Quét lại",
            "Switching back to Vietnamese updates the same window");
        Require(manager.TunnelText.Text == "Đang kết nối relay" &&
            manager.ActivityList.Items.Cast<string>().Any(x => x.Contains("Đã kết nối server.")),
            "Switching back restores original service messages");
        Require(languageEvents >= 1, "Language change event updates the tray");
        manager.ApplyLanguage(originalLanguage);
        var directory = Path.Combine(root, "artifacts", "client-layout"); Directory.CreateDirectory(directory);
        using var bitmap = new System.Drawing.Bitmap(manager.Width, manager.Height);
        manager.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, manager.Width, manager.Height));
        bitmap.Save(Path.Combine(directory, "manager-native-printing.png"));
        manager.StopAgent();
    }

    private static void CheckPrinterAutosave()
    {
        Application.EnableVisualStyles();
        var name = "RemotePrint.Autosave." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var saved = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
        var server = Task.Run(async () =>
        {
            var settings = new DeviceSettings("Test PC", "https://example.test", []);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut,
                        1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    var request = await ServiceProtocol.ReadAsync<ServiceRequest>(pipe, stop.Token);
                    if (request.Action == "configure")
                    {
                        settings = request.Settings!;
                        saved.Enqueue(settings.SharedPrinters.ToArray());
                    }
                    var state = new ServiceSnapshot { Settings = settings, Printers = ["Test printer"],
                        Connection = "Đã kết nối", State = new ClientState { Status = "approved", Room = "Office" } };
                    await ServiceProtocol.WriteAsync(pipe, new ServiceReply(true, state), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var manager = new MainForm(name, headless: true);
            manager.Show();
            void WaitFor(Func<bool> condition)
            {
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10))
                { Application.DoEvents(); Thread.Sleep(20); }
                if (!condition()) throw new Exception("Autosave IPC timed out");
            }
            WaitFor(() => manager.PrintersList.Items.Count == 1);
            manager.PrintersList.SetItemChecked(0, true);
            WaitFor(() => saved.Any(list => list.SequenceEqual(["Test printer"])));
            Require(true, "Selecting a printer automatically saves through the service pipe");
            manager.PrintersList.SetItemChecked(0, false);
            WaitFor(() => saved.Count >= 2 && saved.Last().Length == 0);
            Require(true, "Deselecting a printer automatically saves the removal");
            manager.StopAgent();
        }
        finally { stop.Cancel(); server.GetAwaiter().GetResult(); }
    }

    private static void CheckPrinterRead(string printer)
    {
        // An impossible expected share ensures the method stops before SetPrinter.
        try
        {
            PrinterService.SetWindowsPrinterShare(printer, "RemotePrint-probe-" + Guid.NewGuid().ToString("N"), null);
            throw new Exception("Printer state unexpectedly matched the random test share");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("chia sẻ máy in đã thay đổi", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("PASS: OpenPrinter can read real printer configuration with ADMINISTER | USE; no share was changed.");
        }
    }
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
