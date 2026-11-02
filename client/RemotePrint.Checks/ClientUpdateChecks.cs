using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using RemotePrint.Service;
using RemotePrint.Client;

internal static partial class Program
{
    private static async Task CheckClientUpdatesAsync(string root)
    {
        L.UseLanguage("vi");
        var directory = Path.Combine(root, "artifacts", "update-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(root, "dist", $"RemotePrint.Client-{ClientUpdater.Runtime}.zip");
        var bytes = await File.ReadAllBytesAsync(archive);
        string packageVersion;
        using (var zip = ZipFile.OpenRead(archive))
        using (var reader = new StreamReader(zip.GetEntry("build-info.json")!.Open()))
            packageVersion = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.GetProperty("version").GetString()!;
        var release = new ClientRelease(packageVersion, ClientUpdater.Runtime, "Checks", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "");
        release = release with { DownloadPath = $"/api/client/updates/packages/{release.Sha256}.zip" };
        ClientUpdater.ExtractAndVerify(archive, Path.Combine(directory, "valid"), release);
        Require(File.Exists(Path.Combine(directory, "valid", "Service", "RemotePrint.Service.exe")),
            "Published ZIP extracts with matching version, runtime and SHA-256 for every payload file");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var mode = "new";
        var current = typeof(ClientUpdater).Assembly.GetName().Version!;
        var newer = new Version(current.Major, current.Minor + 1, 0).ToString(3);
        var receivedPaths = new List<string>();
        var badIdentity = false;
        app.Use(async (context, next) =>
        {
            receivedPaths.Add(context.Request.Path);
            if (context.Request.Headers[ClientUpdateProtocol.VersionHeader] != ClientUpdater.CurrentVersion ||
                context.Request.Headers[ClientUpdateProtocol.RuntimeHeader] != ClientUpdater.Runtime) badIdentity = true;
            var command = release with { Version = newer };
            command = mode switch
            {
                "same" => command with { Version = current.ToString(3) },
                "same-four" => command with { Version = current.ToString(4) },
                "downgrade" => command with { Version = "1.0.0" },
                "wrong-runtime" => command with { Runtime = "win-x86" },
                "bad-path" => command with { DownloadPath = "https://other.example/package.zip" },
                "bad-hash" => command with { Sha256 = "invalid" },
                "bad-size" => command with { Size = ClientUpdater.MaxPackageSize + 1 },
                "bad-version" => command with { Version = "01.2.3" },
                "bad-number" => command with { Version = "1.2.65535" },
                _ => command
            };
            if (mode is not ("none" or "old-server"))
                context.Response.Headers[ClientUpdateProtocol.UpdateHeader] = mode == "malformed" ? "!not-base64!" :
                    mode == "oversized" ? new string('x', 2049) :
                    Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(command, ServiceProtocol.Json));
            if (mode == "failed") { context.Response.StatusCode = 503; return; }
            await next();
        });
        app.MapGet("/api/client/status", () => Results.Json(new ClientState { Status = "approved" }));
        app.MapPost("/api/client/register", () => Results.Json(new ClientState { Status = "pending" }));
        app.MapGet("/api/client/peers", () => Results.Json(Array.Empty<PeerClient>()));
        app.MapGet("/api/client/tunnel", () => Results.Json(new TunnelInfo()));
        app.MapPut("/api/client/tunnel", () => Results.Json(new TunnelInfo()));
        app.MapGet(release.DownloadPath, () => Results.Bytes(bytes, "application/zip"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var config = new ClientConfig { ServerUrl = address };
        ClientRelease? offered = null;
        var commands = 0;
        using var api = new RemotePrintApi(config, command => { offered = command; commands++; });
        await api.RegisterAsync();
        Require(offered?.Version == newer, "Registration receives the server update command without a manifest check");
        await api.GetStatusAsync(); await api.GetPeersAsync(); await api.GetTunnelAsync(default);
        await api.EnrollTunnelAsync("checks", default); await api.HeartbeatAsync(default);
        Require(commands == 6 && !badIdentity, "Registration, status, peers, tunnel and heartbeat send version/runtime and receive commands");
        foreach (var value in new[] { "none", "old-server", "same", "same-four", "downgrade", "wrong-runtime", "bad-path", "bad-hash", "bad-size", "bad-version", "bad-number", "malformed", "oversized" })
        {
            mode = value; offered = null;
            await api.HeartbeatAsync(default);
            Require(offered is null, "No automatic upgrade for " + value);
        }
        mode = "failed"; offered = null;
        try { await api.HeartbeatAsync(default); throw new Exception("API failure ignored"); }
        catch (System.Net.Http.HttpRequestException) { Require(offered is null, "Failed API responses never trigger installation"); }
        mode = "new";
        // Verify that the real API command is carried through the engine snapshot and IPC serialization.
        var engineDirectory = Path.Combine(directory, "engine");
        var store = new MachineStore(engineDirectory);
        var saved = store.Load(); saved.ServerUrl = address; store.Save(saved);
        using (var engine = new ClientEngine(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["DataDirectory"] = engineDirectory, ["SkipTestPrinterUpgrade"] = "true" }).Build()))
        using (var heartbeat = new RemotePrintApi(config, command => engine.ReceiveUpdate(address, command)))
        {
            await heartbeat.HeartbeatAsync(default);
            var snapshot = await engine.ExecuteAsync(new("refresh"), default);
            using var stream = new MemoryStream();
            await ServiceProtocol.WriteAsync(stream, new ServiceReply(true, snapshot), default); stream.Position = 0;
            var reply = await ServiceProtocol.ReadAsync<ServiceReply>(stream, default);
            Require(reply.Snapshot?.Update is { } update && update.ServerUrl == address && update.Release.Version == newer,
                "Server-issued command reaches the UI via the engine snapshot and framed IPC");
            engine.ReceiveUpdate("https://old-server.example", release with { Version = "99.0.0" });
            Require((await engine.ExecuteAsync(new("refresh"), default)).Update?.Release.Version == newer,
                "A delayed command from another server cannot replace the current command");
            mode = "none"; await heartbeat.HeartbeatAsync(default);
            Require((await engine.ExecuteAsync(new("refresh"), default)).Update is null, "A server without a command clears the pending update");
        }
        mode = "new";
        var attempts = new AutomaticUpdateAttempts();
        var request = new ClientUpdateRequest(address, release with { Version = newer });
        var now = DateTime.UtcNow;
        Require(attempts.TryBegin(request, now) && !attempts.TryBegin(request, now.AddSeconds(2)), "Repeated server replies do not loop on the same installation");
        Require(attempts.TryBegin(request, now.AddMinutes(15)), "Failed/canceled installation can retry after 15 minutes");
        Require(attempts.TryBegin(request with { Release = request.Release with { Sha256 = new string('1', 64) } }, now.AddMinutes(15).AddSeconds(1)),
            "A newly published package does not wait for the previous retry delay");
        Require(attempts.TryBegin(request with { ServerUrl = "https://another.example" }, now.AddMinutes(15).AddSeconds(2)),
            "A different server does not inherit the previous retry delay");
        Require(!attempts.TryBegin(request, now.AddMinutes(15).AddSeconds(3)),
            "An older in-flight command remains throttled after another package or server was attempted");
        using var updater = new ClientUpdater();
        foreach (var url in new[] { "http://print.example.com", "https://user:pass@print.example.com", "RPPAIR1.bad", "https://print.example.com/?secret=x" })
        {
            try { ClientUpdater.ServerUri(url); throw new Exception("Unsafe server accepted"); }
            catch (InvalidOperationException) { Require(true, "Update server requires HTTPS and excludes credentials/invitation text"); }
        }
        var downloaded = Path.Combine(directory, "download.zip");
        await updater.DownloadAsync(address, release, downloaded, null, default);
        Require((await File.ReadAllBytesAsync(downloaded)).SequenceEqual(bytes), "Downloaded archive matches the published length and SHA-256");
        foreach (var invalid in new[] { release with { Sha256 = new string('0', 64) }, release with { Size = bytes.Length - 1 } })
        {
            try { await updater.DownloadAsync(address, invalid, Path.Combine(directory, Guid.NewGuid() + ".zip"), null, default); throw new Exception("Corrupted download accepted"); }
            catch (InvalidDataException) { Require(true, "Corrupted hash/length is rejected before installation"); }
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await updater.DownloadAsync(address, release, Path.Combine(directory, "cancel.zip"), null, canceled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Require(true, "User cancellation stops download before installation"); }
        }
        foreach (var change in new[] { "traversal", "changed-file", "extra-file", "missing-file", "wrong-version", "wrong-runtime" })
        {
            var mutated = Path.Combine(directory, change + ".zip"); File.Copy(archive, mutated);
            using (var zip = ZipFile.Open(mutated, ZipArchiveMode.Update))
            {
                if (change == "traversal") { using var writer = new StreamWriter(zip.CreateEntry("../escape.exe").Open()); writer.Write("no"); }
                if (change == "extra-file") { using var writer = new StreamWriter(zip.CreateEntry("untracked.exe").Open()); writer.Write("no"); }
                if (change == "changed-file") { zip.GetEntry("translations.json")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("translations.json").Open()); writer.Write("changed"); }
                if (change == "missing-file") zip.GetEntry("RemotePrint.Client.exe")!.Delete();
            }
            var metadata = change == "wrong-version" ? release with { Version = newer } : change == "wrong-runtime" ? release with { Runtime = "win-x86" } : release;
            try { ClientUpdater.ExtractAndVerify(mutated, Path.Combine(directory, change), metadata); throw new Exception("Unsafe ZIP accepted"); }
            catch (InvalidDataException) { Require(true, "Reject ZIP before setup: " + change); }
        }
        Require(!File.Exists(Path.Combine(directory, "escape.exe")), "ZIP traversal writes no file outside staging");
        var previousContext = SynchronizationContext.Current;
        using (var form = new MainForm("RemotePrint.Update.Checks." + Guid.NewGuid().ToString("N"), headless: true))
        {
            var settings = form.MainMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().First();
            Require(!settings.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().Any(item => item.Name == "_checkUpdateItem" || (item.Text ?? "").Contains("Kiểm tra cập nhật")),
                "Settings no longer offers a manual update check");
            form.ApplyLanguage("en");
            Require(!settings.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().Any(item => (item.Text ?? "").Contains("Check for updates")),
                "English settings no longer offers a manual update check");
            form.ApplyLanguage("vi"); form.StopAgent();
        }
        SynchronizationContext.SetSynchronizationContext(previousContext);
        Require(!badIdentity && receivedPaths.All(path => !path.StartsWith("/api/client/updates/win-")),
            "Client traffic carries build identity without a separate manifest polling request");
        await app.StopAsync();
    }
}
