using System.Text.Json;
using RemotePrint.Client;
using RemotePrint.Service;

internal static partial class Program
{
    private static async Task CheckInstalledPrintersAsync(string root)
    {
        Require(NativePrintRelay.QueueLabel("MayKeToan", "Canon LBP2900") == "MayKeToan_Canon LBP2900",
            "Printer name contains only client and printer model without branding or an ID");
        Require(NativePrintRelay.QueueLabel(" Máy/A ", "Canon:2900") == "Máy_A_Canon_2900",
            "Printer labels preserve Unicode and replace Windows name separators");
        Require(NativePrintRelay.QueueLabel(new string('a', 200), "Canon").Length <= 180, "Queue label leaves room for a collision suffix");

        // Run the actual production setup scripts with isolated spooler cmdlets.
        // No Windows printer, job or driver is changed by these scenarios.
        const string spooler = """
            $script:printers=@($a.Printers | ForEach-Object {[pscustomobject]@{Name=$_.Name;PortName=$_.PortName}});
            $script:port=$a.Port; $script:removed=$false;
            function Get-Printer { $script:printers }
            function Get-CimInstance { param($ClassName) $script:port }
            function Get-PrintJob { param($PrinterName) if($a.Jobs){[pscustomobject]@{ID=1}} }
            function Rename-Printer { param($Name,$NewName) ($script:printers | Where-Object Name -eq $Name).Name=$NewName }
            function Remove-Printer { param($Name) $script:printers=@($script:printers | Where-Object Name -ne $Name) }
            function Remove-PrinterPort { param($Name) $script:removed=$true }
            """;
        object Scenario(string name = "RemotePrint old [1234]", string port = "RP_1234", bool jobs = false, string host = "127.0.0.1", bool collision = false) =>
            new { Id = "1234", Queue = name, Label = "Office_Canon", PortName = "RP_1234", QueueId = "1234", Jobs = jobs,
                Port = new { Name = "RP_1234", HostAddress = host, Protocol = 2, Queue = "1234" },
                Printers = collision ? new[] { new { Name = name, PortName = port }, new { Name = "Office_Canon", PortName = "USB001" } }
                    : [new { Name = name, PortName = port }] };
        async Task Reject(string script, object args, string message)
        {
            try { await NativePrinterSetup.PowerShellAsync(spooler + script, args, default); throw new Exception("Unsafe printer operation was allowed"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains(message)) { Require(true, message); }
        }
        var renamed = await NativePrinterSetup.PowerShellAsync(spooler + NativePrinterSetup.PrepareNameScript + "; $script:printers[0].Name", Scenario(), default);
        Require(renamed.Split('\n').Select(s => s.Trim()).SequenceEqual(["Office_Canon", "Office_Canon"]),
            "Reconnect renames the legacy queue in place and returns the shorter name");
        var collisionName = await NativePrinterSetup.PowerShellAsync(spooler + NativePrinterSetup.PrepareNameScript, Scenario(collision: true), default);
        Require(collisionName == "Office_Canon_2", "A local printer with the same name is preserved by adding a short suffix");
        await Reject(NativePrinterSetup.PrepareNameScript, Scenario(jobs: true), "Finish or cancel pending print jobs before renaming the printer");
        var removed = await NativePrinterSetup.PowerShellAsync(spooler + NativePrinterSetup.RemoveScript +
            "; @{Printers=$script:printers.Count;PortRemoved=$script:removed}|ConvertTo-Json -Compress", Scenario(), default);
        Require(removed.Contains("\"Printers\":0") && removed.Contains("\"PortRemoved\":true"), "Removal deletes the owned Windows queue and unused loopback port");
        await Reject(NativePrinterSetup.RemoveScript, Scenario(port: "USB001"), "Queue belongs to another configuration");
        await Reject(NativePrinterSetup.RemoveScript, Scenario(host: "192.0.2.1"), "Port name belongs to another configuration");
        await Reject(NativePrinterSetup.RemoveScript, Scenario(jobs: true), "Finish or cancel pending print jobs before removing the printer");

        var directory = Path.Combine(root, "artifacts", "installed-printer-checks", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(directory, "NativeRelay"); Directory.CreateDirectory(data);
        var id = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var queue = new NativeQueue(id, "offline-peer", "Canon", "Installed-check-" + id, 515);
        var mapping = Path.Combine(data, "queue-" + id + ".json");
        File.WriteAllText(mapping, JsonSerializer.Serialize(queue));
        var host = new RelayHost(directory);
        var relay = new NativePrintRelay(host, localPort: 0, relayPort: 0);
        using var stop = new CancellationTokenSource();
        var running = relay.RunAsync(stop.Token);
        try
        {
            Require(relay.InstalledPrinters.Single() == new InstalledPrinter(id, queue.Queue),
                "Installed printers reload from disk even when their peer is offline");
            var job = new NativePending(Guid.NewGuid().ToString("N"), queue.Id, 1, "hash");
            var manifest = Path.Combine(data, "pending-" + job.Id + ".json.review");
            File.WriteAllText(manifest, JsonSerializer.Serialize(job));
            async Task Blocked()
            {
                try { await relay.RemoveAsync(queue.Id, default); throw new Exception("Pending relay job was discarded"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("còn lệnh")) { Require(File.Exists(mapping), "Pending or uncertain relay jobs keep the queue mapping intact"); }
            }
            await Blocked();
            File.Delete(manifest);
            File.WriteAllText(Path.Combine(data, "local-" + job.Id + ".json"), JsonSerializer.Serialize(job));
            File.WriteAllBytes(Path.Combine(data, job.Id + ".raw"), [1]);
            await Blocked();
            try { await relay.RemoveAsync("../../unrelated", default); throw new Exception("Unknown queue accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("không còn")) { Require(true, "Unknown queue IDs cannot select arbitrary local printers or paths"); }
            File.Delete(Path.Combine(data, job.Id + ".raw"));
            // The fixture never created a Windows queue/port. Removal must also
            // clean a stale mapping after a user already deleted it in Windows.
            await relay.RemoveAsync(id, default);
            Require(relay.InstalledPrinters.Count == 0 && !File.Exists(mapping),
                "Already absent Windows queues can be removed from memory and persisted mappings");
        }
        finally { stop.Cancel(); await running; }
    }
}
