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
        app.MapGet("/api/client/updates/{runtime}", () => mode switch
        {
            "none" => Results.NoContent(), "old-server" => Results.NotFound(),
            "same" => Results.Json(release with { Version = current.ToString(3) }),
            "downgrade" => Results.Json(release with { Version = "1.0.0" }),
            "wrong-runtime" => Results.Json(release with { Version = newer, Runtime = "win-x86" }),
            "bad-path" => Results.Json(release with { Version = newer, DownloadPath = "https://other.example/package.zip" }),
            "failed" => Results.StatusCode(503),
            _ => Results.Json(release with { Version = newer })
        });
        app.MapGet(release.DownloadPath, () => Results.Bytes(bytes, "application/zip"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var updater = new ClientUpdater();
        Require((await updater.CheckAsync(address, default))?.Version == newer, "Client discovers a newer version over real HTTP");
        foreach (var value in new[] { "none", "old-server", "same", "downgrade" })
        {
            mode = value;
            Require(await updater.CheckAsync(address, default) is null, "No upgrade for " + value);
        }
        foreach (var value in new[] { "wrong-runtime", "bad-path", "failed" })
        {
            mode = value;
            try { await updater.CheckAsync(address, default); throw new Exception("Invalid release accepted"); }
            catch (Exception ex) when (ex is InvalidDataException or System.Net.Http.HttpRequestException)
            { Require(true, "Reject update metadata: " + value); }
        }
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
            Require(form.MainMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().First().DropDownItems.Contains(form._checkUpdateItem),
                "Check for updates is available in Settings MenuStrip");
            form.ApplyLanguage("en");
            Require(form._checkUpdateItem.Text == "Check for updates…", "Update menu switches to English immediately");
            form.ApplyLanguage("vi"); form.StopAgent();
        }
        SynchronizationContext.SetSynchronizationContext(previousContext);
        await app.StopAsync();
    }
}
